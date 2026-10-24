using System.Numerics;
using MilkDrop.ShaderToy.Input;
using Xunit;

namespace MilkDrop.ShaderToy.Tests.Input;

/// <summary><see cref="MouseState"/> 的 <c>iMouse</c> 语义（网页端口径，不是上游那份）。</summary>
public sealed class MouseStateTests
{
    [Fact]
    public void InitialState_HasNegativeClickPosition()
    {
        var mouse = new MouseState();

        Assert.Equal(new Vector4(0f, 0f, -1f, -1f), mouse.Value);
        Assert.False(mouse.IsPressed);
    }

    [Fact]
    public void Move_WithoutPress_DoesNotUpdateXY()
    {
        var mouse = new MouseState();

        mouse.Move(new Vector2(10f, 20f));

        Assert.Equal(0f, mouse.Value.X);
        Assert.Equal(0f, mouse.Value.Y);
        Assert.Equal(new Vector2(10f, 20f), mouse.Position); // 位置还是记下来了
    }

    [Fact]
    public void Press_SetsBothCurrentAndClickPosition()
    {
        var mouse = new MouseState();

        mouse.Press(new Vector2(10f, 20f));

        Assert.Equal(new Vector4(10f, 20f, 10f, 20f), mouse.Value);
        Assert.True(mouse.IsPressed);
    }

    [Fact]
    public void Move_WhilePressed_UpdatesXYOnly()
    {
        var mouse = new MouseState();
        mouse.Press(new Vector2(10f, 20f));

        mouse.Move(new Vector2(30f, 40f));

        Assert.Equal(30f, mouse.Value.X);
        Assert.Equal(40f, mouse.Value.Y);
        Assert.Equal(10f, mouse.Value.Z); // 点击位置不动
        Assert.Equal(20f, mouse.Value.W);
    }

    [Fact]
    public void Release_NegatesClickPosition()
    {
        var mouse = new MouseState();
        mouse.Press(new Vector2(10f, 20f));
        mouse.Move(new Vector2(30f, 40f));

        mouse.Release();

        // z <= 0 表示"没在拖拽"，这是作品里判断鼠标状态的唯一依据
        Assert.Equal(new Vector4(30f, 40f, -10f, -20f), mouse.Value);
        Assert.False(mouse.IsPressed);
    }

    [Fact]
    public void Release_WithoutPress_IsNoOp()
    {
        var mouse = new MouseState();

        mouse.Release();

        Assert.Equal(new Vector4(0f, 0f, -1f, -1f), mouse.Value);
    }

    [Fact]
    public void Reset_ReturnsToInitialState()
    {
        var mouse = new MouseState();
        mouse.Press(new Vector2(5f, 6f));
        mouse.Release();

        mouse.Reset();

        Assert.Equal(new Vector4(0f, 0f, -1f, -1f), mouse.Value);
        Assert.Equal(Vector2.Zero, mouse.Position);
    }

    [Fact]
    public void FromWindowPoint_FlipsYAndScalesToFramebuffer()
    {
        // 窗口 1280x720、绘制缓冲 2560x1440（2 倍缩放）时的左上角点击
        var pixel = MouseState.FromWindowPoint(
            new Vector2(0f, 0f),
            new Vector2(1280f, 720f),
            new Vector2(2560f, 1440f));

        Assert.Equal(0f, pixel.X);
        Assert.Equal(1439f, pixel.Y); // 左上角在 gl_FragCoord 里是最高一行
    }

    [Fact]
    public void FromWindowPoint_BottomRowMapsToZero()
    {
        var pixel = MouseState.FromWindowPoint(
            new Vector2(0f, 719f),
            new Vector2(1280f, 720f),
            new Vector2(1280f, 720f));

        Assert.Equal(0f, pixel.Y);
    }
}
