sampler2D s;

float4 PS(float2 uv : TEXCOORD0) : COLOR
{
    float4 a = tex2Dlod(s, float4(uv, 0.0, 1.0));
    float4 b = tex2Dgrad(s, uv, ddx(uv), ddy(uv));
    return a + b;
}
