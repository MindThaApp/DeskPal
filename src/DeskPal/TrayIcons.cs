using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using SkiaSharp;
using Svg.Skia;

namespace DeskPal;

public sealed record LoopInfo(int Frames, int FrameMs, int[]? HoldMs)
{
    public int MsFor(int frame) => HoldMs is { } h && frame < h.Length ? h[frame] : FrameMs;
}

// Renders the tray SVG frames to icons once per loop and keeps them for the app's lifetime.
public sealed class TrayIcons : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private const int Size = 32;
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Tray");
    private readonly Dictionary<string, LoopInfo> _loops = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Icon[]> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Icon Icon, IntPtr Handle)> _owned = [];

    public TrayIcons()
    {
        try
        {
            var json = File.ReadAllText(Path.Combine(_dir, "manifest.json"));
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Manifest>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is not null)
                foreach (var (name, m) in parsed)
                    _loops[name] = new LoopInfo(m.Frames, m.FrameMs, m.HoldMs);
        }
        catch (Exception ex) { Logger.Log("tray manifest failed: " + ex.Message); }
    }

    private sealed record Manifest(int Frames, int FrameMs, int[]? HoldMs);

    public IEnumerable<string> LoopNames => _loops.Keys;

    public LoopInfo? Info(string loop) => _loops.TryGetValue(loop, out var i) ? i : null;

    public Icon[]? Get(string loop)
    {
        if (_icons.TryGetValue(loop, out var cached)) return cached;
        if (!_loops.TryGetValue(loop, out var info)) return null;
        var list = new List<Icon>();
        for (var n = 1; n <= info.Frames; n++)
        {
            var icon = Render(Path.Combine(_dir, $"{loop}-{n}.svg"));
            if (icon is null) return null;
            list.Add(icon);
        }
        return _icons[loop] = list.ToArray();
    }

    private Icon? Render(string path)
    {
        try
        {
            using var svg = new SKSvg();
            if (svg.Load(path) is not { } picture) return null;
            var cull = picture.CullRect;
            using var skBmp = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            using (var canvas = new SKCanvas(skBmp))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Scale(Size / cull.Width, Size / cull.Height);
                canvas.DrawPicture(picture);
            }
            using var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, Size, Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { Marshal.Copy(skBmp.Bytes, 0, data.Scan0, Size * Size * 4); }
            finally { bmp.UnlockBits(data); }
            var handle = bmp.GetHicon();
            var icon = Icon.FromHandle(handle);
            _owned.Add((icon, handle));
            return icon;
        }
        catch (Exception ex)
        {
            Logger.Log("tray frame render failed: " + ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var (icon, handle) in _owned)
        {
            icon.Dispose();
            DestroyIcon(handle);
        }
        _owned.Clear();
        _icons.Clear();
    }
}
