using System.Globalization;

namespace DeskPal.Core;

/// <summary>Settings <c>CrashReports</c>: whether crash reports go to the crash-report service.</summary>
public enum CrashReportingMode
{
    /// <summary>The default: a crash report waits on this PC until the user turns "Send crash reports" on.</summary>
    Ask,

    /// <summary>Crash reports are sent as they happen.</summary>
    Always,

    /// <summary>Crash reporting is off: the reporting library is never started and nothing is kept or sent.</summary>
    Never,
}

public enum CrashGateDecision
{
    Send,
    Queue,
    Drop,
}

public static class CrashReportGate
{
    public static CrashGateDecision Decide(CrashReportingMode mode, bool userConsented) => mode switch
    {
        CrashReportingMode.Never => CrashGateDecision.Drop,
        CrashReportingMode.Always => CrashGateDecision.Send,
        _ => userConsented ? CrashGateDecision.Send : CrashGateDecision.Queue,
    };

    /// <summary>Session tracking sends on its own, without a crash: only when reports are always sent.</summary>
    public static bool TracksSessions(CrashReportingMode mode) => mode == CrashReportingMode.Always;

    public static bool StartsReporter(CrashReportingMode mode) => mode != CrashReportingMode.Never;
}

public interface ICrashReportTransport
{
    string? Send(string eventJson);
}

/// <summary>Redacted crash reports waiting for the user's consent (Ask): one JSON file each, at most <see cref="MaxKept"/>.</summary>
public sealed class PendingCrashReports(string folder)
{
    public const int MaxKept = 10;
    public const int MaxBytes = 512 * 1024;
    private const string Pattern = "pending-*.json";

    public string Folder { get; } = folder;

    public int Count => Files().Count;

    public bool Add(string eventJson)
    {
        if (eventJson.Length > MaxBytes) return false;
        try
        {
            Directory.CreateDirectory(Folder);
            var name = string.Create(CultureInfo.InvariantCulture, $"pending-{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}.json");
            File.WriteAllText(Path.Combine(Folder, name), eventJson);
            foreach (var old in Files().SkipLast(MaxKept)) TryDelete(old);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public IReadOnlyList<string> Files()
    {
        try
        {
            return Directory.Exists(Folder)
                ? [.. Directory.EnumerateFiles(Folder, Pattern).Order(StringComparer.Ordinal)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public int Clear()
    {
        var files = Files();
        foreach (var file in files) TryDelete(file);
        return files.Count;
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class CrashReportOutbox(PendingCrashReports pending)
{
    public PendingCrashReports Pending { get; } = pending;

    /// <summary>True: send now. Otherwise kept for the user's consent (Ask) or dropped (Never).</summary>
    public bool Admit(CrashReportingMode mode, bool userConsented, Func<string> serialize)
    {
        switch (CrashReportGate.Decide(mode, userConsented))
        {
            case CrashGateDecision.Send:
                return true;
            case CrashGateDecision.Queue:
                Pending.Add(serialize());
                return false;
            default:
                return false;
        }
    }

    public IReadOnlyList<string> SendPending(CrashReportingMode mode, ICrashReportTransport transport)
    {
        var ids = new List<string>();
        if (CrashReportGate.Decide(mode, userConsented: true) != CrashGateDecision.Send) return ids;
        foreach (var file in Pending.Files())
        {
            string json;
            try { json = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            if (transport.Send(json) is { Length: > 0 } id)
            {
                ids.Add(id);
                PendingCrashReports.TryDelete(file);
            }
        }
        return ids;
    }

    public int DiscardPending() => Pending.Clear();
}

public static class CrashReportRelease
{
    /// <summary><c>deskpal@1.0.0</c>, or <c>deskpal@1.0.0-beta5</c> for a beta build.</summary>
    public static string Release(string version, string? betaTag)
    {
        var tag = Clean(betaTag);
        return "deskpal@" + (string.IsNullOrWhiteSpace(version) ? "0.0.0" : version.Trim()) + (tag.Length > 0 ? "-" + tag : string.Empty);
    }

    /// <summary>debug (a debug run or Debug build), beta (every other copy for now); production is reserved for the Store.</summary>
    public static string Environment(bool debug) => debug ? "debug" : "beta";

    private static string Clean(string? tag) =>
        tag is null ? string.Empty : new string([.. tag.Where(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-').Take(32)]).ToLowerInvariant();
}
