using System.Text.Json;
using System.Threading.Channels;

namespace DeskPal.Core.Integration;

public sealed record ApCapabilities(string App, string? Version, int Contract, IReadOnlyList<string> Kinds, IReadOnlyList<string> Features)
{
    public bool Supports(string kind) => Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase);
}

public enum ApSendResult { Sent, Absent, Unsupported, Failed }

// Speaks Automation Portal's own pipe shape: {kind,args,name,input,files,wait,json,source} in, {exitCode,output,message} out.
// Never starts Automation Portal; if it isn't running the call just reports Absent.
public sealed class AutomationPortalClient(IPipeConnector connector, string pipeName, TimeProvider time)
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan CapabilitiesLifetime = TimeSpan.FromMinutes(5);
    public const int MaxReplyBytes = 256 * 1024;
    public const string EventKind = "event";
    public const string Source = "deskpal";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private ApCapabilities? _caps;
    private DateTimeOffset _capsAt;

    public sealed class Request
    {
        public string Kind { get; set; } = "";
        public string[] Args { get; set; } = [];
        public string? Name { get; set; }
        public string? Input { get; set; }
        public string[] Files { get; set; } = [];
        public bool Wait { get; set; }
        public bool Json { get; set; }
        public string Source { get; set; } = AutomationPortalClient.Source;
    }

    public sealed class Reply
    {
        public int ExitCode { get; set; }
        public string? Output { get; set; }
        public string? Message { get; set; }
    }

    // Cached for a few minutes while Automation Portal is running; asked again after it was gone.
    public async Task<ApCapabilities?> GetCapabilitiesAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_caps is not null && now - _capsAt < CapabilitiesLifetime) return _caps;
        _caps = null;
        var reply = await SendAsync(new Request { Kind = "capabilities", Json = true }, ct).ConfigureAwait(false);
        if (reply is null || reply.ExitCode != 0 || reply.Output is null) return null;
        _caps = ParseCapabilities(reply.Output);
        _capsAt = now;
        return _caps;
    }

    public void ForgetCapabilities() => _caps = null;

    public static ApCapabilities? ParseCapabilities(string output)
    {
        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            static string? Str(JsonElement o, string name) =>
                o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            static List<string> List(JsonElement o, string name) =>
                o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                    ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : [];
            var contract = root.TryGetProperty("contract", out var c) && c.TryGetInt32(out var n) ? n : 0;
            return new ApCapabilities(Str(root, "app") ?? "", Str(root, "version"), contract, List(root, "kinds"), List(root, "features"));
        }
        catch (JsonException) { return null; }
    }

    // Fire-and-forget from the caller's side: wait:false, and the short reply is read only so the pipe closes cleanly.
    public async Task<ApSendResult> SendEventAsync(DeskPalEvent e, CancellationToken ct)
    {
        var caps = await GetCapabilitiesAsync(ct).ConfigureAwait(false);
        if (caps is null) return ApSendResult.Absent;
        if (!caps.Supports(EventKind)) return ApSendResult.Unsupported;
        var reply = await SendAsync(new Request { Kind = EventKind, Name = e.Name, Input = e.ToJson(), Wait = false }, ct).ConfigureAwait(false);
        if (reply is null)
        {
            ForgetCapabilities();
            return ApSendResult.Absent;
        }
        return reply.ExitCode == 0 ? ApSendResult.Sent : ApSendResult.Failed;
    }

    // Null when Automation Portal isn't reachable or the reply isn't readable.
    public async Task<Reply?> SendAsync(Request request, CancellationToken ct)
    {
        await using var stream = await connector.ConnectAsync(pipeName, ConnectTimeout, ct).ConfigureAwait(false);
        if (stream is null) return null;
        try
        {
            await PipeLines.WriteLineAsync(stream, JsonSerializer.Serialize(request, Json), ReplyTimeout, ct).ConfigureAwait(false);
            var (status, line) = await PipeLines.ReadLineAsync(stream, MaxReplyBytes, ReplyTimeout, ct).ConfigureAwait(false);
            if (status != PipeLines.ReadStatus.Line || line is null) return null;
            return JsonSerializer.Deserialize<Reply>(line, Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException or OperationCanceledException) { return null; }
    }
}

// Sends published events to Automation Portal one at a time on a background task, never on the UI thread.
// When sharing is off nothing is sent or counted; when Automation Portal can't take the event it is counted as skipped for today.
public sealed class AutomationPortalForwarder : IDisposable
{
    private readonly AutomationPortalClient _client;
    private readonly Func<bool> _enabled;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly Channel<DeskPalEvent> _queue = Channel.CreateBounded<DeskPalEvent>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly object _lock = new();
    private DateOnly _skippedDate;
    private int _skipped;

    public AutomationPortalForwarder(AutomationPortalClient client, Func<bool> enabled, TimeProvider time, Action<string>? log = null)
    {
        _client = client;
        _enabled = enabled;
        _time = time;
        _log = log;
        _loop = Task.Run(LoopAsync);
    }

    public event Action? SkippedChanged;

    public int SkippedToday
    {
        get
        {
            lock (_lock) return _skippedDate == Today() ? _skipped : 0;
        }
    }

    public void Post(DeskPalEvent e)
    {
        if (!_enabled()) return;
        Interlocked.Increment(ref _pending);
        if (_queue.Writer.TryWrite(e)) return;
        Interlocked.Decrement(ref _pending);
        CountSkipped();
    }

    // For tests: waits until everything posted so far was handled.
    public async Task DrainAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _pending) > 0)
        {
            if (DateTime.UtcNow > until) return;
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private int _pending;

    private async Task LoopAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var e))
                {
                    try
                    {
                        if (!_enabled()) continue;
                        var result = await _client.SendEventAsync(e, _stop.Token).ConfigureAwait(false);
                        _log?.Invoke($"automation portal event {e.Name}: {result.ToString().ToLowerInvariant()}");
                        if (result != ApSendResult.Sent) CountSkipped();
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"automation portal event {e.Name} failed: {ex.GetType().Name}");
                        CountSkipped();
                    }
                    finally { Interlocked.Decrement(ref _pending); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private DateOnly Today() => DateOnly.FromDateTime(_time.GetLocalNow().Date);

    private void CountSkipped()
    {
        lock (_lock)
        {
            var today = Today();
            if (_skippedDate != today)
            {
                _skippedDate = today;
                _skipped = 0;
            }
            _skipped++;
        }
        try { SkippedChanged?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _queue.Writer.TryComplete();
        try { _loop.Wait(500); } catch { }
        _stop.Dispose();
    }
}
