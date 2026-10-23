namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>Milkdrop 预设相关的固定数量常量。</summary>
/// <remarks>对应 <c>MilkdropPreset/Constants.hpp:10-17</c>。</remarks>
internal static class Constants
{
    /// <summary>Q 变量个数（<c>q1</c>..<c>q32</c>）。</summary>
    public const int QVarCount = 32;

    /// <summary>T 变量个数。</summary>
    public const int TVarCount = 8;

    /// <summary>自定义波形个数（表达式驱动）。</summary>
    public const int CustomWaveformCount = 4;

    /// <summary>自定义形状个数（表达式驱动）。</summary>
    public const int CustomShapeCount = 4;

    /// <summary>波形最大顶点数。</summary>
    public const int WaveformMaxPoints = 512;
}
