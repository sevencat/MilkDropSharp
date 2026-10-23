using ProjectMEvalSharp;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 自定义形状 per-frame 代码的表达式上下文（每个形状一份）。
/// </summary>
/// <remarks>对应 <c>MilkdropPreset/ShapePerFrameContext.hpp</c> 与 <c>.cpp</c>。</remarks>
public sealed class ShapePerFrameContext
{
    private readonly CompileContext _context;
    private readonly VariableReference[] _qVars = new VariableReference[Constants.QVarCount];
    private readonly VariableReference[] _tVars = new VariableReference[Constants.TVarCount];

    private EvalProgram? _perFrameCodeHandle;

    public ShapePerFrameContext(double[] globalRegisters)
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
    public VariableReference R2;
    public VariableReference G2;
    public VariableReference B2;
    public VariableReference A2;
    public VariableReference BorderR;
    public VariableReference BorderG;
    public VariableReference BorderB;
    public VariableReference BorderA;
    public VariableReference X;
    public VariableReference Y;
    public VariableReference Rad;
    public VariableReference Ang;
    public VariableReference Sides;
    public VariableReference Textured;
    public VariableReference Additive;
    public VariableReference Thick;
    public VariableReference NumInst;
    public VariableReference Instance;
    public VariableReference TexZoom;
    public VariableReference TexAng;

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
        X = _context.RegisterVariable("x");
        Y = _context.RegisterVariable("y");
        Rad = _context.RegisterVariable("rad");
        Ang = _context.RegisterVariable("ang");
        TexAng = _context.RegisterVariable("tex_ang");
        TexZoom = _context.RegisterVariable("tex_zoom");
        Sides = _context.RegisterVariable("sides");
        Textured = _context.RegisterVariable("textured");
        Instance = _context.RegisterVariable("instance");
        NumInst = _context.RegisterVariable("num_inst");
        Additive = _context.RegisterVariable("additive");
        Thick = _context.RegisterVariable("thick");
        R = _context.RegisterVariable("r");
        G = _context.RegisterVariable("g");
        B = _context.RegisterVariable("b");
        A = _context.RegisterVariable("a");
        R2 = _context.RegisterVariable("r2");
        G2 = _context.RegisterVariable("g2");
        B2 = _context.RegisterVariable("b2");
        A2 = _context.RegisterVariable("a2");
        BorderR = _context.RegisterVariable("border_r");
        BorderG = _context.RegisterVariable("border_g");
        BorderB = _context.RegisterVariable("border_b");
        BorderA = _context.RegisterVariable("border_a");
    }

    /// <summary>把状态装进变量槽（对应 <c>LoadStateVariables</c>）。</summary>
    public void LoadStateVariables(PresetState state, CustomShape shape, int instance)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(shape);

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
            _qVars[q].Value = state.FrameQVariables[q];
        }

        for (var t = 0; t < Constants.TVarCount; t++)
        {
            _tVars[t].Value = shape.TValuesAfterInitCode[t];
        }

        X.Value = shape.X;
        Y.Value = shape.Y;
        Rad.Value = shape.Radius;
        Ang.Value = shape.Angle;
        TexZoom.Value = shape.TexZoom;
        TexAng.Value = shape.TexAng;
        Sides.Value = shape.Sides;
        Additive.Value = shape.Additive ? 1 : 0;
        Textured.Value = shape.Textured ? 1 : 0;
        NumInst.Value = shape.Instances;
        Instance.Value = instance;
        Thick.Value = shape.ThickOutline ? 1 : 0;
        R.Value = shape.R;
        G.Value = shape.G;
        B.Value = shape.B;
        A.Value = shape.A;
        R2.Value = shape.R2;
        G2.Value = shape.G2;
        B2.Value = shape.B2;
        A2.Value = shape.A2;
        BorderR.Value = shape.BorderR;
        BorderG.Value = shape.BorderG;
        BorderB.Value = shape.BorderB;
        BorderA.Value = shape.BorderA;
    }

    /// <summary>编译并执行 init 代码（对应 <c>EvaluateInitCode</c>）。</summary>
    /// <exception cref="MilkdropCompileException">编译失败。</exception>
    public void EvaluateInitCode(string perFrameInitCode, CustomShape shape)
    {
        ArgumentNullException.ThrowIfNull(perFrameInitCode);
        ArgumentNullException.ThrowIfNull(shape);

        if (perFrameInitCode.Length == 0)
        {
            return;
        }

        EvalProgram? initCode = _context.Compile(perFrameInitCode);
        if (initCode is null)
        {
            throw new MilkdropCompileException(
                "Could not compile custom shape " + shape.Index + " per-frame init code: " +
                _context.Error + " (L" + _context.ErrorLine + ")");
        }

        initCode.Execute();
    }

    /// <summary>编译 per-frame 代码（对应 <c>CompilePerFrameCode</c>）。</summary>
    /// <exception cref="MilkdropCompileException">编译失败。</exception>
    public void CompilePerFrameCode(string perFrameCode, CustomShape shape)
    {
        ArgumentNullException.ThrowIfNull(perFrameCode);
        ArgumentNullException.ThrowIfNull(shape);

        if (perFrameCode.Length == 0)
        {
            return;
        }

        _perFrameCodeHandle = _context.Compile(perFrameCode);
        if (_perFrameCodeHandle is null)
        {
            throw new MilkdropCompileException(
                "Could not compile custom shape " + shape.Index + " per-frame code: " +
                _context.Error + " (L" + _context.ErrorLine + ")");
        }
    }

    /// <summary>执行已编译的 per-frame 代码。</summary>
    public void ExecutePerFrameCode() => _perFrameCodeHandle?.Execute();
}
