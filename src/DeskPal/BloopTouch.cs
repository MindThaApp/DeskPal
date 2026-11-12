using System.Diagnostics;
using System.Numerics;
using DeskPal.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace DeskPal;

// Something that shows Bloop and can react to a touch: the live 3D surface or a 2D picture.
internal interface IBloopReactor
{
    bool Ready { get; }

    // p is in the touch surface's DIPs.
    BodyRegion RegionAt(Vector2 p);

    // Where a region is on screen now, for synthetic (debug) gestures; null when unknown.
    Vector2? PointFor(BodyRegion region);

    void React(in GestureEvent e, BodyRegion region);
}

// Pointer handling for one Bloop surface: captures the pointer while pressed (so a release outside, or a drag, is
// still seen), feeds samples to the gesture classifier and hands each gesture, with the body region where the
// press started, to the current reactor. The same entry points take synthetic samples for --debug-gesture.
internal sealed class BloopTouch
{
    private readonly UIElement _surface;
    private readonly Func<IBloopReactor?> _reactor;
    private readonly Func<Vector2, bool>? _accept;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherQueueTimer _holdTimer;
    private GestureClassifier _classifier = new();
    private double _pixelsPerDip = 1;
    private BodyRegion _region;
    private IBloopReactor? _active;
    private uint? _pointerId;
    private double _lastUp = double.NegativeInfinity;
    private Vector2 _lastDown;

    // relativeTo: the element whose DIP space regions use (defaults to the surface). accept: optional filter on the
    // press point, for surfaces that also hold buttons.
    public BloopTouch(UIElement surface, Func<IBloopReactor?> reactor, UIElement? relativeTo = null,
        Func<Vector2, bool>? accept = null, bool handledEventsToo = false, bool attach = true)
    {
        _surface = surface;
        _reactor = reactor;
        _accept = accept;
        RelativeTo = relativeTo ?? surface;
        _holdTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _holdTimer.Interval = TimeSpan.FromMilliseconds(50);
        _holdTimer.IsRepeating = true;
        _holdTimer.Tick += (_, _) => TickGuard.Run("bloop-hold", () => Emit(_classifier.Tick(Now)));
        // attach: false leaves pointer events to the owner, which feeds samples itself (the floating Bloop window,
        // whose own press-hold-move rule decides when a press is a window drag instead of a gesture).
        if (!attach) return;
        surface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), handledEventsToo);
        surface.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), true);
        surface.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), true);
        surface.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnLost), true);
        surface.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnLost), true);
    }

    public UIElement RelativeTo { get; }

    private double Now => _clock.Elapsed.TotalSeconds;

    private Vector2 PointOf(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(RelativeTo).Position;
        return new Vector2((float)p.X, (float)p.Y);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e) => TickGuard.Run("bloop-press", () =>
    {
        if (_pointerId is not null) return;
        var p = PointOf(e);
        if (_accept is not null && !_accept(p)) return;
        var scale = RelativeTo.XamlRoot?.RasterizationScale ?? 1;
        if (Math.Abs(scale - _pixelsPerDip) > 0.01)
        {
            _pixelsPerDip = scale;
            _classifier = new GestureClassifier(scale);
        }
        if (!SampleDown(p)) return;
        _pointerId = e.Pointer.PointerId;
        _surface.CapturePointer(e.Pointer);
        e.Handled = true;
    });

    private void OnMoved(object sender, PointerRoutedEventArgs e) => TickGuard.Run("bloop-move", () =>
    {
        if (_pointerId != e.Pointer.PointerId) return;
        SampleMove(PointOf(e));
    });

    private void OnReleased(object sender, PointerRoutedEventArgs e) => TickGuard.Run("bloop-release", () =>
    {
        if (_pointerId != e.Pointer.PointerId) return;
        _pointerId = null;
        SampleUp(PointOf(e));
        _surface.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    });

    private void OnLost(object sender, PointerRoutedEventArgs e) => TickGuard.Run("bloop-lost", () =>
    {
        if (_pointerId != e.Pointer.PointerId) return;
        _pointerId = null;
        _holdTimer.Stop();
        Emit(_classifier.Cancel(Now));
    });

    // Synthetic and real samples share these. Returns false when no Bloop is under the press.
    public bool SampleDown(Vector2 p)
    {
        _active = _reactor();
        if (_active is not { Ready: true }) return false;
        var region = _active.RegionAt(p);
        // A quick follow-up tap on the same spot keeps its region even if that part has just moved away
        // (a raised hand, a spinning body), so a tap chain never drops a tap.
        if (region == BodyRegion.None && Now - _lastUp <= GestureClassifier.MultiTapGapSeconds
            && Vector2.Distance(p, _lastDown) <= 40) region = _region;
        if (region == BodyRegion.None) return false;
        _region = region;
        _lastDown = p;
        _classifier.Down(p, Now);
        _holdTimer.Start();
        return true;
    }

    public void SampleMove(Vector2 p)
    {
        if (!_classifier.IsDown) return;
        Emit(_classifier.Move(p, Now));
    }

    public void SampleCancel()
    {
        _holdTimer.Stop();
        Emit(_classifier.Cancel(Now));
    }

    public void SampleUp(Vector2 p)
    {
        _holdTimer.Stop();
        if (!_classifier.IsDown) return;
        _lastUp = Now;
        Emit(_classifier.Up(p, Now));
    }

    private void Emit(GestureEvent? e)
    {
        if (e is not { } g || _active is not { Ready: true } r) return;
        if (g.Kind is not (GestureKind.Holding or GestureKind.Drag or GestureKind.Rub))
            Logger.Log($"bloop gesture {g.Kind} x{g.Count} on {_region}");
        r.React(g, _region);
    }
}
