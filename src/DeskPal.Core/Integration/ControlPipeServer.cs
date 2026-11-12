using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeskPal.Core.Integration;

// Listens on DeskPal-<SID> (CurrentUserOnly). One request line in, one reply line out, then the pipe closes.
// Nothing here may throw into the app: every failure becomes an error reply or a log line.
public sealed class ControlPipeServer : IDisposable
{
    public const int MaxRequestBytes = 64 * 1024;
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    // Long enough for the confirm card's wait plus the work itself.
    public static readonly TimeSpan HandleTimeout = ControlService.ApprovalWait + TimeSpan.FromSeconds(15);
    public const int MaxConcurrent = 8;

    private readonly string _name;
    private readonly Func<string, CallerInfo, Task<ControlReply>> _handler;
    private readonly Action<Exception, string>? _onError;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrent);
    private Task? _loop;

    public ControlPipeServer(string name, Func<string, CallerInfo, Task<ControlReply>> handler, Action<Exception, string>? onError = null)
    {
        _name = name;
        _handler = handler;
        _onError = onError;
    }

    public string Name => _name;

    // False when the pipe can't be created (for example another copy already owns the name).
    public bool Start()
    {
        NamedPipeServerStream first;
        try { first = Create(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _onError?.Invoke(ex, "ControlPipeServer.Start");
            return false;
        }
        _loop = Task.Run(() => AcceptLoopAsync(first));
        return true;
    }

    private NamedPipeServerStream Create() =>
        new(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task AcceptLoopAsync(NamedPipeServerStream pipe)
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            try { await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { pipe.Dispose(); return; }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                pipe.Dispose();
                if (++failures > 20) { _onError?.Invoke(ex, "ControlPipeServer.Accept"); return; }
                try { pipe = Create(); } catch (Exception e2) { _onError?.Invoke(e2, "ControlPipeServer.Create"); return; }
                continue;
            }

            failures = 0;
            var connected = pipe;
            try { pipe = Create(); }
            catch (Exception ex)
            {
                _onError?.Invoke(ex, "ControlPipeServer.Create");
                connected.Dispose();
                return;
            }
            _ = Task.Run(() => ServeAsync(connected));
        }
        pipe.Dispose();
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using var owned = pipe;
        try
        {
            if (!await _slots.WaitAsync(0).ConfigureAwait(false))
            {
                await ReplyAsync(pipe, ControlReply.Fail(ControlErrors.Busy, "Too many requests at once.")).ConfigureAwait(false);
                return;
            }
            try
            {
                var (status, line) = await PipeLines.ReadLineAsync(pipe, MaxRequestBytes, ReadTimeout, _stop.Token).ConfigureAwait(false);
                ControlReply reply;
                switch (status)
                {
                    case PipeLines.ReadStatus.Empty: return;
                    case PipeLines.ReadStatus.TooLarge: reply = ControlReply.Fail(ControlErrors.TooLarge, "Requests are limited to 64 KB."); break;
                    case PipeLines.ReadStatus.TimedOut: reply = ControlReply.Fail(ControlErrors.Timeout, "No complete request arrived in time."); break;
                    default: reply = await HandleSafelyAsync(line!, CallerOf(pipe)).ConfigureAwait(false); break;
                }
                await ReplyAsync(pipe, reply).ConfigureAwait(false);
            }
            finally { _slots.Release(); }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex) { _onError?.Invoke(ex, "ControlPipeServer.Serve"); }
    }

    private async Task<ControlReply> HandleSafelyAsync(string line, CallerInfo caller)
    {
        try
        {
            var work = _handler(line, caller);
            var done = await Task.WhenAny(work, Task.Delay(HandleTimeout, _stop.Token)).ConfigureAwait(false);
            if (done != work) return ControlReply.Fail(ControlErrors.Timeout, "Desk Pal took too long to answer.");
            return await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return ControlReply.Fail(ControlErrors.Timeout, "Desk Pal is closing."); }
        catch (Exception ex)
        {
            _onError?.Invoke(ex, "ControlPipeServer.Handle");
            return ControlReply.Fail(ControlErrors.Internal, "Something went wrong in Desk Pal.");
        }
    }

    private async Task ReplyAsync(NamedPipeServerStream pipe, ControlReply reply)
    {
        await PipeLines.WriteLineAsync(pipe, reply.ToJson(), ReadTimeout, _stop.Token).ConfigureAwait(false);
        if (OperatingSystem.IsWindows()) { try { pipe.WaitForPipeDrain(); } catch (IOException) { } }
    }

    public static CallerInfo CallerOf(NamedPipeServerStream pipe)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid == 0)
                return new CallerInfo(0, null, null);
            return Describe((int)pid);
        }
        catch { return new CallerInfo(0, null, null); }
    }

    public static CallerInfo Describe(int pid)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (process.IsInvalid) return new CallerInfo(pid, null, null);

        string? exe = null;
        var size = 1024;
        var path = new StringBuilder(size);
        if (QueryFullProcessImageName(process, 0, path, ref size)) exe = path.ToString();

        string? family = null;
        var length = 256u;
        var name = new StringBuilder((int)length);
        if (GetPackageFamilyName(process, ref length, name) == 0) family = name.ToString();
        return new CallerInfo(pid, exe, family);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(500); } catch { }
        _stop.Dispose();
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, StringBuilder? name);
}
