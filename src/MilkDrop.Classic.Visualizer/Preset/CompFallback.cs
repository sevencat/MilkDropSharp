namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary>
/// 固定管线合成里一层 echo 的四角 UV（顶点顺序：左上、右上、左下、右下）。
/// </summary>
/// <remarks>
/// 名字用 <c>U0/U1</c> 而不是 left/right：<c>echo_orient</c> 奇数时 UV 是镜像的，
/// 此时 <c>U0 &gt; U1</c>，叫 "left" 会误导。
/// </remarks>
public readonly record struct EchoQuadUv(float U0, float V0, float U1, float V1);

/// <summary>
/// <c>ShowToUser_NoShaders</c>（<c>milkdropfs.cpp:3967-4287</c>）里两段纯算术：
/// video echo 的 UV 缩放/翻转，以及 gamma 的"多次加法重绘"权重。抽出来便于单测。
/// </summary>
public static class CompFallback
{
    /// <summary>gamma 重绘最多追加重绘多少次（原版无上限，这里只是防御异常值）。</summary>
    public const int MaxGammaRedraws = 64;

    /// <summary>
    /// 计算一层 echo 的 UV（<c>milkdropfs.cpp:4094-4117</c>）。
    /// </summary>
    /// <param name="zoom">
    /// <c>echo_zoom</c>；正常层传 1。不夹取 —— 原版同样不夹取（0 会得到无穷大，画面变黑）。
    /// </param>
    /// <param name="orientation"><c>echo_orient</c> 的整数部分（未取模，C 的 <c>%</c> 语义）。</param>
    /// <param name="echoLayer">true = 被缩放/翻转的那一层（C++ 的 <c>i==1</c>）。</param>
    public static EchoQuadUv EchoUv(float zoom, int orientation, bool echoLayer)
    {
        var lo = 0.5f - (0.5f / zoom);
        var hi = 0.5f + (0.5f / zoom);

        if (!echoLayer)
        {
            return new EchoQuadUv(lo, lo, hi, hi);
        }

        // 原版：orient % 2 → 翻 u，orient >= 2 → 翻 v（:4112-4115）。
        // 注意翻转用的是 "1 - uv"，靠 lo + hi == 1 把结果留在 [lo,hi] 内。
        var flipU = (orientation % 2) != 0;
        var flipV = orientation >= 2;

        var u0 = flipU ? 1f - lo : lo;
        var u1 = flipU ? 1f - hi : hi;
        var v0 = flipV ? 1f - lo : lo;
        var v1 = flipV ? 1f - hi : hi;

        return new EchoQuadUv(u0, v0, u1, v1);
    }

    /// <summary>
    /// gamma 的"重绘权重"（<c>milkdropfs.cpp:4134-4142</c> / <c>:4162-4170</c>）。
    /// </summary>
    /// <param name="gammaAdj"><c>gamma</c> 变量（默认 2.0，1.0 = 不变）。</param>
    /// <param name="echoLayer">
    /// true = echo 分支：<c>n = (int)(gamma - 0.0001)</c>，最后一遍权重为小数部分，**总和 ≈ gamma-1**；
    /// false = 无 echo 分支：<c>n = (int)(gamma - 0.001) + 1</c>，最后一遍权重为 <c>gamma - i</c>。
    /// 两套阈值不同（0.0001 vs 0.001）是原版就有的差异，照抄。
    /// </param>
    /// <param name="weights">输出缓冲区（每项是一遍的顶点色亮度）。</param>
    /// <returns>写入的重绘次数；0 表示不需要重绘。</returns>
    public static int GammaWeights(float gammaAdj, bool echoLayer, Span<float> weights)
    {
        int count;
        float last;

        if (echoLayer)
        {
            count = (int)(gammaAdj - 0.0001f);
            last = gammaAdj - (int)(gammaAdj - 0.0001f);
        }
        else
        {
            count = (int)(gammaAdj - 0.001f) + 1;
            last = gammaAdj - (count - 1);
        }

        if (count <= 0)
        {
            return 0;
        }

        if (count > weights.Length)
        {
            count = weights.Length;
            last = gammaAdj - (count - 1);
        }

        if (count > MaxGammaRedraws)
        {
            count = MaxGammaRedraws;
            last = gammaAdj - (count - 1);
        }

        weights[..(count - 1)].Fill(1f);
        weights[count - 1] = last;
        return count;
    }
}
