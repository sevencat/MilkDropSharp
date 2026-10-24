namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>Milkdrop 预设相关的固定数量常量。</summary>
/// <remarks>对应 <c>MilkdropPreset/Constants.hpp:10-17</c>。</remarks>
internal static class Constants
{
    /// <summary>
    /// Q 变量个数（<c>q1</c>..<c>q64</c>）。
    /// </summary>
    /// <remarks>
    /// 原生 projectM 是 32 个（<c>Constants.hpp:11</c> 的 <c>Q_VAR_COUNT</c>），这里扩到 64：
    /// 复杂 mashup 预设经常把 q 变量复用得很紧，多出来的 32 个是纯增量（<c>q1..q32</c> 语义不变）。
    /// 代价只有每帧多传 8 个 <c>float4</c> uniform（32 个 float）与多 32 行 <c>#define</c>。
    /// 全量语料（9795 个预设）里没有一个用到 <c>q33</c> 以上，所以不存在命名冲突。
    /// 若要继续加到 128，需要把 shader 头的 bank 命名改成两位（<c>_qaa</c> 之类），目前不做。
    /// </remarks>
    public const int QVarCount = 64;

    /// <summary>T 变量个数。</summary>
    public const int TVarCount = 8;

    /// <summary>自定义波形个数（表达式驱动）。</summary>
    public const int CustomWaveformCount = 4;

    /// <summary>自定义形状个数（表达式驱动）。</summary>
    public const int CustomShapeCount = 4;

    /// <summary>波形最大顶点数。</summary>
    public const int WaveformMaxPoints = 512;
}
