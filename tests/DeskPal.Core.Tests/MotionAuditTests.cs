using System.Text.RegularExpressions;
using Xunit;

namespace DeskPal.Core.Tests;

public class MotionAuditTests
{
    // Motion.cs owns the settle curve. Bloop character motion (springs, 2D/3D companion) is a separate system and
    // currently uses no XAML easing; list a file here only with a reason.
    private static readonly string[] Allowed = { "Motion.cs" };

    private static readonly Regex Raw = new(@"CubicBezier|KeySpline|ControlPoint[12]|EasingFunction|CubicEase|QuadraticEase|SineEase|ExponentialEase|BackEase|ElasticEase|EasingMode",
        RegexOptions.Compiled);

    [Fact]
    public void NoRawEasingOutsideTheMotionToken()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "DeskPal"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var root = Path.Combine(dir!.FullName, "src", "DeskPal");

        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml"))
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                              && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            if (Allowed.Contains(Path.GetFileName(file))) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (Raw.IsMatch(lines[i])) hits.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
        }
        Assert.True(hits.Count == 0, "Raw easing outside Motion.cs: " + string.Join(", ", hits));
    }
}
