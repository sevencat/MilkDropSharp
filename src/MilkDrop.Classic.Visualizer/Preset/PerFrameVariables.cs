using MilkDrop.Classic.Visualizer.Sound;
using MilkDrop.Eval;

namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary>
/// <c>CState::RegisterBuiltInVariables</c> 中 per-frame VM 那部分（state.cpp:264-348）的移植，
/// 以及 <c>LoadPerFrameEvallibVars</c>（milkdropfs.cpp:460-554）里已经有数据来源的那几个只读量。
/// </summary>
/// <remarks>
/// 变量**必须**在编译预设代码之前注册：注册名与预设里写的名字对不上时，
/// EEL 会把它们当成同一个 VM 内的自动变量，结果就是预设读到 0 而永远不动。
/// </remarks>
public sealed class PerFrameVariables
{
    /// <summary><c>NUM_Q_VAR</c>（state.h:49）。</summary>
    public const int NumQVar = 32;

    /// <summary>state.cpp:267-348 的完整注册顺序（q1..q32 单独循环注册）。</summary>
    private static readonly string[] Names =
    [
        "zoom", "zoomexp", "rot", "warp", "cx", "cy", "dx", "dy", "sx", "sy",
        "time", "fps", "bass", "mid", "treb", "bass_att", "mid_att", "treb_att", "frame",
        "decay",
        "wave_a", "wave_r", "wave_g", "wave_b", "wave_x", "wave_y", "wave_mystery", "wave_mode",
        "progress",
        "ob_size", "ob_r", "ob_g", "ob_b", "ob_a",
        "ib_size", "ib_r", "ib_g", "ib_b", "ib_a",
        "mv_x", "mv_y", "mv_dx", "mv_dy", "mv_l", "mv_r", "mv_g", "mv_b", "mv_a",
        "monitor",
        "echo_zoom", "echo_alpha", "echo_orient",
        "wave_usedots", "wave_thick", "wave_additive", "wave_brighten", "darken_center",
        "gamma", "wrap", "invert", "brighten", "darken", "solarize",
        "meshx", "meshy", "pixelsx", "pixelsy", "aspectx", "aspecty",
        "blur1_min", "blur2_min", "blur3_min", "blur1_max", "blur2_max", "blur3_max",
        "blur1_edge_darken",
    ];

    private readonly Dictionary<string, IEelVariable> _byName = new(StringComparer.Ordinal);
    private readonly IEelVariable[] _q = new IEelVariable[NumQVar];
    private readonly IEelVariable[] _warpIo = new IEelVariable[WarpIoOrder.Length];

    /// <summary>
    /// per-vertex 每帧要拷进自己 VM 的 10 个运动量（milkdropfs.cpp:1760-1769）。
    /// </summary>
    /// <remarks>
    /// 逐顶点热路径按这个固定顺序打包读取，避免每个顶点做 10 次字典查找（见 <see cref="ReadWarpIo"/>）。
    /// </remarks>
    public static readonly string[] WarpIoOrder =
    [
        "zoom", "zoomexp", "rot", "warp", "cx", "cy", "dx", "dy", "sx", "sy",
    ];

    public PerFrameVariables(IExpressionEngine engine)
    {
        Vm = engine.CreateVm("per_frame");

        foreach (var name in Names)
        {
            _byName[name] = Vm.RegisterVariable(name);
        }

        for (var i = 0; i < NumQVar; i++)
        {
            var name = $"q{i + 1}";
            _q[i] = Vm.RegisterVariable(name);
            _byName[name] = _q[i];
        }

        for (var i = 0; i < WarpIoOrder.Length; i++)
        {
            _warpIo[i] = _byName[WarpIoOrder[i]];
        }
    }

    /// <summary>逐顶点热路径：按 <see cref="WarpIoOrder"/> 的顺序取出 10 个值。</summary>
    public void ReadWarpIo(Span<double> destination)
    {
        for (var i = 0; i < _warpIo.Length; i++)
        {
            destination[i] = _warpIo[i].Value;
        }
    }

    /// <summary>逐顶点热路径：按 <see cref="WarpIoOrder"/> 的顺序写回 10 个值。</summary>
    public void WriteWarpIo(ReadOnlySpan<double> values)
    {
        for (var i = 0; i < _warpIo.Length; i++)
        {
            _warpIo[i].Value = values[i];
        }
    }

    public IExpressionVm Vm { get; }

    /// <summary><c>var_pf_q[]</c>。</summary>
    public IReadOnlyList<IEelVariable> Q => _q;

    /// <summary><c>q_values_after_init_code[]</c>（state.h）。</summary>
    public double[] QValuesAfterInitCode { get; } = new double[NumQVar];

    /// <summary><c>monitor_after_init_code</c>。</summary>
    public double MonitorAfterInitCode { get; set; }

    public IEelVariable this[string name] => _byName[name];

    public bool Contains(string name) => _byName.ContainsKey(name);

    public ICompiledExpression? Compile(string code) => Vm.Compile(code);

    /// <summary>
    /// 写入每帧开始时已知的只读量（milkdropfs.cpp:481-494）。
    /// </summary>
    /// <param name="time">预设已播放时长（原版 <c>GetTime() - m_fStartTime</c>）。</param>
    /// <param name="fps">当前帧率。</param>
    /// <param name="mySound"><c>mysound</c>：相对电平的来源。</param>
    /// <param name="frame">帧号。</param>
    /// <param name="progress">预设进度 0..1（需要预设调度时间，暂由调用方给）。</param>
    public void SyncReadOnly(float time, float fps, MySoundData mySound, long frame, double progress)
    {
        // 注意：bass/mid/treb 是**相对**电平（imm_rel），*_att 是 avg_rel。
        _byName["time"].Value = time;
        _byName["fps"].Value = fps;
        _byName["bass"].Value = mySound.ImmRel[0];
        _byName["mid"].Value = mySound.ImmRel[1];
        _byName["treb"].Value = mySound.ImmRel[2];
        _byName["bass_att"].Value = mySound.AvgRel[0];
        _byName["mid_att"].Value = mySound.AvgRel[1];
        _byName["treb_att"].Value = mySound.AvgRel[2];
        _byName["frame"].Value = frame;
        _byName["progress"].Value = progress;
    }

    /// <summary>
    /// 把所有变量清零。对应原版 <c>bReInit</c> 时 <c>RegisterBuiltInVariables</c> 重新注册
    /// （新建的 EEL 变量值都是 0），避免上一个预设的残留值漏进新预设的 init code。
    /// </summary>
    public void ResetValues()
    {
        foreach (var variable in _byName.Values)
        {
            variable.Value = 0;
        }
    }

    /// <summary>
    /// 把 init code 里写过的 <c>q1..q32</c> / <c>monitor</c> 回填成"每帧起始值"（milkdropfs.cpp:491-493）。
    /// </summary>
    public void SyncAfterInitCode()
    {
        for (var i = 0; i < NumQVar; i++)
        {
            _q[i].Value = QValuesAfterInitCode[i];
        }

        _byName["monitor"].Value = MonitorAfterInitCode;
    }

    /// <summary>
    /// 执行一次 <c>per_frame_init</c> 代码并回读 <c>q1..q32</c> / <c>monitor</c>
    /// （state.cpp:1665-1694）。
    /// </summary>
    /// <param name="code">已预处理的 init 代码。</param>
    /// <param name="syncBeforeExecute">
    /// 执行前的变量同步（原版在这里调用 <c>LoadPerFrameEvallibVars</c>，
    /// 因为 init code 也可能读 <c>bass</c>/<c>zoom</c> 等）。
    /// </param>
    /// <param name="error">编译失败时的错误。</param>
    public bool RunInitCode(string code, Action? syncBeforeExecute, out ExpressionError? error)
    {
        var program = Compile(code);
        if (program is null)
        {
            // 编译失败：q/monitor 一律归零（state.cpp:1676-1678）
            error = Vm.LastError;
            Array.Clear(QValuesAfterInitCode);
            MonitorAfterInitCode = 0;
            return false;
        }

        syncBeforeExecute?.Invoke();

        program.Execute();

        for (var i = 0; i < NumQVar; i++)
        {
            QValuesAfterInitCode[i] = _q[i].Value;
        }

        MonitorAfterInitCode = _byName["monitor"].Value;
        error = null;
        return true;
    }
}
