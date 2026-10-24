using MilkDrop.Rendering.Gles3;
using Silk.NET.OpenGLES;

using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ShaderToy.Renderer;

/// <summary>
/// mipmap 生成。
/// </summary>
/// <remarks>
/// <para>
/// 上游在 Vulkan 里没有 <c>glGenerateMipmap</c> 这种便利，只能逐级 blit（9 级、<c>MAX_MIP = 9</c>、
/// <c>max_lod = 8</c>，见 <c>viewport.cpp</c> 的 <c>build_mips</c>）。GLES3 有 <c>glGenerateMipmap</c>，
/// 它会自己补出 level 1..<c>TEXTURE_MAX_LEVEL</c>，所以这里一行就够，效果与网页端一致（box filter）。
/// </para>
/// <para>
/// 必须显式把 <c>TEXTURE_MAX_LEVEL</c> 设成 8：默认上限是 1000，采样 <c>textureLod(..., 8.0)</c> 时
/// 驱动可能去补不存在的层级。
/// </para>
/// </remarks>
public static class MipmapHelper
{
    /// <summary>最高 mip 级索引（0..8 共 9 级）。</summary>
    public const int MaxLevel = 8;

    /// <summary>为纹理生成 0..8 级 mipmap。</summary>
    public static void Generate(Texture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);

        if (texture.Empty)
        {
            return;
        }

        var gl = GlContext.Gl;
        gl.BindTexture(texture.Target, texture.TextureId);
        gl.TexParameter(texture.Target, GLEnum.TextureMaxLevel, MaxLevel);
        gl.GenerateMipmap(texture.Target);
        gl.BindTexture(texture.Target, 0);
    }
}
