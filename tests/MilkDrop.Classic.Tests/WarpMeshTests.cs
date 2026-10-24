using System.Numerics;
using MilkDrop.Classic.Render;
using MilkDrop.Classic.Visualizer.Preset;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// <see cref="WarpMesh.Build"/> 的数值等价性回归：
/// 优化后的版本（rad/ang 查表 + warp 相位递推 + pow 快路径）必须与
/// 下面这份"逐顶点 sqrt/atan2/pow + 8 次三角"的朴素实现给出同样的顶点。
/// </summary>
public class WarpMeshTests
{
    [Fact]
    public void Build_MatchesNaiveReference_WithoutCache() => CompareAll(null);

    [Fact]
    public void Build_MatchesNaiveReference_WithCache() => CompareAll(new WarpMeshCache());

    private static void CompareAll(WarpMeshCache? cache)
    {
        var cases = new (int GridX, int GridY, float AspectX, float AspectY)[]
        {
            (64, 64, 1f, 1f),
            (32, 24, 1f, 1f),
            (64, 48, 0.75f, 1f),
            (48, 64, 1f, 0.75f),
        };

        var times = new[] { 0f, 0.37f, 1.9f, 12.5f, 133.7f };

        foreach (var (gridX, gridY, aspectX, aspectY) in cases)
        {
            var invAspectX = aspectX >= 1f ? 1f : 1f / aspectX;
            var invAspectY = aspectY >= 1f ? 1f : 1f / aspectY;

            for (var ci = 0; ci < ParamCases.Length; ci++)
            {
                for (var ti = 0; ti < times.Length; ti++)
                {
                    var p = ParamCases[ci] with { WarpTime = times[ti] };
                    var count = WarpMesh.VertexCount(gridX, gridY);
                    var actual = new MilkVertex[count];
                    var expected = new MilkVertex[count];

                    WarpMesh.Build(actual, gridX, gridY, 1024f, 768f, aspectX, aspectY, invAspectX, invAspectY,
                        p, 0xFFFFFFFF, cache: cache);
                    BuildReference(expected, gridX, gridY, 1024f, 768f, aspectX, aspectY, invAspectX, invAspectY, p);

                    AssertVerticesEqual(expected, actual, gridX, gridY, ci, ti);
                }
            }
        }
    }

    /// <summary>覆盖 zoom/zoomexp 的快路径与非快路径、非零旋转与平移。</summary>
    private static readonly WarpParams[] ParamCases =
    [
        new(1f, 1f, 0f, 1f, 0.5f, 0.5f, 0f, 0f, 1f, 1f, 0f, 1f),                     // 全默认
        new(1f, 1f, 0f, 0f, 0.5f, 0.5f, 0f, 0f, 1f, 1f, 0f, 1f),                     // warp = 0
        new(1.15f, 1f, 0.6f, 2.5f, 0.4f, 0.6f, 0.1f, -0.2f, 1.1f, 0.9f, 3f, 0.7f),   // zoomexp=1 快路径
        new(1.15f, 1.4f, 0.6f, 2.5f, 0.4f, 0.6f, 0.1f, -0.2f, 1.1f, 0.9f, 3f, 0.7f), // zoomexp≠1 → pow
        new(0.85f, 0.6f, -1.2f, 0.3f, 0.55f, 0.45f, -0.3f, 0.25f, 0.8f, 1.25f, 9f, 2.4f),
        new(1f, 2.2f, 0.3f, 4f, 0.5f, 0.5f, 0f, 0f, 1f, 1f, 5f, 0.2f),               // zoom=1 → 外层 pow 快路径
    ];

    private static void BuildReference(
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
        uint color = 0xFFFFFFFF)
    {
        var warpTime = p.WarpTime;
        var f0 = 11.68f + (4.0f * MathF.Cos((warpTime * 1.413f) + 10));
        var f1 = 8.77f + (3.0f * MathF.Cos((warpTime * 1.113f) + 7));
        var f2 = 10.54f + (3.0f * MathF.Cos((warpTime * 1.233f) + 3));
        var f3 = 11.49f + (4.0f * MathF.Cos((warpTime * 0.933f) + 5));

        var scaleInv = p.WarpScaleInv;
        var warpAmp = p.Warp * 0.0035f;
        var baseCosRot = MathF.Cos(p.Rot);
        var baseSinRot = MathF.Sin(p.Rot);

        var index = 0;
        for (var gy = 0; gy <= gridY; gy++)
        {
            var vy = (gy / (float)gridY * 2f) - 1f;

            for (var gx = 0; gx <= gridX; gx++)
            {
                var vx = (gx / (float)gridX * 2f) - 1f;
                var rad = MathF.Sqrt((vx * vx * aspectX * aspectX) + (vy * vy * aspectY * aspectY));
                var ang = (gx == gridX / 2 && gy == gridY / 2)
                    ? 0f
                    : MathF.Atan2(vy * aspectY, vx * aspectX);

                var vp = p;
                var cosRot = baseCosRot;
                var sinRot = baseSinRot;

                var zoom2 = MathF.Pow(vp.Zoom, MathF.Pow(vp.ZoomExp, (rad * 2.0f) - 1.0f));
                var zoom2Inv = 1.0f / zoom2;

                var u = (vx * aspectX * 0.5f * zoom2Inv) + 0.5f;
                var v = (-vy * aspectY * 0.5f * zoom2Inv) + 0.5f;

                u = ((u - vp.CX) / vp.SX) + vp.CX;
                v = ((v - vp.CY) / vp.SY) + vp.CY;

                u += warpAmp * MathF.Sin((warpTime * 0.333f) + (scaleInv * ((vx * f0) - (vy * f3))));
                v += warpAmp * MathF.Cos((warpTime * 0.375f) - (scaleInv * ((vx * f2) + (vy * f1))));
                u += warpAmp * MathF.Cos((warpTime * 0.753f) - (scaleInv * ((vx * f1) - (vy * f2))));
                v += warpAmp * MathF.Sin((warpTime * 0.825f) + (scaleInv * ((vx * f0) + (vy * f3))));

                var u2 = u - vp.CX;
                var v2 = v - vp.CY;
                u = (u2 * cosRot) - (v2 * sinRot) + vp.CX;
                v = (u2 * sinRot) + (v2 * cosRot) + vp.CY;

                u -= vp.DX;
                v -= vp.DY;

                u = ((u - 0.5f) * invAspectX) + 0.5f;
                v = ((v - 0.5f) * invAspectY) + 0.5f;

                u += p.TexelOffsetX;
                v += p.TexelOffsetY;

                vertices[index++] = new MilkVertex(
                    new Vector3((vx * 0.5f + 0.5f) * width, (vy * 0.5f + 0.5f) * height, 0f),
                    color,
                    new Vector2(u, v),
                    new Vector2((vx * 0.5f) + 0.5f + p.TexelOffsetX, (-vy * 0.5f) + 0.5f + p.TexelOffsetY),
                    new Vector2(rad, ang));
            }
        }
    }

    private static void AssertVerticesEqual(
        MilkVertex[] expected,
        MilkVertex[] actual,
        int gridX,
        int gridY,
        int caseIndex,
        int timeIndex)
    {
        const float UvTolerance = 2e-4f;
        const float PosTolerance = 0.02f;

        for (var i = 0; i < expected.Length; i++)
        {
            var e = expected[i];
            var a = actual[i];

            Assert.True(
                MathF.Abs(e.Position.X - a.Position.X) < PosTolerance
                && MathF.Abs(e.Position.Y - a.Position.Y) < PosTolerance,
                $"grid {gridX}x{gridY} case {caseIndex} t {timeIndex} v{i}: pos {e.Position} vs {a.Position}");

            Assert.True(
                MathF.Abs(e.TexCoord.X - a.TexCoord.X) < UvTolerance
                && MathF.Abs(e.TexCoord.Y - a.TexCoord.Y) < UvTolerance,
                $"grid {gridX}x{gridY} case {caseIndex} t {timeIndex} v{i}: uv {e.TexCoord} vs {a.TexCoord}");

            Assert.True(
                MathF.Abs(e.UvOrig.X - a.UvOrig.X) < UvTolerance
                && MathF.Abs(e.UvOrig.Y - a.UvOrig.Y) < UvTolerance,
                $"grid {gridX}x{gridY} case {caseIndex} t {timeIndex} v{i}: uvOrig {e.UvOrig} vs {a.UvOrig}");

            Assert.True(
                MathF.Abs(e.RadAng.X - a.RadAng.X) < UvTolerance
                && MathF.Abs(e.RadAng.Y - a.RadAng.Y) < UvTolerance,
                $"grid {gridX}x{gridY} case {caseIndex} t {timeIndex} v{i}: radAng {e.RadAng} vs {a.RadAng}");
        }
    }
}
