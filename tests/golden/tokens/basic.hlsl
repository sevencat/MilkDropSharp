// line comment
float4 PS(float2 uv : TEXCOORD0, float4 color : COLOR) : SV_Target
{
    float2 p = uv.xy * 2.0 - 1.0;
    float4 c = color;
    c.rgb = lerp(c.rgb, float3(0.5, 1.0, 0.25), 0.5);
    /* block
       comment */
    return c;
}
