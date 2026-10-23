using MilkDrop.Classic.Render;
using StbImageSharp;

namespace MilkDrop.Classic.Visualizer.Sprites;

/// <summary>
/// 图片文件 → 可采样纹理（替代 <c>D3DXCreateTextureFromFileExW</c>，<c>texmgr.cpp:98-113</c>）。
/// </summary>
/// <remarks>
/// 原版通过 <c>ColorKey = 0xFF000000 | ck</c> 让 D3DX 把匹配颜色的像素 alpha 置 0。
/// 这里在托管侧做同样的事，但**只对本来就完全不透明的像素**生效：
/// 带真实 alpha 的 PNG 不应该被色键歪掉（这是与原版的唯一差异，见 cbplan）。
/// </remarks>
public static class SpriteImage
{
    /// <summary>解码并创建纹理；失败返回 null。</summary>
    /// <param name="backend">渲染后端。</param>
    /// <param name="path">图片路径（png/jpg/gif/bmp 等 STB 支持的全部格式）。</param>
    /// <param name="colorKey">0x00RRGGBB；0 = 黑色（与原版默认一致）。</param>
    public static ITexture? Load(IRenderBackend backend, string path, uint colorKey)
    {
        ImageResult image;
        using (var stream = File.OpenRead(path))
        {
            image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        }

        var width = image.Width;
        var height = image.Height;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var source = image.Data;
        var bgra = new byte[width * height * 4];

        var keyR = (byte)(colorKey & 0xFF);
        var keyG = (byte)((colorKey >> 8) & 0xFF);
        var keyB = (byte)((colorKey >> 16) & 0xFF);

        for (var i = 0; i < width * height; i++)
        {
            var o = i * 4;
            var r = source[o + 0];
            var g = source[o + 1];
            var b = source[o + 2];
            var a = source[o + 3];

            if (a == 255 && r == keyR && g == keyG && b == keyB)
            {
                a = 0;
            }

            // D3D9 的 A8R8G8B8 在内存里是 BGRA
            bgra[o + 0] = b;
            bgra[o + 1] = g;
            bgra[o + 2] = r;
            bgra[o + 3] = a;
        }

        return backend.CreateTexture(width, height, bgra);
    }
}
