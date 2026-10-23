using ProjectMEvalSharp;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// per-frame 代码的表达式上下文：每帧最先执行的代码，结果以 Q 变量形式传给
/// per-pixel / waveform / shape 上下文。
/// </summary>
/// <remarks>
/// <para>对应 <c>MilkdropPreset/PerFrameContext.hpp:21-147</c> 与 <c>PerFrameContext.cpp</c>。</para>
/// <para>
/// C++ 用 <c>PRJM_EVAL_F*</c> 直接指向表达式变量表；C# 侧对应 <see cref="VariableReference"/> 槽位
/// （值类型句柄，指向同一份存储，复制后仍有效）。
/// </para>
/// <para>
/// 与 C++ 的差异：C++ 的上下文持有自己的 gmegabuf 指针，而 <c>MilkDrop.Eval</c> 的
/// <c>CompileContext.GlobalMemory</c> 是进程级静态（见 projectMIssue.md）。
/// </para>
/// </remarks>
public sealed class PerFrameContext
{
    private readonly CompileContext _context;
    private readonly VariableReference[] _qVars = new VariableReference[Constants.QVarCount];
    private readonly double[] _qValuesAfterInitCode = new double[Constants.QVarCount];

    private EvalProgram? _perFrameCodeHandle;

    /// <param name="globalRegisters">
    /// 共享的 <c>reg00</c>..<c>reg99</c> 存储（<see cref="PresetState.GlobalRegisters"/>），
    /// 所有表达式上下文共用同一份。
    /// </param>
    public PerFrameContext(double[] globalRegisters)
    {
        ArgumentNullException.ThrowIfNull(globalRegisters);
        _context = new CompileContext(globalRegisters);
    }

    /// <summary>底层编译/执行上下文。</summary>
    public CompileContext Context => _context;

    /// <summary>已编译的 per-frame 代码（null 表示预设没有 per-frame 代码）。</summary>
    public EvalProgram? PerFrameCodeHandle => _perFrameCodeHandle;

    // ---- 变量槽位（对应 C++ 的 PRJM_EVAL_F* 成员） ----

    public VariableReference Zoom;
    public VariableReference ZoomExp;
    public VariableReference Rot;
    public VariableReference Warp;
    public VariableReference Cx;
    public VariableReference Cy;
    public VariableReference Dx;
    public VariableReference Dy;
    public VariableReference Sx;
    public VariableReference Sy;
    public VariableReference Time;
    public VariableReference Fps;
    public VariableReference Bass;
    public VariableReference Mid;
    public VariableReference Treb;
    public VariableReference BassAtt;
    public VariableReference MidAtt;
    public VariableReference TrebAtt;
    public VariableReference WaveA;
    public VariableReference WaveR;
    public VariableReference WaveG;
    public VariableReference WaveB;
    public VariableReference WaveX;
    public VariableReference WaveY;
    public VariableReference WaveMystery;
    public VariableReference WaveMode;
    public VariableReference Decay;
    public VariableReference Frame;
    public VariableReference Progress;
    public VariableReference ObSize;
    public VariableReference ObR;
    public VariableReference ObG;
    public VariableReference ObB;
    public VariableReference ObA;
    public VariableReference IbSize;
    public VariableReference IbR;
    public VariableReference IbG;
    public VariableReference IbB;
    public VariableReference IbA;
    public VariableReference MvX;
    public VariableReference MvY;
    public VariableReference MvDX;
    public VariableReference MvDY;
    public VariableReference MvL;
    public VariableReference MvR;
    public VariableReference MvG;
    public VariableReference MvB;
    public VariableReference MvA;
    public VariableReference EchoZoom;
    public VariableReference EchoAlpha;
    public VariableReference EchoOrient;
    public VariableReference WaveUsedots;
    public VariableReference WaveThick;
    public VariableReference WaveAdditive;
    public VariableReference WaveBrighten;
    public VariableReference DarkenCenter;
    public VariableReference Gamma;
    public VariableReference Wrap;
    public VariableReference Invert;
    public VariableReference Brighten;
    public VariableReference Darken;
    public VariableReference Solarize;
    public VariableReference Meshx;
    public VariableReference Meshy;
    public VariableReference Pixelsx;
    public VariableReference Pixelsy;
    public VariableReference Aspectx;
    public VariableReference Aspecty;
    public VariableReference Blur1Min;
    public VariableReference Blur2Min;
    public VariableReference Blur3Min;
    public VariableReference Blur1Max;
    public VariableReference Blur2Max;
    public VariableReference Blur3Max;
    public VariableReference Blur1EdgeDarken;

    /// <summary>某个 Q 变量的槽位（<paramref name="index"/> 从 0 开始，对应 <c>q1</c>..<c>q32</c>）。</summary>
    public VariableReference QVar(int index) => _qVars[index];

    /// <summary>注册所有内建变量（对应 <c>PerFrameContext::RegisterBuiltinVariables</c>）。</summary>
    public void RegisterBuiltinVariables()
    {
        _context.ResetVariables();

        Zoom = Reg("zoom");
        ZoomExp = Reg("zoomexp");
        Rot = Reg("rot");
        Warp = Reg("warp");
        Cx = Reg("cx");
        Cy = Reg("cy");
        Dx = Reg("dx");
        Dy = Reg("dy");
        Sx = Reg("sx");
        Sy = Reg("sy");
        Time = Reg("time");
        Fps = Reg("fps");
        Bass = Reg("bass");
        Mid = Reg("mid");
        Treb = Reg("treb");
        BassAtt = Reg("bass_att");
        MidAtt = Reg("mid_att");
        TrebAtt = Reg("treb_att");
        Frame = Reg("frame");
        Decay = Reg("decay");

        WaveA = Reg("wave_a");
        WaveR = Reg("wave_r");
        WaveG = Reg("wave_g");
        WaveB = Reg("wave_b");
        WaveX = Reg("wave_x");
        WaveY = Reg("wave_y");
        WaveMystery = Reg("wave_mystery");
        WaveMode = Reg("wave_mode");

        for (int q = 0; q < Constants.QVarCount; q++)
        {
            _qVars[q] = Reg("q" + (q + 1));
        }

        Progress = Reg("progress");
        ObSize = Reg("ob_size");
        ObR = Reg("ob_r");
        ObG = Reg("ob_g");
        ObB = Reg("ob_b");
        ObA = Reg("ob_a");
        IbSize = Reg("ib_size");
        IbR = Reg("ib_r");
        IbG = Reg("ib_g");
        IbB = Reg("ib_b");
        IbA = Reg("ib_a");

        MvX = Reg("mv_x");
        MvY = Reg("mv_y");
        MvDX = Reg("mv_dx");
        MvDY = Reg("mv_dy");
        MvL = Reg("mv_l");
        MvR = Reg("mv_r");
        MvG = Reg("mv_g");
        MvB = Reg("mv_b");
        MvA = Reg("mv_a");

        EchoZoom = Reg("echo_zoom");
        EchoAlpha = Reg("echo_alpha");
        EchoOrient = Reg("echo_orient");

        WaveUsedots = Reg("wave_usedots");
        WaveThick = Reg("wave_thick");
        WaveAdditive = Reg("wave_additive");
        WaveBrighten = Reg("wave_brighten");

        DarkenCenter = Reg("darken_center");
        Gamma = Reg("gamma");
        Wrap = Reg("wrap");
        Invert = Reg("invert");
        Brighten = Reg("brighten");
        Darken = Reg("darken");
        Solarize = Reg("solarize");

        Meshx = Reg("meshx");
        Meshy = Reg("meshy");
        Pixelsx = Reg("pixelsx");
        Pixelsy = Reg("pixelsy");
        Aspectx = Reg("aspectx");
        Aspecty = Reg("aspecty");

        Blur1Min = Reg("blur1_min");
        Blur2Min = Reg("blur2_min");
        Blur3Min = Reg("blur3_min");
        Blur1Max = Reg("blur1_max");
        Blur2Max = Reg("blur2_max");
        Blur3Max = Reg("blur3_max");
        Blur1EdgeDarken = Reg("blur1_edge_darken");
    }

    /// <summary>把预设当前状态装进变量槽（对应 <c>PerFrameContext::LoadStateVariables</c>）。</summary>
    public void LoadStateVariables(PresetState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        Zoom.Value = state.Zoom;
        ZoomExp.Value = state.ZoomExponent;
        Rot.Value = state.Rot;
        Warp.Value = state.WarpAmount;
        Cx.Value = state.RotCX;
        Cy.Value = state.RotCY;
        Dx.Value = state.XPush;
        Dy.Value = state.YPush;
        Sx.Value = state.StretchX;
        Sy.Value = state.StretchY;
        Time.Value = state.RenderContext.Time;
        Fps.Value = state.RenderContext.Fps;
        Bass.Value = state.AudioData.Bass;
        Mid.Value = state.AudioData.Mid;
        Treb.Value = state.AudioData.Treb;
        BassAtt.Value = state.AudioData.BassAtt;
        MidAtt.Value = state.AudioData.MidAtt;
        TrebAtt.Value = state.AudioData.TrebAtt;
        Frame.Value = state.RenderContext.Frame;

        // 每帧都从 init 代码执行后的 Q 值重新开始（C++ 同样如此）。
        for (int q = 0; q < Constants.QVarCount; q++)
        {
            _qVars[q].Value = _qValuesAfterInitCode[q];
        }

        Progress.Value = state.RenderContext.Progress;
        Decay.Value = state.Decay;
        WaveA.Value = state.WaveAlpha;
        WaveR.Value = state.WaveR;
        WaveG.Value = state.WaveG;
        WaveB.Value = state.WaveB;
        WaveX.Value = state.WaveX;
        WaveY.Value = state.WaveY;
        WaveMystery.Value = state.WaveParam;
        WaveMode.Value = state.WaveMode;

        ObSize.Value = state.OuterBorderSize;
        ObR.Value = state.OuterBorderR;
        ObG.Value = state.OuterBorderG;
        ObB.Value = state.OuterBorderB;
        ObA.Value = state.OuterBorderA;
        IbSize.Value = state.InnerBorderSize;
        IbR.Value = state.InnerBorderR;
        IbG.Value = state.InnerBorderG;
        IbB.Value = state.InnerBorderB;
        IbA.Value = state.InnerBorderA;

        MvX.Value = state.MvX;
        MvY.Value = state.MvY;
        MvDX.Value = state.MvDX;
        MvDY.Value = state.MvDY;
        MvL.Value = state.MvL;
        MvR.Value = state.MvR;
        MvG.Value = state.MvG;
        MvB.Value = state.MvB;
        MvA.Value = state.MvA;

        EchoZoom.Value = state.VideoEchoZoom;
        EchoAlpha.Value = state.VideoEchoAlpha;
        EchoOrient.Value = state.VideoEchoOrientation;

        WaveUsedots.Value = state.WaveDots ? 1 : 0;
        WaveThick.Value = state.WaveThick ? 1 : 0;
        WaveAdditive.Value = state.AdditiveWaves ? 1 : 0;
        WaveBrighten.Value = state.MaximizeWaveColor ? 1 : 0;

        DarkenCenter.Value = state.DarkenCenter ? 1 : 0;
        Gamma.Value = state.GammaAdj;
        Wrap.Value = state.TexWrap ? 1 : 0;
        Invert.Value = state.Invert ? 1 : 0;
        Brighten.Value = state.Brighten ? 1 : 0;
        Darken.Value = state.Darken ? 1 : 0;
        Solarize.Value = state.Solarize ? 1 : 0;

        Meshx.Value = state.RenderContext.PerPixelMeshX;
        Meshy.Value = state.RenderContext.PerPixelMeshY;
        Pixelsx.Value = state.RenderContext.ViewportSizeX;
        Pixelsy.Value = state.RenderContext.ViewportSizeY;
        Aspectx.Value = state.RenderContext.InvAspectX;
        Aspecty.Value = state.RenderContext.InvAspectY;

        Blur1Min.Value = state.Blur1Min;
        Blur2Min.Value = state.Blur2Min;
        Blur3Min.Value = state.Blur3Min;
        Blur1Max.Value = state.Blur1Max;
        Blur2Max.Value = state.Blur2Max;
        Blur3Max.Value = state.Blur3Max;
        Blur1EdgeDarken.Value = state.Blur1EdgeDarken;
    }

    /// <summary>
    /// 编译并执行 per-frame init 代码，把执行后的 Q 值留作每帧起点
    /// （对应 <c>PerFrameContext::EvaluateInitCode</c>）。
    /// </summary>
    /// <exception cref="MilkdropCompileException">init 代码编译失败。</exception>
    public void EvaluateInitCode(PresetState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.PerFrameInitCode.Length == 0)
        {
            return;
        }

        EvalProgram? initCode = _context.Compile(state.PerFrameInitCode);
        if (initCode is null)
        {
            throw new MilkdropCompileException(
                "Could not compile per-frame init code: " + _context.Error + " (L" + _context.ErrorLine + ")");
        }

        initCode.Execute();

        for (int q = 0; q < Constants.QVarCount; q++)
        {
            _qValuesAfterInitCode[q] = _qVars[q].Value;
            state.FrameQVariables[q] = _qVars[q].Value;
        }
    }

    /// <summary>
    /// 编译 per-frame 代码（对应 <c>PerFrameContext::CompilePerFrameCode</c>）。
    /// 空代码直接跳过（预设可以只有 init 代码）。
    /// </summary>
    /// <exception cref="MilkdropCompileException">代码编译失败。</exception>
    public void CompilePerFrameCode(string perFrameCode)
    {
        ArgumentNullException.ThrowIfNull(perFrameCode);

        if (perFrameCode.Length == 0)
        {
            return;
        }

        _perFrameCodeHandle = _context.Compile(perFrameCode);
        if (_perFrameCodeHandle is null)
        {
            throw new MilkdropCompileException(
                "Could not compile per-frame code: " + _context.Error + " (L" + _context.ErrorLine + ")");
        }
    }

    /// <summary>执行已编译的 per-frame 代码（对应 <c>PerFrameContext::ExecutePerFrameCode</c>）。</summary>
    public void ExecutePerFrameCode() => _perFrameCodeHandle?.Execute();

    private VariableReference Reg(string name) => _context.RegisterVariable(name);
}
