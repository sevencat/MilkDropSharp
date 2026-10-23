sampler3D s3;

float4 PS(float3 uvw : TEXCOORD0) : COLOR
{
    float4 a = tex3D(s3, uvw);
    float4 b = tex3Dbias(s3, float4(uvw, 1.5));
    float4 c = tex3Dlod(s3, float4(uvw, 0.0));
    return a + b + c;
}
