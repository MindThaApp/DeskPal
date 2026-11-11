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
    float3 hemi = lerp(float3(0.30, 0.27, 0.24), float3(0.52, 0.56, 0.62), n.y * 0.5 + 0.5);

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
