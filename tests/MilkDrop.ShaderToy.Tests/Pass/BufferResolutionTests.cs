using MilkDrop.ShaderToy.Pass;
using Xunit;

namespace MilkDrop.ShaderToy.Tests.Pass;

/// <summary><see cref="BufferResolution"/> 的三种模式（对应网页端的 viewport / fixed / scale）。</summary>
public sealed class BufferResolutionTests
{
    [Fact]
    public void Viewport_FollowsViewportSize()
    {
        var resolution = new BufferResolution();

        Assert.Equal((1280, 720), resolution.Compute(1280, 720));
        Assert.Equal("viewport", resolution.Describe());
    }

    [Fact]
    public void Fixed_IgnoresViewport()
    {
        var resolution = new BufferResolution { Mode = BufferResolutionMode.Fixed };

        // Shadertoy 网页端 buffer 的默认尺寸
        Assert.Equal((1024, 256), resolution.Compute(1920, 1080));
        Assert.Equal("fixed 1024x256", resolution.Describe());
    }

    [Fact]
    public void Scale_MultipliesViewportAndRounds()
    {
        var resolution = new BufferResolution { Mode = BufferResolutionMode.Scale, Scale = 0.5f };

        Assert.Equal((640, 360), resolution.Compute(1280, 720));
        Assert.Equal((256, 144), resolution.Compute(512, 288));
        Assert.Equal("scale 0.5x", resolution.Describe());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-10, -10)]
    public void Compute_NeverReturnsZeroOrNegative(int width, int height)
    {
        foreach (BufferResolutionMode mode in Enum.GetValues<BufferResolutionMode>())
        {
            var resolution = new BufferResolution { Mode = mode };
            (int w, int h) = resolution.Compute(width, height);

            Assert.True(w >= 1);
            Assert.True(h >= 1);
        }
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var resolution = new BufferResolution { Mode = BufferResolutionMode.Fixed, FixedWidth = 320 };
        BufferResolution clone = resolution.Clone();
        clone.FixedWidth = 640;

        Assert.Equal(320, resolution.FixedWidth);
        Assert.Equal(BufferResolutionMode.Fixed, clone.Mode);
    }
}
