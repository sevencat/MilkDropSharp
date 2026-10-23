float g_a;
float g_b;
int g_i;
int g_j;

float4 PS(float2 uv : TEXCOORD0) : COLOR
{
    float f = g_a % g_b;
    int i = g_i % g_j;
    return float4(f, float(i), uv);
}
