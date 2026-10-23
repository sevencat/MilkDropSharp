namespace MilkDrop.ProjectM.MilkdropPreset.Waveforms;

/// <summary>按 <see cref="WaveformMode"/> 创建波形实现（对应 <c>Waveforms/Factory.cpp</c>）。</summary>
public static class Factory
{
    /// <summary>创建波形实现；未知模式返回 null。</summary>
    public static WaveformMath? Create(WaveformMode mode) => mode switch
    {
        WaveformMode.Circle => new Circle(),

        WaveformMode.XYOscillationSpiral => new XYOscillationSpiral(),

        // CenteredSpiro 与 CenteredSpiroVolume 用同一份实现，只有 alpha 不同。
        WaveformMode.CenteredSpiro or WaveformMode.CenteredSpiroVolume => new CenteredSpiro(),

        WaveformMode.DerivativeLine => new DerivativeLine(),
        WaveformMode.ExplosiveHash => new ExplosiveHash(),
        WaveformMode.Line => new Line(),
        WaveformMode.DoubleLine => new DoubleLine(),
        WaveformMode.SpectrumLine => new SpectrumLine(),
        WaveformMode.Milkdrop2077Wave9 => new Milkdrop2077Wave9(),
        WaveformMode.Milkdrop2077WaveX => new Milkdrop2077WaveX(),
        WaveformMode.Milkdrop2077Wave11 => new Milkdrop2077Wave11(),
        WaveformMode.Milkdrop2077WaveSkewed => new Milkdrop2077WaveSkewed(),
        WaveformMode.Milkdrop2077WaveStar => new Milkdrop2077WaveStar(),
        WaveformMode.Milkdrop2077WaveFlower => new Milkdrop2077WaveFlower(),
        WaveformMode.Milkdrop2077WaveLasso => new Milkdrop2077WaveLasso(),
        _ => null,
    };
}
