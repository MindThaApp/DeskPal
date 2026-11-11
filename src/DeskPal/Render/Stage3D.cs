using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;
using Vortice.D3DCompiler;
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

        var vsMesh = Compiler.Compile(Shaders.Mesh, "VS", "mesh", "vs_4_0", ShaderFlags.OptimizationLevel3);
        var psMesh = Compiler.Compile(Shaders.Mesh, "PS", "mesh", "ps_4_0", ShaderFlags.OptimizationLevel3);
        var vsShadow = Compiler.Compile(Shaders.Shadow, "VS", "shadow", "vs_4_0", ShaderFlags.OptimizationLevel3);
        var psShadow = Compiler.Compile(Shaders.Shadow, "PS", "shadow", "ps_4_0", ShaderFlags.OptimizationLevel3);

        var meshLayout = d.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
            new InputElementDescription("BLENDINDICES", 0, Format.R32G32B32A32_Float, 32, 0),
            new InputElementDescription("BLENDWEIGHT", 0, Format.R32G32B32A32_Float, 48, 0),
        ], vsMesh.Span);
        var shadowLayout = d.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
        ], vsShadow.Span);

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
            MeshVs = d.CreateVertexShader(vsMesh.Span),
            MeshPs = d.CreatePixelShader(psMesh.Span),
            MeshLayout = meshLayout,
            ShadowVs = d.CreateVertexShader(vsShadow.Span),
            ShadowPs = d.CreatePixelShader(psShadow.Span),
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

internal static class Shaders
{
    public const string Mesh = """
        cbuffer Frame : register(b0)
        {
            row_major float4x4 ViewProj;
            float3 LightDir; float Pad0;
            float3 CameraPos; float Pad1;
            float3 KeyColor; float Pad2;
        };
        cbuffer Draw : register(b1)
        {
            row_major float4x4 Joints[8];
            float4 BaseColor;
            float3 Offset; float Roughness;
            float UseTexture; float Unlit; float2 Pad;
        };
        Texture2D Tex : register(t0);
        SamplerState Smp : register(s0);

        struct VSIn { float3 pos : POSITION; float3 nrm : NORMAL; float2 uv : TEXCOORD0; float4 j : BLENDINDICES; float4 w : BLENDWEIGHT; };
        struct PSIn { float4 pos : SV_Position; float3 wpos : TEXCOORD1; float3 nrm : NORMAL; float2 uv : TEXCOORD0; };

        PSIn VS(VSIn i)
        {
            float4x4 m = Joints[(int)i.j.x] * i.w.x + Joints[(int)i.j.y] * i.w.y + Joints[(int)i.j.z] * i.w.z + Joints[(int)i.j.w] * i.w.w;
            float4 wp = mul(float4(i.pos + Offset, 1), m);
            PSIn o;
            o.pos = mul(wp, ViewProj);
            o.wpos = wp.xyz;
            o.nrm = mul(float4(i.nrm, 0), m).xyz;
            o.uv = i.uv;
            return o;
        }

        float3 ToSrgb(float3 c)
        {
            c = saturate(c);
            return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
        }

        float4 PS(PSIn i) : SV_Target
        {
            float3 albedo = BaseColor.rgb;
            if (UseTexture > 0.5) albedo *= Tex.Sample(Smp, i.uv).rgb;
            if (Unlit > 0.5) return float4(ToSrgb(albedo), 1);

            float3 n = normalize(i.nrm);
            float3 v = normalize(CameraPos - i.wpos);
            if (dot(n, v) < 0) n = -n;
            float3 l = normalize(LightDir);
            float ndl = dot(n, l);
            float wrap = saturate((ndl + 0.35) / 1.35);
            float3 hemi = lerp(float3(0.22, 0.19, 0.16), float3(0.42, 0.46, 0.52), n.y * 0.5 + 0.5);

            float3 h = normalize(l + v);
            float a = Roughness * Roughness;
            float a2 = a * a;
            float ndh = saturate(dot(n, h));
            float dd = ndh * ndh * (a2 - 1) + 1;
            float D = a2 / (3.14159 * dd * dd);
            float ndv = saturate(dot(n, v));
            float nl = saturate(ndl);
            float k = (Roughness + 1) * (Roughness + 1) / 8;
            float G = (ndv / (ndv * (1 - k) + k)) * (nl / (nl * (1 - k) + k));
            float F = 0.04 + 0.96 * pow(1 - saturate(dot(h, v)), 5);
            float spec = D * G * F / max(4 * ndv * nl, 0.001) * nl;
            float rim = pow(1 - ndv, 3) * 0.12;

            float3 c = albedo * (KeyColor * wrap + hemi) + KeyColor * spec + rim * hemi;
            return float4(ToSrgb(c), 1);
        }
        """;

    public const string Shadow = """
        cbuffer Frame : register(b0)
        {
            row_major float4x4 ViewProj;
            float3 LightDir; float Pad0;
            float3 CameraPos; float Pad1;
            float3 KeyColor; float Pad2;
        };
        struct VSIn { float3 pos : POSITION; float2 uv : TEXCOORD0; };
        struct PSIn { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        PSIn VS(VSIn i) { PSIn o; o.pos = mul(float4(i.pos, 1), ViewProj); o.uv = i.uv; return o; }
        float4 PS(PSIn i) : SV_Target
        {
            float r = length((i.uv - 0.5) * 2);
            float a = 0.42 * (1 - smoothstep(0.1, 1.0, r));
            return float4(0, 0, 0, a);
        }
        """;
}
