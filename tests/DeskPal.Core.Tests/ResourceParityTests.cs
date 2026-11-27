using System.Text.RegularExpressions;
using System.Xml.Linq;
using DeskPal.Core;

namespace DeskPal.Core.Tests;

public class ResourceParityTests
{
    private static readonly Regex Placeholder = new(@"\{[^{}]*\}", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeskPal.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DeskPal.sln not found above " + AppContext.BaseDirectory);
    }

    private static Dictionary<string, string> Load(string path)
    {
        var doc = XDocument.Load(path);
        return doc.Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");
    }

    private static string Signature(string value) =>
        string.Join("|", Placeholder.Matches(value).Select(m => m.Value).OrderBy(s => s, StringComparer.Ordinal));

    public static IEnumerable<object[]> Languages() =>
        SupportedLanguages.All.Where(l => l.Tag != SupportedLanguages.Fallback).Select(l => new object[] { l.Tag });

    private static void Check(string englishPath, string path, List<string> problems)
    {
        if (!File.Exists(path)) { problems.Add("missing file " + path); return; }
        var english = Load(englishPath);
        var other = Load(path);
        var name = Path.GetFileName(Path.GetDirectoryName(path)!) + "/" + Path.GetFileName(path);
        foreach (var key in english.Keys.Except(other.Keys)) problems.Add($"{name}: missing key {key}");
        foreach (var key in other.Keys.Except(english.Keys)) problems.Add($"{name}: extra key {key}");
        foreach (var (key, value) in other)
        {
            if (string.IsNullOrWhiteSpace(value)) { problems.Add($"{name}: empty value {key}"); continue; }
            if (english.TryGetValue(key, out var en) && Signature(en) != Signature(value))
                problems.Add($"{name}: placeholders differ in {key}");
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void AppStrings_MatchEnglish(string tag)
    {
        var root = RepoRoot();
        var problems = new List<string>();
        Check(Path.Combine(root, "src", "DeskPal", "Strings", "en-US", "Resources.resw"),
            Path.Combine(root, "src", "DeskPal", "Strings", tag, "Resources.resw"), problems);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(10)) + $" ({problems.Count} problems)");
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void CoreStrings_MatchNeutral(string tag)
    {
        var root = RepoRoot();
        var problems = new List<string>();
        Check(Path.Combine(root, "src", "DeskPal.Core", "Strings.resx"),
            Path.Combine(root, "src", "DeskPal.Core", $"Strings.{tag}.resx"), problems);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(10)) + $" ({problems.Count} problems)");
    }
}
