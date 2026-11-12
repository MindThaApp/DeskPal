using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace DeskPal.Core.Integration;

// Pipe names shared with Automation Portal's conventions: <App>-<user SID>, plus -<8 hex> for a non-default data root.
public static class PipeNames
{
    [SupportedOSPlatform("windows")]
    public static string UserSid => WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;

    // debugDataRoot: null for the real data folder; the debug folder gets its own suffix so test runs never answer for the real app.
    public static string DeskPal(string sid, string? debugDataRoot) => WithRoot("DeskPal-" + sid, debugDataRoot);

    // Desk Pal only talks to the user's real Automation Portal, never one on a test data root.
    public static string AutomationPortal(string sid) => "AutomationPortal-" + sid;

    public static string WithRoot(string name, string? dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) return name;
        var full = Path.GetFullPath(dataRoot).TrimEnd('\\', '/').ToLowerInvariant();
        return name + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..8];
    }
}

public static class PipeLines
{
    public static readonly UTF8Encoding Utf8 = new(false);

    public enum ReadStatus { Line, Empty, TooLarge, TimedOut }

    // Reads up to the first line break. Empty when the other side left without sending anything.
    public static async Task<(ReadStatus Status, string? Line)> ReadLineAsync(Stream stream, int maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var buffer = new byte[8 * 1024];
        using var line = new MemoryStream();
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    if (line.Length == 0) return (ReadStatus.Empty, null);
                    break;
                }
                var end = Array.IndexOf(buffer, (byte)'\n', 0, n);
                var take = end < 0 ? n : end;
                if (line.Length + take > maxBytes) return (ReadStatus.TooLarge, null);
                line.Write(buffer, 0, take);
                if (end >= 0) break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (ReadStatus.TimedOut, null);
        }
        var text = Utf8.GetString(line.GetBuffer(), 0, (int)line.Length);
        return (ReadStatus.Line, text.EndsWith('\r') ? text[..^1] : text);
    }

    public static async Task WriteLineAsync(Stream stream, string json, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var bytes = Utf8.GetBytes(json + "\n");
        await stream.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
        await stream.FlushAsync(cts.Token).ConfigureAwait(false);
    }
}

// Opens a client connection; null when nobody is listening in time or the pipe refuses us.
public interface IPipeConnector
{
    Task<Stream?> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct);
}

public sealed class NamedPipeConnector : IPipeConnector
{
    public async Task<Stream?> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
            return pipe;
        }
        // Not running (timeout), busy, or an elevation mismatch (CurrentUserOnly refuses elevated and non-elevated pairs): all mean "not reachable".
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }
}
