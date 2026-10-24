using MilkDrop.Eval;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 自定义波形 per-point 代码的表达式上下文（每个波形一份）。
/// </summary>
/// <remarks>对应 <c>MilkdropPreset/WaveformPerPointContext.hpp</c> 与 <c>.cpp</c>。</remarks>
public sealed class WaveformPerPointContext
{
    private readonly CompileContext _context;
    private readonly VariableReference[] _qVars = new VariableReference[Constants.QVarCount];
    private readonly VariableReference[] _tVars = new VariableReference[Constants.TVarCount];

    private EvalProgram? _perPointCodeHandle;

    public WaveformPerPointContext(double[] globalRegisters)
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
    public VariableReference Sample;
    public VariableReference Value1;
    public VariableReference Value2;
    public VariableReference X;
    public VariableReference Y;
    public VariableReference R;
    public VariableReference G;
    public VariableReference B;
    public VariableReference A;

    internal VariableReference QVar(int index) => _qVars[index];

    internal VariableReference TVar(int index) => _tVars[index];

    /// <summary>注册内建变量（对应 <c>RegisterBuiltinVariables</c>）。</summary>
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
        Sample = _context.RegisterVariable("sample");
        Value1 = _context.RegisterVariable("value1");
        Value2 = _context.RegisterVariable("value2");
        X = _context.RegisterVariable("x");
        Y = _context.RegisterVariable("y");
        R = _context.RegisterVariable("r");
        G = _context.RegisterVariable("g");
        B = _context.RegisterVariable("b");
        A = _context.RegisterVariable("a");
    }

    /// <summary>装载只读状态（对应 <c>LoadReadOnlyStateVariables</c>）。</summary>
    public void LoadReadOnlyStateVariables(PerFrameContext presetPerFrameContext)
    {
        ArgumentNullException.ThrowIfNull(presetPerFrameContext);

        Time.Value = presetPerFrameContext.Time.Value;
        Frame.Value = presetPerFrameContext.Frame.Value;
        Fps.Value = presetPerFrameContext.Fps.Value;
        Progress.Value = presetPerFrameContext.Progress.Value;
        Bass.Value = presetPerFrameContext.Bass.Value;
        Mid.Value = presetPerFrameContext.Mid.Value;
        Treb.Value = presetPerFrameContext.Treb.Value;
        BassAtt.Value = presetPerFrameContext.BassAtt.Value;
        MidAtt.Value = presetPerFrameContext.MidAtt.Value;
        TrebAtt.Value = presetPerFrameContext.TrebAtt.Value;
    }

    /// <summary>编译 per-point 代码（对应 <c>CompilePerPointCode</c>）。</summary>
    /// <exception cref="MilkdropCompileException">编译失败。</exception>
    public void CompilePerPointCode(string perPointCode, CustomWaveform waveform)
    {
        ArgumentNullException.ThrowIfNull(perPointCode);
        ArgumentNullException.ThrowIfNull(waveform);

        if (perPointCode.Length == 0)
        {
            return;
        }

        _perPointCodeHandle = _context.Compile(perPointCode);
        if (_perPointCodeHandle is null)
        {
            throw new MilkdropCompileException(
                "Could not compile custom wave " + waveform.Index + " per-point code: " +
                _context.Error + " (L" + _context.ErrorLine + ")");
        }
    }

    /// <summary>执行已编译的 per-point 代码。</summary>
    public void ExecutePerPointCode() => _perPointCodeHandle?.Execute();
}
