using System.Text;
using System.Text.Json;
using DeskPal.Core;
using Sentry;
using Sentry.Protocol;

namespace DeskPal;

/// <summary>
/// Crash reports through Sentry (EU region), behind the consent gate and the redaction in Core, as in Files Portal:
/// On (the default): reports go out as they happen. Off (Never): the SDK is never started and nothing is kept. Reports that were
/// queued while the old "ask first" setting was in force are never sent; they stay in the app's own pending folder until the user turns reporting off. Every event passes <see cref="BeforeSend"/>:
/// paths, the user name and the PC name are replaced, the server name and user details are dropped, and only our own redacted
/// log tail stays attached. No performance tracing.
/// </summary>
internal sealed class CrashReporting
{
    // The project's public client key: it can only submit events.
    private const string Dsn = "https://27eff5636a3fd2ce839b077f4cff6c7a@o4512289248706560.ingest.de.sentry.io/4512413434576976";
    private const string ConsentTag = "dp.consented";
    private const string LogTailName = "log-tail.txt";
    private const int LogTailLines = 200;

#if DEBUG
    private const bool DevBuild = true;
#else
    private const bool DevBuild = false;
#endif

    private static readonly string BetaTag = System.Reflection.Assembly.GetEntryAssembly()?
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>()
        .FirstOrDefault(static a => a.Key == "DpBetaTag")?.Value ?? string.Empty;

    public static string ReleaseText => CrashReportRelease.Release(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0", BetaTag);

    private static readonly object Gate = new();
    private static readonly CrashReportRedactor Redactor = CrashReportRedactor.ForThisPc(AppContext.BaseDirectory);
    private static readonly CrashReportOutbox Outbox = new(new PendingCrashReports(Path.Combine(AppData.Dir, "CrashReports", "pending")));
    private static readonly string OutboxFolder = Path.Combine(AppData.Dir, "CrashReports", "Outbox");
    private static IDisposable? _sdk;
    private static CrashReportingMode _mode = CrashReportingMode.Never;

    // Only our own field: reading it never loads the SDK.
    public static bool IsRunning => _sdk is not null;

    public static int PendingCount => Outbox.Pending.Count;

    public static void Apply(CrashReportingMode mode)
    {
        lock (Gate)
        {
            if (_sdk is not null && _mode == mode) return;
            _mode = mode;
            _sdk?.Dispose();
            _sdk = null;

            if (!CrashReportGate.StartsReporter(mode))
            {
                // Nothing kept: what waited for consent and what the SDK had cached go too.
                Outbox.DiscardPending();
                TryDeleteFolder(OutboxFolder);
                return;
            }

            try { _sdk = StartSdk(mode); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Logger.Log("crash reporting failed to start: " + ex.GetType().Name);
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static IDisposable StartSdk(CrashReportingMode mode) => SentrySdk.Init(options =>
    {
        options.Dsn = Dsn;
        options.Debug = false;
        options.SendDefaultPii = false;
        options.IsEnvironmentUser = false;
        options.AutoSessionTracking = CrashReportGate.TracksSessions(mode);
        options.IsGlobalModeEnabled = true;
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
        options.Release = CrashReportRelease.Release(version, BetaTag);
        options.Environment = CrashReportRelease.Environment(DevBuild || AppData.IsDebugRun);
        options.CacheDirectoryPath = OutboxFolder;
        options.InitCacheFlushTimeout = TimeSpan.Zero;
        options.ShutdownTimeout = TimeSpan.FromSeconds(2);
        options.AttachStacktrace = false;
        options.SendClientReports = false;
        options.MaxBreadcrumbs = 50;
        // App.xaml.cs reports its own handlers with a source tag.
        options.DisableAppDomainUnhandledExceptionCapture();
        options.DisableUnobservedTaskExceptionCapture();
        options.SetBeforeSend(BeforeSend);
        options.SetBeforeBreadcrumb(RedactBreadcrumb);
        options.SetBeforeSendTransaction(static _ => null);
    });

    public static void CaptureCrash(Exception? exception, string source, bool handled)
    {
        if (!IsRunning || exception is null) return;
        try
        {
            Capture(exception, source, handled);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Reporting a crash must never be the next crash.
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Capture(Exception exception, string source, bool handled)
    {
        exception.Data[Mechanism.HandledKey] = handled;
        exception.Data[Mechanism.MechanismKey] = source;
        var tail = ReadLogTail();
        SentrySdk.CaptureException(exception, scope =>
        {
            scope.SetTag("source", source);
            if (tail.Length > 0) scope.AddAttachment(Encoding.UTF8.GetBytes(tail), LogTailName, AttachmentType.Default, "text/plain");
        });
        // A fatal crash ends the process next: let the event leave (or reach the outbox) first.
        if (!handled) SentrySdk.Flush(TimeSpan.FromSeconds(2));
    }

    public static void Breadcrumb(string message)
    {
        if (!IsRunning) return;
        try { AddBreadcrumb(message); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AddBreadcrumb(string message)
    {
        if (message.StartsWith("CRASH", StringComparison.Ordinal)) return;
        var line = message.Split('\n', 2)[0].Trim();
        SentrySdk.AddBreadcrumb(line.Length > 200 ? line[..200] : line, "deskpal");
    }

    private static string ReadLogTail()
    {
        try
        {
            var path = Path.Combine(AppData.Dir, "logs", "deskpal.log");
            if (!File.Exists(path)) return string.Empty;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var queue = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                queue.Enqueue(line);
                if (queue.Count > LogTailLines) queue.Dequeue();
            }
            return Redactor.Redact(string.Join(Environment.NewLine, queue)) ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static SentryEvent? BeforeSend(SentryEvent evt, SentryHint hint)
    {
        try
        {
            var consented = evt.Tags.ContainsKey(ConsentTag);
            if (consented) evt.UnsetTag(ConsentTag);

            // No screenshots, no files: only our own redacted log tail.
            var keep = hint.Attachments.Where(static a => a.FileName == LogTailName).ToList();
            hint.Attachments.Clear();
            foreach (var attachment in keep) hint.Attachments.Add(attachment);

            Redact(evt);
            return Outbox.Admit(_mode, consented, () => Serialize(evt)) ? evt : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unsure what is in it: it does not go.
            return null;
        }
    }

    private static Breadcrumb? RedactBreadcrumb(Breadcrumb crumb)
    {
        try
        {
            var data = crumb.Data?.ToDictionary(static p => p.Key, p => Redactor.Redact(p.Value) ?? string.Empty);
            return new Breadcrumb(Redactor.Redact(crumb.Message) ?? string.Empty, crumb.Type ?? "default", data, crumb.Category, crumb.Level);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static void Redact(SentryEvent evt)
    {
        evt.ServerName = null;
        evt.User.Username = null;
        evt.User.Email = null;
        evt.User.IpAddress = null;
        evt.Contexts.Device.Name = null;
        evt.Request = new SentryRequest();
        evt.TransactionName = Redactor.Redact(evt.TransactionName);
        evt.Logger = Redactor.Redact(evt.Logger);
        if (evt.Message is { } message)
        {
            message.Message = Redactor.Redact(message.Message);
            message.Formatted = Redactor.Redact(message.Formatted);
            message.Params = null;
        }

        foreach (var exception in evt.SentryExceptions ?? [])
        {
            exception.Value = Redactor.Redact(exception.Value);
            RedactFrames(exception.Stacktrace);
        }

        foreach (var thread in evt.SentryThreads ?? [])
        {
            thread.Name = Redactor.Redact(thread.Name);
            RedactFrames(thread.Stacktrace);
        }

        foreach (var image in evt.DebugImages ?? [])
        {
            image.CodeFile = Redactor.Redact(image.CodeFile);
            image.DebugFile = Redactor.Redact(image.DebugFile);
        }

        foreach (var (key, value) in evt.Extra.ToList())
        {
            evt.SetExtra(key, value is string text ? Redactor.Redact(text) : value?.ToString() is { } other ? Redactor.Redact(other) : null);
        }

        foreach (var (key, value) in evt.Tags.ToList())
        {
            evt.SetTag(key, Redactor.Redact(value) ?? string.Empty);
        }
    }

    private static void RedactFrames(SentryStackTrace? stack)
    {
        foreach (var frame in stack?.Frames ?? [])
        {
            frame.FileName = Redactor.Redact(frame.FileName);
            frame.AbsolutePath = Redactor.Redact(frame.AbsolutePath);
            frame.ContextLine = Redactor.Redact(frame.ContextLine);
            frame.Package = Redactor.Redact(frame.Package);
            frame.Vars.Clear();
        }
    }

    private static string Serialize(SentryEvent evt)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            evt.WriteTo(writer, null);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // Only the SDK cache folder this app created.
    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
