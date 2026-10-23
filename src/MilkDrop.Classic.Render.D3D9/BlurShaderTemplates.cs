namespace MilkDrop.Classic.Render.D3D9;

/// <summary>
/// blur pass 的内置 shader（对应 data/blur_vs.fx、blur1_ps.fx、blur2_ps.fx）。
/// </summary>
public static class BlurShaderTemplates
{
    public const string VertexShaderSource = """
        void VS(float3 vPosIn : POSITION,
                float4 vDiffuseIn : COLOR,
                float2 uvIn : TEXCOORD0,
                out float4 vPosProj : POSITION,
                out float2 uv : TEXCOORD0)
        {
            vPosProj = float4(vPosIn.xyz, 1);
            uv = uvIn;
        }
        """;

    /// <summary>长水平 pass（blur1_ps.fx）。</summary>
    public const string HorizontalShaderSource = """
        sampler2D sampler_main : register(s0);
        float4 _c0 : register(c0);
        float4 _c1 : register(c1);
        float4 _c2 : register(c2);
        float4 _c3 : register(c3);

        void PS(float2 uv : TEXCOORD0, out float4 ret : COLOR0)
        {
            float2 uv2 = uv.xy + _c0.zw * float2(1, 1);

            float3 blur =
                (tex2D(sampler_main, uv2 + float2( _c2.x * _c0.z, 0)).xyz
               + tex2D(sampler_main, uv2 + float2(-_c2.x * _c0.z, 0)).xyz) * _c1.x +
                (tex2D(sampler_main, uv2 + float2( _c2.y * _c0.z, 0)).xyz
               + tex2D(sampler_main, uv2 + float2(-_c2.y * _c0.z, 0)).xyz) * _c1.y +
                (tex2D(sampler_main, uv2 + float2( _c2.z * _c0.z, 0)).xyz
               + tex2D(sampler_main, uv2 + float2(-_c2.z * _c0.z, 0)).xyz) * _c1.z +
                (tex2D(sampler_main, uv2 + float2( _c2.w * _c0.z, 0)).xyz
               + tex2D(sampler_main, uv2 + float2(-_c2.w * _c0.z, 0)).xyz) * _c1.w;

            blur.xyz *= _c3.z;
            blur.xyz = blur.xyz * _c3.x + _c3.y;

            ret.xyz = blur;
            ret.w = 1;
        }
        """;

    /// <summary>短垂直 pass（blur2_ps.fx）。</summary>
    public const string VerticalShaderSource = """
        sampler2D sampler_main : register(s0);
        float4 _c0 : register(c0);
        float4 _c5 : register(c1);
        float4 _c6 : register(c2);

        void PS(float2 uv : TEXCOORD0, out float4 ret : COLOR0)
        {
            float2 uv2 = uv.xy + _c0.zw * float2(1, 0);

            float3 blur =
                (tex2D(sampler_main, uv2 + float2(0,  _c5.z * _c0.w)).xyz
               + tex2D(sampler_main, uv2 + float2(0, -_c5.z * _c0.w)).xyz) * _c5.x +
                (tex2D(sampler_main, uv2 + float2(0,  _c5.w * _c0.w)).xyz
               + tex2D(sampler_main, uv2 + float2(0, -_c5.w * _c0.w)).xyz) * _c5.y;

            blur.xyz *= _c6.x;

            float t = min(min(uv.x, uv.y), 1 - max(uv.x, uv.y));
            t = sqrt(t);
            t = _c6.y + _c6.z * saturate(t * _c6.w);
            blur.xyz *= t;

            ret.xyz = blur;
            ret.w = 1;
        }
        """;
}
