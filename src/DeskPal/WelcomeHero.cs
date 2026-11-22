using System.Numerics;
using DeskPal.Core;
using DeskPal.Render;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace DeskPal;

// Bloop in the welcome window: the same flat, soft or live 3D Bloop as on Home, in the user's picture style. It waves when
// the greeting shows and reacts to a tap, a click or the "Say hi" button through the same touch path as Home.
internal sealed class WelcomeHero : Grid
{
    public const double Size = 280;

    private readonly SettingsHub _hub;
    private readonly Grid _stage = new() { Width = Size, Height = Size, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Image _avatar = new();
    private readonly SwapChainPanel _panel = new() { Width = Size, Height = Size, Opacity = 0, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Bloop2D _flat;
    private readonly BloopTouch _touch;
    private LiveViewport? _viewport;
    private bool _live;
    private string _style = "";
    private bool _closed;
    private bool _wavePending = true;
    private readonly Border _bubble;
    private readonly TextBlock _bubbleText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 150 };
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _bubbleTimer;

    public WelcomeHero(SettingsHub hub)
    {
        _hub = hub;
        Width = Size;
        HorizontalAlignment = HorizontalAlignment.Center;
        _bubble = BuildBubble();
        _flat = new Bloop2D(_avatar, _stage, () => _style, 640);
        _touch = new BloopTouch(_stage, Reactor);
        _stage.Children.Add(_avatar);
        _stage.Children.Add(_panel);
        Children.Add(_stage);
        Children.Add(_bubble);
        // A tap or click on Bloop gets the same little greeting as the button.
        _stage.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => Say()), true);
        AutomationProperties.SetName(_stage, Loc.Get("Wizard_Hero_Name"));
        Loaded += (_, _) => _ = StartAsync();
    }

    private IBloopReactor? Reactor() =>
        _live && _panel.Visibility == Visibility.Visible && _viewport is { Ready: true } v ? v : _flat;

    private async Task StartAsync()
    {
        var style = AppData.Debug3D ? "3d" : _hub.Settings.AvatarStyle;
        _style = style;
        _live = style == "3d" && !Stage3D.Failed;
        _panel.Visibility = _live ? Visibility.Visible : Visibility.Collapsed;
        // The flat picture always loads first so the stage is never empty; the live model fades in over it.
        var bloop = await NudgeCardWindow.LoadAvatarAsync(style == "3d" ? "3d" : style, "bloop-idle", 640);
        if (_closed) return;
        _avatar.Source = bloop;
        _flat.ResetPictures();
        if (_live) StartLive();
        else if (_wavePending) { _wavePending = false; _flat.Wave(); }
    }

    private void StartLive()
    {
        _viewport = new LiveViewport(_panel);
        _viewport.FirstFrame += () =>
        {
            var sb = new Storyboard();
            Fade(_panel, 0, 1);
            Fade(_avatar, 1, 0);
            sb.Begin();
            if (_wavePending) { _wavePending = false; _viewport?.PlaySequence("wave"); }

            void Fade(UIElement target, double from, double to)
            {
                var a = new DoubleAnimation { From = from, To = to, Duration = new Duration(Motion.Ms(150)) };
                Storyboard.SetTarget(a, target);
                Storyboard.SetTargetProperty(a, "Opacity");
                sb.Children.Add(a);
            }
        };
        _viewport.Failed += () =>
        {
            _live = false;
            _panel.Visibility = Visibility.Collapsed;
            _avatar.Opacity = 1;
        };
        _viewport.Show(XamlRoot?.RasterizationScale ?? 1.0);
    }

    // A speech bubble beside Bloop's head for a moment. It is part of the picture (not the live region) so it never
    // depends on the 3D surface; it fades with the chrome motion setting and just appears when motion is off.
    private Border BuildBubble()
    {
        var b = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' CornerRadius='8' Padding='10,6' BorderThickness='1' Opacity='0' IsHitTestVisible='False' HorizontalAlignment='Right' VerticalAlignment='Top' Margin='0,8,0,0' Background='{ThemeResource SolidBackgroundFillColorSecondaryBrush}' BorderBrush='{ThemeResource ControlStrongStrokeColorDefaultBrush}' />");
        b.Child = _bubbleText;
        AutomationProperties.SetAccessibilityView(b, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return b;
    }

    // The window's top layer. The bubble lives there, above the live 3D surface, which would otherwise draw over it.
    public Grid? Overlay { get; set; }

    public void Say()
    {
        if (Overlay is { } top)
        {
            if (_bubble.Parent != top)
            {
                Children.Remove(_bubble);
                _bubble.HorizontalAlignment = HorizontalAlignment.Left;
                top.Children.Add(_bubble);
                Grid.SetRowSpan(_bubble, 2);
                Grid.SetColumnSpan(_bubble, 2);
            }
            var at = _stage.TransformToVisual(top).TransformPoint(new Windows.Foundation.Point(0, 0));
            _bubble.Margin = new Thickness(at.X + Size * 0.5, at.Y + Size * 0.12, 0, 0);
        }
        _bubbleText.Text = Loc.Get("Wizard_Hero_Hi");
        Fade(1, 120);
        _bubbleTimer ??= DispatcherQueue.CreateTimer();
        _bubbleTimer.Stop();
        _bubbleTimer.IsRepeating = false;
        _bubbleTimer.Interval = TimeSpan.FromMilliseconds(AppData.DebugPoke ? 9000 : 1800);
        _bubbleTimer.Tick -= HideBubble;
        _bubbleTimer.Tick += HideBubble;
        _bubbleTimer.Start();
    }

    private void HideBubble(Microsoft.UI.Dispatching.DispatcherQueueTimer t, object e) { t.Stop(); Fade(0, 167); }

    private void Fade(double to, int ms)
    {
        var a = new DoubleAnimation { To = to, Duration = new Duration(Motion.Ms(ms)) };
        Storyboard.SetTarget(a, _bubble);
        Storyboard.SetTargetProperty(a, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(a);
        sb.Begin();
        if (!Motion.Enabled) _bubble.Opacity = to;
    }

    // The greeting wave, again.
    public void Wave()
    {
        if (_live && _viewport is { Ready: true } v) v.PlaySequence("wave");
        else if (!_live && _avatar.Source is not null) _flat.Wave();
        else _wavePending = true;
    }

    // Same path as a real tap on the belly.
    public void Poke()
    {
        Say();
        if (Reactor()?.PointFor(BodyRegion.Belly) is not { } p) return;
        if (_touch.SampleDown(p)) _touch.SampleUp(p);
    }

    public void Stop()
    {
        _bubbleTimer?.Stop();
        _closed = true;
        _viewport?.Hide();
    }
}
