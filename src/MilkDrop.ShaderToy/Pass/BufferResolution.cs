namespace MilkDrop.ShaderToy.Pass;

/// <summary>buffer pass 的分辨率策略。</summary>
public enum BufferResolutionMode
{
    /// <summary>跟随视口（上游默认）。</summary>
    Viewport,

    /// <summary>固定尺寸（Shadertoy 网页端的 1024x256，fluid 类作品靠它跑）。</summary>
    Fixed,

    /// <summary>视口乘以系数（网页端 "half/quarter resolution" 那一档）。</summary>
    Scale,
}

/// <summary>
/// buffer pass 的目标尺寸策略。
/// </summary>
/// <remarks>
/// Shadertoy 网页端有三种：viewport / 固定尺寸 / 视口倍数；上游 desktop-shadertoy 只有 viewport 一种
/// （<c>viewport.cpp:559</c> 直接用 <c>Resolution</c>），所以拿网页画面做基准时对不上。
/// 这里三种都实现，<c>iChannelResolution</c> 按实际尺寸回填。
/// </remarks>
public sealed class BufferResolution
{
    public BufferResolutionMode Mode { get; set; } = BufferResolutionMode.Viewport;

    public int FixedWidth { get; set; } = 1024;

    public int FixedHeight { get; set; } = 256;

    public float Scale { get; set; } = 0.5f;

    /// <summary>按视口尺寸算出 buffer 尺寸（最小 1x1）。</summary>
    public (int Width, int Height) Compute(int viewportWidth, int viewportHeight)
    {
        viewportWidth = Math.Max(1, viewportWidth);
        viewportHeight = Math.Max(1, viewportHeight);

        return Mode switch
        {
            BufferResolutionMode.Fixed => (Math.Max(1, FixedWidth), Math.Max(1, FixedHeight)),
            BufferResolutionMode.Scale => (
                Math.Max(1, (int)MathF.Round(viewportWidth * Scale)),
                Math.Max(1, (int)MathF.Round(viewportHeight * Scale))),
            _ => (viewportWidth, viewportHeight),
        };
    }

    /// <summary>命令行/UI 上显示的名字。</summary>
    public string Describe() => Mode switch
    {
        BufferResolutionMode.Fixed => $"fixed {FixedWidth}x{FixedHeight}",
        BufferResolutionMode.Scale => $"scale {Scale:0.##}x",
        _ => "viewport",
    };

    public BufferResolution Clone() => new()
    {
        Mode = Mode,
        FixedWidth = FixedWidth,
        FixedHeight = FixedHeight,
        Scale = Scale,
    };
}
