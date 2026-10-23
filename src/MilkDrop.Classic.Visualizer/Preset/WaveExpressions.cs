using MilkDrop.Eval;

namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary>
/// 自定义波形（<c>CWave</c>）的两套 VM：per-frame（<c>m_pf_eel</c>）与 per-point（<c>m_pp_eel</c>）。
/// 变量注册见 state.cpp:393-459，求值顺序见 milkdropfs.cpp:2525-2546、2591-2620。
/// </summary>
/// <remarks>
/// 与 per-frame VM 的关系：<c>q1..q32</c> 是<b>拷贝</b>过来的（不是共享），
/// <c>t1..t8</c> 来自 init 代码执行后的回读值；per-point 又再拷一份 per-frame 的结果。
/// 画波形本身属于 M6，这里只负责"变量 + 编译 + 同步"。
/// </remarks>
public sealed class WaveExpressions
{
    /// <summary><c>NUM_T_VAR</c>（state.h:50）。</summary>
    public const int NumTVar = 8;

    /// <summary>state.cpp:398-401、427-430 共有的只读量。</summary>
    private static readonly string[] ReadOnlyNames =
        ["time", "fps", "frame", "progress", "bass", "mid", "treb", "bass_att", "mid_att", "treb_att"];

    /// <summary>per-frame 的 i/o：颜色与采样数（state.cpp:420-424）。</summary>
    private static readonly string[] PerFrameIoNames = ["r", "g", "b", "a", "samples"];

    /// <summary>per-point 的只读输入（state.cpp:449-451）。</summary>
    private static readonly string[] PerPointInputNames = ["sample", "value1", "value2"];

    /// <summary>per-point 的 i/o（state.cpp:452-457）。</summary>
    private static readonly string[] PerPointIoNames = ["x", "y", "r", "g", "b", "a"];

    private readonly Dictionary<string, IEelVariable> _pf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IEelVariable> _pp = new(StringComparer.Ordinal);

    private readonly IEelVariable[] _q = new IEelVariable[PerFrameVariables.NumQVar];
    private readonly IEelVariable[] _t = new IEelVariable[NumTVar];
    private readonly IEelVariable[] _ppQ = new IEelVariable[PerFrameVariables.NumQVar];
    private readonly IEelVariable[] _ppT = new IEelVariable[NumTVar];
    private readonly IEelVariable[] _ppIn = new IEelVariable[3];
    private readonly IEelVariable[] _ppOut = new IEelVariable[6];

    public WaveExpressions(IExpressionEngine engine, int index)
    {
        PerFrameVm = engine.CreateVm($"wave{index}_per_frame");
        PerPointVm = engine.CreateVm($"wave{index}_per_point");

        foreach (var name in ReadOnlyNames)
        {
            _pf[name] = PerFrameVm.RegisterVariable(name);
            _pp[name] = PerPointVm.RegisterVariable(name);
        }

        for (var i = 0; i < PerFrameVariables.NumQVar; i++)
        {
            _q[i] = _pf[$"q{i + 1}"] = PerFrameVm.RegisterVariable($"q{i + 1}");
            _ppQ[i] = _pp[$"q{i + 1}"] = PerPointVm.RegisterVariable($"q{i + 1}");
        }

        for (var i = 0; i < NumTVar; i++)
        {
            _t[i] = _pf[$"t{i + 1}"] = PerFrameVm.RegisterVariable($"t{i + 1}");
            _ppT[i] = _pp[$"t{i + 1}"] = PerPointVm.RegisterVariable($"t{i + 1}");
        }

        foreach (var name in PerFrameIoNames)
        {
            _pf[name] = PerFrameVm.RegisterVariable(name);
        }

        foreach (var name in PerPointInputNames)
        {
            _pp[name] = PerPointVm.RegisterVariable(name);
        }

        foreach (var name in PerPointIoNames)
        {
            _pp[name] = PerPointVm.RegisterVariable(name);
        }

        _ppIn[0] = _pp["sample"];
        _ppIn[1] = _pp["value1"];
        _ppIn[2] = _pp["value2"];
        _ppOut[0] = _pp["x"];
        _ppOut[1] = _pp["y"];
        _ppOut[2] = _pp["r"];
        _ppOut[3] = _pp["g"];
        _ppOut[4] = _pp["b"];
        _ppOut[5] = _pp["a"];
    }

    public IExpressionVm PerFrameVm { get; }

    public IExpressionVm PerPointVm { get; }

    public ICompiledExpression? PerFrameCode { get; private set; }

    public ICompiledExpression? PerPointCode { get; private set; }

    /// <summary><c>t_values_after_init_code[]</c>：init 代码执行后的 t1..t8。</summary>
    public double[] TValuesAfterInitCode { get; } = new double[NumTVar];

    /// <summary>per-frame VM 的变量（测试与 M6 绘制用）。</summary>
    public IEelVariable PerFrameVar(string name) => _pf[name];

    /// <summary>per-point VM 的变量（测试与 M6 绘制用）。</summary>
    public IEelVariable PerPointVar(string name) => _pp[name];

    /// <summary>
    /// 编译并执行 init 代码（state.cpp:1729-1765）：执行后回读 <c>t1..t8</c>。
    /// </summary>
    /// <returns>编译/执行是否成功（失败时 t 归零，与原版一致）。</returns>
    public bool CompileInit(
        string code,
        in PerFrameContext ctx,
        PerFrameVariables perFrame,
        PresetWave wave,
        out ExpressionError? error)
    {
        var program = PerFrameVm.Compile(code);
        if (program is null)
        {
            error = PerFrameVm.LastError;
            Array.Clear(TValuesAfterInitCode);
            CopyQFrom(perFrame);
            return false;
        }

        SyncPerFrame(ctx, perFrame, wave);
        program.Execute();

        for (var i = 0; i < NumTVar; i++)
        {
            TValuesAfterInitCode[i] = _t[i].Value;
        }

        error = null;
        return true;
    }

    /// <summary>清掉上一次编译的程序与 t 回读值（代码被改成空串时必须调）。</summary>
    public void ResetPrograms()
    {
        PerFrameCode = null;
        PerPointCode = null;
        Array.Clear(TValuesAfterInitCode);
    }

    public bool CompilePerFrame(string code, out ExpressionError? error)
    {
        PerFrameCode = PerFrameVm.Compile(code);
        error = PerFrameCode is null ? PerFrameVm.LastError : null;
        return PerFrameCode is not null;
    }

    public bool CompilePerPoint(string code, out ExpressionError? error)
    {
        PerPointCode = PerPointVm.Compile(code);
        error = PerPointCode is null ? PerPointVm.LastError : null;
        return PerPointCode is not null;
    }

    /// <summary>
    /// <c>LoadCustomWavePerFrameEvallibVars</c>（milkdropfs.cpp:2442-2463）。
    /// </summary>
    public void SyncPerFrame(in PerFrameContext ctx, PerFrameVariables perFrame, PresetWave wave)
    {
        _pf["time"].Value = ctx.Time;
        _pf["fps"].Value = ctx.Fps;
        _pf["frame"].Value = ctx.Frame;
        _pf["progress"].Value = ctx.Progress;
        _pf["bass"].Value = ctx.MySound.ImmRel[0];
        _pf["mid"].Value = ctx.MySound.ImmRel[1];
        _pf["treb"].Value = ctx.MySound.ImmRel[2];
        _pf["bass_att"].Value = ctx.MySound.AvgRel[0];
        _pf["mid_att"].Value = ctx.MySound.AvgRel[1];
        _pf["treb_att"].Value = ctx.MySound.AvgRel[2];

        CopyQFrom(perFrame);
        for (var i = 0; i < NumTVar; i++)
        {
            _t[i].Value = TValuesAfterInitCode[i];
        }

        _pf["r"].Value = wave.R;
        _pf["g"].Value = wave.G;
        _pf["b"].Value = wave.B;
        _pf["a"].Value = wave.A;
        _pf["samples"].Value = wave.Samples;
    }

    /// <summary>执行一次 per-frame 代码（milkdropfs.cpp:2541）；调用前先 <see cref="SyncPerFrame"/>。</summary>
    public void RunPerFrame() => PerFrameCode?.Execute();

    /// <summary>
    /// per-point 的只读量拷贝 + 每点输入（milkdropfs.cpp:2528-2546、2598-2606）。
    /// </summary>
    public void SyncPerPoint(double sample, double value1, double value2, double x, double y)
    {
        foreach (var name in ReadOnlyNames)
        {
            _pp[name].Value = _pf[name].Value;
        }

        for (var i = 0; i < PerFrameVariables.NumQVar; i++)
        {
            _ppQ[i].Value = _q[i].Value;
        }

        for (var i = 0; i < NumTVar; i++)
        {
            _ppT[i].Value = _t[i].Value;
        }

        _ppIn[0].Value = sample;
        _ppIn[1].Value = value1;
        _ppIn[2].Value = value2;
        _ppOut[0].Value = x;
        _ppOut[1].Value = y;
        _ppOut[2].Value = _pf["r"].Value;
        _ppOut[3].Value = _pf["g"].Value;
        _ppOut[4].Value = _pf["b"].Value;
        _ppOut[5].Value = _pf["a"].Value;
    }

    public void RunPerPoint() => PerPointCode?.Execute();

    /// <summary>per-point 代码写回的点坐标与颜色（对应 milkdropfs.cpp:2612-2619）。</summary>
    public (double X, double Y, double R, double G, double B, double A) ReadPerPointOutputs()
        => (_ppOut[0].Value, _ppOut[1].Value, _ppOut[2].Value, _ppOut[3].Value, _ppOut[4].Value, _ppOut[5].Value);

    /// <summary>per-frame 代码写回的波形颜色/采样数（<c>samples</c> 由调用方夹到 1..512）。</summary>
    public (double R, double G, double B, double A, double Samples) ReadPerFrameOutputs()
        => (_pf["r"].Value, _pf["g"].Value, _pf["b"].Value, _pf["a"].Value, _pf["samples"].Value);

    private void CopyQFrom(PerFrameVariables perFrame)
    {
        var source = perFrame.Q;
        for (var i = 0; i < _q.Length; i++)
        {
            _q[i].Value = source[i].Value;
        }
    }
}
