using MilkDrop.ShaderToy.Audio;
using MilkDrop.ShaderToy.Import;
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Tests.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.ShaderToy.Tests.Audio;

/// <summary>
/// Sound pass 的 GPU 回路（S6-1）：两种 <c>mainSound</c> 签名都要真的算出波形。
/// </summary>
/// <remarks>
/// 断言的是<b>样本值</b>而不是像素：音频本来就是 float 数据，绕过 RGBA8 中转才能验证
/// "440 Hz 就是 440 Hz"、"样本序号真的传进去了"。
/// </remarks>
[Collection("GL")]
public sealed class SoundPassRendererTests(OffscreenGlFixture fixture, ITestOutputHelper output)
{
    private const int Batch = SoundPassRenderer.BatchWidth * SoundPassRenderer.BatchHeight;

    [Fact]
    public void TimeOnlySignature_RendersSineAtRequestedFrequency()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        var pass = new ShaderPass
        {
            Type = ShaderPassType.Sound,
            Name = "Sound",
            Code = """
                vec2 mainSound(in float time)
                {
                    return vec2(sin(6.2831853 * 440.0 * time));
                }
                """,
        };

        using var renderer = new SoundPassRenderer(pass, null);
        foreach (string note in renderer.Notes)
        {
            output.WriteLine("note: " + note);
        }

        var samples = new float[Batch * 2];
        Assert.Equal(Batch, renderer.RenderBatch(0, samples));

        // 全部在 [-1,1]，且左右声道一致
        for (var i = 0; i < Batch; i++)
        {
            Assert.InRange(samples[i * 2], -1f, 1f);
            Assert.Equal(samples[i * 2], samples[(i * 2) + 1], 4);
        }

        // 440 Hz 在 1024 个采样里是 10.2 个周期 → 约 20 次过零
        int crossings = CountZeroCrossings(samples, Batch);
        output.WriteLine($"过零次数: {crossings}");
        Assert.InRange(crossings, 18, 23);

        // 首个样本应该是 0 附近（sin(0)）
        Assert.InRange(MathF.Abs(samples[0]), 0f, 0.01f);
    }

    [Fact]
    public void SampleIndexSignature_GetsAbsoluteSampleIndex()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        // 只靠样本序号算值：左 = 序号/44100，右 = 序号是否为偶数
        var pass = new ShaderPass
        {
            Type = ShaderPassType.Sound,
            Name = "Sound",
            Code = """
                vec2 mainSound(int s, float t)
                {
                    return vec2(float(s) / 44100.0, mod(float(s), 2.0));
                }
                """,
        };

        using var renderer = new SoundPassRenderer(pass, null);

        var samples = new float[Batch * 2];
        renderer.RenderBatch(1024, samples);

        // 第二批：第一个样本的序号必须正好是 1024
        Assert.Equal(1024f / 44100f, samples[0], 5);
        Assert.Equal(1024f / 44100f + (Batch - 1) / 44100f, samples[(Batch - 1) * 2], 4);

        // 偶数序号在右声道是 0，奇数序号是 1
        Assert.Equal(0f, samples[1], 4);          // 1024 是偶数
        Assert.Equal(1f, samples[(1 * 2) + 1], 4); // 1025 是奇数
    }

    [Fact]
    public void SampleRate_IsPassedToShader()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        var pass = new ShaderPass
        {
            Type = ShaderPassType.Sound,
            Name = "Sound",
            Code = "vec2 mainSound(float t) { return vec2(iSampleRate / 44100.0); }",
        };

        using var renderer = new SoundPassRenderer(pass, null);

        var samples = new float[Batch * 2];
        renderer.RenderBatch(0, samples);
        Assert.Equal(1f, samples[0], 4);

        renderer.RenderBatch(0, 22050, samples);
        Assert.Equal(0.5f, samples[0], 4);
    }

    [Fact]
    public void MusicChannelIsBoundForSoundPass()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        // sound pass 里读 music 通道（波形行）：没有音频时应该是 0.5 灰
        var pass = new ShaderPass
        {
            Type = ShaderPassType.Sound,
            Name = "Sound",
            Code = "vec2 mainSound(float t) { return vec2(texture(iChannel0, vec2(0.5, 0.75)).x); }",
        };

        pass.Inputs.Add(new ShaderPassInput { Channel = 0, Type = ShaderInputType.Music });

        using var music = new MusicTexture();
        using var renderer = new SoundPassRenderer(pass, null, music);

        var samples = new float[Batch * 2];
        renderer.RenderBatch(0, samples);

        Assert.InRange(samples[0], 0.45f, 0.55f);
        Assert.Equal(samples[0], samples[1], 4);
    }

    [Fact]
    public void RealFlGyWcSoundPass_CompilesAndProducesAudio()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        ShaderToyProject project = ShaderToyJsonImporter.LoadFile(TestPaths.FlGyWcJson);
        ShaderPass? soundPass = project.SoundPass;
        Assert.NotNull(soundPass);

        using var renderer = new SoundPassRenderer(soundPass!, project.BuildCommonCode());
        foreach (string note in renderer.Notes)
        {
            output.WriteLine("note: " + note);
        }

        var samples = new float[Batch * 2];
        renderer.RenderBatch(0, samples);
        renderer.RenderBatch(Batch, samples);

        float peak = 0f;
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.True(float.IsFinite(samples[i]));
            peak = Math.Max(peak, MathF.Abs(samples[i]));
        }

        output.WriteLine($"峰值: {peak:0.0000}");
        Assert.True(peak > 0.001f, "取回的全是 0：sound pass 没算出波形（检查 RGBA32F 回读或 mainSound 签名）");
    }

    [Fact]
    public void RenderBatch_SynchronousReadbackCostIsAcceptable()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        // 用真实作品（S6-3 要的是"最重的那种 shader"的代价）
        ShaderToyProject project = ShaderToyJsonImporter.LoadFile(TestPaths.FlGyWcJson);
        using var renderer = new SoundPassRenderer(project.SoundPass!, project.BuildCommonCode());

        var samples = new float[Batch * 2];

        // 预热：第一批判 shader/驱动初始化，不计入
        renderer.RenderBatch(0, samples);

        const int iterations = 60;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            renderer.RenderBatch(i * Batch, samples);
        }

        stopwatch.Stop();

        double perBatch = stopwatch.Elapsed.TotalMilliseconds / iterations;
        output.WriteLine($"{iterations} 批 / {perBatch:0.00} ms 每批（{Batch} 个采样，约 {Batch / 44.1:0.0} ms 音频）");

        // 一批音频要覆盖 23 ms 的播放时间，渲染必须远快于它，否则会持续欠载
        Assert.True(perBatch < 20.0, $"每批 {perBatch:0.00} ms 太慢，44.1 kHz 下会欠载");
    }

    private static int CountZeroCrossings(float[] interleaved, int frames)
    {
        var crossings = 0;
        for (var i = 1; i < frames; i++)
        {
            float previous = interleaved[(i - 1) * 2];
            float current = interleaved[i * 2];
            if ((previous <= 0f && current > 0f) || (previous >= 0f && current < 0f))
            {
                crossings++;
            }
        }

        return crossings;
    }
}
