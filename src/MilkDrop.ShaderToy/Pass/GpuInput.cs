using System.Numerics;
using MilkDrop.Rendering.Gles3;

namespace MilkDrop.ShaderToy.Pass;

/// <summary>
/// 每帧推给 shader 的输入（对应上游 <c>viewport.inl</c> 的 <c>GpuInput</c>，47 行）。
/// </summary>
/// <remarks>
/// <para>
/// 上游把这套结构塞进 BDA 的 push constant 里一次推送；GLES3 没有 BDA，这里拆成逐 uniform 赋值
/// （<see cref="PushTo"/>），名字与 Shadertoy 网页端一致。
/// </para>
/// <para>
/// 与上游的差异：上游只真正写 <c>Resolution/Time/TimeDelta/FrameRate/Frame/Mouse</c>，
/// <c>iDate/iChannelResolution/iSampleRate</c> 留在零值；这里全部填上 —— 依赖 <c>iChannelResolution</c>
/// 的 buffer 作品（texelFetch 定位像素那类）在上游会画错（见 <c>shaderToyIssue.md</c> S3-3）。
/// </para>
/// </remarks>
public sealed class GpuInput
{
    /// <summary>绘制缓冲尺寸（像素），<c>z</c> 固定 1.0。</summary>
    public Vector3 Resolution { get; set; }

    /// <summary>播放时间（秒）。</summary>
    public float Time { get; set; }

    /// <summary>距上一帧的秒数（暂停时为 0）。</summary>
    public float TimeDelta { get; set; }

    /// <summary>帧率，由 <see cref="TimeDelta"/> 推得。</summary>
    public float FrameRate { get; set; }

    /// <summary>播放帧号。</summary>
    public int Frame { get; set; }

    /// <summary><c>xy</c>：当前鼠标位置（按住时）；<c>zw</c>：点击位置，释放后取负。</summary>
    public Vector4 Mouse { get; set; } = new(0f, 0f, -1f, -1f);

    /// <summary><c>(年, 月, 日, 当天秒数)</c>；月份与浏览器 <c>Date.getMonth()</c> 一致，0 起。</summary>
    public Vector4 Date { get; set; }

    /// <summary>音频采样率（sound pass 用；上游不填）。</summary>
    public float SampleRate { get; set; } = 44100f;

    /// <summary>每个通道的播放时长（秒）。P5 接入通道后填。</summary>
    public float[] ChannelTime { get; } = new float[4];

    /// <summary>每个通道的分辨率（像素）。P5 接入通道后填。</summary>
    public Vector3[] ChannelResolution { get; } = new Vector3[4];

    /// <summary>按当前本地时间生成 <c>iDate</c>。</summary>
    public static Vector4 MakeDate(DateTime now) => new(
        now.Year,
        now.Month - 1,
        now.Day,
        (float)now.TimeOfDay.TotalSeconds);

    /// <summary>把全部字段按名字推给 program（找不到的 uniform 会被静默跳过）。</summary>
    public void PushTo(Shader shader)
    {
        ArgumentNullException.ThrowIfNull(shader);

        shader.SetUniformFloat3("iResolution", Resolution);
        shader.SetUniformFloat("iTime", Time);
        shader.SetUniformFloat("iTimeDelta", TimeDelta);
        shader.SetUniformFloat("iFrameRate", FrameRate);
        shader.SetUniformInt("iFrame", Frame);
        shader.SetUniformFloat4("iMouse", Mouse);
        shader.SetUniformFloat4("iDate", Date);
        shader.SetUniformFloat("iSampleRate", SampleRate);

        for (int channel = 0; channel < 4; channel++)
        {
            shader.SetUniformFloat($"iChannelTime[{channel}]", ChannelTime[channel]);
            shader.SetUniformFloat3($"iChannelResolution[{channel}]", ChannelResolution[channel]);
        }
    }
}
