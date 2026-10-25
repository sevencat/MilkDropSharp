namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>按 <see cref="WaveformMode"/> 创建波形实现（对应 <c>Waveforms/Factory.cpp</c>）。</summary>
/// <remarks>
/// 用委托表而不是 <c>switch</c>：<see cref="IsSupported"/> 需要"有没有实现"这个信息，
/// 单独再维护一份 <c>switch</c> 容易和 <see cref="Create"/> 漂移。
/// </remarks>
public static class Factory
{
    private static readonly Dictionary<WaveformMode, Func<WaveformMath>> Creators = new()
    {
        [WaveformMode.Circle] = static () => new Circle(),

        [WaveformMode.XYOscillationSpiral] = static () => new XYOscillationSpiral(),

        // CenteredSpiro 与 CenteredSpiroVolume 用同一份实现，只有 alpha 不同。
        [WaveformMode.CenteredSpiro] = static () => new CenteredSpiro(),
        [WaveformMode.CenteredSpiroVolume] = static () => new CenteredSpiro(),

        [WaveformMode.DerivativeLine] = static () => new DerivativeLine(),
        [WaveformMode.ExplosiveHash] = static () => new ExplosiveHash(),
        [WaveformMode.Line] = static () => new Line(),
        [WaveformMode.DoubleLine] = static () => new DoubleLine(),
        [WaveformMode.SpectrumLine] = static () => new SpectrumLine(),
        [WaveformMode.Milkdrop2077Wave9] = static () => new Milkdrop2077Wave9(),
        [WaveformMode.Milkdrop2077WaveX] = static () => new Milkdrop2077WaveX(),
        [WaveformMode.Milkdrop2077Wave11] = static () => new Milkdrop2077Wave11(),
        [WaveformMode.Milkdrop2077WaveSkewed] = static () => new Milkdrop2077WaveSkewed(),
        [WaveformMode.Milkdrop2077WaveStar] = static () => new Milkdrop2077WaveStar(),
        [WaveformMode.Milkdrop2077WaveFlower] = static () => new Milkdrop2077WaveFlower(),
        [WaveformMode.Milkdrop2077WaveLasso] = static () => new Milkdrop2077WaveLasso(),
    };

    /// <summary>创建波形实现；未知模式返回 null。</summary>
    public static WaveformMath? Create(WaveformMode mode)
        => Creators.TryGetValue(mode, out Func<WaveformMath>? create) ? create() : null;

    /// <summary>该模式有没有实现（不创建对象，因此没有 GL 副作用）。</summary>
    public static bool IsSupported(WaveformMode mode) => Creators.ContainsKey(mode);
}
