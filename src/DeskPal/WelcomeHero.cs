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
    public const double Size = 224;

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

    public WelcomeHero(SettingsHub hub)
    {
        _hub = hub;
        Width = Size;
        _flat = new Bloop2D(_avatar, _stage, () => _style, 640);
        _touch = new BloopTouch(_stage, Reactor);
        _stage.Children.Add(_avatar);
        _stage.Children.Add(_panel);
        Children.Add(_stage);
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
        if (Reactor()?.PointFor(BodyRegion.Belly) is not { } p) return;
        if (_touch.SampleDown(p)) _touch.SampleUp(p);
    }

    public void Stop()
    {
        _closed = true;
        _viewport?.Hide();
    }
}
