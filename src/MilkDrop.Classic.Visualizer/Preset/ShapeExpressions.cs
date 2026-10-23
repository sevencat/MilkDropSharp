using MilkDrop.Eval;

namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary>
/// 自定义图形（<c>CShape</c>）的 per-frame VM（<c>m_pf_eel</c>）。
/// 变量注册见 state.cpp:461-513，同步见 milkdropfs.cpp:2400-2440。
/// </summary>
/// <remarks>
/// 图形只有一套 VM：init 代码执行后回读 <c>t1..t8</c>，per-frame 代码在每次绘制实例时执行。
/// 画图形属于 M6，这里只负责"变量 + 编译 + 同步"。
/// </remarks>
public sealed class ShapeExpressions
{
    /// <summary><c>NUM_T_VAR</c>（state.h:50）。</summary>
    public const int NumTVar = 8;

    private static readonly string[] ReadOnlyNames =
        ["time", "fps", "frame", "progress", "bass", "mid", "treb", "bass_att", "mid_att", "treb_att"];

    /// <summary>state.cpp:488-511 的 i/o 量（顺序与注册顺序一致）。</summary>
    private static readonly string[] IoNames =
    [
        "x", "y", "rad", "ang", "tex_ang", "tex_zoom", "sides", "textured", "instance", "num_inst",
        "additive", "thick", "r", "g", "b", "a", "r2", "g2", "b2", "a2",
        "border_r", "border_g", "border_b", "border_a",
    ];

    private readonly Dictionary<string, IEelVariable> _byName = new(StringComparer.Ordinal);
    private readonly IEelVariable[] _q = new IEelVariable[PerFrameVariables.NumQVar];
    private readonly IEelVariable[] _t = new IEelVariable[NumTVar];

    public ShapeExpressions(IExpressionEngine engine, int index)
    {
        Vm = engine.CreateVm($"shape{index}_per_frame");

        foreach (var name in ReadOnlyNames)
        {
            _byName[name] = Vm.RegisterVariable(name);
        }

        for (var i = 0; i < PerFrameVariables.NumQVar; i++)
        {
            _q[i] = _byName[$"q{i + 1}"] = Vm.RegisterVariable($"q{i + 1}");
        }

        for (var i = 0; i < NumTVar; i++)
        {
            _t[i] = _byName[$"t{i + 1}"] = Vm.RegisterVariable($"t{i + 1}");
        }

        foreach (var name in IoNames)
        {
            _byName[name] = Vm.RegisterVariable(name);
        }
    }

    public IExpressionVm Vm { get; }

    public ICompiledExpression? PerFrameCode { get; private set; }

    /// <summary><c>t_values_after_init_code[]</c>：init 代码执行后的 t1..t8。</summary>
    public double[] TValuesAfterInitCode { get; } = new double[NumTVar];

    public IEelVariable this[string name] => _byName[name];

    /// <summary>
    /// 编译并执行 init 代码（state.cpp:1795-1836）：执行后回读 <c>t1..t8</c>。
    /// </summary>
    public bool CompileInit(
        string code,
        in PerFrameContext ctx,
        PerFrameVariables perFrame,
        PresetShape shape,
        out ExpressionError? error)
    {
        var program = Vm.Compile(code);
        if (program is null)
        {
            error = Vm.LastError;
            Array.Clear(TValuesAfterInitCode);
            CopyQFrom(perFrame);
            return false;
        }

        // 原版 init 时 instance 传 0（milkdropfs.cpp:1821）
        SyncPerFrame(ctx, perFrame, shape, instance: 0);
        program.Execute();

        for (var i = 0; i < NumTVar; i++)
        {
            TValuesAfterInitCode[i] = _t[i].Value;
        }

        error = null;
        return true;
    }

    /// <summary>清掉上一次编译的程序与 t 回读值（代码被改成空串时必须调）。</summary>
    public void ResetProgram()
    {
        PerFrameCode = null;
        Array.Clear(TValuesAfterInitCode);
    }

    public bool CompilePerFrame(string code, out ExpressionError? error)
    {
        PerFrameCode = Vm.Compile(code);
        error = PerFrameCode is null ? Vm.LastError : null;
        return PerFrameCode is not null;
    }

    /// <summary>
    /// <c>LoadCustomShapePerFrameEvallibVars</c>（milkdropfs.cpp:2400-2440）。
    /// </summary>
    public void SyncPerFrame(in PerFrameContext ctx, PerFrameVariables perFrame, PresetShape shape, int instance)
    {
        _byName["time"].Value = ctx.Time;
        _byName["fps"].Value = ctx.Fps;
        _byName["frame"].Value = ctx.Frame;
        _byName["progress"].Value = ctx.Progress;
        _byName["bass"].Value = ctx.MySound.ImmRel[0];
        _byName["mid"].Value = ctx.MySound.ImmRel[1];
        _byName["treb"].Value = ctx.MySound.ImmRel[2];
        _byName["bass_att"].Value = ctx.MySound.AvgRel[0];
        _byName["mid_att"].Value = ctx.MySound.AvgRel[1];
        _byName["treb_att"].Value = ctx.MySound.AvgRel[2];

        CopyQFrom(perFrame);
        for (var i = 0; i < NumTVar; i++)
        {
            _t[i].Value = TValuesAfterInitCode[i];
        }

        _byName["x"].Value = shape.X;
        _byName["y"].Value = shape.Y;
        _byName["rad"].Value = shape.Rad;
        _byName["ang"].Value = shape.Ang;
        _byName["tex_ang"].Value = shape.TexAng;
        _byName["tex_zoom"].Value = shape.TexZoom;
        _byName["sides"].Value = shape.Sides;
        _byName["textured"].Value = shape.Textured;
        _byName["num_inst"].Value = shape.Instances;
        _byName["instance"].Value = instance;
        _byName["additive"].Value = shape.Additive;
        _byName["thick"].Value = shape.ThickOutline;
        _byName["r"].Value = shape.R;
        _byName["g"].Value = shape.G;
        _byName["b"].Value = shape.B;
        _byName["a"].Value = shape.A;
        _byName["r2"].Value = shape.R2;
        _byName["g2"].Value = shape.G2;
        _byName["b2"].Value = shape.B2;
        _byName["a2"].Value = shape.A2;
        _byName["border_r"].Value = shape.BorderR;
        _byName["border_g"].Value = shape.BorderG;
        _byName["border_b"].Value = shape.BorderB;
        _byName["border_a"].Value = shape.BorderA;
    }

    public void RunPerFrame() => PerFrameCode?.Execute();

    private void CopyQFrom(PerFrameVariables perFrame)
    {
        var source = perFrame.Q;
        for (var i = 0; i < _q.Length; i++)
        {
            _q[i].Value = source[i].Value;
        }
    }
}
