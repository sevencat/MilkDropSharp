namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>预设加载/解析错误。</summary>
/// <remarks>对应 <c>MilkdropPreset/MilkdropPresetExceptions.hpp:12-29</c>。</remarks>
public sealed class MilkdropPresetLoadException(string message) : Exception(message);

/// <summary>预设表达式（per-frame / per-pixel / waveform 等）编译错误。</summary>
/// <remarks>对应 <c>MilkdropPreset/MilkdropPresetExceptions.hpp:35-52</c>。</remarks>
public sealed class MilkdropCompileException(string message) : Exception(message);
