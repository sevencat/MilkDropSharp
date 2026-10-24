using MilkDrop.Rendering.Gles3;
using Silk.NET.OpenGLES;
using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ShaderToy.Channels;

/// <summary>
/// 未绑定通道用的占位纹理。
/// </summary>
/// <remarks>
/// <para>
/// 必须按**声明的采样器类型**给占位：给 <c>samplerCube</c> 绑一张 2D 纹理是
/// <c>GL_INVALID_OPERATION</c>，而不是"采到黑色"。所以三种占位都要有。
/// </para>
/// <para>
/// 尺寸用 1x1（audio 除外），采样结果是 <c>(0,0,0,1)</c>，与 Shadertoy 网页端"空通道"一致。
/// </para>
/// </remarks>
public sealed class PlaceholderTextures : IDisposable
{
    public PlaceholderTextures(bool texture3DSupported)
    {
        Black2D = CreateBlack2D();
        BlackCube = CreateBlackCube();
        Black3D = texture3DSupported ? CreateBlack3D() : null;
    }

    /// <summary>1x1 黑色 2D 纹理。</summary>
    public Texture? Black2D { get; }

    /// <summary>1x1x6 黑色立方体贴图。</summary>
    public Texture? BlackCube { get; }

    /// <summary>1x1x1 黑色 3D 纹理（不支持 <c>sampler3D</c> 时为 null）。</summary>
    public Texture? Black3D { get; }

    public void Dispose()
    {
        Black2D?.Dispose();
        BlackCube?.Dispose();
        Black3D?.Dispose();
    }

    private static Texture CreateBlack2D(int width = 1, int height = 1, string name = "placeholder2d")
    {
        var texture = new Texture(name, width, height, (int)GLEnum.Rgba8, GLEnum.Rgba, GLEnum.UnsignedByte, false);

        var gl = GlContext.Gl;
        var black = new byte[width * height * 4];
        for (var i = 3; i < black.Length; i += 4)
        {
            black[i] = 255;
        }

        gl.BindTexture(GLEnum.Texture2D, texture.TextureId);
        gl.PixelStore(GLEnum.UnpackAlignment, 4);
        gl.TexSubImage2D(GLEnum.Texture2D, 0, 0, 0, (uint)width, (uint)height, GLEnum.Rgba, GLEnum.UnsignedByte, black);
        gl.BindTexture(GLEnum.Texture2D, 0);

        return texture;
    }

    private static Texture CreateBlackCube()
    {
        var gl = GlContext.Gl;
        uint id = gl.GenTexture();
        gl.BindTexture(GLEnum.TextureCubeMap, id);

        var black = new byte[] { 0, 0, 0, 255 };
        for (var face = 0; face < 6; face++)
        {
            gl.TexImage2D(
                (GLEnum)((int)GLEnum.TextureCubeMapPositiveX + face),
                0,
                (int)GLEnum.Rgba8,
                1,
                1,
                0,
                GLEnum.Rgba,
                GLEnum.UnsignedByte,
                black);
        }

        gl.TexParameter(GLEnum.TextureCubeMap, GLEnum.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(GLEnum.TextureCubeMap, GLEnum.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(GLEnum.TextureCubeMap, GLEnum.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(GLEnum.TextureCubeMap, GLEnum.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.BindTexture(GLEnum.TextureCubeMap, 0);

        return new Texture("placeholderCube", id, GLEnum.TextureCubeMap, 1, 1, false);
    }

    private static Texture CreateBlack3D()
    {
        var gl = GlContext.Gl;
        uint id = gl.GenTexture();
        gl.BindTexture(GLEnum.Texture3D, id);

        var black = new byte[] { 0, 0, 0, 255 };
        gl.TexImage3D(
            GLEnum.Texture3D,
            0,
            (int)GLEnum.Rgba8,
            1,
            1,
            1,
            0,
            GLEnum.Rgba,
            GLEnum.UnsignedByte,
            black);

        gl.TexParameter(GLEnum.Texture3D, GLEnum.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(GLEnum.Texture3D, GLEnum.TextureMagFilter, (int)GLEnum.Linear);
        gl.BindTexture(GLEnum.Texture3D, 0);

        return new Texture("placeholder3d", id, GLEnum.Texture3D, 1, 1, false);
    }
}
