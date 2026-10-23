using System.Numerics;
using MilkDrop.Classic.Render;
using MilkDrop.Classic.Visualizer.Preset;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// motion vector 场（<c>DrawMotionVectors</c> / <c>ReversePropagatePoint</c>）的几何。
/// 这里用"恒等网格"当标尺：恒等网格下反查应还原出原坐标，线段长度应被拉到最小 1 像素。
/// </summary>
public sealed class MotionVectorsTests
{
    private const int Grid = 8;

    /// <summary>恒等 warp 网格：像素 (fx,fy) 采样到 (fx,fy)。</summary>
    private static MilkVertex[] IdentityMesh(int gridX, int gridY)
    {
        var mesh = new MilkVertex[(gridX + 1) * (gridY + 1)];
        for (var y = 0; y <= gridY; y++)
        {
            for (var x = 0; x <= gridX; x++)
            {
                var u = x / (float)gridX;
                var v = y / (float)gridY;
                mesh[(y * (gridX + 1)) + x] = new MilkVertex(Vector3.Zero, 0xFFFFFFFF, new Vector2(u, 1f - v));
            }
        }

        return mesh;
    }

    [Theory]
    [InlineData(0.25f, 0.25f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.73f, 0.11f)]
    public void IdentityMeshRoundTrips(float fx, float fy)
    {
        var mesh = IdentityMesh(Grid, Grid);

        Assert.True(MotionVectors.ReversePropagate(mesh, Grid, Grid, fx, fy, out var tx, out var ty));
        Assert.Equal(fx, tx, 5);
        Assert.Equal(fy, ty, 5);
    }

    [Fact]
    public void ShiftedMeshReportsSourcePosition()
    {
        // 网格整体往右偏 0.25：每个顶点的 tu 比恒等网格少 0.25
        var mesh = IdentityMesh(Grid, Grid);
        for (var i = 0; i < mesh.Length; i++)
        {
            mesh[i] = new MilkVertex(mesh[i].Position, mesh[i].Color, mesh[i].TexCoord - new Vector2(0.25f, 0f));
        }

        Assert.True(MotionVectors.ReversePropagate(mesh, Grid, Grid, 0.75f, 0.5f, out var tx, out var ty));
        Assert.Equal(0.5f, tx, 5);
        Assert.Equal(0.5f, ty, 5);
    }

    [Fact]
    public void SamplesOutsideGridAreRejected()
    {
        var mesh = IdentityMesh(Grid, Grid);

        Assert.False(MotionVectors.ReversePropagate(mesh, Grid, Grid, 1f, 0.5f, out _, out _));
        Assert.False(MotionVectors.ReversePropagate(mesh, Grid, Grid, 0.5f, 1f, out _, out _));

        // 注：fx = -0.01 不会被拒（C 的截断让 x0 仍是 0），只有整个格子出界才拒
        Assert.False(MotionVectors.ReversePropagate(mesh, Grid, Grid, -0.2f, 0.5f, out _, out _));
    }

    [Fact]
    public void IdentityMeshGivesMinimumLengthSegments()
    {
        var mesh = IdentityMesh(Grid, Grid);
        Span<MilkVertex> dst = stackalloc MilkVertex[MotionVectors.MaxColumns * 2];

        var count = MotionVectors.BuildRow(
            dst, mesh, Grid, Grid,
            row: 2, rows: 4, columns: 4,
            fracX: 0f, fracY: 0f, offsetX: 0f, offsetY: 0f,
            // 用正方形分辨率，让 uv 长度与像素长度同比例（原版 min_len = 1/texSizeX 本身就是近似）
            lengthScale: 1f, width: 640f, height: 640f, color: 0xFFFFFFFF);

        // 4 列里最后一列的 fx 正好是 1.0，被 0.9999 的边界挡掉 → 3 段
        Assert.Equal(6, count);

        // 恒等网格下位移恰好为 0 → 原版的 else 分支：dx = dy = min_len（一段 45° 的短线，长度 √2 像素）
        for (var i = 0; i < count; i += 2)
        {
            var start = dst[i].Position;
            var end = dst[i + 1].Position;
            Assert.Equal(1f, end.X - start.X, 2);
            Assert.Equal(1f, end.Y - start.Y, 2);
        }
    }

    [Fact]
    public void RowOutsideUnitRangeIsSkipped()
    {
        var mesh = IdentityMesh(Grid, Grid);
        Span<MilkVertex> dst = stackalloc MilkVertex[MotionVectors.MaxColumns * 2];

        // offsetY = 1 → fy ≈ -0.9，整行丢弃
        var count = MotionVectors.BuildRow(
            dst, mesh, Grid, Grid,
            row: 0, rows: 4, columns: 4,
            fracX: 0f, fracY: 0f, offsetX: 0f, offsetY: 1f,
            lengthScale: 1f, width: 640f, height: 480f, color: 0xFFFFFFFF);

        Assert.Equal(0, count);
    }

    [Fact]
    public void OffsetMovesSamplesHorizontally()
    {
        var mesh = IdentityMesh(Grid, Grid);
        Span<MilkVertex> dst = stackalloc MilkVertex[MotionVectors.MaxColumns * 2];

        var count = MotionVectors.BuildRow(
            dst, mesh, Grid, Grid,
            row: 2, rows: 4, columns: 2,
            fracX: 0f, fracY: 0f, offsetX: 0.25f, offsetY: 0f,
            lengthScale: 1f, width: 1f, height: 1f, color: 0xFFFFFFFF);

        // 第 1 列的 fx = 1.25/1.25 + 0.25 = 1.25，超出右边界被丢弃
        Assert.Equal(2, count);

        // 第 0 列：fx = 0.25/(2+0+0.25-1) + 0.25 = 0.2 + 0.25
        Assert.Equal(0.45f, dst[0].Position.X, 4);
    }

    [Fact]
    public void BufferLimitStopsWriting()
    {
        var mesh = IdentityMesh(Grid, Grid);
        Span<MilkVertex> small = stackalloc MilkVertex[4];

        var count = MotionVectors.BuildRow(
            small, mesh, Grid, Grid,
            row: 1, rows: 4, columns: 8,
            fracX: 0f, fracY: 0f, offsetX: 0f, offsetY: 0f,
            lengthScale: 1f, width: 100f, height: 100f, color: 0xFFFFFFFF);

        Assert.Equal(4, count);
    }
}
