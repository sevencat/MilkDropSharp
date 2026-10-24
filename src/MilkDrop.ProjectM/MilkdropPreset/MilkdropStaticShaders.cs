using System.Reflection;
using HlslParser;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 静态 Milkdrop GLSL 着色器（含预设公共头）的单例持有者。
/// </summary>
/// <remarks>
/// <para>对应 <c>MilkdropPreset/MilkdropStaticShaders.hpp.in</c> 与 <c>MilkdropStaticShaders.cpp.in</c>。</para>
/// <para>
/// C++ 侧资源由 CMake（<c>GenerateStaticShaders.cmake</c>）在构建期读成字符串常量；C# 侧以
/// <c>EmbeddedResource</c> 打包，文件名与 C++ 的 <c>Shaders/*</c> 一一对应。
/// </para>
/// <para>
/// 与 C++ 的差异：C++ 构造时查询 <c>GL_SHADING_LANGUAGE_VERSION</c>，再按 <c>USE_GLES</c> 决定
/// <c>#version 330</c> 还是 <c>#version 300 es</c>；本移植只走 GLES 3.0 分支（plan D1），
/// 因此版本头与 <c>GLSLGenerator::Version</c> 固定为 300 es。
/// </para>
/// <para>
/// 当前打包了预设头、两个预设顶点 / 一个默认 warp 片元着色器、模糊链的 3 个着色器，
/// 以及 motion vectors 顶点着色器与 "无纹理 / 有纹理" 两个通用绘制着色器。
/// </para>
/// </remarks>
public sealed class MilkdropStaticShaders
{
    private const string ResourcePrefix = "MilkDrop.ProjectM.MilkdropPreset.Shaders.";

    private static readonly Lazy<MilkdropStaticShaders> LazyInstance =
        new(() => new MilkdropStaticShaders(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _cache =
        new(StringComparer.Ordinal);

    private MilkdropStaticShaders()
    {
        VersionHeader = "#version 300 es";
        GlslGeneratorVersion = GLSLGenerator.Version.Version_300_ES;
    }

    /// <summary>返回单例（对应 <c>MilkdropStaticShaders::Get()</c>）。</summary>
    /// <remarks>
    /// 用 <see cref="Lazy{T}"/> 而不是 <c>??=</c>：并发首次访问可能建出两个实例，
    /// 而下面那个普通 <see cref="Dictionary{TKey,TValue}"/> 缓存被多线程同时写会抛
    /// <see cref="InvalidOperationException"/>（或读出半成品），表现为随机的 "HLSL parsing failed"。
    /// </remarks>
    public static MilkdropStaticShaders Get() => LazyInstance.Value;

    /// <summary>拼在 <c>.vert</c>/<c>.frag</c> 前的版本行。</summary>
    public string VersionHeader { get; }

    /// <summary>传给 hlslparser 生成器的目标版本。</summary>
    public GLSLGenerator.Version GlslGeneratorVersion { get; }

    /// <summary>
    /// 预设着色器公共头（uniform 群 + <c>q1..q32</c>/<c>time</c> 等宏）。
    /// </summary>
    /// <remarks>
    /// 对应 <c>GetPresetShaderHeader()</c>：<c>.inc</c> 不加版本头（CMake 里的
    /// <c>DECLARE_SHADER_ACCESSOR_NO_HEADER</c>），因为它要作为 HLSL 与预设代码一起交给转译器。
    /// </remarks>
    public string GetPresetShaderHeader() => ReadResource("PresetShaderHeaderGlsl330.inc", addVersionHeader: false);

    /// <summary>warp 预设的顶点着色器（对应 <c>GetPresetWarpVertexShader()</c>）。</summary>
    public string GetPresetWarpVertexShader() => ReadResource("PresetWarpVertexShaderGlsl330.vert", addVersionHeader: true);

    /// <summary>
    /// 默认 warp 片元着色器（对应 <c>GetPresetWarpFragmentShader()</c>）：
    /// 预设没有 warp shader 时，用它与 <see cref="GetPresetWarpVertexShader"/> 组成固定管线。
    /// </summary>
    public string GetPresetWarpFragmentShader() => ReadResource("PresetWarpFragmentShaderGlsl330.frag", addVersionHeader: true);

    /// <summary>composite 预设的顶点着色器（对应 <c>GetPresetCompVertexShader()</c>）。</summary>
    public string GetPresetCompVertexShader() => ReadResource("PresetCompVertexShaderGlsl330.vert", addVersionHeader: true);

    /// <summary>模糊链的顶点着色器（对应 <c>GetBlurVertexShader()</c>）。</summary>
    public string GetBlurVertexShader() => ReadResource("BlurVertexShaderGlsl330.vert", addVersionHeader: true);

    /// <summary>模糊链水平 pass 的片元着色器（对应 <c>GetBlur1FragmentShader()</c>）。</summary>
    public string GetBlur1FragmentShader() => ReadResource("Blur1FragmentShaderGlsl330.frag", addVersionHeader: true);

    /// <summary>模糊链垂直 pass 的片元着色器（对应 <c>GetBlur2FragmentShader()</c>）。</summary>
    public string GetBlur2FragmentShader() => ReadResource("Blur2FragmentShaderGlsl330.frag", addVersionHeader: true);

    /// <summary>运动矢量线的顶点着色器（对应 <c>GetPresetMotionVectorsVertexShader()</c>）。</summary>
    public string GetPresetMotionVectorsVertexShader()
        => ReadResource("PresetMotionVectorsVertexShaderGlsl330.vert", addVersionHeader: true);

    /// <summary>不贴图的通用顶点着色器（对应 <c>GetUntexturedDrawVertexShader()</c>）。</summary>
    public string GetUntexturedDrawVertexShader()
        => ReadResource("UntexturedDrawVertexShaderGlsl330.vert", addVersionHeader: true);

    /// <summary>不贴图的通用片元着色器（对应 <c>GetUntexturedDrawFragmentShader()</c>）。</summary>
    public string GetUntexturedDrawFragmentShader()
        => ReadResource("UntexturedDrawFragmentShaderGlsl330.frag", addVersionHeader: true);

    /// <summary>贴图的通用顶点着色器（对应 <c>GetTexturedDrawVertexShader()</c>）。</summary>
    public string GetTexturedDrawVertexShader()
        => ReadResource("TexturedDrawVertexShaderGlsl330.vert", addVersionHeader: true);

    /// <summary>贴图的通用片元着色器（对应 <c>GetTexturedDrawFragmentShader()</c>）。</summary>
    public string GetTexturedDrawFragmentShader()
        => ReadResource("TexturedDrawFragmentShaderGlsl330.frag", addVersionHeader: true);

    private string ReadResource(string fileName, bool addVersionHeader)
    {
        if (_cache.TryGetValue(fileName, out string? cached))
        {
            return cached;
        }

        Assembly assembly = typeof(MilkdropStaticShaders).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourcePrefix + fileName)
                              ?? throw new InvalidOperationException("找不到内嵌着色器资源: " + ResourcePrefix + fileName);

        using var reader = new StreamReader(stream);
        // 源文件是 LF；这里再兜底一次，保证与 C++ 侧 R"(...)" 的字节内容一致。
        string text = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);

        if (addVersionHeader)
        {
            text = VersionHeader + "\n" + text;
        }

        _cache[fileName] = text;
        return text;
    }
}
