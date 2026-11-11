using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace DeskPal.Render;

// One live surface: a composition swap chain in a SwapChainPanel. There is no free-running loop: frames are driven by
// the display (CompositionTarget.Rendering) only while a breath, clip, blink, spring or eye move is in progress, then the
// handler is detached (0 fps). All motion uses real elapsed time; springs use fixed 1/240 s sub-steps.
internal sealed class LiveViewport : IDisposable
{
    private static readonly TimeSpan Teardown = TimeSpan.FromMinutes(2);
    private const double AwakeSeconds = 10;
    private const double BlinkWakeGap = 8;
    private const double BlinkWakeWindow = 30;
    private const double FidgetWindow = 180;
    private const float SproutScale = 0.33f;
    private static readonly Vector3 SproutAt = new(0.09f, 0, -0.01f);
    private static readonly Guid SwapChainPanelNativeIid = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    public static int Presents;

    private readonly SwapChainPanel _panel;
    private readonly bool _home;
    private readonly double _centerX;
    private readonly Random _random = new();
    private bool _rendering;
    private double _lastPresent;
    private double _minInterval;
    private readonly List<double> _runIntervals = [];
    private readonly List<double> _runRenderMs = [];
    private readonly HashSet<string> _runClips = [];
    private double _runStart;
    private double _acc;
    private readonly DispatcherQueueTimer _teardownTimer;
    private readonly DispatcherQueueTimer _sleepTimer;
    private readonly Stopwatch _clock = new();
    private readonly Stopwatch _renderWatch = new();
    private Stage3D? _stage;
    private BloopAnimator? _anim;
    private Pose? _sproutPose;
    private Matrix4x4[]? _sproutWorld;
    private readonly List<Action<BloopAnimator>> _pending = [];
    private IDXGISwapChain1? _swap;
    private ID3D11Texture2D? _msaa;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11Texture2D? _depth;
    private ID3D11DepthStencilView? _dsv;
    private uint _width, _height;
    private bool _visible;
    private bool _firstPending;
    private bool _failed;
    private long _showVersion;
    private bool _battery;

    private double _lastTick;
    private double _awakeUntil;
    private double _envelope;
    private double _lastInteraction;
    private double _nextBlink;
    private double _lastBlinkWake = double.NegativeInfinity;
    private double _nextFidget;
    private double _sproutPulse = -1;
    private double _squash, _squashVel;
    private Vector2 _eye, _eyeVel, _eyeTarget;

    public LiveViewport(SwapChainPanel panel, bool home = false, double centerX = double.NaN)
    {
        _panel = panel;
        _home = home;
        _centerX = double.IsNaN(centerX) ? panel.Width / 2 : centerX;
        var dq = DispatcherQueue.GetForCurrentThread();
        _teardownTimer = dq.CreateTimer();
        _teardownTimer.IsRepeating = false;
        _teardownTimer.Interval = Teardown;
        _teardownTimer.Tick += (_, _) => TickGuard.Run("live-3d-teardown", () => Guarded("teardown", ReleaseGpu));
        _sleepTimer = dq.CreateTimer();
        _sleepTimer.IsRepeating = false;
        _sleepTimer.Tick += (_, _) => TickGuard.Run("live-3d-sleep", () => Guarded("sleep", OnSleepTimer));
    }

    public event Action? FirstFrame;
    public event Action? Failed;

    // Premultiplied colour the surface clears to. Transparent everywhere except where the swap chain hides a
    // translucent layer behind it (see HomePage).
    public Color4 Backdrop { get; set; } = new(0, 0, 0, 0);

    public bool HasSprout => _home && _stage?.Sprout is not null;

    public int SproutStage { get; set; } = 2;

    public async void Show(double scale)
    {
        var version = ++_showVersion;
        _visible = true;
        _teardownTimer.Stop();
        if (_failed || Stage3D.Failed)
        {
            Failed?.Invoke();
            return;
        }
        try
        {
            if (_stage is null)
            {
                var stage = await Stage3D.AcquireAsync();
                if (_stage is null) _stage = stage;
                else Stage3D.Release();
            }
            if (version != _showVersion) return;
            if (!_visible)
            {
                _teardownTimer.Start();
                return;
            }
            EnsureSwapChain(scale);
            if (_anim?.Model != _stage.Bloop.Model)
            {
                _anim = new BloopAnimator(_stage.Bloop.Model);
                _anim.ClipStarted += name =>
                {
                    _runClips.Add(name);
                    Logger.Log($"3d clip {name}{(_home ? " (home)" : " (card)")}");
                };
            }
            if (_stage.Sprout is { } sprout && _sproutPose?.W.Length != sprout.Model.Nodes.Length) _sproutPose = new Pose(sprout.Model);
            _anim.Reset();
            _clock.Restart();
            _lastTick = 0;
            _awakeUntil = AwakeSeconds;
            _lastInteraction = 0;
            _lastBlinkWake = double.NegativeInfinity;
            _nextBlink = NextBlinkGap();
            _nextFidget = NextFidgetGap();
            _envelope = 0;
            _sproutPulse = -1;
            _squash = _squashVel = 0;
            _eye = _eyeTarget = _eyeVel = Vector2.Zero;
            foreach (var request in _pending) request(_anim);
            _pending.Clear();
            _firstPending = true;
            RenderFrame();
            Kick();
        }
        catch (Exception ex)
        {
            Fail("show", ex);
        }
    }

    public void Hide()
    {
        _visible = false;
        _pending.Clear();
        _sleepTimer.Stop();
        StopLoop();
        if (_stage is not null) _teardownTimer.Start();
    }

    public void Poke(bool withClip)
    {
        if (!Live) return;
        // An impulse on top of the running wobble, never a reset.
        _squashVel = Math.Max(_squashVel - 12, -24);
        Logger.Log($"3d poke (loop {(_rendering ? "running" : "asleep")})");
        if (withClip && _anim is { } a && a.CurrentClip is null or "poke_squish") a.Play("poke_squish");
        Wake();
    }

    public void Play(string clip, float weight = 1) => Request(a => a.Play(clip, weight));

    public void PlaySequence(params string[] clips) => Request(a =>
    {
        a.Play(clips[0]);
        foreach (var c in clips.Skip(1)) a.Enqueue(c);
    });

    // Plays the clip (after anything queued), then again after each gap until StopRepeat.
    public void Repeat(string clip, float gap) => Request(a => a.Repeat(clip, gap));

    public void Loop(string clip) => Request(a => a.Enqueue(clip, loop: true));

    public void StopRepeat() => Request(a => a.StopRepeat());

    public void SproutHappy()
    {
        if (!Live || !HasSprout) return;
        _sproutPulse = 0;
        Logger.Log("3d sprout happy");
        Wake();
    }

    public void LookAt(Vector2 normalized)
    {
        if (!Live) return;
        _eyeTarget = Vector2.Clamp(normalized, -Vector2.One, Vector2.One);
        Wake();
    }

    public void LookAway()
    {
        if (!Live) return;
        _eyeTarget = Vector2.Zero;
        Kick();
    }

    private void Request(Action<BloopAnimator> request)
    {
        if (!_visible || _failed) return;
        if (Live && _anim is not null)
        {
            request(_anim);
            Wake();
        }
        else _pending.Add(request);
    }

    private bool Live => _visible && !_failed && _swap is not null;

    private double NextBlinkGap() => Now + 3 + 3 * _random.NextDouble();

    private double NextFidgetGap() => Now + 20 + 20 * _random.NextDouble();

    private void Wake()
    {
        _awakeUntil = Now + AwakeSeconds;
        _lastInteraction = Now;
        _nextFidget = NextFidgetGap();
        Kick();
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    private void Kick()
    {
        if (!Live || _rendering) return;
        _sleepTimer.Stop();
        _rendering = true;
        _lastTick = _lastPresent = _runStart = Now;
        _acc = 0;
        _runIntervals.Clear();
        _runRenderMs.Clear();
        _runClips.Clear();
        if (_anim?.CurrentClip is { } clip) _runClips.Add(clip);
        _battery = OnBattery();
        _minInterval = _battery ? 1.0 / 30 : 0;
        CompositionTarget.Rendering += OnRendering;
        Logger.Log("3d loop wake");
    }

    private void StopLoop()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    // Asleep (0 fps): a timer brings back a rare blink (at most one per 8 s, none 30 s after the last interaction)
    // and, on Home, a small fidget every 20 to 40 s for a few minutes after the last interaction.
    private void ScheduleSleepWake()
    {
        if (!Live) return;
        var now = Now;
        var at = double.PositiveInfinity;
        if (now - _lastInteraction < BlinkWakeWindow)
        {
            var blinkAt = Math.Max(_nextBlink, _lastBlinkWake + BlinkWakeGap);
            if (blinkAt - _lastInteraction < BlinkWakeWindow) at = blinkAt;
        }
        if (_home && _nextFidget - _lastInteraction < FidgetWindow) at = Math.Min(at, _nextFidget);
        if (double.IsPositiveInfinity(at)) return;
        _sleepTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.05, at - now));
        _sleepTimer.Start();
    }

    private void OnSleepTimer()
    {
        if (!Live || _rendering || _anim is null) return;
        var now = Now;
        if (_home && now >= _nextFidget - 0.01 && _nextFidget - _lastInteraction < FidgetWindow)
        {
            Fidget(now);
            return;
        }
        if (now >= _nextBlink - 0.01 && now - _lastInteraction < BlinkWakeWindow && now - _lastBlinkWake >= BlinkWakeGap - 0.01)
        {
            _lastBlinkWake = now;
            _nextBlink = NextBlinkGap();
            _anim.Blink();
            Kick();
            return;
        }
        ScheduleSleepWake();
    }

    private void Fidget(double now)
    {
        _nextFidget = NextFidgetGap();
        if (_anim is null || _anim.ActionActive) return;
        if (_random.Next(2) == 0) _anim.Play("wave", 0.8f);
        else _anim.Play("shoulder_shrug", 0.5f);
        _awakeUntil = Math.Max(_awakeUntil, now + 3);
        Kick();
    }

    private void OnRendering(object? sender, object e) => TickGuard.Run("live-3d-frame", () => Guarded("frame", Step));

    private static bool OnBattery() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    private void LogRun()
    {
        if (_runIntervals.Count == 0) return;
        var sorted = _runIntervals.OrderBy(x => x).ToArray();
        var p95 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.95))];
        var cpu = _runRenderMs.OrderBy(x => x).ToArray();
        var cpu95 = cpu[Math.Min(cpu.Length - 1, (int)(cpu.Length * 0.95))];
        var dur = Now - _runStart;
        var clips = _runClips.Count == 0 ? "idle" : string.Join(",", _runClips);
        Logger.Log($"3d loop idle: {_runIntervals.Count + 1} presents in {dur:F2} s ({(_runIntervals.Count + 1) / dur:F1}/s), interval ms mean {_runIntervals.Average():F1} p95 {p95:F1} max {sorted[^1]:F1}; render cpu ms mean {cpu.Average():F2} p95 {cpu95:F2}; clips {clips}");
    }

    private void Guarded(string where, Action body)
    {
        try { body(); }
        catch (Exception ex) { Fail(where, ex); }
    }

    private void Fail(string where, Exception ex)
    {
        _failed = true;
        StopLoop();
        _teardownTimer.Stop();
        _sleepTimer.Stop();
        Stage3D.MarkFailed(where, ex);
        try { ReleaseGpu(); } catch { }
        Failed?.Invoke();
    }

    private void Step()
    {
        if (!Live || _anim is null) { StopLoop(); return; }
        var now = Now;
        if (now - _lastPresent < _minInterval - 0.002) return;
        var dt = Math.Min(now - _lastTick, 0.1);
        _lastTick = now;

        var awake = now < _awakeUntil ? 1.0 : 0.0;
        _envelope += Math.Clamp(awake - _envelope, -dt, dt);

        const double h = 1.0 / 240;
        _acc += dt;
        while (_acc >= h)
        {
            _acc -= h;
            // Poke: about 7 Hz, damping ratio 0.3.
            _squashVel += (-1935 * _squash - 26.4 * _squashVel) * h;
            _squash += _squashVel * h;
            // Body turn: critically damped approach to the pointer goal (time constant about 0.1 s), speed capped.
            var accel = 400f * (_eyeTarget - _eye) - 40f * _eyeVel;
            _eyeVel += accel * (float)h;
            var speed = _eyeVel.Length();
            if (speed > 9f) _eyeVel *= 9f / speed;
            _eye += _eyeVel * (float)h;
        }

        var springs = Math.Abs(_squash) > 1e-4 || Math.Abs(_squashVel) > 1e-3
            || Vector2.Distance(_eye, _eyeTarget) > 1e-3 || _eyeVel.Length() > 1e-2;
        if (!springs)
        {
            _squash = _squashVel = 0;
            _eye = _eyeTarget;
            _eyeVel = Vector2.Zero;
        }

        if (now >= _nextBlink)
        {
            _nextBlink = NextBlinkGap();
            _anim.Blink();
        }
        if (_home && now >= _nextFidget && _nextFidget - _lastInteraction < FidgetWindow) Fidget(now);

        _anim.Advance((float)dt, (float)_envelope);
        if (_sproutPulse >= 0)
        {
            _sproutPulse += dt;
            if (_sproutPulse > SproutPulseSeconds) _sproutPulse = -1;
        }

        if (now - _lastPresent < 0.25) _runIntervals.Add((now - _lastPresent) * 1000);
        _lastPresent = now;
        _renderWatch.Restart();
        RenderFrame();
        _runRenderMs.Add(_renderWatch.Elapsed.TotalMilliseconds);

        var busy = springs || _anim.ActionActive || _anim.Blinking || _sproutPulse >= 0;
        if (!busy && _envelope <= 0)
        {
            LogRun();
            StopLoop();
            ScheduleSleepWake();
            return;
        }
        // Breathing alone is slow; clips, blinks, springs and turns run at the display rate (30 on battery).
        var floor = _battery ? 1.0 / 30 : 0;
        _minInterval = busy ? floor : 1.0 / 20;
    }

    private const double SproutPulseSeconds = 0.8;

    private void RenderFrame()
    {
        if (_stage is not { } s || _anim is null || _swap is null || _rtv is null || _dsv is null || _msaa is null) return;
        if (AppData.Debug3DFail) throw new COMException("forced device removal (--debug-3d-fail)", unchecked((int)0x887A0005));

        var ctx = s.Context;
        var model = s.Bloop.Model;
        var pose = _anim.Evaluate();

        var body = model.NodeIndex("body");
        if (body >= 0)
        {
            var sy = (float)(1 + Math.Max(_squash, -0.4));
            var sxz = 1 / MathF.Sqrt(sy);
            pose.S[body] *= new Vector3(sxz, sy, sxz);
            var turn = Quaternion.CreateFromYawPitchRoll(0.35f * _eye.X, 0.15f * _eye.Y, 0);
            pose.R[body] = Quaternion.Concatenate(pose.R[body], turn);
        }

        Span<Matrix4x4> world = stackalloc Matrix4x4[model.Nodes.Length];
        model.ComputeWorld(pose, world);
        var skin = new SkinConstants();
        Span<Matrix4x4> joints = skin.Joints;
        model.ComputeJoints(world, joints);

        var camPos = new Vector3(0, 0.065f, 0.29f);
        var view = Matrix4x4.CreateLookAt(camPos, new Vector3(0, 0.042f, 0), Vector3.UnitY);
        const float near = 0.01f;
        var top = near * MathF.Tan(15f * MathF.PI / 180);
        var unit = 2 * top / _height;
        var cx = (float)(_centerX / _panel.Width * _width);
        var proj = Matrix4x4.CreatePerspectiveOffCenter(-cx * unit, (_width - cx) * unit, -top, top, near, 1f);
        var frame = new FrameConstants
        {
            ViewProj = view * proj,
            LightDir = Vector3.Normalize(new Vector3(-0.55f, 0.8f, 0.55f)),
            CameraPos = camPos,
            KeyColor = new Vector3(1.0f, 0.97f, 0.92f),
        };
        ctx.UpdateSubresource(in frame, s.FrameCb);

        ctx.ClearRenderTargetView(_rtv, Backdrop);
        ctx.ClearDepthStencilView(_dsv, DepthStencilClearFlags.Depth, 1, 0);
        ctx.OMSetRenderTargets(_rtv, _dsv);
        ctx.RSSetViewport(0, 0, _width, _height);
        ctx.RSSetState(s.Raster);
        ctx.OMSetBlendState(s.Blend);
        ctx.VSSetConstantBuffer(0, s.FrameCb);
        ctx.PSSetConstantBuffer(0, s.FrameCb);

        var sprout = HasSprout ? s.Sprout : null;
        ctx.OMSetDepthStencilState(s.DepthOff);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.IASetInputLayout(s.ShadowLayout);
        ctx.IASetVertexBuffer(0, s.ShadowQuad, 20);
        ctx.VSSetShader(s.ShadowVs);
        ctx.PSSetShader(s.ShadowPs);
        ctx.VSSetConstantBuffer(3, s.ShadowCb);
        ctx.PSSetConstantBuffer(3, s.ShadowCb);
        DrawShadow(new Vector4(0, 0, 0.062f, 0.042f), 0.42f);
        if (sprout is not null) DrawShadow(new Vector4(SproutAt.X, SproutAt.Z, 0.022f, 0.016f), 0.32f);

        ctx.OMSetDepthStencilState(s.DepthOn);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetInputLayout(s.MeshLayout);
        ctx.VSSetShader(s.MeshVs);
        ctx.PSSetShader(s.MeshPs);
        ctx.VSSetConstantBuffer(1, s.DrawCb);
        ctx.PSSetConstantBuffer(1, s.DrawCb);
        ctx.VSSetConstantBuffer(2, s.SkinCb);
        ctx.PSSetSampler(0, s.Sampler);

        ctx.UpdateSubresource(in skin, s.SkinCb);
        DrawModel(s.Bloop, pose, null);

        if (sprout is not null && _sproutPose is { } sp)
        {
            var stageName = $"stage_{Math.Clamp(SproutStage, 1, 4)}_";
            var lift = _envelope * 0.12 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * Now / 4.5));
            if (_sproutPulse >= 0) lift += 0.9 * Math.Sin(Math.PI * _sproutPulse / SproutPulseSeconds);
            var m = sprout.Model;
            for (var i = 0; i < m.Nodes.Length; i++)
            {
                var names = m.Nodes[i].TargetNames;
                var at = Array.IndexOf(names, "leaf_lift");
                if (at >= 0 && at < sp.W[i].Length) sp.W[i][at] = (float)Math.Clamp(lift, 0, 1);
            }
            var sproutWorld = _sproutWorld ??= new Matrix4x4[m.Nodes.Length];
            m.ComputeWorld(sp, sproutWorld);
            var place = Matrix4x4.CreateScale(SproutScale) * Matrix4x4.CreateTranslation(SproutAt);
            DrawModel(sprout, sp, part =>
            {
                var name = m.Nodes[part.Node].Name;
                if (name.StartsWith("stage_", StringComparison.Ordinal) && !name.StartsWith(stageName, StringComparison.Ordinal)) return false;
                var placed = new SkinConstants();
                placed.Joints[0] = sproutWorld[part.Node] * place;
                ctx.UpdateSubresource(in placed, s.SkinCb);
                return true;
            });
        }

        using (var back = _swap.GetBuffer<ID3D11Texture2D>(0))
        {
            if (s.SampleCount > 1) ctx.ResolveSubresource(back, 0, _msaa, 0, Format.B8G8R8A8_UNorm);
            else ctx.CopyResource(back, _msaa);
        }
        _swap.Present(1, PresentFlags.None).CheckError();
        Interlocked.Increment(ref Presents);

        if (_firstPending)
        {
            _firstPending = false;
            FirstFrame?.Invoke();
        }

        void DrawShadow(Vector4 placement, float strength)
        {
            var c = new ShadowConstants { Placement = placement, Strength = new Vector4(strength, 0, 0, 0) };
            ctx.UpdateSubresource(in c, s.ShadowCb);
            ctx.Draw(4, 0);
        }

        void DrawModel(GpuModel gm, Pose p, Func<ModelPart, bool>? prepare)
        {
            if (gm.TextureView is not null) ctx.PSSetShaderResource(0, gm.TextureView);
            if (gm.MorphView is not null) ctx.VSSetShaderResource(1, gm.MorphView);
            var draw = new DrawConstants();
            foreach (var part in gm.Parts)
            {
                var mp = part.Part;
                if (prepare is not null && !prepare(mp)) continue;
                draw.BaseColor = mp.BaseColor;
                draw.Roughness = Math.Clamp(mp.Roughness, 0.05f, 1f);
                draw.UseTexture = mp.Textured && gm.TextureView is not null ? 1 : 0;
                draw.Unlit = mp.Kind == PartKind.Glint ? 1 : 0;
                draw.MorphBase = part.MorphBase;
                draw.MorphCount = (uint)mp.MorphCount;
                draw.VertexCount = (uint)mp.Vertices.Length;
                Span<Vector4> weights = draw.MorphWeights;
                weights.Clear();
                var w = p.W[mp.Node];
                for (var k = 0; k < mp.MorphCount && k < w.Length; k++) weights[k >> 2][k & 3] = w[k];
                ctx.UpdateSubresource(in draw, s.DrawCb);
                ctx.IASetVertexBuffer(0, part.Vertices, (uint)Marshal.SizeOf<MeshVertex>());
                ctx.IASetIndexBuffer(part.Indices, Format.R32_UInt, 0);
                ctx.DrawIndexed((uint)mp.Indices.Length, 0, 0);
            }
        }
    }

    private void EnsureSwapChain(double scale)
    {
        var s = _stage!;
        var w = (uint)Math.Max(1, Math.Round(_panel.Width * scale));
        var h = (uint)Math.Max(1, Math.Round(_panel.Height * scale));
        if (_swap is not null && w == _width && h == _height) return;
        ReleaseTargets();
        _width = w;
        _height = h;

        var desc = new SwapChainDescription1
        {
            Width = w,
            Height = h,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
            Scaling = Scaling.Stretch,
        };
        _swap = s.Factory.CreateSwapChainForComposition(s.Device, desc);
        using (var swap2 = _swap.QueryInterface<IDXGISwapChain2>())
            swap2.MatrixTransform = Matrix3x2.CreateScale((float)(1 / scale));
        SetPanelSwapChain(_swap.NativePointer);

        _msaa = s.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, w, h, 1, 1, BindFlags.RenderTarget,
            ResourceUsage.Default, CpuAccessFlags.None, s.SampleCount, 0));
        _rtv = s.Device.CreateRenderTargetView(_msaa);
        _depth = s.Device.CreateTexture2D(new Texture2DDescription(Format.D24_UNorm_S8_UInt, w, h, 1, 1, BindFlags.DepthStencil,
            ResourceUsage.Default, CpuAccessFlags.None, s.SampleCount, 0));
        _dsv = s.Device.CreateDepthStencilView(_depth);
    }

    private unsafe void SetPanelSwapChain(IntPtr swapChain)
    {
        var unknown = ((WinRT.IWinRTObject)_panel).NativeObject.ThisPtr;
        var iid = SwapChainPanelNativeIid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var native));
        try
        {
            // ISwapChainPanelNative::SetSwapChain is the first method after IUnknown.
            var setSwapChain = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(void***)native)[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
        }
        finally
        {
            Marshal.Release(native);
        }
    }

    private void ReleaseTargets()
    {
        if (_swap is not null)
        {
            try { SetPanelSwapChain(IntPtr.Zero); } catch { }
        }
        _dsv?.Dispose(); _dsv = null;
        _depth?.Dispose(); _depth = null;
        _rtv?.Dispose(); _rtv = null;
        _msaa?.Dispose(); _msaa = null;
        _swap?.Dispose(); _swap = null;
    }

    private void ReleaseGpu()
    {
        if (_visible && !_failed) return;
        ReleaseTargets();
        _anim = null;
        _sproutPose = null;
        _sproutWorld = null;
        if (_stage is not null)
        {
            _stage = null;
            Stage3D.Release();
        }
    }

    public void Dispose()
    {
        _visible = false;
        StopLoop();
        _teardownTimer.Stop();
        _sleepTimer.Stop();
        ReleaseGpu();
    }
}
