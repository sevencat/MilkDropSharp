using System.Numerics;
using MilkDrop.Classic.Visualizer.Sound;

namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary>
/// <c>LoadPerFrameEvallibVars</c>（milkdropfs.cpp:460-554）需要的插件级只读量。
/// </summary>
/// <param name="Time">预设已播放秒数（<c>GetTime() - m_fStartTime</c>）。</param>
/// <param name="Fps">当前帧率。</param>
/// <param name="MySound"><c>mysound</c>：<c>bass/mid/treb</c> 的来源。</param>
/// <param name="Frame">帧号。</param>
/// <param name="Progress">预设进度 0..1。</param>
/// <param name="GridX"><c>meshx</c>：warp 网格列数。</param>
/// <param name="GridY"><c>meshy</c>：warp 网格行数。</param>
/// <param name="Width"><c>pixelsx</c>。</param>
/// <param name="Height"><c>pixelsy</c>。</param>
/// <param name="InvAspectX"><c>aspectx</c> = 1/aspect。</param>
/// <param name="InvAspectY"><c>aspecty</c> = aspect 或 1/aspect，取决于宽高比方向。</param>
/// <param name="Mouse">
/// shader 兼容层的 <c>mouse</c>（<c>_mouse</c> 常量）：xy = 归一化到 -1..1 的光标位置，zw 暂未使用。
/// </param>
public readonly record struct PerFrameContext(
    float Time,
    float Fps,
    MySoundData MySound,
    long Frame,
    double Progress,
    int GridX,
    int GridY,
    int Width,
    int Height,
    float InvAspectX,
    float InvAspectY,
    Vector4 Mouse = default);
