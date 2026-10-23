float g_scale;

float4 PS(float2 uv : TEXCOORD0) : COLOR
{
    float3 col = float3(uv, 1.0);
    if (g_scale > 0.5)
    {
        col = col * g_scale + frac(uv.xyy);
    }
    return float4(col, 1.0);
}
