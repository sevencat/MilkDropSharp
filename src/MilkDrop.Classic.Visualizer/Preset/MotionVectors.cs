using System.Numerics;
using MilkDrop.Classic.Render;

namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary>
/// <c>CPlugin::DrawMotionVectors</c>（<c>milkdropfs.cpp:1156-1308</c>）的纯几何部分：
/// 用本帧的 warp 网格反查每个采样点"上一帧在哪里"，再画成一组独立线段。
/// </summary>
/// <remarks>
/// 这不是 shader 的输入（<c>include.fx</c> 里没有对应的 sampler），
/// 而是预设可控的可见图案：<c>mv_x/mv_y</c> 网格密度、<c>mv_dx/mv_dy</c> 平移、
/// <c>mv_l</c> 拖尾倍率、<c>mv_r/g/b/a</c> 颜色与总开关。
/// </remarks>
public static class MotionVectors
{
    /// <summary>原版的硬上限（<c>milkdropfs.cpp:1177-1178</c>）。</summary>
    public const int MaxColumns = 64;

    /// <summary>原版的硬上限（<c>milkdropfs.cpp:1177-1178</c>）。</summary>
    public const int MaxRows = 48;

    /// <summary>
    /// <c>CPlugin::ReversePropagatePoint</c>（<c>milkdropfs.cpp:1431-1466</c>）：
    /// 在 warp 网格上双线性插值 <c>tu/tv</c>，得到 (fx,fy) 这一像素在上一帧的采样位置。
    /// </summary>
    /// <param name="mesh">warp 网格顶点（行优先，每行 <paramref name="gridX"/> + 1 个）。</param>
    /// <returns>落到网格外时返回 false（原版同样跳过这个点）。</returns>
    public static bool ReversePropagate(
        ReadOnlySpan<MilkVertex> mesh,
        int gridX,
        int gridY,
        float fx,
        float fy,
        out float targetX,
        out float targetY)
    {
        targetX = 0f;
        targetY = 0f;

        var x0 = (int)(fx * gridX);
        var y0 = (int)(fy * gridY);
        var dx = (fx * gridX) - x0;
        var dy = (fy * gridY) - y0;

        var x1 = x0 + 1;
        var y1 = y0 + 1;

        if (x0 < 0 || y0 < 0 || x1 > gridX || y1 > gridY)
        {
            return false;
        }

        var stride = gridX + 1;
        var i00 = (y0 * stride) + x0;
        var i10 = (y0 * stride) + x1;
        var i01 = (y1 * stride) + x0;
        var i11 = (y1 * stride) + x1;
        if (i11 >= mesh.Length)
        {
            return false;
        }

        var w00 = (1f - dx) * (1f - dy);
        var w10 = dx * (1f - dy);
        var w01 = (1f - dx) * dy;
        var w11 = dx * dy;

        var tu = (mesh[i00].TexCoord.X * w00) + (mesh[i10].TexCoord.X * w10)
               + (mesh[i01].TexCoord.X * w01) + (mesh[i11].TexCoord.X * w11);
        var tv = (mesh[i00].TexCoord.Y * w00) + (mesh[i10].TexCoord.Y * w10)
               + (mesh[i01].TexCoord.Y * w01) + (mesh[i11].TexCoord.Y * w11);

        targetX = tu;
        targetY = 1f - tv;   // 纹理 v（0=顶）→ 2D 空间的 y（0=顶）
        return true;
    }

    /// <summary>
    /// 生成一行线段（<c>milkdropfs.cpp:1229-1303</c> 的循环体）。
    /// </summary>
    /// <param name="dst">输出顶点，每段 2 个顶点；容量至少 <c>columns * 2</c>。</param>
    /// <param name="rows">本帧行数（<c>mv_y</c> 的整数部分，已夹取）。</param>
    /// <param name="columns">本帧列数（<c>mv_x</c> 的整数部分，已夹取）。</param>
    /// <param name="fracX"><c>mv_x</c> 的小数部分（网格疏密过渡）。</param>
    /// <param name="fracY"><c>mv_y</c> 的小数部分。</param>
    /// <param name="offsetX"><c>mv_dx</c>。</param>
    /// <param name="offsetY"><c>mv_dy</c>。</param>
    /// <param name="lengthScale"><c>mv_l</c>。</param>
    /// <returns>写入的顶点数（偶数，0 表示这一行没有可画的点）。</returns>
    public static int BuildRow(
        Span<MilkVertex> dst,
        ReadOnlySpan<MilkVertex> mesh,
        int gridX,
        int gridY,
        int row,
        int rows,
        int columns,
        float fracX,
        float fracY,
        float offsetX,
        float offsetY,
        float lengthScale,
        float width,
        float height,
        uint color)
    {
        var fy = ((row + 0.25f) / (rows + fracY + 0.25f - 1f)) - offsetY;
        if (fy <= 0.0001f || fy >= 0.9999f)
        {
            return 0;
        }

        // 最短拖尾长度 = 1 个像素（原版 inv_texsize = 1/texSizeX，:1216-1217）
        var minLength = 1f / width;
        var n = 0;

        for (var x = 0; x < columns; x++)
        {
            var fx = ((x + 0.25f) / (columns + fracX + 0.25f - 1f)) + offsetX;
            if (fx <= 0.0001f || fx >= 0.9999f)
            {
                continue;
            }

            if (!ReversePropagate(mesh, gridX, gridY, fx, fy, out var fx2, out var fy2))
            {
                continue;
            }

            // 长度不足一个像素时沿原方向拉长（长度为 0 就给一个斜向小段，原版如此，:1256-1282）
            var dx = (fx2 - fx) * lengthScale;
            var dy = (fy2 - fy) * lengthScale;
            var length = MathF.Sqrt((dx * dx) + (dy * dy));

            if (length > minLength)
            {
                // 够长，原样使用
            }
            else if (length > 0.00000001f)
            {
                var scale = minLength / length;
                dx *= scale;
                dy *= scale;
            }
            else
            {
                dx = minLength;
                dy = minLength;
            }

            fx2 = fx + dx;
            fy2 = fy + dy;

            if (n + 2 > dst.Length)
            {
                break;
            }

            // 2D 空间的 (fx,fy)（0=左上）→ 像素坐标
            dst[n] = new MilkVertex(new Vector3(fx * width, fy * height, 0f), color, Vector2.Zero);
            dst[n + 1] = new MilkVertex(new Vector3(fx2 * width, fy2 * height, 0f), color, Vector2.Zero);
            n += 2;
        }

        return n;
    }
}
