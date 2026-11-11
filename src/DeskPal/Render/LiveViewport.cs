using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace DeskPal.Render;

// One live surface: a composition swap chain in a SwapChainPanel. There is no free-running loop: the frame
// timer runs only while a breath, glance, spring or eye move is in progress, then stops (0 fps).
internal sealed class LiveViewport : IDisposable
{
    private static readonly TimeSpan Teardown = TimeSpan.FromMinutes(2);
    private const double AwakeSeconds = 10;
    private static readonly Guid SwapChainPanelNativeIid = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    public static int Presents;

    private readonly SwapChainPanel _panel;
    private readonly DispatcherQueueTimer _frameTimer;
    private readonly DispatcherQueueTimer _teardownTimer;
    private readonly Stopwatch _clock = new();
    private Stage3D? _stage;
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

    private double _lastTick;
    private double _awakeUntil;
    private double _envelope;
    private double _squash, _squashVel;
    private double _arm, _armVel;
    private Vector2 _eye, _eyeTarget;

    public LiveViewport(SwapChainPanel panel)
    {
        _panel = panel;
        var dq = DispatcherQueue.GetForCurrentThread();
        _frameTimer = dq.CreateTimer();
        _frameTimer.Tick += (_, _) => TickGuard.Run("live-3d-frame", () => Guarded("frame", Step));
        _teardownTimer = dq.CreateTimer();
        _teardownTimer.IsRepeating = false;
        _teardownTimer.Interval = Teardown;
        _teardownTimer.Tick += (_, _) => TickGuard.Run("live-3d-teardown", () => Guarded("teardown", ReleaseGpu));
    }

    public event Action? FirstFrame;
    public event Action? Failed;

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
            _stage ??= await Stage3D.AcquireAsync();
            if (!_visible || version != _showVersion) return;
            EnsureSwapChain(scale);
            _clock.Restart();
            _lastTick = 0;
            _awakeUntil = AwakeSeconds;
            _envelope = 0;
            _eye = _eyeTarget = Vector2.Zero;
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
        _frameTimer.Stop();
        if (_stage is not null) _teardownTimer.Start();
    }

    public void Poke()
    {
        if (!Live) return;
        _squashVel -= 2.2;
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

    private bool Live => _visible && !_failed && _swap is not null;

    private void Wake()
    {
        _awakeUntil = Now + AwakeSeconds;
        Kick();
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    private void Kick()
    {
        if (!Live || _frameTimer.IsRunning) return;
        _lastTick = Now;
        _frameTimer.Interval = TimeSpan.FromMilliseconds(33);
        _frameTimer.Start();
    }

    private void Guarded(string where, Action body)
    {
        try { body(); }
        catch (Exception ex) { Fail(where, ex); }
    }

    private void Fail(string where, Exception ex)
    {
        _failed = true;
        _frameTimer.Stop();
        _teardownTimer.Stop();
        Stage3D.MarkFailed(where, ex);
        try { ReleaseGpu(); } catch { }
        Failed?.Invoke();
    }

    private void Step()
    {
        if (!Live) { _frameTimer.Stop(); return; }
        var now = Now;
        var dt = Math.Min(now - _lastTick, 0.1);
        _lastTick = now;

        var awake = now < _awakeUntil ? 1.0 : 0.0;
        _envelope += Math.Clamp(awake - _envelope, -dt, dt);

        for (var t = 0.0; t < dt; t += 1.0 / 240)
        {
            var h = Math.Min(1.0 / 240, dt - t);
            _squashVel += (-220 * _squash - 9 * _squashVel) * h;
            _squash += _squashVel * h;
            _armVel += (-150 * (_arm + 3 * _squash) - 6 * _armVel) * h;
            _arm += _armVel * h;
        }
        _eye += (_eyeTarget - _eye) * (float)(1 - Math.Exp(-12 * dt));

        var springs = Math.Abs(_squash) > 1e-4 || Math.Abs(_squashVel) > 1e-3 || Math.Abs(_arm) > 1e-4 || Math.Abs(_armVel) > 1e-3
            || Vector2.Distance(_eye, _eyeTarget) > 1e-3;
        if (!springs)
        {
            _squash = _squashVel = _arm = _armVel = 0;
            _eye = _eyeTarget;
        }

        RenderFrame();

        if (!springs && _envelope <= 0)
        {
            _frameTimer.Stop();
            return;
        }
        // Springs and eyes at 30 fps; breathing alone at 15 fps.
        _frameTimer.Interval = TimeSpan.FromMilliseconds(springs ? 33 : 66);
    }

    private void RenderFrame()
    {
        if (_stage is not { } s || _swap is null || _rtv is null || _dsv is null || _msaa is null) return;
        if (AppData.Debug3DFail) throw new COMException("forced device removal (--debug-3d-fail)", unchecked((int)0x887A0005));

        var ctx = s.Context;
        var model = s.Model;
        var t = Now;
        var breath = 0.012 * Math.Sin(2 * Math.PI * t / 3) * _envelope;
        var yaw = (float)(0.2 * Math.Sin(2 * Math.PI * t / 9) * _envelope + 0.3 * _eye.X);
        var pitch = (float)(0.12 * _eye.Y);
        var sy = (float)(1 + Math.Max(_squash, -0.4) + breath);
        var sxz = 1 / MathF.Sqrt(sy);

        var body = model.NodeIndex("body");
        var armL = model.NodeIndex("arm.L");
        var armR = model.NodeIndex("arm.R");
        Span<Matrix4x4> joints = stackalloc Matrix4x4[model.JointNodes.Length];
        model.ComputeJointMatrices(i =>
        {
            if (i == body)
                return Matrix4x4.CreateScale(sxz, sy, sxz) * Matrix4x4.CreateRotationX(pitch) * Matrix4x4.CreateRotationY(yaw) * BloopModel.Local(model.Nodes[i]);
            if (i == armL || i == armR)
            {
                var n = model.Nodes[i];
                var angle = (float)_arm * (i == armL ? 1 : -1);
                return Matrix4x4.CreateScale(n.Scale) * Matrix4x4.CreateFromQuaternion(n.Rotation) * Matrix4x4.CreateRotationZ(angle) * Matrix4x4.CreateTranslation(n.Translation);
            }
            return null;
        }, joints);

        var camPos = new Vector3(0, 0.06f, 0.245f);
        var view = Matrix4x4.CreateLookAt(camPos, new Vector3(0, 0.031f, 0), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(30f * MathF.PI / 180, (float)_width / _height, 0.01f, 1f);
        var frame = new FrameConstants
        {
            ViewProj = view * proj,
            LightDir = Vector3.Normalize(new Vector3(-0.55f, 0.8f, 0.55f)),
            CameraPos = camPos,
            KeyColor = new Vector3(1.0f, 0.97f, 0.92f),
        };
        ctx.UpdateSubresource(in frame, s.FrameCb);

        ctx.ClearRenderTargetView(_rtv, new Color4(0, 0, 0, 0));
        ctx.ClearDepthStencilView(_dsv, DepthStencilClearFlags.Depth, 1, 0);
        ctx.OMSetRenderTargets(_rtv, _dsv);
        ctx.RSSetViewport(0, 0, _width, _height);
        ctx.RSSetState(s.Raster);
        ctx.OMSetBlendState(s.Blend);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.VSSetConstantBuffer(0, s.FrameCb);
        ctx.PSSetConstantBuffer(0, s.FrameCb);

        ctx.OMSetDepthStencilState(s.DepthOff);
        ctx.IASetInputLayout(s.ShadowLayout);
        ctx.IASetVertexBuffer(0, s.ShadowQuad, 20);
        ctx.VSSetShader(s.ShadowVs);
        ctx.PSSetShader(s.ShadowPs);
        ctx.Draw(4, 0);

        ctx.OMSetDepthStencilState(s.DepthOn);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetInputLayout(s.MeshLayout);
        ctx.VSSetShader(s.MeshVs);
        ctx.PSSetShader(s.MeshPs);
        ctx.VSSetConstantBuffer(1, s.DrawCb);
        ctx.PSSetConstantBuffer(1, s.DrawCb);
        if (s.TextureView is not null) ctx.PSSetShaderResource(0, s.TextureView);
        ctx.PSSetSampler(0, s.Sampler);

        var draw = new DrawConstants();
        for (var j = 0; j < joints.Length && j < 8; j++) draw.Joints[j] = joints[j];
        var eyeOffset = new Vector3(0.0028f * _eye.X, -0.0022f * _eye.Y, 0);
        foreach (var part in s.Parts)
        {
            var p = part.Part;
            draw.BaseColor = p.BaseColor;
            draw.Roughness = Math.Clamp(p.Roughness, 0.05f, 1f);
            draw.UseTexture = p.Textured && s.TextureView is not null ? 1 : 0;
            draw.Unlit = p.Kind == PartKind.Glint ? 1 : 0;
            draw.Offset = p.Kind is PartKind.Eye or PartKind.Glint ? eyeOffset : Vector3.Zero;
            ctx.UpdateSubresource(in draw, s.DrawCb);
            ctx.IASetVertexBuffer(0, part.Vertices, (uint)Marshal.SizeOf<MeshVertex>());
            ctx.IASetIndexBuffer(part.Indices, Format.R32_UInt, 0);
            ctx.DrawIndexed((uint)p.Indices.Length, 0, 0);
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
        if (_stage is not null)
        {
            _stage = null;
            Stage3D.Release();
        }
    }

    public void Dispose()
    {
        _visible = false;
        _frameTimer.Stop();
        _teardownTimer.Stop();
        ReleaseGpu();
    }
}
