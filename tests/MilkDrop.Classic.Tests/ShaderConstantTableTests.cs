using MilkDrop.Classic.Render.D3D9;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 常量表解析 —— 替代 D3DX9 的 <c>ID3DXConstantTable</c>（cbplan.md §2.2 / §4.2）。
/// 只要这里错，M5 的预设 shader 就会整片黑。
/// </summary>
public sealed class ShaderConstantTableTests
{
    [Fact]
    public void ParsesFloatRegistersAndSamplers()
    {
        const string source = """
            float4 _c0 : register(c0);
            float4x3 rot_s1 : register(c13);
            sampler2D sampler_main : register(s0);
            sampler2D noise_lq : register(s3);

            float4 PS(float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(sampler_main, uv) * _c0;
            }
            """;

        var table = ShaderConstantTable.Parse(source);

        Assert.Equal(0, table.FloatRegisters["_c0"]);
        Assert.Equal(13, table.FloatRegisters["rot_s1"]);
        Assert.Equal(0, table.Samplers["sampler_main"]);
        Assert.Equal(3, table.Samplers["noise_lq"]);
    }

    [Fact]
    public void IsCaseInsensitive()
    {
        var table = ShaderConstantTable.Parse("float4 Rand_Frame : register(c7);");
        Assert.True(table.TryGetFloatRegister("rand_frame", out var register));
        Assert.Equal(7, register);
    }

    [Fact]
    public void IgnoresSemanticsThatAreNotRegisters()
    {
        const string source = """
            struct VS_OUT { float4 pos : POSITION; float2 uv : TEXCOORD0; };
            float4 PS(float2 uv : TEXCOORD0) : COLOR { return float4(uv, 0, 1); }
            """;

        var table = ShaderConstantTable.Parse(source);

        Assert.Empty(table.FloatRegisters);
        Assert.Empty(table.Samplers);
    }

    [Fact]
    public void ToleratesWhitespaceAndNoSpaceBeforeColon()
    {
        var table = ShaderConstantTable.Parse("float4 _qa:register( c24 );\nfloat4 _qe :  register (c28);");

        Assert.Equal(24, table.FloatRegisters["_qa"]);
        Assert.Equal(28, table.FloatRegisters["_qe"]);
    }
}
