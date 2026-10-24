using Silk.NET.OpenGLES;
using StbImageWriteSharp;

namespace MilkDrop.Rendering.Gles3;

/// <summary>把当前帧缓冲（FBO 0）读回来存成 PNG。</summary>
public static class Screenshot
{
    /// <summary>把默认帧缓冲的内容存成 PNG。</summary>
    /// <param name="gl">GL 实例。</param>
    /// <param name="width">宽（像素）。</param>
    /// <param name="height">高（像素）。</param>
    /// <param name="path">输出路径（目录不存在会自动创建）。</param>
    /// <returns>实际写入的绝对路径。</returns>
    public static unsafe string Save(GL gl, int width, int height, string path)
    {
        ArgumentNullException.ThrowIfNull(gl);

        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var pixels = new byte[width * height * 4];

        // Framebuffer.Unbind() 只解绑 draw（与 C++ 一致），read 还指着上一个离屏目标。
        // 那个目标可能是 RGBA16F —— 用 GL_UNSIGNED_BYTE 读它会直接 GL_INVALID_OPERATION 并返回全 0。
        gl.BindFramebuffer(GLEnum.ReadFramebuffer, 0);

        fixed (byte* pointer = pixels)
        {
            // 默认帧缓冲的行宽不一定是 4 字节对齐，先关掉 pack 对齐。
            gl.PixelStore(GLEnum.PackAlignment, 1);
            gl.ReadPixels(0, 0, (uint)width, (uint)height, GLEnum.Rgba, GLEnum.UnsignedByte, pointer);
        }

        // GL 原点在左下，PNG 是自顶向下，逐行翻转。
        var flipped = new byte[pixels.Length];
        var stride = width * 4;

        for (var y = 0; y < height; y++)
        {
            System.Buffer.BlockCopy(pixels, (height - 1 - y) * stride, flipped, y * stride, stride);
        }

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (FileStream stream = File.Create(fullPath))
        {
            fixed (byte* pointer = flipped)
            {
                var writer = new ImageWriter();
                writer.WritePng(pointer, width, height, ColorComponents.RedGreenBlueAlpha, stream);
            }
        }

        return fullPath;
    }
}
