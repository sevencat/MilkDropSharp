using MilkDrop.ShaderToy.Audio;
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Tests.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.ShaderToy.Tests.Audio;

/// <summary>
/// 音频闭环的编排（S6-2）：按水位补渲染、写环形缓冲、同时喂 music 纹理。
/// </summary>
[Collection("GL")]
public sealed class AudioEngineTests(OffscreenGlFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public void Pump_FillsRingBufferAndMusicTexture()
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
            Code = "vec2 mainSound(float t) { return vec2(sin(6.2831853 * 220.0 * t)); }",
        };

        using var renderer = new SoundPassRenderer(pass, null);
        using var music = new MusicTexture();
        using var engine = new AudioEngine(renderer, music);

        // 没开后端时不该偷偷渲染（否则缓冲会满、music 纹理变成快进的垃圾）
        Assert.Equal(0, engine.Pump());
        Assert.Equal(0, engine.Buffer.AvailableFrames);

        // Start 会先预填到水位，避免设备一开就欠载
        engine.Start(new NullAudioSink());
        output.WriteLine($"预填 {engine.SamplesRendered} 个采样，缓冲 {engine.Buffer.AvailableFrames} 帧");

        Assert.True(engine.SamplesRendered >= SoundPassRenderer.BatchWidth * SoundPassRenderer.BatchHeight);
        Assert.True(engine.Buffer.AvailableFrames >= AudioEngine.TargetBufferedFrames);
        Assert.True(music.TotalFrames > 0);

        // 水位已到，Pump 不该再渲染
        Assert.Equal(0, engine.Pump());
    }

    [Fact]
    public void Reset_StartsFromSampleZeroAgain()
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
            Code = "vec2 mainSound(int s, float t) { return vec2(float(s) / 44100.0); }",
        };

        using var renderer = new SoundPassRenderer(pass, null);
        using var music = new MusicTexture();
        using var engine = new AudioEngine(renderer, music);

        engine.Start(new NullAudioSink());
        engine.Pump();
        Assert.True(engine.SamplesRendered > 0);

        engine.Reset();
        Assert.Equal(0, engine.SamplesRendered);
        Assert.Equal(0, engine.Buffer.AvailableFrames);
    }
}
