using System.Runtime.InteropServices;
using Vortice.Direct3D9;

namespace MilkDrop.Classic.Render.D3D9;

/// <summary>
/// 后备缓冲截屏（存 32bpp BMP，不引入额外依赖）。
/// M4 起的 golden image 回归测试（cbplan.md §6.4）会用到它。
/// </summary>
public static class ScreenCapture
{
    /// <summary>把当前后备缓冲存成 BMP。必须在 Present 之前调用。</summary>
    public static bool TrySaveBackBuffer(IDirect3DDevice9 device, string path, out string? error)
    {
        try
        {
            using var backBuffer = device.GetRenderTarget(0);
            var desc = backBuffer.Description;

            using var surface = device.CreateOffscreenPlainSurface(desc.Width, desc.Height, desc.Format, Pool.SystemMemory);
            device.GetRenderTargetData(backBuffer, surface);

            var rect = surface.LockRect(LockFlags.ReadOnly);
            try
            {
                WriteBmp(path, (int)desc.Width, (int)desc.Height, rect);
            }
            finally
            {
                surface.UnlockRect();
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void WriteBmp(string path, int width, int height, LockedRectangle locked)
    {
        var rowBytes = width * 4;
        var imageSize = rowBytes * height;
        var fileSize = 14 + 40 + imageSize;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        // BITMAPFILEHEADER
        writer.Write((ushort)0x4D42); // 'BM'
        writer.Write(fileSize);
        writer.Write(0);
        writer.Write(14 + 40);

        // BITMAPINFOHEADER
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(imageSize);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        // D3D 表面是自上而下，BMP 期望自下而上
        var row = new byte[rowBytes];
        for (var y = height - 1; y >= 0; y--)
        {
            Marshal.Copy(locked.DataPointer + (y * locked.Pitch), row, 0, rowBytes);
            writer.Write(row);
        }
    }
}
