using MilkDrop.ProjectM.Audio;

namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>
/// 直线类波形（Line/DoubleLine/SpectrumLine/Milkdrop2077Wave9/X/11）的公共部分：
/// 把一条超出屏幕的线裁剪到 <c>[-1.1, 1.1]</c> 内，并算出逐样本的步进向量。
/// </summary>
/// <remarks>对应 <c>MilkdropPreset/Waveforms/LineBase.hpp</c> 与 <c>LineBase.cpp</c>。</remarks>
public abstract class LineBase : WaveformMath
{
    /// <summary>波形起始样本偏移；小于 480 时只画波形中段。</summary>
    protected int SampleOffset;

    /// <summary>波形左边缘偏移。</summary>
    protected float EdgeX;

    /// <summary>波形上边缘偏移。</summary>
    protected float EdgeY;

    /// <summary>波形 X 方向步长（拉伸）。</summary>
    protected float DistanceX;

    /// <summary>波形 Y 方向步长。</summary>
    protected float DistanceY;

    /// <summary>垂直于走向的 X 分量。</summary>
    protected float PerpetualDX;

    /// <summary>垂直于走向的 Y 分量。</summary>
    protected float PerpetualDY;

    /// <summary>
    /// 按角度算出波形首尾并裁剪到画面内，再求逐样本增量（对应 <c>LineBase::ClipWaveformEdges</c>）。
    /// </summary>
    protected void ClipWaveformEdges(float angle)
    {
        DistanceX = MathF.Cos(angle);
        DistanceY = MathF.Sin(angle);

        var edgeX = new[]
        {
            WaveX * MathF.Cos(angle + 1.57f) - DistanceX * 3.0f,
            WaveX * MathF.Cos(angle + 1.57f) + DistanceX * 3.0f,
        };

        var edgeY = new[]
        {
            WaveX * MathF.Sin(angle + 1.57f) - DistanceY * 3.0f,
            WaveX * MathF.Sin(angle + 1.57f) + DistanceY * 3.0f,
        };

        for (var i = 0; i < 2; i++)
        {
            for (var j = 0; j < 4; j++)
            {
                var t = 0.0f;
                var clip = false;

                switch (j)
                {
                    case 0:
                        if (edgeX[i] > 1.1f)
                        {
                            t = (1.1f - edgeX[1 - i]) / (edgeX[i] - edgeX[1 - i]);
                            clip = true;
                        }

                        break;

                    case 1:
                        if (edgeX[i] < -1.1f)
                        {
                            t = (-1.1f - edgeX[1 - i]) / (edgeX[i] - edgeX[1 - i]);
                            clip = true;
                        }

                        break;

                    case 2:
                        if (edgeY[i] > 1.1f)
                        {
                            t = (1.1f - edgeY[1 - i]) / (edgeY[i] - edgeY[1 - i]);
                            clip = true;
                        }

                        break;

                    case 3:
                        if (edgeY[i] < -1.1f)
                        {
                            t = (-1.1f - edgeY[1 - i]) / (edgeY[i] - edgeY[1 - i]);
                            clip = true;
                        }

                        break;
                }

                if (!clip)
                {
                    continue;
                }

                float diffX = edgeX[i] - edgeX[1 - i];
                float diffY = edgeY[i] - edgeY[1 - i];
                edgeX[i] = edgeX[1 - i] + diffX * t;
                edgeY[i] = edgeY[1 - i] + diffY * t;
            }
        }

        SampleOffset = (AudioConstants.WaveformSamples - Samples) / 2;

        DistanceX = (edgeX[1] - edgeX[0]) / Samples;
        DistanceY = (edgeY[1] - edgeY[0]) / Samples;

        EdgeX = edgeX[0];
        EdgeY = edgeY[0];

        float angle2 = MathF.Atan2(DistanceY, DistanceX);
        PerpetualDX = MathF.Cos(angle2 + 1.57f);
        PerpetualDY = MathF.Sin(angle2 + 1.57f);
    }
}
