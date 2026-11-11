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
