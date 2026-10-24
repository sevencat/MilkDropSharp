using MilkDrop.Rendering.Gles3;
using Silk.NET.OpenGLES;
using StbImageSharp;
using Buffer = System.Buffer;
using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ShaderToy.Resources;

/// <summary>
/// 把图片文件解码成 GL 纹理。
/// </summary>
/// <remarks>
/// <para>
/// 上游用 stb_image：<c>stbi_set_flip_vertically_on_load(1)</c> + 强制 4 通道
/// （<c>viewport.cpp:731-748</c>）。这里用 StbImageSharp 做同样的事，vflip 自己翻行
/// （StbImageSharp 的 flip 是全局状态，多线程/多次加载容易互相干扰）。
/// </para>
/// <para>
/// 图片一律按 RGBA8 上传：Shadertoy 的作品仓库里都是 png/jpg，没有 HDR/exr 那种
/// （真遇到 exr 再按 <c>--hdr</c> 特判）。
/// </para>
/// </remarks>
public static class TextureLoader
{
    /// <summary>加载 2D 图片。<paramref name="vflip"/> 为 true 时上下翻转（Shadertoy 的 <c>vflip</c>）。</summary>
    /// <exception cref="IOException">文件读不出来。</exception>
    /// <exception cref="InvalidDataException">不是能解码的图片。</exception>
    public static Texture Load2D(string path, bool vflip, bool generateMipmaps = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] bytes = File.ReadAllBytes(path);

        ImageResult image;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha)
                ?? throw new InvalidDataException($"无法解码图片: {path}");
        }

        int width = image.Width;
        int height = image.Height;
        byte[] pixels = image.Data;

        if (vflip)
        {
            FlipRows(pixels, width, height);
        }

        var texture = new Texture(
            Path.GetFileNameWithoutExtension(path),
            width,
            height,
            (int)GLEnum.Rgba8,
            GLEnum.Rgba,
            GLEnum.UnsignedByte,
            isUserTexture: true);

        Upload2D(texture, width, height, pixels);

        if (generateMipmaps)
        {
            Renderer.MipmapHelper.Generate(texture);
        }

        return texture;
    }

    private static unsafe void Upload2D(Texture texture, int width, int height, byte[] pixels)
    {
        var gl = GlContext.Gl;
        gl.BindTexture(GLEnum.Texture2D, texture.TextureId);
        gl.PixelStore(GLEnum.UnpackAlignment, 4);

        fixed (byte* data = pixels)
        {
            gl.TexImage2D(
                GLEnum.Texture2D,
                0,
                (int)GLEnum.Rgba8,
                (uint)width,
                (uint)height,
                0,
                GLEnum.Rgba,
                GLEnum.UnsignedByte,
                data);
        }

        gl.BindTexture(GLEnum.Texture2D, 0);
    }

    /// <summary>原地按行翻转。</summary>
    internal static void FlipRows(byte[] pixels, int width, int height)
    {
        int stride = width * 4;
        var temp = new byte[stride];

        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            int topOffset = top * stride;
            int bottomOffset = bottom * stride;
            Buffer.BlockCopy(pixels, topOffset, temp, 0, stride);
            Buffer.BlockCopy(pixels, bottomOffset, pixels, topOffset, stride);
            Buffer.BlockCopy(temp, 0, pixels, bottomOffset, stride);
        }
    }
}
