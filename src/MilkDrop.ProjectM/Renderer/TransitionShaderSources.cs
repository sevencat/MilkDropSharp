using System.Reflection;

namespace MilkDrop.ProjectM.Renderer;

/// <summary>
/// 内置转场着色器源码（公共头 + 6 个 <c>mainImage</c> 实现 + 顶点着色器）。
/// </summary>
/// <remarks>
/// <para>
/// 对应 <c>Renderer/BuiltInTransitionsResources.hpp.in</c>（CMake 在构建期把
/// <c>Renderer/TransitionShaders/*</c> 读成字符串常量）。
/// </para>
/// <para>
/// 与 C++ 的差异：只走 GLES 分支，版本头固定为
/// <c>#version 300 es</c> + <c>precision mediump float/sampler3D</c>（见 <c>TransitionShaderManager.cpp:35</c>）。
/// </para>
/// </remarks>
public static class TransitionShaderSources
{
    private const string ResourcePrefix = "MilkDrop.ProjectM.Renderer.TransitionShaders.";

    /// <summary>GLES 3.0 版本头（含精度限定符）。</summary>
    public const string VersionHeader = "#version 300 es\n\nprecision mediump float;\nprecision mediump sampler3D;\n";

    /// <summary>转场公共头（<c>iChannel0/1</c>、噪声纹理、<c>mainImage</c> 之外的 uniform）。</summary>
    public static string Header => Read("TransitionShaderHeaderGlsl330.frag");

    /// <summary>把 <c>mainImage</c> 接到 <c>FragColor</c> 的尾巴。</summary>
    public static string Main => Read("TransitionShaderMainGlsl330.frag");

    /// <summary>转场顶点着色器（不含版本头）。</summary>
    public static string VertexShader => Read("TransitionVertexShaderGlsl330.vert");

    /// <summary>圆形显隐。</summary>
    public static string Circle => Read("TransitionShaderBuiltInCircleGlsl330.frag");

    /// <summary>等离子体。</summary>
    public static string Plasma => Read("TransitionShaderBuiltInPlasmaGlsl330.frag");

    /// <summary>简单混合。</summary>
    public static string SimpleBlend => Read("TransitionShaderBuiltInSimpleBlendGlsl330.frag");

    /// <summary>扫过。</summary>
    public static string Sweep => Read("TransitionShaderBuiltInSweepGlsl330.frag");

    /// <summary>扭曲。</summary>
    public static string Warp => Read("TransitionShaderBuiltInWarpGlsl330.frag");

    /// <summary>缩放模糊。</summary>
    public static string ZoomBlur => Read("TransitionShaderBuiltInZoomBlurGlsl330.frag");

    private static string Read(string fileName)
    {
        Assembly assembly = typeof(TransitionShaderSources).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourcePrefix + fileName);

        if (stream is null)
        {
            throw new InvalidOperationException("Missing transition shader resource: " + fileName);
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
