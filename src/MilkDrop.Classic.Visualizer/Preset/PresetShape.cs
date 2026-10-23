namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary><c>CShape</c>（state.h:97-155）：自定义图形的参数与代码。</summary>
public sealed class PresetShape
{
    public int Enabled;
    public int Sides;
    public int Additive;
    public int ThickOutline;
    public int Textured;
    public int Instances;

    public float X;
    public float Y;
    public float Rad;
    public float Ang;

    public float R;
    public float G;
    public float B;
    public float A;

    public float R2;
    public float G2;
    public float B2;
    public float A2;

    public float BorderR;
    public float BorderG;
    public float BorderB;
    public float BorderA;

    public float TexAng;
    public float TexZoom;

    public string InitCode = "";
    public string PerFrameCode = "";

    /// <summary>整块拷贝（mash-up 时对应 <c>memcpy(CState)</c> 的一部分）。</summary>
    internal void CopyFrom(PresetShape other)
    {
        Enabled = other.Enabled;
        Sides = other.Sides;
        Additive = other.Additive;
        ThickOutline = other.ThickOutline;
        Textured = other.Textured;
        Instances = other.Instances;
        X = other.X;
        Y = other.Y;
        Rad = other.Rad;
        Ang = other.Ang;
        R = other.R;
        G = other.G;
        B = other.B;
        A = other.A;
        R2 = other.R2;
        G2 = other.G2;
        B2 = other.B2;
        A2 = other.A2;
        BorderR = other.BorderR;
        BorderG = other.BorderG;
        BorderB = other.BorderB;
        BorderA = other.BorderA;
        TexAng = other.TexAng;
        TexZoom = other.TexZoom;
        InitCode = other.InitCode;
        PerFrameCode = other.PerFrameCode;
    }

    /// <summary>对应 <c>CState::Default</c> 里 <c>m_shape[i]</c> 的那一段（state.cpp:609-635,642-647）。</summary>
    public void SetDefaults()
    {
        Enabled = 0;
        Sides = 4;
        Additive = 0;
        ThickOutline = 0;
        Textured = 0;
        Instances = 1;
        TexZoom = 1.0f;
        TexAng = 0.0f;
        X = 0.5f;
        Y = 0.5f;
        Rad = 0.1f;
        Ang = 0.0f;
        R = 1.0f;
        G = 0.0f;
        B = 0.0f;
        A = 1.0f;
        R2 = 0.0f;
        G2 = 1.0f;
        B2 = 0.0f;
        A2 = 0.0f;
        BorderR = 1.0f;
        BorderG = 1.0f;
        BorderB = 1.0f;
        BorderA = 0.1f;

        InitCode = "";
        PerFrameCode = "";
    }
}
