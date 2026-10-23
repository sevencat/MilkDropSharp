namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>预设着色器用到的最大主纹理模糊级别。</summary>
/// <remarks>
/// 对应 <c>MilkdropPreset/BlurTexture.hpp:34-40</c> 的 <c>BlurTexture::BlurLevel</c>。
/// C++ 把它嵌在 <c>BlurTexture</c> 里，但 <c>MilkdropShader</c> 与 <c>BlurTexture</c> 都要用，
/// 且 <c>BlurTexture</c> 本身随 P4 才移植，所以先提成顶层枚举，P4 直接复用。
/// </remarks>
public enum BlurLevel
{
    /// <summary>不使用模糊纹理。</summary>
    None,

    /// <summary>第一级模糊（2 趟）。</summary>
    Blur1,

    /// <summary>第二级模糊（4 趟）。</summary>
    Blur2,

    /// <summary>第三级模糊（6 趟）。</summary>
    Blur3,
}
