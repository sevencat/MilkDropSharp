using System.Numerics;
using MilkDrop.Classic.Render;

namespace MilkDrop.Classic.Visualizer.Sprites;

/// <summary>
/// 一个 sprite 的绘制参数，全部来自它的 EEL 输出变量（<c>milkdropfs.cpp:3542-3556</c>）。
/// </summary>
/// <param name="X"><c>x</c>：0..1（0.5 = 屏幕中心）。</param>
/// <param name="Y"><c>y</c>：0..1（0.5 = 屏幕中心）。</param>
/// <param name="Sx"><c>sx</c>：半宽，单位是"半个屏幕"。</param>
/// <param name="Sy"><c>sy</c>：半高，单位是"半个屏幕"。</param>
/// <param name="Rot"><c>rot</c>：弧度。</param>
/// <param name="FlipX"><c>flipx</c> 非 0 时左右镜像。</param>
/// <param name="FlipY"><c>flipy</c> 非 0 时上下镜像。</param>
/// <param name="RepeatX"><c>repeatx</c>：UV 在 x 方向重复次数。</param>
/// <param name="RepeatY"><c>repeaty</c>：UV 在 y 方向重复次数。</param>
/// <param name="R"><c>r</c>：0..1。</param>
/// <param name="G"><c>g</c>：0..1。</param>
/// <param name="B"><c>b</c>：0..1。</param>
/// <param name="A"><c>a</c>：0..1。</param>
/// <param name="BlendMode"><c>blendmode</c>：0..4（见 <see cref="SpriteGeometry.BlendFor"/>）。</param>
/// <param name="ImageWidth">纹理原始像素宽（<c>img_w</c>）。</param>
/// <param name="ImageHeight">纹理原始像素高（<c>img_h</c>）。</param>
/// <param name="BurnInAspect">是否为"烧进反馈纹理"那一遍（多一次 4:3 宽高比修正）。</param>
public readonly record struct SpriteQuadParams(
    double X,
    double Y,
    double Sx,
    double Sy,
    double Rot,
    bool FlipX,
    bool FlipY,
    double RepeatX,
    double RepeatY,
    double R,
    double G,
    double B,
    double A,
    int BlendMode,
    int ImageWidth,
    int ImageHeight,
    bool BurnInAspect = false);

/// <summary>
/// <c>CPlugin::DrawUserSprites</c> 里"算出四个角"的那段（<c>milkdropfs.cpp:3542-3640</c>）的纯数学移植。
/// </summary>
/// <remarks>
/// 几何全部在<b>原版的 2D clip 空间</b>里算完再一次性换成像素：C++ 的正交矩阵是
/// <c>D3DXMatrixOrthoLH(2, -2, ...)</c>（<c>support.cpp:179</c>），所以它的 <b>y = -1 在屏幕顶部</b>、
/// x/y 是两个独立的线性轴。旋转必须在这个空间里做 —— 挪到像素空间做会因为宽高不等而变成剪切。
/// <para>
/// 三段宽高比修正的顺序不能动：先按图片自身宽高比、再旋转/平移、再按屏幕宽高比，
/// 最后（只有烧进反馈纹理那一遍）按 4:3 再修一次。顺序错了 sprite 会变形或位置飘。
/// </para>
/// </remarks>
public static class SpriteGeometry
{
    /// <summary>sprite 是 <c>D3DPT_TRIANGLESTRIP</c>，四个角。</summary>
    public const int VertexCount = 4;

    /// <summary>blendmode → 混合方式（<c>milkdropfs.cpp:3642-3728</c>）。</summary>
    public static BlendMode BlendFor(int blendMode) => blendMode switch
    {
        1 => BlendMode.Replace,      // decal：原版连 ALPHABLENDENABLE 都关掉
        2 => BlendMode.Additive,     // additive
        3 => BlendMode.SourceColor,  // srccolor
        _ => BlendMode.Alpha,        // 0 = alpha blend，4 = colorkey（也是 SRCALPHA/INVSRCALPHA）
    };

    /// <summary>blendmode → 第 0 级纹理的 alpha 来源。</summary>
    public static AlphaSource AlphaSourceFor(int blendMode)
        => blendMode == 4 ? AlphaSource.VertexTimesTexture : AlphaSource.Vertex;

    /// <summary>
    /// 生成四个角（像素空间 + UV）。调用方负责绑定纹理与混合状态。
    /// </summary>
    /// <param name="vertices">长度至少 <see cref="VertexCount"/>。</param>
    /// <param name="p">sprite 参数（未夹紧的原始值，本方法内部按原版夹紧）。</param>
    /// <param name="width">当前渲染目标宽。</param>
    /// <param name="height">当前渲染目标高。</param>
    public static void Build(Span<MilkVertex> vertices, in SpriteQuadParams p, int width, int height)
    {
        if (vertices.Length < VertexCount)
        {
            throw new ArgumentException($"sprite 需要 {VertexCount} 个顶点", nameof(vertices));
        }

        // ---- 1. 夹紧（milkdropfs.cpp:3542-3556）----
        var x = Math.Clamp((float)((p.X * 2.0) - 1.0), -1000f, 1000f);
        var y = Math.Clamp((float)((p.Y * 2.0) - 1.0), -1000f, 1000f);
        var sx = Math.Clamp((float)p.Sx, -1000f, 1000f);
        var sy = Math.Clamp((float)p.Sy, -1000f, 1000f);
        var rot = (float)p.Rot;
        var repeatX = Math.Clamp((float)p.RepeatX, 0.01f, 100f);
        var repeatY = Math.Clamp((float)p.RepeatY, 0.01f, 100f);
        var blendMode = Math.Clamp(p.BlendMode, 0, 4);
        var r = Math.Clamp((float)p.R, 0f, 1f);
        var g = Math.Clamp((float)p.G, 0f, 1f);
        var b = Math.Clamp((float)p.B, 0f, 1f);
        var a = Math.Clamp((float)p.A, 0f, 1f);

        Span<float> px = stackalloc float[VertexCount];
        Span<float> py = stackalloc float[VertexCount];

        var fx = p.FlipX ? 1 : 0;
        var fy = p.FlipY ? 1 : 0;

        // ---- 2. 四个角（milkdropfs.cpp:3558-3566）：翻转靠"把坐标换到另一个下标"实现 ----
        px[0 + fx] = -sx;
        px[1 - fx] = sx;
        px[2 + fx] = -sx;
        px[3 - fx] = sx;

        py[0 + (fy * 2)] = -sy;
        py[1 + (fy * 2)] = -sy;
        py[2 - (fy * 2)] = sy;
        py[3 - (fy * 2)] = sy;

        // ---- 3. 第一段宽高比：修正非 1:1 的图片（milkdropfs.cpp:3568-3576）----
        if (p.ImageWidth > 0 && p.ImageHeight > 0)
        {
            var imageAspect = p.ImageHeight / (float)p.ImageWidth;
            if (imageAspect < 1f)
            {
                for (var i = 0; i < VertexCount; i++)
                {
                    py[i] *= imageAspect;
                }
            }
            else
            {
                for (var i = 0; i < VertexCount; i++)
                {
                    px[i] /= imageAspect;
                }
            }
        }

        // ---- 4. 2D 旋转（milkdropfs.cpp:3578-3589）----
        {
            var cosRot = MathF.Cos(rot);
            var sinRot = MathF.Sin(rot);
            for (var i = 0; i < VertexCount; i++)
            {
                var x2 = (px[i] * cosRot) - (py[i] * sinRot);
                var y2 = (px[i] * sinRot) + (py[i] * cosRot);
                px[i] = x2;
                py[i] = y2;
            }
        }

        // ---- 5. 平移（milkdropfs.cpp:3591-3596）----
        for (var i = 0; i < VertexCount; i++)
        {
            px[i] += x;
            py[i] += y;
        }

        // ---- 6. 第二段宽高比：归一化到屏幕宽度（milkdropfs.cpp:3598-3606）----
        if (height > 0)
        {
            var screenAspect = width / (float)height;
            if (screenAspect > 1f)
            {
                for (var i = 0; i < VertexCount; i++)
                {
                    py[i] *= screenAspect;
                }
            }
            else
            {
                for (var i = 0; i < VertexCount; i++)
                {
                    px[i] /= screenAspect;
                }
            }
        }

        // ---- 7. 第三段宽高比：烧进反馈纹理那一遍按 4:3（milkdropfs.cpp:3608-3616）----
        if (p.BurnInAspect && height > 0)
        {
            var burnAspect = width / (height * (4f / 3f));
            if (burnAspect < 1f)
            {
                for (var i = 0; i < VertexCount; i++)
                {
                    px[i] *= burnAspect;
                }
            }
            else
            {
                for (var i = 0; i < VertexCount; i++)
                {
                    py[i] /= burnAspect;
                }
            }
        }

        // ---- 8. UV（milkdropfs.cpp:3621-3640）----
        // 原版 dtu/dtv 恒为 0.5（原本与 tex_w 相关，已被改成定值），再做 repeat。
        Span<float> tu = stackalloc float[VertexCount];
        Span<float> tv = stackalloc float[VertexCount];
        tu[0] = -0.5f;
        tu[1] = 0.5f;
        tu[2] = -0.5f;
        tu[3] = 0.5f;
        tv[0] = -0.5f;
        tv[1] = -0.5f;
        tv[2] = 0.5f;
        tv[3] = 0.5f;

        for (var i = 0; i < VertexCount; i++)
        {
            tu[i] = (tu[i] * repeatX) + 0.5f;
            tv[i] = (tv[i] * repeatY) + 0.5f;
        }

        // ---- 9. 顶点色（milkdropfs.cpp:3648-3728）----
        uint color;
        switch (blendMode)
        {
            case 1:  // decal：rgb 预乘 alpha，alpha 固定 1
            case 2:  // additive
                color = PackColor(r * a, g * a, b * a, 1f);
                break;
            case 3:  // srccolor：颜色不参与
                color = 0xFFFFFFFF;
                break;
            default: // 0 / 4
                color = PackColor(r, g, b, a);
                break;
        }

        // ---- 10. clip → 像素（y 向下：clip y=-1 是屏幕顶部）----
        for (var i = 0; i < VertexCount; i++)
        {
            vertices[i] = new MilkVertex(
                new Vector3(((px[i] * 0.5f) + 0.5f) * width, ((py[i] * 0.5f) + 0.5f) * height, 0f),
                color,
                new Vector2(tu[i], tv[i]));
        }
    }

    /// <summary>按 <c>0xAARRGGBB</c> 打包，分量范围 0..1（对应 <c>D3DCOLOR_RGBA_01</c>）。</summary>
    private static uint PackColor(float r, float g, float b, float a)
        => ((uint)ToByte(a) << 24) | ((uint)ToByte(r) << 16) | ((uint)ToByte(g) << 8) | ToByte(b);

    private static byte ToByte(float value) => (byte)Math.Clamp((int)(value * 255f), 0, 255);
}
