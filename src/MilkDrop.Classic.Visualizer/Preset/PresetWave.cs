namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary><c>CWave</c>（state.h:157-180）：自定义波形的参数与代码。</summary>
public sealed class PresetWave
{
    public int Enabled;
    public int Samples;
    public int Sep;
    public float Scaling;
    public float Smoothing;
    public float X;
    public float Y;
    public float R;
    public float G;
    public float B;
    public float A;
    public int Spectrum;
    public int UseDots;
    public int DrawThick;
    public int Additive;

    public string InitCode = "";
    public string PerFrameCode = "";
    public string PerPointCode = "";

    /// <summary>整块拷贝（mash-up 时对应 <c>memcpy(CState)</c> 的一部分）。</summary>
    internal void CopyFrom(PresetWave other)
    {
        Enabled = other.Enabled;
        Samples = other.Samples;
        Sep = other.Sep;
        Scaling = other.Scaling;
        Smoothing = other.Smoothing;
        X = other.X;
        Y = other.Y;
        R = other.R;
        G = other.G;
        B = other.B;
        A = other.A;
        Spectrum = other.Spectrum;
        UseDots = other.UseDots;
        DrawThick = other.DrawThick;
        Additive = other.Additive;
        InitCode = other.InitCode;
        PerFrameCode = other.PerFrameCode;
        PerPointCode = other.PerPointCode;
    }

    /// <summary>对应 <c>CState::Default</c> 里 <c>m_wave[i]</c> 的那一段（state.cpp:593-608,636-641）。</summary>
    public void SetDefaults()
    {
        Enabled = 0;
        Samples = 512;
        Sep = 0;
        Scaling = 1.0f;
        Smoothing = 0.5f;
        R = 1.0f;
        G = 1.0f;
        B = 1.0f;
        A = 1.0f;
        Spectrum = 0;
        UseDots = 0;
        DrawThick = 0;
        Additive = 0;

        InitCode = "";
        PerFrameCode = "";
        PerPointCode = "";
    }
}
