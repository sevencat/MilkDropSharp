using MilkDrop.ShaderToy.Compile;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.ShaderToy.Tests.Audio;

/// <summary>
/// <c>mainSound</c> 的签名探测（S6-1）：两种写法都要认，注释里的不能被认。
/// </summary>
public sealed class MainSoundEntryTests(ITestOutputHelper output)
{
    [Fact]
    public void Detect_TimeOnly()
    {
        const string code = """
            vec2 mainSound(float time)
            {
                return vec2(sin(6.2831 * 440.0 * time));
            }
            """;

        Assert.True(MainSoundEntry.TryDetect(code, out MainSoundSignature? signature, out _));
        Assert.Equal(1, signature!.Count);
        Assert.True(signature.HasTime);
        Assert.False(signature.HasSampleIndex);
        output.WriteLine(signature.Describe());
    }

    [Fact]
    public void Detect_SampleIndexAndTime()
    {
        const string code = """
            vec2 mainSound(int s, float t)
            {
                return vec2(float(s) / 44100.0 + t);
            }
            """;

        Assert.True(MainSoundEntry.TryDetect(code, out MainSoundSignature? signature, out _));
        Assert.Equal(2, signature!.Count);
        Assert.Equal(MainSoundParameter.SampleIndex, signature.Parameters[0]);
        Assert.Equal(MainSoundParameter.Time, signature.Parameters[1]);
    }

    [Fact]
    public void Detect_QualifiersAndLineBreaks()
    {
        // 形参带 in/highp 修饰、跨行、类型顺序反过来，都要能认出来
        const string code = """
            vec2 mainSound(
                in highp float t,
                in int samp)
            {
                return vec2(t);
            }
            """;

        Assert.True(MainSoundEntry.TryDetect(code, out MainSoundSignature? signature, out _));
        Assert.Equal(MainSoundParameter.Time, signature!.Parameters[0]);
        Assert.Equal(MainSoundParameter.SampleIndex, signature.Parameters[1]);
    }

    [Fact]
    public void Detect_IgnoresCommentedOutDefinition()
    {
        // 与 S3-7 同类：注释里的 mainSound 不能当成真实签名
        const string code = """
            // vec2 mainSound(int s, float t) { return vec2(0.0); }
            vec2 mainSound(float time) { return vec2(0.0); }
            """;

        Assert.True(MainSoundEntry.TryDetect(code, out MainSoundSignature? signature, out _));
        Assert.Equal(1, signature!.Count);

        Assert.False(MainSoundEntry.TryDetect("// vec2 mainSound(int s, float t);", out _, out string? problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void BuildEntryPoint_CallsSignatureWithMatchingArguments()
    {
        var timeOnly = new MainSoundSignature([MainSoundParameter.Time]);
        string source = MainSoundEntry.BuildEntryPoint(timeOnly);

        Assert.Contains("mainSound(ds_time)", source, StringComparison.Ordinal);
        Assert.Contains("ds_sampleIndex", source, StringComparison.Ordinal);
        Assert.Contains("clamp(ds_sample, -1.0, 1.0)", source, StringComparison.Ordinal);

        var both = new MainSoundSignature([MainSoundParameter.SampleIndex, MainSoundParameter.Time]);
        string bothSource = MainSoundEntry.BuildEntryPoint(both);
        Assert.Contains("mainSound(ds_index, ds_time)", bothSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Transpile_SoundPassProducesMainWrapper()
    {
        var pass = new MilkDrop.ShaderToy.Pass.ShaderPass
        {
            Type = MilkDrop.ShaderToy.Pass.ShaderPassType.Sound,
            Name = "Sound",
            Code = "vec2 mainSound(int s, float t) { return vec2(sin(t * 440.0)); }",
        };

        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(pass);

        Assert.Contains("mainSound(ds_index, ds_time)", result.Source, StringComparison.Ordinal);
        Assert.Contains("out vec4 ds_fragColor", result.Source, StringComparison.Ordinal);
        Assert.Contains(result.Notes, n => n.Contains("mainSound 签名", StringComparison.Ordinal));
    }
}
