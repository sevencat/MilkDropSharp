using MilkDrop.Classic.Render;
using MilkDrop.Classic.Visualizer.Sprites;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 锁死 <c>DrawUserSprites</c> 的四个角（<c>milkdropfs.cpp:3542-3640</c>）：
/// 夹紧、翻转、三段宽高比、repeat 的顺序与符号都是"调好的"，改一处画面就会歪。
/// </summary>
public sealed class SpriteGeometryTests
{
    private static SpriteQuadParams DefaultParams(
        double x = 0.5,
        double y = 0.5,
        double sx = 1.0,
        double sy = 1.0,
        int imageWidth = 100,
        int imageHeight = 100,
        int blendMode = 0,
        double repeatX = 1.0,
        double repeatY = 1.0,
        bool burnInAspect = false)
        => new(
            x, y, sx, sy,
            Rot: 0,
            FlipX: false,
            FlipY: false,
            RepeatX: repeatX,
            RepeatY: repeatY,
            R: 1, G: 1, B: 1, A: 1,
            BlendMode: blendMode,
            ImageWidth: imageWidth,
            ImageHeight: imageHeight,
            BurnInAspect: burnInAspect);

    private static MilkVertex[] Build(in SpriteQuadParams p, int width = 1000, int height = 1000)
    {
        var vertices = new MilkVertex[SpriteGeometry.VertexCount];
        SpriteGeometry.Build(vertices, p, width, height);
        return vertices;
    }

    [Fact]
    public void SquareSpriteCoversScreen()
    {
        // 1000x1000、1:1 图片、sx=sy=1 → 正好铺满
        var v = Build(DefaultParams());

        Assert.Equal(0f, v[0].Position.X, 3);
        Assert.Equal(0f, v[0].Position.Y, 3);
        Assert.Equal(1000f, v[1].Position.X, 3);
        Assert.Equal(0f, v[1].Position.Y, 3);
        Assert.Equal(0f, v[2].Position.X, 3);
        Assert.Equal(1000f, v[2].Position.Y, 3);
        Assert.Equal(1000f, v[3].Position.X, 3);
        Assert.Equal(1000f, v[3].Position.Y, 3);

        // UV：0 号是左上、2 号是左下
        Assert.Equal(0f, v[0].TexCoord.X, 5);
        Assert.Equal(0f, v[0].TexCoord.Y, 5);
        Assert.Equal(1f, v[1].TexCoord.X, 5);
        Assert.Equal(1f, v[2].TexCoord.Y, 5);
        Assert.Equal(1f, v[3].TexCoord.Y, 5);
    }

    [Fact]
    public void WideImageIsSquashedVertically()
    {
        // 2:1 的图片：宽铺满，高只有一半（1000 宽 → 400 高，屏幕上居中）
        var v = Build(DefaultParams(imageWidth: 200, imageHeight: 100));

        Assert.Equal(1000f, v[3].Position.X - v[0].Position.X, 3);
        Assert.Equal(500f, v[2].Position.Y - v[0].Position.Y, 3);
        Assert.Equal(250f, v[0].Position.Y, 3);
        Assert.Equal(750f, v[2].Position.Y, 3);
    }

    [Fact]
    public void ScreenAspectKeepsSpriteSquareOnWideScreens()
    {
        // 800x600 屏幕上的 1:1 图片：clip 空间里 y 要乘 aspect(1.333) 才是屏幕上的正方形
        var v = Build(DefaultParams(), width: 800, height: 600);

        Assert.Equal(800f, v[3].Position.X - v[0].Position.X, 3);
        Assert.Equal(800f, v[2].Position.Y - v[0].Position.Y, 3);
        Assert.Equal(-100f, v[0].Position.Y, 3);
        Assert.Equal(700f, v[3].Position.Y, 3);
    }

    [Fact]
    public void FlipSwapsCornersButNotUvs()
    {
        var flipped = Build(DefaultParams() with { FlipX = true, FlipY = true });

        // x 翻转：0 号跑到右边；y 翻转：0 号跑到下边
        Assert.Equal(1000f, flipped[0].Position.X, 3);
        Assert.Equal(1000f, flipped[0].Position.Y, 3);
        Assert.Equal(0f, flipped[1].Position.X, 3);
        Assert.Equal(1000f, flipped[2].Position.X, 3);
        Assert.Equal(0f, flipped[2].Position.Y, 3);
        Assert.Equal(0f, flipped[3].Position.X, 3);

        // UV 仍按顶点下标绑定 —— 正是这一点让画面被镜像
        Assert.Equal(0f, flipped[0].TexCoord.X, 5);
        Assert.Equal(0f, flipped[0].TexCoord.Y, 5);
    }

    [Fact]
    public void PositionIsInZeroToOneSpace()
    {
        // x=0.25,y=0.75（数学空间）→ 屏幕左上角偏移
        var v = Build(DefaultParams(x: 0.25, y: 0.75, sx: 0.25, sy: 0.25));

        // 中心 = (250, 750)，半宽 125、半高 125
        Assert.Equal(125f, v[0].Position.X, 3);
        Assert.Equal(625f, v[0].Position.Y, 3);
        Assert.Equal(375f, v[1].Position.X, 3);
        Assert.Equal(875f, v[2].Position.Y, 3);
    }

    [Fact]
    public void RepeatScalesUvAroundCenter()
    {
        var v = Build(DefaultParams(repeatX: 2.0, repeatY: 4.0));

        Assert.Equal(-0.5f, v[0].TexCoord.X, 5);
        Assert.Equal(1.5f, v[1].TexCoord.X, 5);
        Assert.Equal(-1.5f, v[0].TexCoord.Y, 5);
        Assert.Equal(2.5f, v[3].TexCoord.Y, 5);
    }

    [Fact]
    public void RepeatIsClampedToSafeRange()
    {
        // 原版把 repeat 夹在 0.01..100（milkdropfs.cpp:3549-3550）
        var v = Build(DefaultParams(repeatX: 0.0, repeatY: 1000.0));

        Assert.Equal(0.495f, v[0].TexCoord.X, 5);
        Assert.Equal(50.5f, v[3].TexCoord.Y, 5);
    }

    [Fact]
    public void ScaleIsClampedToThousand()
    {
        var v = Build(DefaultParams(sx: 1e6, sy: -1e6));

        // sx 夹到 1000 → 半宽 = 1000 * 500 = 500000；sy 夹到 -1000（负号保留，画面上下翻）
        Assert.Equal(-499500f, v[0].Position.X, 1);
        Assert.Equal(500500f, v[1].Position.X, 1);
        Assert.Equal(-499500f, v[2].Position.Y, 1);
    }

    [Fact]
    public void AdditiveBlendPremultipliesAlpha()
    {
        var v = Build(DefaultParams(blendMode: 2) with { R = 1.0, G = 0.5, B = 0.0, A = 0.5 });

        Assert.Equal(255, (int)(v[0].Color >> 24));
        Assert.InRange((int)((v[0].Color >> 16) & 0xFF), 126, 128); // r*a = 0.5
        Assert.InRange((int)((v[0].Color >> 8) & 0xFF), 62, 64);    // g*a = 0.25
        Assert.Equal(0, (int)(v[0].Color & 0xFF));
    }

    [Fact]
    public void SourceColorBlendIgnoresVertexColour()
    {
        var v = Build(DefaultParams(blendMode: 3) with { R = 0.1, G = 0.2, B = 0.3, A = 0.4 });
        Assert.Equal(0xFFFFFFFFu, v[0].Color);
    }

    [Fact]
    public void DecalAndAlphaBlendUseDifferentAlpha()
    {
        var decal = Build(DefaultParams(blendMode: 1) with { A = 0.5 });
        var alpha = Build(DefaultParams(blendMode: 0) with { A = 0.5 });

        Assert.Equal(255, (int)(decal[0].Color >> 24));
        Assert.InRange((int)(alpha[0].Color >> 24), 126, 128);
    }

    [Fact]
    public void BurnInPassShrinksToFourByThree()
    {
        // 1000x1000 → 4:3 修正 ratio = 0.75，x 方向压到 75%
        var normal = Build(DefaultParams());
        var burnIn = Build(DefaultParams(burnInAspect: true));

        Assert.Equal(0f, normal[0].Position.X, 3);
        Assert.Equal(125f, burnIn[0].Position.X, 3);
        Assert.Equal(875f, burnIn[1].Position.X, 3);
        Assert.Equal(normal[0].Position.Y, burnIn[0].Position.Y, 3);
    }

    [Theory]
    [InlineData(0, BlendMode.Alpha)]
    [InlineData(1, BlendMode.Replace)]
    [InlineData(2, BlendMode.Additive)]
    [InlineData(3, BlendMode.SourceColor)]
    [InlineData(4, BlendMode.Alpha)]
    public void BlendModeMapping(int mode, BlendMode expected)
        => Assert.Equal(expected, SpriteGeometry.BlendFor(mode));

    [Fact]
    public void OnlyColorKeyModeUsesTextureAlpha()
    {
        Assert.Equal(AlphaSource.Vertex, SpriteGeometry.AlphaSourceFor(0));
        Assert.Equal(AlphaSource.VertexTimesTexture, SpriteGeometry.AlphaSourceFor(4));
    }
}
