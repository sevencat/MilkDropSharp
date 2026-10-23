using System.Numerics;
using MilkDrop.Classic.Render;
using MilkDrop.Classic.Visualizer.Sound;

namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary>一个自定义波形的绘制参数（顶点由 <see cref="WaveGeometry"/> 填好）。</summary>
/// <param name="Count">顶点数；0 表示这一帧不画。</param>
/// <param name="UseDots">点模式（<c>D3DPT_POINTLIST</c>）。</param>
/// <param name="Additive">加法混合。</param>
/// <param name="DrawThick">描粗（4 遍错位绘制）。</param>
/// <param name="PointSize">点模式下的点大小。</param>
/// <param name="Smoothed">顶点在"平滑后"的缓冲里（否则在原始缓冲里）。</param>
public readonly record struct WaveDraw(int Count, bool UseDots, bool Additive, bool DrawThick, float PointSize, bool Smoothed);

/// <summary>
/// <c>CPlugin::DrawCustomWaves</c>（milkdropfs.cpp:2496-2680）的几何部分：
/// 取波形/频谱 → 平滑 → 逐点跑 per-point 代码 → 输出<b>像素空间</b>顶点。
/// </summary>
/// <remarks>
/// 与 C 版的换算：C 里顶点是裁剪空间（<c>x = (pp_x*2-1)*invAspectX</c>、<c>y = (pp_y*-2+1)*invAspectY</c>，
/// Y 向上），本后端的 <see cref="MilkVertex"/> 是像素空间、左上原点、Y 向下，所以
/// <c>px = (x*0.5+0.5)*width</c>、<c>py = (0.5-y*0.5)*height</c>。
/// </remarks>
public static class WaveGeometry
{
    /// <summary><c>NUM_WAVEFORM_SAMPLES</c>（defines.h:188）。</summary>
    public const int NumWaveformSamples = 480;

    /// <summary><c>tempdata[2][512]</c> 的上限。</summary>
    public const int MaxSamples = 512;

    /// <summary>平滑后最多 <c>2*n-1</c> 个点。</summary>
    public const int MaxVertices = (MaxSamples * 2) - 1;

    /// <summary>
    /// 生成一个波形的顶点。
    /// </summary>
    /// <param name="wave">波形参数（samples/spectrum/sep/scaling/smoothing/dots 等）。</param>
    /// <param name="expressions">该波形的 per-frame / per-point VM。</param>
    /// <param name="state">预设状态（提供 q/t 与 <c>WaveScale</c>）。</param>
    /// <param name="context">本帧上下文。</param>
    /// <param name="sound">波形/频谱数据。</param>
    /// <param name="alphaMultiplier">混合时旧状态的 alpha 权重。</param>
    /// <param name="invAspectX">还原宽高比用的 X 系数。</param>
    /// <param name="invAspectY">还原宽高比用的 Y 系数。</param>
    /// <param name="width">目标宽（像素）。</param>
    /// <param name="height">目标高（像素）。</param>
    /// <param name="vertices">原始顶点缓冲，至少 <see cref="MaxSamples"/> 个。</param>
    /// <param name="smoothed">平滑后的顶点缓冲，至少 <see cref="MaxVertices"/> 个（点模式可传空）。</param>
    public static WaveDraw Build(
        PresetWave wave,
        WaveExpressions expressions,
        PresetState state,
        in PerFrameContext context,
        SoundAnalysis sound,
        float alphaMultiplier,
        float invAspectX,
        float invAspectY,
        int width,
        int height,
        Span<MilkVertex> vertices,
        Span<MilkVertex> smoothed)
    {
        if (wave.Enabled == 0)
        {
            return default;
        }

        var spectrum = wave.Spectrum != 0;
        var useDots = wave.UseDots != 0;
        var maxSamples = spectrum ? MaxSamples : NumWaveformSamples;

        var nSamples = Math.Min(wave.Samples, maxSamples) - wave.Sep;
        if (nSamples < 1)
        {
            return default;
        }

        // 1. per-frame 代码（milkdropfs.cpp:2525-2546）
        expressions.SyncPerFrame(context, state.PerFrame, wave);
        expressions.RunPerFrame();

        var frameOutputs = expressions.ReadPerFrameOutputs();
        nSamples = Math.Min(MaxSamples, (int)frameOutputs.Samples);

        if (nSamples < 2 && !(useDots && nSamples >= 1))
        {
            return default;
        }

        // 2. 取数据 + 双向平滑（milkdropfs.cpp:2554-2584）
        var data1 = spectrum ? sound.Spectrum[0] : sound.Waveform[0];
        var data2 = spectrum ? sound.Spectrum[1] : sound.Waveform[1];

        var mult = (spectrum ? 0.15f : 0.004f) * wave.Scaling * state.WaveScale.Eval(context.Time);
        var j0 = spectrum ? 0 : ((maxSamples - nSamples) / 2) - (wave.Sep / 2);
        var j1 = spectrum ? 0 : ((maxSamples - nSamples) / 2) + (wave.Sep / 2);
        var step = spectrum ? (maxSamples - wave.Sep) / (float)nSamples : 1f;

        var mix1 = MathF.Sqrt(MathF.Max(0f, wave.Smoothing * 0.98f));
        var mix2 = 1f - mix1;

        Span<float> temp1 = stackalloc float[MaxSamples];
        Span<float> temp2 = stackalloc float[MaxSamples];

        temp1[0] = Sample(data1, j0);
        temp2[0] = Sample(data2, j1);

        for (var j = 1; j < nSamples; j++)
        {
            temp1[j] = (Sample(data1, (int)((j * step) + j0)) * mix2) + (temp1[j - 1] * mix1);
            temp2[j] = (Sample(data2, (int)((j * step) + j1)) * mix2) + (temp2[j - 1] * mix1);
        }

        // 反向再平滑一次，抹掉首尾的不对称
        for (var j = nSamples - 2; j >= 0; j--)
        {
            temp1[j] = (temp1[j] * mix2) + (temp1[j + 1] * mix1);
            temp2[j] = (temp2[j] * mix2) + (temp2[j + 1] * mix1);
        }

        for (var j = 0; j < nSamples; j++)
        {
            temp1[j] *= mult;
            temp2[j] *= mult;
        }

        // 3. 逐点跑 per-point 代码（milkdropfs.cpp:2593-2620）
        var jMult = 1f / (nSamples - 1);
        for (var j = 0; j < nSamples; j++)
        {
            var t = j * jMult;
            var value1 = temp1[j];
            var value2 = temp2[j];

            expressions.SyncPerPoint(t, value1, value2, 0.5f + value1, 0.5f + value2);
            expressions.RunPerPoint();

            var (x, y, r, g, b, a) = expressions.ReadPerPointOutputs();

            // 裁剪空间 → 像素空间（见类注释）
            var clipX = ((float)x * 2f - 1f) * invAspectX;
            var clipY = ((float)y * -2f + 1f) * invAspectY;

            vertices[j] = new MilkVertex(
                new Vector3(((clipX * 0.5f) + 0.5f) * width, (0.5f - (clipY * 0.5f)) * height, 0f),
                PackColor((float)(a * 255f * alphaMultiplier), (float)(r * 255f), (float)(g * 255f), (float)(b * 255f)),
                Vector2.Zero);
        }

        var count = nSamples;
        var smoothedOutput = false;

        // 4. 平滑（点模式下不做，否则点会翻倍）
        if (!useDots && smoothed.Length >= MaxVertices)
        {
            count = SmoothWave(vertices[..nSamples], smoothed);
            smoothedOutput = true;
        }

        var pointSize = (width >= 1024 ? 2f : 1f) + (wave.DrawThick != 0 ? 1f : 0f);

        return new WaveDraw(count, useDots, wave.Additive != 0, wave.DrawThick != 0, pointSize, smoothedOutput);
    }

    /// <summary>
    /// <c>SmoothWave</c>（milkdropfs.cpp:2466-2494）：在相邻点之间插一个加权点，点数约翻倍。
    /// </summary>
    /// <returns>输出的点数（<c>2*n-1</c>）。</returns>
    public static int SmoothWave(ReadOnlySpan<MilkVertex> input, Span<MilkVertex> output)
    {
        const float c1 = -0.15f;
        const float c2 = 1.15f;
        const float c3 = 1.15f;
        const float c4 = -0.15f;
        var inverseSum = 1f / (c1 + c2 + c3 + c4);

        var n = input.Length;
        if (n < 2)
        {
            if (n == 1)
            {
                output[0] = input[0];
            }

            return n;
        }

        var below = 0;
        var above2 = 1;
        var index = 0;

        for (var i = 0; i < n - 1; i++)
        {
            var above = above2;
            above2 = Math.Min(n - 1, i + 2);

            output[index] = input[i];
            output[index + 1] = new MilkVertex(
                new Vector3(
                    ((c1 * input[below].Position.X) + (c2 * input[i].Position.X) + (c3 * input[above].Position.X) + (c4 * input[above2].Position.X)) * inverseSum,
                    ((c1 * input[below].Position.Y) + (c2 * input[i].Position.Y) + (c3 * input[above].Position.Y) + (c4 * input[above2].Position.Y)) * inverseSum,
                    0f),
                input[i].Color,
                Vector2.Zero);

            below = i;
            index += 2;
        }

        output[index++] = input[n - 1];
        return index;
    }

    private static float Sample(float[] data, int index)
    {
        if (index < 0 || index >= data.Length)
        {
            return 0f;
        }

        return data[index];
    }

    /// <summary>C 版：<c>((int)(v*255)) &amp; 0xFF</c>。</summary>
    private static uint PackColor(float a, float r, float g, float b)
        => ((uint)(byte)(int)a << 24) | ((uint)(byte)(int)r << 16) | ((uint)(byte)(int)g << 8) | (byte)(int)b;
}
