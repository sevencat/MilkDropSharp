namespace MilkDrop.Classic.Visualizer;

/// <summary>
/// 每帧管线阶段，顺序严格对应 <c>milkdropfs.cpp:751-1140 RenderFrame</c>（见 cbplan.md §4.1）。
/// 移植时不要重排顺序 —— 反馈纹理的时序依赖它。
/// </summary>
public enum PipelineStage
{
    /// <summary>0 全屏状态初始化（<c>milkdropfs.cpp:887-940</c>）。</summary>
    SetupFrame,

    /// <summary>1 标题纹理（<c>:943-948 → RenderStringToTitleTexture :230-460</c>）。</summary>
    TitleTexture,

    /// <summary>2 motion vectors → VS0（<c>:950-965 → :1156-1308</c>）。</summary>
    MotionVectors,

    /// <summary>3 首帧清 VS0（<c>:971-981</c>）。</summary>
    ClearFeedback,

    /// <summary>4 warp：RT=VS1 读 VS0（<c>:983-991</c>）。</summary>
    Warp,

    /// <summary>4b per-vertex 等式求 UV/alpha（<c>:1006 → ComputeGridAlphaValues :1681-1864</c>）。</summary>
    PerVertexEquations,

    /// <summary>4c 波形顶点拉伸（<c>:1866 / :2090</c>）。</summary>
    WarpBlit,

    /// <summary>4d 混合期双 pass（<c>:1025-1046</c>）。</summary>
    BlendPass,

    /// <summary>5 blur 链 6 级（<c>:1050-1051 → BlurPasses :1501-1679</c>）。</summary>
    Blur,

    /// <summary>6 自定义 shape/wave/sprite（<c>:1054-1057</c>）。</summary>
    CustomShapes,

    /// <summary>7 标题动画烧进画面（<c>:1062-1067 → ShowSongTitleAnim :4477</c>）。</summary>
    SongTitle,

    /// <summary>8 合成到后备缓冲（<c>:1069-1120</c>）。</summary>
    Composite,

    /// <summary>9 用户 sprite（<c>:1131 → DrawUserSprites :3432</c>）。</summary>
    UserSprites,

    /// <summary>11 交换反馈纹理 VS0 ↔ VS1（<c>:1134-1136</c>）。</summary>
    SwapFeedback,

    /// <summary>12 Present（<c>pluginshell.cpp:1393-1444</c>）。</summary>
    Present,
}
