using MilkDrop.Eval;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 自定义波形 per-frame 代码的表达式上下文（每个波形一份）。
/// </summary>
/// <remarks>对应 <c>MilkdropPreset/WaveformPerFrameContext.hpp</c> 与 <c>.cpp</c>。</remarks>
public sealed class WaveformPerFrameContext
{
    private readonly CompileContext _context;
    private readonly VariableReference[] _qVars = new VariableReference[Constants.QVarCount];
    private readonly VariableReference[] _tVars = new VariableReference[Constants.TVarCount];

    private EvalProgram? _perFrameCodeHandle;

    public WaveformPerFrameContext(double[] globalRegisters)
    {
        ArgumentNullException.ThrowIfNull(globalRegisters);
        _context = new CompileContext(globalRegisters);
    }

    public VariableReference Time;
    public VariableReference Fps;
    public VariableReference Frame;
    public VariableReference Progress;
    public VariableReference Bass;
    public VariableReference Mid;
    public VariableReference Treb;
    public VariableReference BassAtt;
    public VariableReference MidAtt;
    public VariableReference TrebAtt;
    public VariableReference R;
    public VariableReference G;
    public VariableReference B;
    public VariableReference A;
    public VariableReference Samples;

    internal VariableReference QVar(int index) => _qVars[index];

    internal VariableReference TVar(int index) => _tVars[index];

    /// <summary>注册内建变量（对应 <c>WaveformPerFrameContext::RegisterBuiltinVariables</c>）。</summary>
    public void RegisterBuiltinVariables()
    {
        _context.ResetVariables();

        Time = _context.RegisterVariable("time");
        Fps = _context.RegisterVariable("fps");
        Frame = _context.RegisterVariable("frame");
        Progress = _context.RegisterVariable("progress");

        for (var q = 0; q < Constants.QVarCount; q++)
        {
            _qVars[q] = _context.RegisterVariable("q" + (q + 1));
        }

        for (var t = 0; t < Constants.TVarCount; t++)
        {
            _tVars[t] = _context.RegisterVariable("t" + (t + 1));
        }

        Bass = _context.RegisterVariable("bass");
        Mid = _context.RegisterVariable("mid");
        Treb = _context.RegisterVariable("treb");
        BassAtt = _context.RegisterVariable("bass_att");
        MidAtt = _context.RegisterVariable("mid_att");
        TrebAtt = _context.RegisterVariable("treb_att");
        R = _context.RegisterVariable("r");
        G = _context.RegisterVariable("g");
        B = _context.RegisterVariable("b");
        A = _context.RegisterVariable("a");
        Samples = _context.RegisterVariable("samples");
    }

    /// <summary>把状态装进变量槽（对应 <c>LoadStateVariables</c>）。</summary>
    public void LoadStateVariables(PresetState state, PerFrameContext presetPerFrameContext, CustomWaveform waveform)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(presetPerFrameContext);
        ArgumentNullException.ThrowIfNull(waveform);

        Time.Value = state.RenderContext.Time;
        Frame.Value = state.RenderContext.Frame;
        Fps.Value = state.RenderContext.Fps;
        Progress.Value = state.RenderContext.Progress;
        Bass.Value = state.AudioData.Bass;
        Mid.Value = state.AudioData.Mid;
        Treb.Value = state.AudioData.Treb;
        BassAtt.Value = state.AudioData.BassAtt;
        MidAtt.Value = state.AudioData.MidAtt;
        TrebAtt.Value = state.AudioData.TrebAtt;

        for (var q = 0; q < Constants.QVarCount; q++)
        {
            _qVars[q].Value = presetPerFrameContext.QVar(q).Value;
        }

        for (var t = 0; t < Constants.TVarCount; t++)
        {
            _tVars[t].Value = waveform.TValuesAfterInitCode[t];
        }

        R.Value = waveform.R;
        G.Value = waveform.G;
        B.Value = waveform.B;
        A.Value = waveform.A;
        Samples.Value = waveform.Samples;
    }

    /// <summary>编译并执行 init 代码（对应 <c>EvaluateInitCode</c>）。</summary>
    /// <exception cref="MilkdropCompileException">编译失败。</exception>
    public void EvaluateInitCode(string perFrameInitCode, CustomWaveform waveform)
    {
        ArgumentNullException.ThrowIfNull(perFrameInitCode);
        ArgumentNullException.ThrowIfNull(waveform);

        if (perFrameInitCode.Length == 0)
        {
            return;
        }

        EvalProgram? initCode = _context.Compile(perFrameInitCode);
        if (initCode is null)
        {
            throw new MilkdropCompileException(
                "Could not compile custom wave " + waveform.Index + " per-frame init code: " +
                _context.Error + " (L" + _context.ErrorLine + ")");
        }

        initCode.Execute();
    }

    /// <summary>编译 per-frame 代码（对应 <c>CompilePerFrameCode</c>）。</summary>
    /// <exception cref="MilkdropCompileException">编译失败。</exception>
    public void CompilePerFrameCode(string perFrameCode, CustomWaveform waveform)
    {
        ArgumentNullException.ThrowIfNull(perFrameCode);
        ArgumentNullException.ThrowIfNull(waveform);

        if (perFrameCode.Length == 0)
        {
            return;
        }

        _perFrameCodeHandle = _context.Compile(perFrameCode);
        if (_perFrameCodeHandle is null)
        {
            throw new MilkdropCompileException(
                "Could not compile custom wave " + waveform.Index + " per-frame code: " +
                _context.Error + " (L" + _context.ErrorLine + ")");
        }
    }

    /// <summary>执行已编译的 per-frame 代码。</summary>
    public void ExecutePerFrameCode() => _perFrameCodeHandle?.Execute();
}
