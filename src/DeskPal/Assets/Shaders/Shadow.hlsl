cbuffer Frame : register(b0)
{
    row_major float4x4 ViewProj;
    float3 LightDir; float Pad0;
    float3 CameraPos; float Pad1;
    float3 KeyColor; float Pad2;
};
cbuffer Shadow : register(b3)
{
    float4 Placement; // centre x, centre z, half width, half depth
    float4 Strength;
};
struct VSIn { float3 pos : POSITION; float2 uv : TEXCOORD0; };
struct PSIn { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
PSIn VS(VSIn i)
{
    PSIn o;
    float3 p = float3(Placement.x + i.pos.x * Placement.z, i.pos.y, Placement.y + i.pos.z * Placement.w);
    o.pos = mul(float4(p, 1), ViewProj);
    o.uv = i.uv;
    return o;
}
// Premultiplied output: black with coverage alpha, zero outside the ellipse so the quad never darkens its corners.
float4 PS(PSIn i) : SV_Target
{
    float r = length((i.uv - 0.5) * 2);
    float a = Strength.x * (1 - smoothstep(0.1, 1.0, r));
    return float4(0, 0, 0, r < 1 ? a : 0);
}
