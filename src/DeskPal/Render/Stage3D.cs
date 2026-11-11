using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskPal.Render;

[InlineArray(8)]
internal struct JointArray { private Matrix4x4 _e; }

[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public Matrix4x4 ViewProj;
    public Vector3 LightDir;
    public float Pad0;
    public Vector3 CameraPos;
    public float Pad1;
    public Vector3 KeyColor;
    public float Pad2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DrawConstants
{
    public JointArray Joints;
    public Vector4 BaseColor;
    public Vector3 Offset;
    public float Roughness;
    public float UseTexture;
    public float Unlit;
    public Vector2 Pad;
}

internal sealed class GpuPart(ModelPart part, ID3D11Buffer vb, ID3D11Buffer ib)
{
    public ModelPart Part { get; } = part;
    public ID3D11Buffer Vertices { get; } = vb;
    public ID3D11Buffer Indices { get; } = ib;
}

// The one shared graphics device, shaders and character data. Created lazily on a worker thread, released
// when the last live surface lets go. Any failure marks 3D as failed for the rest of the session.
internal sealed class Stage3D : IDisposable
{
    public static bool Failed { get; private set; }

    private static Stage3D? _shared;
    private static Task<Stage3D>? _pending;
    private static int _users;

    public required IDXGIFactory2 Factory { get; init; }
    public required ID3D11Device Device { get; init; }
    public required ID3D11DeviceContext Context { get; init; }
    public required BloopModel Model { get; init; }
    public required GpuPart[] Parts { get; init; }
    public required uint SampleCount { get; init; }
    public required ID3D11VertexShader MeshVs { get; init; }
    public required ID3D11PixelShader MeshPs { get; init; }
    public required ID3D11InputLayout MeshLayout { get; init; }
    public required ID3D11VertexShader ShadowVs { get; init; }
    public required ID3D11PixelShader ShadowPs { get; init; }
    public required ID3D11InputLayout ShadowLayout { get; init; }
    public required ID3D11Buffer ShadowQuad { get; init; }
    public required ID3D11Buffer FrameCb { get; init; }
    public required ID3D11Buffer DrawCb { get; init; }
    public required ID3D11BlendState Blend { get; init; }
    public required ID3D11RasterizerState Raster { get; init; }
    public required ID3D11DepthStencilState DepthOn { get; init; }
    public required ID3D11DepthStencilState DepthOff { get; init; }
    public required ID3D11SamplerState Sampler { get; init; }
    public ID3D11Texture2D? Texture { get; init; }
    public ID3D11ShaderResourceView? TextureView { get; init; }

    // Bytecode is built offline by tools\compile-shaders.ps1, so the runtime shader compiler is never loaded.
    private static byte[] LoadShader(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", name + ".cso"));

    public static void MarkFailed(string where, Exception ex)
    {
        Failed = true;
        try
        {
            Logger.Log($"3d failed ({where}), using the Soft 3D card for this session: {ex.GetType().Name}: {ex.Message}");
            CrashReporting.CaptureCrash(ex, "Stage3D." + where, handled: true);
        }
        catch { }
    }

    public static async Task<Stage3D> AcquireAsync()
    {
        if (Failed) throw new InvalidOperationException("3d disabled for this session");
        _users++;
        try
        {
            if (_shared is not null) return _shared;
            _pending ??= Task.Run(Create);
            var stage = await _pending;
            _shared = stage;
            return stage;
        }
        catch
        {
            _users--;
            _pending = null;
            throw;
        }
    }

    public static void Release()
    {
        if (--_users > 0 || _shared is null) return;
        var stage = _shared;
        _shared = null;
        _pending = null;
        stage.Dispose();
        Logger.Log("3d device released");
    }

    private static Stage3D Create()
    {
        var sw = Stopwatch.StartNew();
        var path = BloopModel.FindModelPath() ?? throw new FileNotFoundException("bloop model missing");
        var model = BloopModel.Load(path);

        var factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
        factory.EnumAdapterByGpuPreference(0, GpuPreference.MinimumPower, out IDXGIAdapter1? adapter).CheckError();
        using var _ = adapter;
        var info = adapter!.Description1;
        if ((info.Flags & AdapterFlags.Software) != 0) throw new NotSupportedException("only a software adapter is available");

        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        var d = device;
        var ctx = context;

        var samples = d.CheckMultisampleQualityLevels(Format.B8G8R8A8_UNorm, 4) > 0 ? 4u : 1u;

        var vsMesh = LoadShader("Mesh.VS");
        var psMesh = LoadShader("Mesh.PS");
        var vsShadow = LoadShader("Shadow.VS");
        var psShadow = LoadShader("Shadow.PS");

        var meshLayout = d.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
            new InputElementDescription("BLENDINDICES", 0, Format.R32G32B32A32_Float, 32, 0),
            new InputElementDescription("BLENDWEIGHT", 0, Format.R32G32B32A32_Float, 48, 0),
        ], vsMesh);
        var shadowLayout = d.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
        ], vsShadow);

        var parts = model.Parts.Select(p => new GpuPart(p,
            d.CreateBuffer(p.Vertices, BindFlags.VertexBuffer),
            d.CreateBuffer(p.Indices, BindFlags.IndexBuffer))).ToArray();

        const float sx = 0.075f, sz = 0.05f, y = 0.0002f;
        float[] quad = [-sx, y, -sz, 0, 0, sx, y, -sz, 1, 0, -sx, y, sz, 0, 1, sx, y, sz, 1, 1];

        ID3D11Texture2D? tex = null;
        ID3D11ShaderResourceView? srv = null;
        if (model.Texture is { } png)
        {
            var bounds = SKBitmap.DecodeBounds(png);
            using var bmp = SKBitmap.Decode(png, new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new InvalidDataException("face texture did not decode");
            tex = d.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm_SRgb, (uint)bmp.Width, (uint)bmp.Height, 1, 0,
                BindFlags.ShaderResource | BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.GenerateMips));
            ctx.UpdateSubresource(tex, 0, null, bmp.GetPixels(), (uint)bmp.RowBytes, 0);
            srv = d.CreateShaderResourceView(tex);
            ctx.GenerateMips(srv);
        }

        var stage = new Stage3D
        {
            Factory = factory,
            Device = d,
            Context = ctx,
            Model = model,
            Parts = parts,
            SampleCount = samples,
            MeshVs = d.CreateVertexShader(vsMesh),
            MeshPs = d.CreatePixelShader(psMesh),
            MeshLayout = meshLayout,
            ShadowVs = d.CreateVertexShader(vsShadow),
            ShadowPs = d.CreatePixelShader(psShadow),
            ShadowLayout = shadowLayout,
            ShadowQuad = d.CreateBuffer(quad, BindFlags.VertexBuffer),
            FrameCb = d.CreateBuffer(new BufferDescription((uint)Unsafe.SizeOf<FrameConstants>(), BindFlags.ConstantBuffer)),
            DrawCb = d.CreateBuffer(new BufferDescription((uint)Unsafe.SizeOf<DrawConstants>(), BindFlags.ConstantBuffer)),
            Blend = d.CreateBlendState(new BlendDescription(Vortice.Direct3D11.Blend.One, Vortice.Direct3D11.Blend.InverseSourceAlpha)),
            Raster = d.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { MultisampleEnable = samples > 1 }),
            DepthOn = d.CreateDepthStencilState(DepthStencilDescription.Default),
            DepthOff = d.CreateDepthStencilState(DepthStencilDescription.None),
            Sampler = d.CreateSamplerState(SamplerDescription.LinearClamp),
            Texture = tex,
            TextureView = srv,
        };
        Logger.Log($"3d device ready: {info.Description}, {d.FeatureLevel}, msaa {samples}x, {model.Parts.Length} parts, {sw.ElapsedMilliseconds} ms");
        return stage;
    }

    public void Dispose()
    {
        try
        {
            Context.ClearState();
            Context.Flush();
        }
        catch { }
        foreach (var p in Parts)
        {
            p.Vertices.Dispose();
            p.Indices.Dispose();
        }
        TextureView?.Dispose();
        Texture?.Dispose();
        Sampler.Dispose();
        DepthOff.Dispose();
        DepthOn.Dispose();
        Raster.Dispose();
        Blend.Dispose();
        DrawCb.Dispose();
        FrameCb.Dispose();
        ShadowQuad.Dispose();
        ShadowLayout.Dispose();
        ShadowPs.Dispose();
        ShadowVs.Dispose();
        MeshLayout.Dispose();
        MeshPs.Dispose();
        MeshVs.Dispose();
        Context.Dispose();
        Device.Dispose();
        Factory.Dispose();
    }
}
