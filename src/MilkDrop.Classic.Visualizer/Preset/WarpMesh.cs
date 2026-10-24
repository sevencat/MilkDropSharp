using System.Numerics;
using MilkDrop.Classic.Render;

namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary>
/// warp 网格的参数（对应 <c>ComputeGridAlphaValues</c> 里从 <c>var_pf_*</c> 取出的那一组，milkdropfs.cpp:1728-1737）。
/// </summary>
/// <param name="Zoom">拉伸强度（&lt;1 收缩、&gt;1 放大）。</param>
/// <param name="ZoomExp">按半径二次调制 zoom 的指数。</param>
/// <param name="Rot">旋转角（弧度）。</param>
/// <param name="Warp">扭曲强度。</param>
/// <param name="CX">拉伸/旋转中心 X。</param>
/// <param name="CY">拉伸/旋转中心 Y。</param>
/// <param name="DX">平移 X。</param>
/// <param name="DY">平移 Y。</param>
/// <param name="SX">X 向拉伸分母。</param>
/// <param name="SY">Y 向拉伸分母。</param>
/// <param name="WarpTime">warp 噪声时间（<c>GetTime() * m_fWarpAnimSpeed</c>）。</param>
/// <param name="WarpScaleInv"><c>1 / m_fWarpScale.eval(now)</c>。</param>
/// <param name="TexelOffsetX">半像素对齐偏移。</param>
/// <param name="TexelOffsetY">半像素对齐偏移。</param>
public readonly record struct WarpParams(
    float Zoom,
    float ZoomExp,
    float Rot,
    float Warp,
    float CX,
    float CY,
    float DX,
    float DY,
    float SX,
    float SY,
    float WarpTime,
    float WarpScaleInv,
    float TexelOffsetX = 0f,
    float TexelOffsetY = 0f);

/// <summary>
/// warp 网格里"只跟网格坐标有关、跟运动量无关"的中间量缓存。
/// </summary>
/// <remarks>
/// 64×64 网格下 <c>rad</c>/<c>ang</c> 每帧要算 4225 次 <c>sqrt</c>+<c>atan2</c>，
/// 但只要 <c>(gridX, gridY, aspectX, aspectY)</c> 不变，结果就是同一张表 ——
/// 窗口尺寸和网格密度都不常变，所以缓存起来每帧只付一次数组读取。
/// </remarks>
public sealed class WarpMeshCache
{
    private int _gridX = -1;
    private int _gridY = -1;
    private float _aspectX;
    private float _aspectY;
    private float[] _rad = [];
    private float[] _ang = [];

    /// <summary>每个顶点的半径（压缩空间）。</summary>
    public ReadOnlySpan<float> Rad => _rad;

    /// <summary>每个顶点的极角（网格中心为 0）。</summary>
    public ReadOnlySpan<float> Ang => _ang;

    /// <summary>缓存是否已就绪（顶点数 = <c>(gridX+1)*(gridY+1)</c>）。</summary>
    public bool HasData(int gridX, int gridY)
        => _rad.Length == WarpMesh.VertexCount(gridX, gridY);

    /// <summary>网格参数没变就直接返回，变了才重算整张表。</summary>
    public void Ensure(int gridX, int gridY, float aspectX, float aspectY)
    {
        var count = WarpMesh.VertexCount(gridX, gridY);
        if (_rad.Length == count
            && _gridX == gridX
            && _gridY == gridY
            && _aspectX.Equals(aspectX)
            && _aspectY.Equals(aspectY))
        {
            return;
        }

        if (_rad.Length != count)
        {
            _rad = new float[count];
            _ang = new float[count];
        }

        _gridX = gridX;
        _gridY = gridY;
        _aspectX = aspectX;
        _aspectY = aspectY;

        var index = 0;
        for (var gy = 0; gy <= gridY; gy++)
        {
            var vy = (gy / (float)gridY * 2f) - 1f;
            var vyA = vy * aspectY;

            for (var gx = 0; gx <= gridX; gx++)
            {
                var vx = (gx / (float)gridX * 2f) - 1f;
                var vxA = vx * aspectX;

                _rad[index] = MathF.Sqrt((vxA * vxA) + (vyA * vyA));
                _ang[index] = (gx == gridX / 2 && gy == gridY / 2)
                    ? 0f
                    : MathF.Atan2(vyA, vxA);
                index++;
            }
        }
    }
}

/// <summary>
/// <c>ComputeGridAlphaValues</c> 的 UV 计算部分（milkdropfs.cpp:1698-1860）的移植。
/// </summary>
/// <remarks>
/// 注意几处容易写错的地方：
/// <list type="number">
///   <item>全流程都在"像素等比"空间里算（aspectX/Y 先把长边压到 1），最后再用
///   <c>(x-0.5)*invAspect+0.5</c> 还原，所以 zoom/rot 在屏幕上是各向同性的；</item>
///   <item><c>rad</c> 是<b>压缩后</b>空间的半径（可以用到 1.41 以上），
///   因此 <c>powf(zoomExp, rad*2-1)</c> 的指数可以 &gt;1；</item>
///   <item>warp 的四次三角函数相位/符号各不相同，抄错会变成"呼吸"而不是"流动"。</item>
/// </list>
/// </remarks>
public static class WarpMesh
{
    /// <summary>网格顶点数：(gridX+1)*(gridY+1)。</summary>
    public static int VertexCount(int gridX, int gridY) => (gridX + 1) * (gridY + 1);

    /// <summary>
    /// 生成一格一格的屏幕网格 + 已变形的 UV。
    /// </summary>
    /// <param name="vertices">长度至少 <see cref="VertexCount"/>；位置是像素空间。</param>
    /// <param name="color">顶点色（decay 就靠它的 RGB 乘到纹理上）。</param>
    /// <param name="perVertex">per-vertex 代码；非 null 时每个顶点先跑一遍再算 UV（milkdropfs.cpp:1750-1792）。</param>
    /// <param name="perFrame">per-vertex 代码的输入来源；<paramref name="perVertex"/> 有代码时必须给。</param>
    /// <param name="cache">
    /// <c>rad</c>/<c>ang</c> 缓存；传 null 时每次重算（等价、但慢），只用于回归测试。
    /// </param>
    public static void Build(
        Span<MilkVertex> vertices,
        int gridX,
        int gridY,
        float width,
        float height,
        float aspectX,
        float aspectY,
        float invAspectX,
        float invAspectY,
        in WarpParams p,
        uint color = 0xFFFFFFFF,
        PerVertexVariables? perVertex = null,
        PerFrameVariables? perFrame = null,
        WarpMeshCache? cache = null)
    {
        var needed = VertexCount(gridX, gridY);
        if (vertices.Length < needed)
        {
            throw new ArgumentException($"网格需要 {needed} 个顶点", nameof(vertices));
        }

        if (gridX < 1 || gridY < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(gridX), "网格至少要 1x1");
        }

        // 四个低频噪声项（milkdropfs.cpp:1702-1705）：
        // 常数 11.68/8.77/10.54/11.49 与各自的频率、相位都是"调好的"，不要改。
        var warpTime = p.WarpTime;
        var f0 = 11.68f + (4.0f * MathF.Cos((warpTime * 1.413f) + 10));
        var f1 = 8.77f + (3.0f * MathF.Cos((warpTime * 1.113f) + 7));
        var f2 = 10.54f + (3.0f * MathF.Cos((warpTime * 1.233f) + 3));
        var f3 = 11.49f + (4.0f * MathF.Cos((warpTime * 0.933f) + 5));

        var hasPerVertex = perVertex is { Code: not null };
        if (hasPerVertex && perFrame is null)
        {
            throw new ArgumentNullException(nameof(perFrame), "per-vertex 代码需要 per-frame VM 作为输入来源");
        }
        var scaleInv = p.WarpScaleInv;
        var warpAmp = p.Warp * 0.0035f;
        var baseCosRot = MathF.Cos(p.Rot);
        var baseSinRot = MathF.Sin(p.Rot);

        cache?.Ensure(gridX, gridY, aspectX, aspectY);
        var useTable = cache is not null && cache.HasData(gridX, gridY);
        var radTable = useTable ? cache!.Rad : ReadOnlySpan<float>.Empty;
        var angTable = useTable ? cache!.Ang : ReadOnlySpan<float>.Empty;

        // ---- warp 四项的相位递推准备 ----
        // 每一项都是 sin/cos(T_k ± scaleInv * (vx*f_i ± vy*f_j))，相位是 (vx,vy) 的线性函数。
        // 沿一行走时 vx 每次 +dx，相位增量固定，所以每行只在行首精确算一次 sin/cos，
        // 行内用旋转递推 sin(θ+Δ)=sinθ·cosΔ+cosθ·sinΔ 推进（乘加代替三角函数）。
        var dx = 2f / gridX;
        var t0 = warpTime * 0.333f;
        var t1 = warpTime * 0.375f;
        var t2 = warpTime * 0.753f;
        var t3 = warpTime * 0.825f;

        var d1 = (double)(scaleInv * dx * f0);
        var d2 = (double)(-scaleInv * dx * f2);
        var d3 = (double)(-scaleInv * dx * f1);
        var d4 = (double)(scaleInv * dx * f0);
        var cd1 = Math.Cos(d1);
        var sd1 = Math.Sin(d1);
        var cd2 = Math.Cos(d2);
        var sd2 = Math.Sin(d2);
        var cd3 = Math.Cos(d3);
        var sd3 = Math.Sin(d3);
        var cd4 = Math.Cos(d4);
        var sd4 = Math.Sin(d4);

        // 没有 per-vertex 时 zoomexp 每帧是常数，ln 只算一次
        var logZoomExpBase = hasPerVertex ? 0f : MathF.Log(p.ZoomExp);

        var index = 0;
        for (var gy = 0; gy <= gridY; gy++)
        {
            var vy = (gy / (float)gridY * 2f) - 1f;

            // 行首（gx=0，即 vx=-1）的四个相位，每行精确算一次
            var ph1 = t0 + (scaleInv * (-f0 - (vy * f3)));
            var ph2 = t1 - (scaleInv * (-f2 + (vy * f1)));
            var ph3 = t2 - (scaleInv * (-f1 - (vy * f2)));
            var ph4 = t3 + (scaleInv * (-f0 + (vy * f3)));
            var s1 = Math.Sin(ph1);
            var c1 = Math.Cos(ph1);
            var s2 = Math.Sin(ph2);
            var c2 = Math.Cos(ph2);
            var s3 = Math.Sin(ph3);
            var c3 = Math.Cos(ph3);
            var s4 = Math.Sin(ph4);
            var c4 = Math.Cos(ph4);

            for (var gx = 0; gx <= gridX; gx++)
            {
                var vx = (gx / (float)gridX * 2f) - 1f;

                // 半径与角度都在"像素等比"空间里算（plugin.cpp:2289-2293）
                var rad = useTable
                    ? radTable[index]
                    : MathF.Sqrt((vx * vx * aspectX * aspectX) + (vy * vy * aspectY * aspectY));
                var ang = useTable
                    ? angTable[index]
                    : (gx == gridX / 2 && gy == gridY / 2)
                        ? 0f
                        : MathF.Atan2(vy * aspectY, vx * aspectX);

                // 有 per-vertex 代码时，运动量由它逐顶点改写（不回写状态）
                var vp = hasPerVertex
                    ? perVertex!.Evaluate(p, perFrame!, vx, vy, rad, ang, aspectX, aspectY)
                    : p;

                var cosRot = baseCosRot;
                var sinRot = baseSinRot;

                if (hasPerVertex)
                {
                    cosRot = MathF.Cos(vp.Rot);
                    sinRot = MathF.Sin(vp.Rot);
                    warpAmp = vp.Warp * 0.0035f;
                }

                // 内置 zoom（按半径调制）：
                // zoomExp==1 时内层 pow(1, rad*2-1) 恒为 1，zoom==1 时外层也恒为 1 ——
                // 两个默认情况都能跳过一次 pow（64×64 网格下这是每帧 8000+ 次超越函数）。
                float zoom2;
                if (MathF.Abs(vp.ZoomExp - 1f) < 1e-6f)
                {
                    zoom2 = vp.Zoom;
                }
                else if (MathF.Abs(vp.Zoom - 1f) < 1e-6f)
                {
                    zoom2 = 1f;
                }
                else
                {
                    var logZoomExp = hasPerVertex ? MathF.Log(vp.ZoomExp) : logZoomExpBase;
                    zoom2 = MathF.Pow(vp.Zoom, MathF.Exp(((rad * 2f) - 1f) * logZoomExp));
                }

                var zoom2Inv = 1.0f / zoom2;

                var u = (vx * aspectX * 0.5f * zoom2Inv) + 0.5f;
                var v = (-vy * aspectY * 0.5f * zoom2Inv) + 0.5f;

                // 拉伸以 (cx,cy) 为中心
                u = ((u - vp.CX) / vp.SX) + vp.CX;
                v = ((v - vp.CY) / vp.SY) + vp.CY;

                // 扭曲：四次不同相位/符号的三角项（sin/cos 由行内递推给出）
                u += warpAmp * (float)s1;
                v += warpAmp * (float)c2;
                u += warpAmp * (float)c3;
                v += warpAmp * (float)s4;

                // 旋转
                var u2 = u - vp.CX;
                var v2 = v - vp.CY;
                u = (u2 * cosRot) - (v2 * sinRot) + vp.CX;
                v = (u2 * sinRot) + (v2 * cosRot) + vp.CY;

                // 平移
                u -= vp.DX;
                v -= vp.DY;

                // 还原宽高比
                u = ((u - 0.5f) * invAspectX) + 0.5f;
                v = ((v - 0.5f) * invAspectY) + 0.5f;

                // 半像素对齐
                u += p.TexelOffsetX;
                v += p.TexelOffsetY;

                vertices[index++] = new MilkVertex(
                    new Vector3((vx * 0.5f + 0.5f) * width, (vy * 0.5f + 0.5f) * height, 0f),
                    color,
                    new Vector2(u, v),
                    new Vector2((vx * 0.5f) + 0.5f + p.TexelOffsetX, (-vy * 0.5f) + 0.5f + p.TexelOffsetY),
                    new Vector2(rad, ang));

                // 相位推进到下一列（递推用 double，避免 65 步后误差可见）
                var n1 = (s1 * cd1) + (c1 * sd1);
                c1 = (c1 * cd1) - (s1 * sd1);
                s1 = n1;
                var n2 = (s2 * cd2) + (c2 * sd2);
                c2 = (c2 * cd2) - (s2 * sd2);
                s2 = n2;
                var n3 = (s3 * cd3) + (c3 * sd3);
                c3 = (c3 * cd3) - (s3 * sd3);
                s3 = n3;
                var n4 = (s4 * cd4) + (c4 * sd4);
                c4 = (c4 * cd4) - (s4 * sd4);
                s4 = n4;
            }
        }
    }

    /// <summary>
    /// 由预设状态生成 <see cref="WarpParams"/>：这些量都是 <c>eval(-1)</c> 的（不参与混合）。
    /// </summary>
    public static WarpParams FromState(PresetState state, float time, float texSizeX, float texSizeY)
    {
        var vars = state.PerFrame;

        return new WarpParams(
            Zoom: (float)vars["zoom"].Value,
            ZoomExp: (float)vars["zoomexp"].Value,
            Rot: (float)vars["rot"].Value,
            Warp: (float)vars["warp"].Value,
            CX: (float)vars["cx"].Value,
            CY: (float)vars["cy"].Value,
            DX: (float)vars["dx"].Value,
            DY: (float)vars["dy"].Value,
            SX: (float)vars["sx"].Value,
            SY: (float)vars["sy"].Value,
            WarpTime: time * state.WarpAnimSpeed,
            WarpScaleInv: 1.0f / state.WarpScale.Eval(time),
            TexelOffsetX: 0.5f / texSizeX,
            TexelOffsetY: 0.5f / texSizeY);
    }
}
