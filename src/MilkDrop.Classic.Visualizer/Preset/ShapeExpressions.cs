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

    /// <summary>热路径（每个实例的绘制都要同步一遍）按名字表顺序预绑定，避免字典查找。</summary>
    private readonly IEelVariable[] _readOnly = new IEelVariable[ReadOnlyNames.Length];
    private readonly IEelVariable[] _io = new IEelVariable[IoNames.Length];

    public ShapeExpressions(IExpressionEngine engine, int index)
    {
        Vm = engine.CreateVm($"shape{index}_per_frame");

        for (var i = 0; i < ReadOnlyNames.Length; i++)
        {
            var name = ReadOnlyNames[i];
            _byName[name] = _readOnly[i] = Vm.RegisterVariable(name);
        }

        for (var i = 0; i < PerFrameVariables.NumQVar; i++)
        {
            _q[i] = _byName[$"q{i + 1}"] = Vm.RegisterVariable($"q{i + 1}");
        }

        for (var i = 0; i < NumTVar; i++)
        {
            _t[i] = _byName[$"t{i + 1}"] = Vm.RegisterVariable($"t{i + 1}");
        }

        for (var i = 0; i < IoNames.Length; i++)
        {
            var name = IoNames[i];
            _byName[name] = _io[i] = Vm.RegisterVariable(name);
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
        _readOnly[0].Value = ctx.Time;
        _readOnly[1].Value = ctx.Fps;
        _readOnly[2].Value = ctx.Frame;
        _readOnly[3].Value = ctx.Progress;
        _readOnly[4].Value = ctx.MySound.ImmRel[0];
        _readOnly[5].Value = ctx.MySound.ImmRel[1];
        _readOnly[6].Value = ctx.MySound.ImmRel[2];
        _readOnly[7].Value = ctx.MySound.AvgRel[0];
        _readOnly[8].Value = ctx.MySound.AvgRel[1];
        _readOnly[9].Value = ctx.MySound.AvgRel[2];

        CopyQFrom(perFrame);
        for (var i = 0; i < NumTVar; i++)
        {
            _t[i].Value = TValuesAfterInitCode[i];
        }

        // 顺序见 IoNames：x, y, rad, ang, tex_ang, tex_zoom, sides, textured, instance, num_inst,
        // additive, thick, r, g, b, a, r2, g2, b2, a2, border_r, border_g, border_b, border_a
        _io[0].Value = shape.X;
        _io[1].Value = shape.Y;
        _io[2].Value = shape.Rad;
        _io[3].Value = shape.Ang;
        _io[4].Value = shape.TexAng;
        _io[5].Value = shape.TexZoom;
        _io[6].Value = shape.Sides;
        _io[7].Value = shape.Textured;
        _io[8].Value = instance;
        _io[9].Value = shape.Instances;
        _io[10].Value = shape.Additive;
        _io[11].Value = shape.ThickOutline;
        _io[12].Value = shape.R;
        _io[13].Value = shape.G;
        _io[14].Value = shape.B;
        _io[15].Value = shape.A;
        _io[16].Value = shape.R2;
        _io[17].Value = shape.G2;
        _io[18].Value = shape.B2;
        _io[19].Value = shape.A2;
        _io[20].Value = shape.BorderR;
        _io[21].Value = shape.BorderG;
        _io[22].Value = shape.BorderB;
        _io[23].Value = shape.BorderA;
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
