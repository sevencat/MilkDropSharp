using MilkDrop.Classic.Visualizer.Sound;
using MilkDrop.Eval;

namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary>
/// per-vertex（原版叫 "per-pixel"，<c>m_szPerPixelExpr</c>）VM 的变量注册
/// （state.cpp:353-390）与逐顶点求值（milkdropfs.cpp:1750-1792）。
/// </summary>
/// <remarks>
/// 与 per-frame VM 的差别（不能想当然地照抄）：
/// <list type="number">
///   <item><c>x</c>/<c>y</c>/<c>rad</c>/<c>ang</c> 是只读的网格信息，每个顶点不同；</item>
///   <item><c>time</c>/<c>fps</c>/<c>frame</c>/<c>progress</c>/<c>bass</c>…/<c>mesh*</c>/<c>pixels*</c>/<c>aspect*</c>
///   每帧只灌一次（milkdropfs.cpp:640-655）；</item>
///   <item><c>zoom</c>…<c>sy</c> 这 10 个量在每个顶点开头从 per-frame VM 拷进来，
///   执行完再把结果读回去算 UV（<b>不回写</b>状态）；</item>
///   <item><c>q1..q32</c> 是<b>独立</b>的一套 —— 与 per-frame 的 q 不共享，跨顶点/跨帧累积。</item>
/// </list>
/// </remarks>
public sealed class PerVertexVariables
{
    /// <summary>state.cpp:355-390 的注册顺序（q1..q32 单独循环注册）。</summary>
    private static readonly string[] Names =
    [
        "zoom", "zoomexp", "rot", "warp", "cx", "cy", "dx", "dy", "sx", "sy",
        "time", "fps", "bass", "mid", "treb", "bass_att", "mid_att", "treb_att", "frame",
        "x", "y", "rad", "ang",
        "progress", "meshx", "meshy", "pixelsx", "pixelsy", "aspectx", "aspecty",
    ];

    private readonly Dictionary<string, IEelVariable> _byName = new(StringComparer.Ordinal);
    private readonly IEelVariable[] _q = new IEelVariable[PerFrameVariables.NumQVar];

    // 逐顶点热路径用的直接引用（65×65 网格下每帧 4k+ 次调用，不能再走字典查找）
    private readonly IEelVariable _vx;
    private readonly IEelVariable _vy;
    private readonly IEelVariable _vRad;
    private readonly IEelVariable _vAng;
    private readonly IEelVariable[] _warpIo = new IEelVariable[PerFrameVariables.WarpIoOrder.Length];

    /// <summary>per-frame → per-vertex 的 10 个量在这里中转（原来是每顶点 stackalloc，每帧 4k+ 次清零）。</summary>
    private readonly double[] _ioBuffer = new double[PerFrameVariables.WarpIoOrder.Length];

    private const int IoZoom = 0;
    private const int IoZoomExp = 1;
    private const int IoRot = 2;
    private const int IoWarp = 3;
    private const int IoCx = 4;
    private const int IoCy = 5;
    private const int IoDx = 6;
    private const int IoDy = 7;
    private const int IoSx = 8;
    private const int IoSy = 9;

    public PerVertexVariables(IExpressionEngine engine)
    {
        Vm = engine.CreateVm("per_vertex");

        foreach (var name in Names)
        {
            _byName[name] = Vm.RegisterVariable(name);
        }

        for (var i = 0; i < PerFrameVariables.NumQVar; i++)
        {
            var name = $"q{i + 1}";
            _q[i] = Vm.RegisterVariable(name);
            _byName[name] = _q[i];
        }

        _vx = _byName["x"];
        _vy = _byName["y"];
        _vRad = _byName["rad"];
        _vAng = _byName["ang"];

        for (var i = 0; i < PerFrameVariables.WarpIoOrder.Length; i++)
        {
            _warpIo[i] = _byName[PerFrameVariables.WarpIoOrder[i]];
        }
    }

    public IExpressionVm Vm { get; }

    /// <summary>per-vertex 的 <c>q1..q32</c>（与 per-frame 的 q 无关）。</summary>
    public IReadOnlyList<IEelVariable> Q => _q;

    /// <summary>已编译的 per-vertex 代码（<c>m_pp_codehandle</c>）。</summary>
    public ICompiledExpression? Code { get; set; }

    public IEelVariable this[string name] => _byName[name];

    public bool Contains(string name) => _byName.ContainsKey(name);

    public ICompiledExpression? Compile(string code) => Vm.Compile(code);

    /// <summary>全部变量清零（<c>bReInit</c> 时重新注册内置变量的等价物）。</summary>
    public void ResetValues()
    {
        foreach (var variable in _byName.Values)
        {
            variable.Value = 0;
        }
    }

    /// <summary>
    /// 每帧一次：把只读量灌进来（milkdropfs.cpp:638-655）。
    /// </summary>
    public void SyncOncePerFrame(in PerFrameContext ctx)
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
        _byName["meshx"].Value = ctx.GridX;
        _byName["meshy"].Value = ctx.GridY;
        _byName["pixelsx"].Value = ctx.Width;
        _byName["pixelsy"].Value = ctx.Height;
        _byName["aspectx"].Value = ctx.InvAspectX;
        _byName["aspecty"].Value = ctx.InvAspectY;
    }

    /// <summary>
    /// 求一个顶点的 warp 参数：灌入网格信息 + per-frame 的运动量 → 执行 → 读回结果
    /// （milkdropfs.cpp:1756-1791）。
    /// </summary>
    /// <param name="baseParams">本帧的运动量（已经 <c>eval(-1)</c>）。</param>
    /// <param name="perFrame">per-frame VM；10 个 i/o 量从这里取"执行完 per-frame 之后"的值。</param>
    /// <param name="vx">网格坐标 −1..1。</param>
    /// <param name="vy">网格坐标 −1..1。</param>
    /// <param name="rad"><c>sqrt(vx²·aspectX² + vy²·aspectY²)</c>。</param>
    /// <param name="ang"><c>atan2(vy·aspectY, vx·aspectX)</c>（网格中心点为 0）。</param>
    /// <param name="aspectX">像素等比空间的 X 压缩系数。</param>
    /// <param name="aspectY">像素等比空间的 Y 压缩系数。</param>
    public WarpParams Evaluate(
        in WarpParams baseParams,
        PerFrameVariables perFrame,
        float vx,
        float vy,
        float rad,
        float ang,
        float aspectX,
        float aspectY)
    {
        // 只读的网格信息（milkdropfs.cpp:1756-1759）
        _vx.Value = (vx * 0.5f * aspectX) + 0.5f;
        _vy.Value = (vy * -0.5f * aspectY) + 0.5f;
        _vRad.Value = rad;
        _vAng.Value = ang;

        // 每个顶点重新灌入 per-frame 的结果（milkdropfs.cpp:1760-1769）
        var io = _ioBuffer;
        perFrame.ReadWarpIo(io);
        for (var i = 0; i < io.Length; i++)
        {
            _warpIo[i].Value = io[i];
        }

        Code?.Execute();

        return baseParams with
        {
            Zoom = (float)_warpIo[IoZoom].Value,
            ZoomExp = (float)_warpIo[IoZoomExp].Value,
            Rot = (float)_warpIo[IoRot].Value,
            Warp = (float)_warpIo[IoWarp].Value,
            CX = (float)_warpIo[IoCx].Value,
            CY = (float)_warpIo[IoCy].Value,
            DX = (float)_warpIo[IoDx].Value,
            DY = (float)_warpIo[IoDy].Value,
            SX = (float)_warpIo[IoSx].Value,
            SY = (float)_warpIo[IoSy].Value,
        };
    }


}
