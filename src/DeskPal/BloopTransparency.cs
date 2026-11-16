using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.System.Power;
using Windows.UI.ViewManagement;

namespace DeskPal;

// Watches what decides whether the floating Bloop window can be see-through: Windows "Transparency effects" and
// Energy saver. When either is off the window falls back to a shape cut to Bloop's outline, so nothing but Bloop
// is ever drawn even if the clear background is not honoured. --debug-bloop-noeffects (with --debug-quickpanel)
// pretends effects are off so that fallback can be checked on any machine.
internal sealed class BloopTransparency : IDisposable
{
    public static readonly bool DebugNoEffects = Environment.GetCommandLineArgs().Skip(1)
        .Any(a => string.Equals(a, "--debug-bloop-noeffects", StringComparison.OrdinalIgnoreCase));

    private readonly UISettings _ui = new();
    private readonly DispatcherQueue _queue;
    private bool _disposed;

    public event Action? Changed;

    public BloopTransparency(DispatcherQueue queue)
    {
        _queue = queue;
        _ui.AdvancedEffectsEnabledChanged += OnChanged;
        try { PowerManager.EnergySaverStatusChanged += OnPowerChanged; } catch { }
    }

    public bool EffectsEnabled
    {
        get { try { return _ui.AdvancedEffectsEnabled; } catch { return true; } }
    }

    public static string EnergySaver
    {
        get { try { return PowerManager.EnergySaverStatus.ToString(); } catch { return "unknown"; } }
    }

    // True when the window should not rely on a clear background.
    public bool UseFallback => DebugNoEffects || !EffectsEnabled || EnergySaver == nameof(EnergySaverStatus.On);

    public string Describe() =>
        $"effects {(EffectsEnabled ? "on" : "off")}, energy saver {EnergySaver}, debug-noeffects {DebugNoEffects}, " +
        $"theme {ThemeName()}, gpu {GpuNames()}, os {Environment.OSVersion.Version}";

    private void OnChanged(UISettings sender, object args) => Raise();

    private void OnPowerChanged(object? sender, object e) => Raise();

    private void Raise()
    {
        if (_disposed) return;
        _queue.TryEnqueue(() => TickGuard.Run("bloop-transparency-changed", () => { if (!_disposed) Changed?.Invoke(); }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ui.AdvancedEffectsEnabledChanged -= OnChanged;
        try { PowerManager.EnergySaverStatusChanged -= OnPowerChanged; } catch { }
    }

    private string ThemeName()
    {
        try
        {
            var bg = _ui.GetColorValue(UIColorType.Background);
            return bg.R + bg.G + bg.B > 382 ? "light" : "dark";
        }
        catch { return "unknown"; }
    }

    // The display adapters, so a log shows which GPU (or hybrid pair) drew the window.
    private static string GpuNames()
    {
        try
        {
            using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
            var names = new List<string>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            {
                using (adapter)
                {
                    var d = adapter.Description1;
                    if ((d.Flags & Vortice.DXGI.AdapterFlags.Software) == 0) names.Add(d.Description.Trim());
                }
            }
            return names.Count > 0 ? string.Join(" + ", names) : "none";
        }
        catch (Exception ex) { return "unknown (" + ex.GetType().Name + ")"; }
    }

    // A window region covering the pixels of a BGRA image whose alpha is at least the threshold, placed at
    // (left, top). Built one run of pixels per row at a time. Returns IntPtr.Zero when nothing is opaque.
    public static IntPtr RegionFromAlpha(byte[] bgra, int width, int height, int left, int top, byte threshold = 128)
    {
        var region = IntPtr.Zero;
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            var x = 0;
            while (x < width)
            {
                while (x < width && bgra[row + x * 4 + 3] < threshold) x++;
                if (x >= width) break;
                var start = x;
                while (x < width && bgra[row + x * 4 + 3] >= threshold) x++;
                var run = CreateRectRgn(left + start, top + y, left + x, top + y + 1);
                if (region == IntPtr.Zero) region = run;
                else
                {
                    CombineRgn(region, region, run, RGN_OR);
                    DeleteObject(run);
                }
            }
        }
        return region;
    }

    private const int RGN_OR = 2;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
