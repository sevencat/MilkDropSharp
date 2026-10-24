using Silk.NET.OpenGLES;
using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ShaderToy.Tests.Rendering;

/// <summary>
/// 把一张离屏纹理拷进 RGBA8 FBO 再回读像素 —— 与真实"上屏"路径一致，比读 RGBA16F 稳。
/// </summary>
internal static class GlReadback
{
    public static byte[] Read(OffscreenGlFixture fixture, Texture? texture, int width, int height)
    {
        var gl = fixture.Gl;

        using var screen = new MilkDrop.Rendering.Gles3.Framebuffer(1);
        screen.CreateColorAttachment(0, 0);
        screen.SetSize(width, height);

        using var copy = new MilkDrop.Rendering.Gles3.CopyTexture();
        screen.Bind(0);
        gl.Viewport(0, 0, (uint)width, (uint)height);

        if (texture is not null)
        {
            copy.Draw(texture);
        }

        var pixels = new byte[width * height * 4];
        gl.ReadPixels(0, 0, (uint)width, (uint)height, GLEnum.Rgba, GLEnum.UnsignedByte, pixels);

        MilkDrop.Rendering.Gles3.Framebuffer.Unbind();
        return pixels;
    }

    public static byte Channel(byte[] pixels, int width, int x, int y, int channel) =>
        pixels[((y * width) + x) * 4 + channel];

    public static byte Red(byte[] pixels, int width, int x, int y) => Channel(pixels, width, x, y, 0);

    public static byte Green(byte[] pixels, int width, int x, int y) => Channel(pixels, width, x, y, 1);

    public static byte Blue(byte[] pixels, int width, int x, int y) => Channel(pixels, width, x, y, 2);

    public static byte Alpha(byte[] pixels, int width, int x, int y) => Channel(pixels, width, x, y, 3);
}
