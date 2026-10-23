using System.Numerics;
using MilkDrop.Classic.Render;

namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary>
/// comp shader 用的全屏网格（对应 plugin.cpp:2041-2118 的 <c>m_comp_verts</c>）。
/// </summary>
/// <remarks>
/// 原版是 32×24 的网格，并在中心线放了一圈冗余顶点、只为让 <c>ang</c> 插值干净。
/// 这里先用规则网格（33×25 个顶点）覆盖整屏；<c>uv</c>/<c>rad/ang</c>/<c>hue</c> 都对得上，
/// 中心线的 ang 修正留到以后需要时再补。
/// </remarks>
public static class CompMesh
{
    public const int GridX = 32;
    public const int GridY = 24;

    public static int VertexCount => (GridX + 1) * (GridY + 1);

    /// <summary>
    /// 生成 comp 网格顶点（像素空间 + 左上原点）。
    /// </summary>
    /// <param name="vertices">长度至少 <see cref="VertexCount"/>。</param>
    /// <param name="width">目标宽（像素）。</param>
    /// <param name="height">目标高（像素）。</param>
    /// <param name="aspectX">压缩后的 X 比例（长边为 1）。</param>
    /// <param name="aspectY">压缩后的 Y 比例。</param>
    /// <param name="time">动画时间（给 hue 用）。</param>
    /// <param name="randStart">插件级随机相位（对应 <c>m_fRandStart</c>）。</param>
    public static void Build(
        Span<MilkVertex> vertices,
        int width,
        int height,
        float aspectX,
        float aspectY,
        float time,
        in Vector4 randStart)
    {
        var needed = VertexCount;
        if (vertices.Length < needed)
        {
            throw new ArgumentException($"comp 网格需要 {needed} 个顶点", nameof(vertices));
        }

        // 四角 hue（milkdropfs.cpp:4323-4339）
        Span<float> shade = stackalloc float[12];
        for (var i = 0; i < 4; i++)
        {
            shade[(i * 3) + 0] = 0.6f + (0.3f * MathF.Sin((time * 30.0f * 0.0143f) + 3 + (i * 21) + randStart.W));
            shade[(i * 3) + 1] = 0.6f + (0.3f * MathF.Sin((time * 30.0f * 0.0107f) + 1 + (i * 13) + randStart.Y));
            shade[(i * 3) + 2] = 0.6f + (0.3f * MathF.Sin((time * 30.0f * 0.0129f) + 6 + (i * 9) + randStart.Z));

            var max = MathF.Max(shade[(i * 3) + 0], MathF.Max(shade[(i * 3) + 1], shade[(i * 3) + 2]));
            for (var k = 0; k < 3; k++)
            {
                var normalized = max > 0f ? shade[(i * 3) + k] / max : 1f;
                shade[(i * 3) + k] = 0.5f + (0.5f * normalized);
            }
        }

        var radScale = 1f / MathF.Sqrt((aspectX * aspectX) + (aspectY * aspectY));

        var index = 0;
        for (var y = 0; y <= GridY; y++)
        {
            var v = y / (float)GridY;

            for (var x = 0; x <= GridX; x++)
            {
                var u = x / (float)GridX;

                var px = ((u * 2f) - 1f) * aspectX;
                var py = ((v * 2f) - 1f) * aspectY;
                var rad = MathF.Sqrt((px * px) + (py * py)) * radScale;
                var ang = MathF.Atan2(py, px);
                if (ang < 0f)
                {
                    ang += MathF.Tau;
                }

                // 原版 p->x*0.5+0.5 = u、p->y*0.5+0.5 = 1-v（milkdropfs.cpp:4347-4348）
                var hx = u;
                var hy = 1f - v;
                var r = (shade[0] * hx * hy) + (shade[3] * (1f - hx) * hy) +
                        (shade[6] * hx * (1f - hy)) + (shade[9] * (1f - hx) * (1f - hy));
                var g = (shade[1] * hx * hy) + (shade[4] * (1f - hx) * hy) +
                        (shade[7] * hx * (1f - hy)) + (shade[10] * (1f - hx) * (1f - hy));
                var b = (shade[2] * hx * hy) + (shade[5] * (1f - hx) * hy) +
                        (shade[8] * hx * (1f - hy)) + (shade[11] * (1f - hx) * (1f - hy));

                vertices[index++] = new MilkVertex(
                    new Vector3(u * width, v * height, 0f),
                    PackColor(r, g, b),
                    new Vector2(u, v),
                    new Vector2(u, v),
                    new Vector2(rad, ang));
            }
        }
    }

    private static uint PackColor(float r, float g, float b)
        => 0xFF000000u
           | ((uint)ToByte(r) << 16)
           | ((uint)ToByte(g) << 8)
           | ToByte(b);

    private static byte ToByte(float value) => (byte)Math.Clamp((int)(value * 255f), 0, 255);
}
