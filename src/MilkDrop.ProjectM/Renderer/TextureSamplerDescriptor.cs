using System.Numerics;
using MilkDrop.ProjectM.Rendering;

namespace MilkDrop.ProjectM.Renderer;

/// <summary>
/// 纹理 + 采样器的组合，供 shader 里 <c>sampler_xxx</c> / <c>texsize_xxx</c> uniform 使用。
/// </summary>
/// <remarks>
/// <para>对应 <c>Renderer/TextureSamplerDescriptor.hpp:25-114</c> 与 <c>.cpp:10-154</c>。</para>
/// <para>
/// C++ 用 <c>weak_ptr</c> 存纹理，好让纹理管理器淘汰纹理后自动失效；C# 侧暂时直接持有引用，
/// <c>TryUpdate(TextureManager&amp;)</c> 等 P7 移植 <c>TextureManager</c> 时再补。
/// </para>
/// </remarks>
public sealed class TextureSamplerDescriptor
{
    public TextureSamplerDescriptor()
    {
        SamplerName = string.Empty;
        SizeName = string.Empty;
    }

    /// <param name="texture">纹理。</param>
    /// <param name="sampler">采样器。</param>
    /// <param name="samplerName">shader 里的原始采样器名，例如 <c>mytex_pw</c>。</param>
    /// <param name="sizeName">texsize uniform 名，例如 <c>mytex</c>。</param>
    public TextureSamplerDescriptor(Texture? texture, Sampler? sampler, string samplerName, string sizeName)
    {
        Texture = texture;
        Sampler = sampler;
        SamplerName = samplerName;
        SizeName = sizeName;
    }

    /// <summary>纹理（可能为 null / 空）。</summary>
    public Texture? Texture { get; set; }

    /// <summary>采样器。</summary>
    public Sampler? Sampler { get; set; }

    /// <summary>shader 里的采样器名（不含 <c>sampler_</c> 前缀）。</summary>
    public string SamplerName { get; }

    /// <summary>texsize uniform 名（不含 <c>texsize_</c> 前缀）。</summary>
    public string SizeName { get; }

    /// <summary>纹理与采样器都无效。</summary>
    public bool Empty() => Texture is null || Texture.Empty;

    /// <summary>把纹理 + 采样器绑到指定单元，并写入 <c>sampler_*</c>/<c>texsize_*</c> uniform。</summary>
    /// <param name="unit">纹理单元。</param>
    /// <param name="shader">已经绑定过的 shader。</param>
    public void Bind(int unit, Shader shader)
    {
        ArgumentNullException.ThrowIfNull(shader);

        if (Texture is null || Sampler is null)
        {
            return;
        }

        Texture.Bind(unit, Sampler);

        shader.SetUniformInt("sampler_" + SamplerName, unit);

        // 不同 wrap/filter 组合可能重复设置，但极少发生。
        var texSize = new Vector4(
            Texture.Width,
            Texture.Height,
            1f / Texture.Width,
            1f / Texture.Height);

        shader.SetUniformFloat4("texsize_" + SizeName, texSize);

        // 带前缀的随机纹理：额外提供短名 texsize_randNN。
        if (IsShortRandomName(SizeName))
        {
            shader.SetUniformFloat4("texsize_" + SizeName[..6], texSize);
        }
    }

    /// <summary>解绑指定单元上的纹理与采样器。</summary>
    public void Unbind(int unit)
    {
        Texture?.Unbind(unit);
        Sampler.Unbind((uint)unit);
    }

    /// <summary>shader 里需要的 sampler 声明。</summary>
    public string SamplerDeclaration()
    {
        if (Texture is null || Sampler is null)
        {
            return string.Empty;
        }

        var declaration = "uniform ";
        declaration += Texture.Target == Silk.NET.OpenGLES.GLEnum.Texture3D ? "sampler3D sampler_" : "sampler2D sampler_";
        declaration += SamplerName;
        declaration += ";\n";

        // 例如声明了 sampler_rand00_smalltiled 时，再补一个 sampler_rand00。
        if (IsShortRandomName(SamplerName))
        {
            declaration += "uniform sampler2D sampler_";
            declaration += SamplerName[..6];
            declaration += ";\n";
        }

        return declaration;
    }

    /// <summary>shader 里需要的 texsize 声明。</summary>
    public string TexSizeDeclaration()
    {
        if (Texture is null || Sampler is null || SizeName.Length == 0)
        {
            return string.Empty;
        }

        var declaration = "uniform float4 texsize_" + SizeName + ";\n";

        if (IsShortRandomName(SizeName))
        {
            declaration += "uniform float4 texsize_" + SizeName[..6] + ";\n";
        }

        return declaration;
    }

    /// <summary>
    /// 是否是"带前后缀的随机纹理短名"：<c>randNN_xxx</c>（原版判定 <c>substr(0,4)=="rand" &amp;&amp; len&gt;7 &amp;&amp; [6]=='_'</c>）。
    /// </summary>
    private static bool IsShortRandomName(string name)
        => name.Length > 7 && name.StartsWith("rand", StringComparison.Ordinal) && name[6] == '_';
}
