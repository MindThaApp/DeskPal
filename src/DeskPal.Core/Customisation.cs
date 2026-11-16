using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeskPal.Core;

// Bloop and Sprout customisation (a Desk Pal Evolution feature).
//
// The saved choices live in DeskPalSettings.Look as plain strings, so a file written by a newer version with an
// option this one does not know still loads (the unknown value shows as the default and is kept as written).
// What is drawn is always CharacterLook.Effective(saved, state): the saved look while Pro is unlocked, the
// default look otherwise. The saved values are never cleared when Pro lapses.

public enum BloopColour { Blue, Mint, Peach, Lilac, Sunny, Bubblegum }

public enum BloopHat { None, Beanie, PartyHat, LeafSprig }

public enum PotStyle { Terracotta, SkyGlaze, Cream }

public sealed class LookSettings
{
    public string BloopColour { get; set; } = "";
    public string Hat { get; set; } = "";
    public string Pot { get; set; } = "";

    public CharacterLook ToLook() => new(
        Parse(BloopColour, Core.BloopColour.Blue),
        Parse(Hat, BloopHat.None),
        Parse(Pot, PotStyle.Terracotta));

    public void Set(BloopColour colour) => BloopColour = colour.ToString();
    public void Set(BloopHat hat) => Hat = hat.ToString();
    public void Set(PotStyle pot) => Pot = pot.ToString();

    private static T Parse<T>(string? text, T standard) where T : struct, Enum =>
        !string.IsNullOrWhiteSpace(text) && Enum.TryParse<T>(text, true, out var v) && Enum.IsDefined(v) ? v : standard;
}

public readonly record struct CharacterLook(BloopColour Colour, BloopHat Hat, PotStyle Pot)
{
    public static readonly CharacterLook Default = new(BloopColour.Blue, BloopHat.None, PotStyle.Terracotta);

    public bool IsDefault => this == Default;

    // A short stable key for caches ("blue-none-terracotta").
    public string Key => $"{Colour}-{Hat}-{Pot}".ToLowerInvariant();

    public static CharacterLook Effective(LookSettings? saved, ProState state) =>
        state.Pick(saved?.ToLook() ?? Default, Default);

    public ColourShift? BloopShift => ColourShift.ForBloop(Colour);
    public ColourShift? PotShift => ColourShift.ForPot(Pot);
}

// Moves one colour family (hue window around SourceHue) to another hue, scaling saturation and lightness, and
// leaves everything else alone. Dark, grey and off-hue colours (eyes, mouth, blush, shadows, highlights) keep
// their colour because they fall outside the window or below the saturation and lightness floors.
public sealed record ColourShift(double SourceHue, double Tolerance, double MinSaturation, double MinLightness,
    double TargetHue, double SaturationScale, double LightnessScale)
{
    private const double BloopHue = 210, PotHue = 23;

    public static ColourShift? ForBloop(BloopColour colour) => colour switch
    {
        BloopColour.Mint => Bloop(158, 0.72, 1.0),
        BloopColour.Peach => Bloop(22, 0.95, 1.04),
        BloopColour.Lilac => Bloop(268, 0.62, 1.02),
        BloopColour.Sunny => Bloop(46, 1.1, 1.0),
        BloopColour.Bubblegum => Bloop(322, 0.78, 1.04),
        _ => null,
    };

    public static ColourShift? ForPot(PotStyle pot) => pot switch
    {
        PotStyle.SkyGlaze => Pot(204, 0.72, 1.06),
        PotStyle.Cream => Pot(38, 0.32, 1.32),
        _ => null,
    };

    private static ColourShift Bloop(double hue, double sat, double light) => new(BloopHue, 20, 0.35, 0.42, hue, sat, light);
    private static ColourShift Pot(double hue, double sat, double light) => new(PotHue, 12, 0.35, 0.31, hue, sat, light);

    public bool Matches(double h, double s, double l) =>
        s >= MinSaturation && l >= MinLightness && HueDistance(h, SourceHue) <= Tolerance;

    public (byte R, byte G, byte B) Apply(byte r, byte g, byte b)
    {
        var (h, s, l) = ToHsl(r, g, b);
        if (!Matches(h, s, l)) return (r, g, b);
        var nh = h + (TargetHue - SourceHue);
        nh = ((nh % 360) + 360) % 360;
        var ns = Math.Clamp(s * SaturationScale, 0, 1);
        var nl = Math.Clamp(l * LightnessScale, 0, 0.97);
        return FromHsl(nh, ns, nl);
    }

    public static double HueDistance(double a, double b)
    {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    public static (double H, double S, double L) ToHsl(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var l = (max + min) / 2;
        if (max - min < 1e-9) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == rf) h = (gf - bf) / d + (gf < bf ? 6 : 0);
        else if (max == gf) h = (bf - rf) / d + 2;
        else h = (rf - gf) / d + 4;
        return (h * 60, s, l);
    }

    public static (byte R, byte G, byte B) FromHsl(double h, double s, double l)
    {
        if (s <= 0) { var v = ToByte(l); return (v, v, v); }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        var hk = h / 360;
        return (ToByte(Hue(p, q, hk + 1 / 3.0)), ToByte(Hue(p, q, hk)), ToByte(Hue(p, q, hk - 1 / 3.0)));

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1 / 2.0) return q;
            if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
            return p;
        }
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    // Rewrites every #RGB / #RRGGBB colour in an SVG document through the shift.
    public string ApplyToSvg(string svg) => HexColour().Replace(svg, m =>
    {
        var hex = m.Groups[1].Value;
        if (hex.Length == 3) hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        var r = byte.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber);
        var g = byte.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber);
        var b = byte.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber);
        var (nr, ng, nb) = Apply(r, g, b);
        return nr == r && ng == g && nb == b ? m.Value : $"#{nr:X2}{ng:X2}{nb:X2}";
    });

    private static Regex? _hex;
    private static Regex HexColour() => _hex ??= new Regex("#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})(?![0-9A-Fa-f])", RegexOptions.CultureInvariant);
}

// Applies a look to the character pictures (the flat and soft 3D SVGs): Bloop's body colour and hat, Sprout's pot.
public static class CharacterSvg
{
    private static readonly Regex BodyEllipse = new(
        "<ellipse cx=\"0\" cy=\"0\" rx=\"(?<rx>[0-9.]+)\" ry=\"(?<ry>[0-9.]+)\" fill=\"(#[0-9A-Fa-f]{6}|url\\(#[A-Za-z0-9]+-body\\))\"\\s*/>",
        RegexOptions.CultureInvariant);

    public static bool IsBloop(string name) => name.StartsWith("bloop", StringComparison.OrdinalIgnoreCase);
    public static bool IsSprout(string name) => name.StartsWith("sprout", StringComparison.OrdinalIgnoreCase);

    public static string Apply(string svg, string name, CharacterLook look)
    {
        if (look.IsDefault) return svg;
        if (IsBloop(name))
        {
            if (look.BloopShift is { } shift) svg = shift.ApplyToSvg(svg);
            if (look.Hat != BloopHat.None) svg = AddHat(svg, look.Hat);
        }
        else if (IsSprout(name) && look.PotShift is { } pot)
        {
            svg = pot.ApplyToSvg(svg);
        }
        return svg;
    }

    // Puts the hat on top of Bloop's body ellipse, inside the same group so a tilted pose tilts the hat too, and
    // after everything else in that group so highlights on the body do not paint over it.
    public static string AddHat(string svg, BloopHat hat)
    {
        if (hat == BloopHat.None) return svg;
        var body = BodyEllipse.Match(svg);
        if (!body.Success) return svg;
        var rx = double.Parse(body.Groups["rx"].Value, CultureInfo.InvariantCulture);
        var ry = double.Parse(body.Groups["ry"].Value, CultureInfo.InvariantCulture);
        var scale = rx / 42.0;
        var markup = $"<g transform=\"translate(0 {F(-ry)}) scale({F(scale)})\">{HatMarkup(hat)}</g>";

        var bodyAt = body.Index;
        var open = svg.LastIndexOf("<g", bodyAt, StringComparison.Ordinal);
        var closedBefore = open >= 0 && svg.IndexOf("</g>", open, bodyAt - open, StringComparison.Ordinal) >= 0;
        int insertAt;
        if (open >= 0 && !closedBefore)
        {
            insertAt = svg.IndexOf("</g>", bodyAt, StringComparison.Ordinal);
            if (insertAt < 0) return svg;
        }
        else
        {
            insertAt = svg.LastIndexOf("</svg>", StringComparison.Ordinal);
            if (insertAt < 0) return svg;
        }
        return svg.Insert(insertAt, markup);
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // Hat drawings, with the origin at the top of Bloop's head and Bloop's idle body (42 x 36) as the unit.
    public static string HatMarkup(BloopHat hat)
    {
        var sb = new StringBuilder();
        switch (hat)
        {
            case BloopHat.Beanie:
                sb.Append("<path d=\"M-25 3 C-25 -25 25 -25 25 3 Z\" fill=\"#EE7F86\"/>");
                sb.Append("<path d=\"M-15 -6 C-13 -15 -6 -18 2 -18\" fill=\"none\" stroke=\"#FFFFFF\" stroke-opacity=\".35\" stroke-width=\"3\" stroke-linecap=\"round\"/>");
                sb.Append("<path d=\"M-9 2 L-9 -14 M0 2 L0 -18 M9 2 L9 -14\" fill=\"none\" stroke=\"#D9606A\" stroke-opacity=\".45\" stroke-width=\"1.6\" stroke-linecap=\"round\"/>");
                sb.Append("<path d=\"M-28 10 Q0 -3 28 10 L28 1 Q0 -12 -28 1 Z\" fill=\"#D95F68\"/>");
                sb.Append("<path d=\"M-24 2.5 Q0 -8 24 2.5\" fill=\"none\" stroke=\"#FFFFFF\" stroke-opacity=\".3\" stroke-width=\"1.4\" stroke-linecap=\"round\"/>");
                sb.Append("<circle cx=\"0\" cy=\"-21\" r=\"6.5\" fill=\"#FFF4E6\"/>");
                sb.Append("<circle cx=\"-2\" cy=\"-23\" r=\"2.2\" fill=\"#FFFFFF\" fill-opacity=\".8\"/>");
                break;
            case BloopHat.PartyHat:
                sb.Append("<g transform=\"rotate(10 0 4)\">");
                sb.Append("<path d=\"M-14 4 L0 -38 L14 4 Q0 9 -14 4 Z\" fill=\"#8C7CF0\"/>");
                sb.Append("<path d=\"M-9.5 -9 L9.5 -9 L7.6 -15 L-7.6 -15 Z M-5.3 -22 L5.3 -22 L3.4 -28 L-3.4 -28 Z\" fill=\"#FFD45C\"/>");
                sb.Append("<path d=\"M-6 -2 L-1 -30\" fill=\"none\" stroke=\"#FFFFFF\" stroke-opacity=\".35\" stroke-width=\"2\" stroke-linecap=\"round\"/>");
                sb.Append("<circle cx=\"0\" cy=\"-39\" r=\"4.5\" fill=\"#FF8FA8\"/>");
                sb.Append("<circle cx=\"-1.3\" cy=\"-40.5\" r=\"1.5\" fill=\"#FFFFFF\" fill-opacity=\".75\"/>");
                sb.Append("</g>");
                break;
            case BloopHat.LeafSprig:
                sb.Append("<path d=\"M0 3 Q1 -10 -1 -20\" fill=\"none\" stroke=\"#4E9A51\" stroke-width=\"3\" stroke-linecap=\"round\"/>");
                sb.Append("<path d=\"M-1 -14 Q-20 -30 -25 -15 Q-13 -7 -1 -14 Z\" fill=\"#6BBF59\"/>");
                sb.Append("<path d=\"M-3 -14.5 Q-14 -20 -22 -16\" fill=\"none\" stroke=\"#C4EDB4\" stroke-opacity=\".7\" stroke-width=\"1.2\" stroke-linecap=\"round\"/>");
                sb.Append("<path d=\"M-1 -19 Q18 -38 25 -22 Q12 -12 -1 -19 Z\" fill=\"#8AD17A\"/>");
                sb.Append("<path d=\"M1 -19.5 Q13 -27 22 -22.5\" fill=\"none\" stroke=\"#E4F9DA\" stroke-opacity=\".7\" stroke-width=\"1.2\" stroke-linecap=\"round\"/>");
                break;
        }
        return sb.ToString();
    }
}
