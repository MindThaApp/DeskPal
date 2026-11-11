using System.Text;
using System.Text.RegularExpressions;

namespace DeskPal.Core;

/// <summary>
/// Takes the personal parts out of crash-report text before it leaves the PC (messages, exception messages, stack frame paths,
/// breadcrumbs, extra data, log text):
/// <list type="bullet">
/// <item>the user profile folder becomes <c>%USERPROFILE%</c>, the app's own folder <c>&lt;app&gt;</c>;</item>
/// <item>in every other path, file and folder names become <c>&lt;folder&gt;</c> and <c>&lt;file&gt;.ext</c> (only the extension
/// stays), except well-known folders (Documents, AppData, Temp…) and the Windows and Program Files folders, which stay as they are;
/// a network path's server and share become <c>&lt;server&gt;</c> and <c>&lt;share&gt;</c>, another profile's name <c>&lt;user&gt;</c>;</item>
/// <item>the user name and the machine name, wherever they still appear as words, become <c>&lt;user&gt;</c> and <c>&lt;machine&gt;</c>.</item>
/// </list>
/// Running it again over its own output changes nothing.
/// </summary>
public sealed partial class CrashReportRedactor
{
    public const string ProfileToken = "%USERPROFILE%";
    public const string AppToken = "<app>";
    public const string UserToken = "<user>";
    public const string MachineToken = "<machine>";
    public const string FolderToken = "<folder>";
    public const string FileToken = "<file>";
    public const string NameToken = "<name>";
    public const string ServerToken = "<server>";
    public const string ShareToken = "<share>";

    // Folder names that say nothing about the user and help reading a report.
    private static readonly HashSet<string> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos", "Favorites", "Links", "Contacts", "Searches",
        "Saved Games", "3D Objects", "OneDrive", "AppData", "Local", "LocalLow", "Roaming", "Temp", "Tmp", "Packages",
        "Microsoft", "Windows", "WindowsApps", "Recent", "Start Menu", "Programs", "Startup", "Explorer", "INetCache",
        "DeskPal", "Desk Pal", "Cache", "Logs", "Dumps", "Data", "CrashReports", "Outbox", "Users", "Public",
        "ProgramData", "Program Files", "Program Files (x86)", "System32", "SysWOW64",
    };

    // Profiles that belong to no one in particular.
    private static readonly HashSet<string> SharedProfiles = new(StringComparer.OrdinalIgnoreCase) { "Public", "Default", "Default User", "All Users" };

    private readonly (string Prefix, string Token)[] _prefixes;
    private readonly string[] _keptRoots;
    private readonly Regex? _user;
    private readonly Regex? _machine;

    /// <param name="userProfile">The profile folder (<c>C:\Users\name</c>).</param>
    /// <param name="userName">The Windows user name.</param>
    /// <param name="machineName">The PC's name.</param>
    /// <param name="appDirectory">The app's own folder (shown as <c>&lt;app&gt;</c>, its files as they are).</param>
    /// <param name="keptRoots">Folders whose paths stay whole (the Windows and Program Files folders).</param>
    public CrashReportRedactor(string? userProfile, string? userName, string? machineName, string? appDirectory = null, IEnumerable<string>? keptRoots = null)
    {
        var prefixes = new List<(string, string)>();
        AddPrefix(prefixes, appDirectory, AppToken);
        AddPrefix(prefixes, userProfile, ProfileToken);
        // The longest first: an app folder inside the profile is the app's, not the profile's.
        _prefixes = [.. prefixes.OrderByDescending(static p => p.Item1.Length)];
        _keptRoots = [.. (keptRoots ?? []).Where(static r => !string.IsNullOrWhiteSpace(r)).Select(static r => r.TrimEnd('\\', '/'))];
        _user = WordPattern(userName);
        _machine = WordPattern(machineName);
    }

    /// <summary>For this PC: its profile, user name and machine name; the Windows and Program Files folders stay whole.</summary>
    public static CrashReportRedactor ForThisPc(string? appDirectory) =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName, appDirectory,
            [
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ]);

    /// <summary>The text with every personal part replaced (null and empty stay as they are).</summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(text))]
    public string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = ReplacePrefixes(text);
        result = RedactPaths(result);
        result = RedactEscapedUncPaths(result);
        if (_user is not null)
        {
            result = _user.Replace(result, UserToken);
        }

        if (_machine is not null)
        {
            result = _machine.Replace(result, MachineToken);
        }

        return result;
    }

    private static void AddPrefix(List<(string, string)> prefixes, string? folder, string token)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var trimmed = folder.Trim().TrimEnd('\\', '/');
        // A drive root ("C:") would swallow every path on the drive.
        if (trimmed.Length < 4)
        {
            return;
        }

        // As written, with forward slashes (file: URIs) and with doubled backslashes (JSON text).
        var plain = trimmed.Replace('/', '\\');
        foreach (var form in new[] { plain, plain.Replace('\\', '/'), plain.Replace("\\", "\\\\", StringComparison.Ordinal) }.Distinct(StringComparer.Ordinal))
        {
            prefixes.Add((form, token));
        }
    }

    private static Regex? WordPattern(string? word)
    {
        var trimmed = word?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length < 2)
        {
            return null;
        }

        // A whole word only, and never inside our own placeholders (<user>, %USERPROFILE%).
        return new Regex(@"(?<![\w<%])" + Regex.Escape(trimmed) + @"(?![\w>%])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private string ReplacePrefixes(string text)
    {
        foreach (var (prefix, token) in _prefixes)
        {
            StringBuilder? builder = null;
            var copied = 0;
            var search = 0;
            int index;
            while (search < text.Length && (index = text.IndexOf(prefix, search, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var end = index + prefix.Length;
                // C:\Users\ann is not C:\Users\anna (nor C:\Users\ann.old).
                var bounded = end >= text.Length || !ContinuesName(text, end);
                var startsFresh = index == 0 || !IsNameChar(text[index - 1]);
                if (bounded && startsFresh)
                {
                    builder ??= new StringBuilder(text.Length);
                    builder.Append(text, copied, index - copied).Append(token);
                    copied = end;
                    search = end;
                }
                else
                {
                    search = index + 1;
                }
            }

            if (builder is not null)
            {
                builder.Append(text, copied, text.Length - copied);
                text = builder.ToString();
            }
        }

        return text;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '~';

    private static bool ContinuesName(string text, int at) =>
        IsNameChar(text[at]) || (text[at] == '.' && at + 1 < text.Length && IsNameChar(text[at + 1]));

    private static bool IsSeparator(char c) => c is '\\' or '/';

    // Characters no path name holds (or that end one in running text).
    private static bool IsTerminator(char c) => c is ':' or '*' or '?' or '"' or '<' or '>' or '|' or '\r' or '\n' or '\t' or '\'' or '\0';

    private string RedactPaths(string text)
    {
        StringBuilder? builder = null;
        var copied = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (TryRoot(text, i, out var rootLength, out var kind))
            {
                var end = ScanPath(text, i + rootLength, out var segments);
                var original = text[i..end];
                var redacted = RedactOne(text.Substring(i, rootLength), kind, segments, original);
                if (!string.Equals(redacted, original, StringComparison.Ordinal))
                {
                    builder ??= new StringBuilder(text.Length);
                    builder.Append(text, copied, i - copied).Append(redacted);
                    copied = end;
                }

                i = Math.Max(end, i + 1);
                continue;
            }

            i++;
        }

        if (builder is null)
        {
            return text;
        }

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }

    // A network path doubled up the way JSON (or anything else that backslash-escapes itself) writes it: "\\\\server\\share\\file.txt"
    // for the real "\\server\share\file.txt". RedactPaths never sees this as a UNC root (it only recognises a plain "\\"), so the
    // server and share names - the most identifying part of a network path - would otherwise leave the PC exactly as typed whenever
    // one shows up already escaped inside free-form report text (an error message that carries a serialized object, for example).
    private static readonly string EscapedSeparator = new('\\', 4);
    private static readonly string EscapedPart = new('\\', 2);

    private string RedactEscapedUncPaths(string text)
    {
        StringBuilder? builder = null;
        var copied = 0;
        var search = 0;
        int index;
        while (search < text.Length && (index = text.IndexOf(EscapedSeparator, search, StringComparison.Ordinal)) >= 0)
        {
            var previous = index > 0 ? text[index - 1] : ' ';
            if (previous == '\\' || IsNameChar(previous))
            {
                search = index + 1;
                continue;
            }

            var cursor = index + EscapedSeparator.Length;
            var names = new List<string> { ReadEscapedSegment(text, ref cursor) };
            while (cursor < text.Length && text.AsSpan(cursor).StartsWith(EscapedPart, StringComparison.Ordinal))
            {
                cursor += EscapedPart.Length;
                names.Add(ReadEscapedSegment(text, ref cursor));
            }

            if (names.Count < 2 || names[0].Length == 0)
            {
                search = index + 1;
                continue;
            }

            var redacted = new string[names.Count];
            for (var n = 0; n < names.Count; n++)
            {
                redacted[n] = n switch
                {
                    0 => ServerToken,
                    1 => names[n].Length == 0 ? names[n] : ShareToken,
                    _ => RedactName(names[n], last: n == names.Count - 1),
                };
            }

            builder ??= new StringBuilder(text.Length);
            builder.Append(text, copied, index - copied).Append(EscapedSeparator).Append(string.Join(EscapedPart, redacted));
            copied = cursor;
            search = cursor;
        }

        if (builder is null)
        {
            return text;
        }

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }

    // One escaped path segment's name: up to the next escaped separator, a closing quote, whitespace, or the end of the text.
    private static string ReadEscapedSegment(string text, ref int cursor)
    {
        var start = cursor;
        while (cursor < text.Length && text[cursor] != '\\' && text[cursor] != '"' && !char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }

        return text[start..cursor];
    }

    private enum RootKind
    {
        Profile,
        Drive,
        Unc,
    }

    private static bool TryRoot(string text, int i, out int length, out RootKind kind)
    {
        length = 0;
        kind = RootKind.Drive;
        var previous = i > 0 ? text[i - 1] : ' ';
        if (text.AsSpan(i).StartsWith(ProfileToken, StringComparison.Ordinal))
        {
            length = ProfileToken.Length;
            kind = RootKind.Profile;
            return i + length < text.Length && IsSeparator(text[i + length]);
        }

        if (char.IsAsciiLetter(text[i]) && i + 2 < text.Length && text[i + 1] == ':' && IsSeparator(text[i + 2])
            && !char.IsLetterOrDigit(previous) && previous is not '%' and not '_')
        {
            length = 2;
            kind = RootKind.Drive;
            return true;
        }

        if (text[i] == '\\' && i + 2 < text.Length && text[i + 1] == '\\' && char.IsLetterOrDigit(text[i + 2]) && !IsSeparator(previous) && !char.IsLetterOrDigit(previous) && previous != ':')
        {
            length = 0;
            kind = RootKind.Unc;
            return true;
        }

        return false;
    }

    // Reads separators and names from start; the last name ends at its extension or the first space. Returns the end.
    private static int ScanPath(string text, int start, out List<(string Separator, string Name)> segments)
    {
        segments = [];
        var p = start;
        while (p < text.Length)
        {
            var separatorStart = p;
            while (p < text.Length && IsSeparator(text[p]))
            {
                p++;
            }

            if (p == separatorStart)
            {
                break;
            }

            var separator = text[separatorStart..p];
            var nameStart = p;
            while (p < text.Length && !IsSeparator(text[p]) && !IsTerminator(text[p]))
            {
                p++;
            }

            if (p < text.Length && IsSeparator(text[p]))
            {
                segments.Add((separator, text[nameStart..p]));
                continue;
            }

            var name = text[nameStart..p];
            var cut = LastNameLength(name);
            segments.Add((separator, name[..cut]));
            p = nameStart + cut;
            break;
        }

        return p;
    }

    // "My File.txt because …" → "My File.txt"; "folder is missing" → "folder"; a sentence's full stop is not part of the name.
    private static int LastNameLength(string name)
    {
        var extension = ExtensionInText().Match(name);
        var cut = extension.Success ? extension.Index + extension.Length : name.IndexOfAny([' ', '\u00A0']) is >= 0 and var space ? space : name.Length;
        while (cut > 0 && name[cut - 1] is '.' or ',' or ';')
        {
            cut--;
        }

        return cut;
    }

    [GeneratedRegex(@"\.[A-Za-z0-9]{1,8}(?=$|[\s,;)\]]|\.(?:\s|$))", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionInText();

    private string RedactOne(string root, RootKind kind, List<(string Separator, string Name)> segments, string original)
    {
        if (kind == RootKind.Drive && IsKept(original))
        {
            return original;
        }

        var builder = new StringBuilder(original.Length).Append(root);
        for (var index = 0; index < segments.Count; index++)
        {
            var (separator, name) = segments[index];
            builder.Append(separator);
            var last = index == segments.Count - 1;
            if (kind == RootKind.Unc && index < 2)
            {
                builder.Append(name.Length == 0 ? name : index == 0 ? ServerToken : ShareToken);
                continue;
            }

            // X:\Users\<someone>: a profile seen by another name (8.3 short name, another account).
            if (kind == RootKind.Drive && index == 1 && segments[0].Name.Equals("Users", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(name.Length == 0 || SharedProfiles.Contains(name) || IsPlaceholder(name) ? name : UserToken);
                continue;
            }

            builder.Append(RedactName(name, last));
        }

        return builder.ToString();
    }

    private bool IsKept(string path)
    {
        foreach (var root in _keptRoots)
        {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (path.Length == root.Length || IsSeparator(path[root.Length])))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPlaceholder(string name) => name.Length > 2 && name[0] == '<' && name[^1] == '>';

    private static string RedactName(string name, bool last)
    {
        if (name.Length == 0 || IsPlaceholder(name) || KnownFolders.Contains(name))
        {
            return name;
        }

        // "OneDrive - Contoso" names the employer.
        if (name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase) && (name.Length == 8 || name[8] is ' ' or '-'))
        {
            return "OneDrive";
        }

        if (!last)
        {
            return FolderToken;
        }

        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1 && name.Length - dot - 1 <= 8 && name.AsSpan(dot + 1).ContainsOnlyAsciiLettersOrDigits())
        {
            return FileToken + name[dot..];
        }

        return NameToken;
    }
}

internal static class SpanNameExtensions
{
    public static bool ContainsOnlyAsciiLettersOrDigits(this ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
