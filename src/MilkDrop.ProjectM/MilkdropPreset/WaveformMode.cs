namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 主波形的绘制模式（对应 <c>MilkdropPreset/WaveformMode.hpp</c>），数值即 <c>nWaveMode</c>。
/// </summary>
public enum WaveformMode
{
    /// <summary>圆形波形。</summary>
    Circle = 0,

    /// <summary>随时间绕圈的 X-Y 振荡螺旋。</summary>
    XYOscillationSpiral,

    /// <summary>居中的 spiro（alpha 恒定）。</summary>
    CenteredSpiro,

    /// <summary>居中的 spiro（alpha 跟随音量）。</summary>
    CenteredSpiroVolume,

    /// <summary>水平"脚本线"，左声道。</summary>
    DerivativeLine,

    /// <summary>爆炸式复合图案。</summary>
    ExplosiveHash,

    /// <summary>可调角度的左声道直线，带时间对齐。</summary>
    Line,

    /// <summary>同 Line，但双声道。</summary>
    DoubleLine,

    /// <summary>同 Line，但用频谱数据。</summary>
    SpectrumLine,

    /// <summary>Milkdrop2077 追加波形：同 Line，振幅 ×4。</summary>
    Milkdrop2077Wave9,

    /// <summary>Milkdrop2077 追加波形：X 形。</summary>
    Milkdrop2077WaveX,

    /// <summary>Milkdrop2077 追加波形：双线。</summary>
    Milkdrop2077Wave11,

    /// <summary>Milkdrop2077 追加波形：斜切形。</summary>
    Milkdrop2077WaveSkewed,

    /// <summary>Milkdrop2077 追加波形：星形。</summary>
    Milkdrop2077WaveStar,

    /// <summary>Milkdrop2077 追加波形：花形。</summary>
    Milkdrop2077WaveFlower,

    /// <summary>Milkdrop2077 追加波形：套索形。</summary>
    Milkdrop2077WaveLasso,

    /// <summary>模式总数，用来对预设里的 <c>nWaveMode</c> 取模。</summary>
    Count,
}
