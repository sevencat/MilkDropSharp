using MilkDrop.Classic.Visualizer.Preset;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 锁死固定管线合成（<c>ShowToUser_NoShaders</c>）里 video echo 的 UV 与 gamma 重绘权重。
/// 这两段是"调好的魔法数"（阈值 0.0001 / 0.001 还不一样），改错不会有编译错误，只会画面变暗或镜像。
/// </summary>
public sealed class CompFallbackTests
{
    [Fact]
    public void NormalLayerCoversWholeTexture()
    {
        var uv = CompFallback.EchoUv(1f, 0, echoLayer: false);

        Assert.Equal(0f, uv.U0);
        Assert.Equal(0f, uv.V0);
        Assert.Equal(1f, uv.U1);
        Assert.Equal(1f, uv.V1);
    }

    [Fact]
    public void EchoLayerSamplesCentreForZoomTwo()
    {
        var uv = CompFallback.EchoUv(2f, 0, echoLayer: true);

        Assert.Equal(0.25f, uv.U0, 5);
        Assert.Equal(0.25f, uv.V0, 5);
        Assert.Equal(0.75f, uv.U1, 5);
        Assert.Equal(0.75f, uv.V1, 5);
    }

    [Fact]
    public void ZoomBelowOneSamplesOutsideTexture()
    {
        // echo_zoom=0.5 → UV 范围 -0.5..1.5（靠 wrap 采样，原版不夹取）
        var uv = CompFallback.EchoUv(0.5f, 0, echoLayer: true);

        Assert.Equal(-0.5f, uv.U0, 5);
        Assert.Equal(1.5f, uv.U1, 5);
    }

    [Theory]
    [InlineData(1, true, false)]   // 奇数：水平镜像
    [InlineData(2, false, true)]   // >=2：垂直镜像
    [InlineData(3, true, true)]    // 两者都有
    [InlineData(0, false, false)]
    [InlineData(4, false, true)]   // 调用方会先 %4；这里验证不取模时按 C 语义：4 % 2 == 0、4 >= 2
    [InlineData(-1, true, false)]  // C 的负数取模：-1 % 2 == -1
    public void OrientationFlipsUv(int orientation, bool flipU, bool flipV)
    {
        var uv = CompFallback.EchoUv(2f, orientation, echoLayer: true);

        // 翻转靠 lo + hi == 1 把结果留在 [0.25, 0.75] 内
        Assert.Equal(flipU ? 0.75f : 0.25f, uv.U0, 5);
        Assert.Equal(flipU ? 0.25f : 0.75f, uv.U1, 5);
        Assert.Equal(flipV ? 0.75f : 0.25f, uv.V0, 5);
        Assert.Equal(flipV ? 0.25f : 0.75f, uv.V1, 5);
    }

    [Fact]
    public void OrientationDoesNotAffectNormalLayer()
    {
        var a = CompFallback.EchoUv(1f, 0, echoLayer: false);
        var b = CompFallback.EchoUv(1f, 3, echoLayer: false);

        Assert.Equal(a, b);
    }

    [Fact]
    public void GammaWeightsForEchoLayer()
    {
        Span<float> weights = stackalloc float[CompFallback.MaxGammaRedraws];

        // gamma=2：echo 分支只多画 1 次、权重 1.0（总亮度 2 倍）
        var n = CompFallback.GammaWeights(2f, echoLayer: true, weights);
        Assert.Equal(1, n);
        Assert.Equal(1f, weights[0], 5);

        // gamma=2.5：1.0 + 0.5
        n = CompFallback.GammaWeights(2.5f, echoLayer: true, weights);
        Assert.Equal(2, n);
        Assert.Equal(1f, weights[0], 5);
        Assert.Equal(0.5f, weights[1], 5);

        // gamma<=1：不重绘（画面本来就是 1 倍）
        Assert.Equal(0, CompFallback.GammaWeights(1f, echoLayer: true, weights));
        Assert.Equal(0, CompFallback.GammaWeights(0.5f, echoLayer: true, weights));
    }

    [Fact]
    public void GammaWeightsWithoutEcho()
    {
        Span<float> weights = stackalloc float[CompFallback.MaxGammaRedraws];

        // 无 echo 分支的门槛是 0.001 且 +1：gamma=2 有 2 遍（1 + 1）
        var n = CompFallback.GammaWeights(2f, echoLayer: false, weights);
        Assert.Equal(2, n);
        Assert.Equal(1f, weights[0], 5);
        Assert.Equal(1f, weights[1], 5);

        // gamma=2.5 → 1 + 1 + 0.5
        n = CompFallback.GammaWeights(2.5f, echoLayer: false, weights);
        Assert.Equal(3, n);
        Assert.Equal(1f, weights[0], 5);
        Assert.Equal(1f, weights[1], 5);
        Assert.Equal(0.5f, weights[2], 5);

        // gamma<1：只画一遍，用 gamma 本身当亮度
        n = CompFallback.GammaWeights(0.5f, echoLayer: false, weights);
        Assert.Equal(1, n);
        Assert.Equal(0.5f, weights[0], 5);

        // gamma=1：正好一遍全亮
        n = CompFallback.GammaWeights(1f, echoLayer: false, weights);
        Assert.Equal(1, n);
        Assert.Equal(1f, weights[0], 5);
    }

    [Fact]
    public void GammaWeightsAreClampedToBuffer()
    {
        Span<float> small = stackalloc float[2];
        var n = CompFallback.GammaWeights(10f, echoLayer: false, small);

        Assert.Equal(2, n);
        Assert.Equal(9f, small[1], 5);   // 截断后最后一遍拿走剩余亮度，避免"变暗"

        Span<float> big = stackalloc float[CompFallback.MaxGammaRedraws + 8];
        Assert.Equal(CompFallback.MaxGammaRedraws, CompFallback.GammaWeights(1000f, echoLayer: false, big));
    }
}
