using MilkDrop.Classic.Render;
using MilkDrop.Classic.Visualizer.Sound;
using MilkDrop.Eval;

namespace MilkDrop.Classic.Visualizer.Sprites;

/// <summary>
/// <c>td_tex</c>（<c>texmgr.h:60-89</c>）里"每个槽一份"的那部分：纹理 + 时间戳 + 专属 EEL VM。
/// </summary>
/// <remarks>
/// 变量注册顺序照抄 <c>texmgr.cpp:276-310</c>；<b>只有 27 个变量</b>，没有 <c>q1..q32</c>，
/// 也没有 <c>monitor</c> —— 所以 sprite 代码里写 <c>q1</c> 会变成 VM 内的自动变量（读 0），这是原版行为。
/// </remarks>
public sealed class SpriteSlot
{
    /// <summary><c>texmgr.cpp:282-309</c> 的注册顺序（先输入、后输出，顺序不影响语义但要一致）。</summary>
    private static readonly string[] VariableNames =
    [
        "time", "frame", "fps", "progress",
        "bass", "bass_att", "mid", "mid_att", "treb", "treb_att",
        "x", "y", "sx", "sy", "repeatx", "repeaty", "rot", "flipx", "flipy",
        "r", "g", "b", "a", "blendmode", "done", "burn",
    ];

    private readonly Dictionary<string, IEelVariable> _vars = new(StringComparer.Ordinal);

    public SpriteSlot(IExpressionEngine engine, int index)
    {
        Index = index;
        Vm = engine.CreateVm($"sprite{index:00}");

        foreach (var name in VariableNames)
        {
            _vars[name] = Vm.RegisterVariable(name);
        }

        ApplyDefaults();
    }

    public int Index { get; }

    public IExpressionVm Vm { get; }

    /// <summary><c>pSurface</c>；null = 这个槽是空的。</summary>
    public ITexture? Texture { get; internal set; }

    /// <summary><c>img_w</c>（解码后的原始像素宽，不是 2 的幂）。</summary>
    public int ImageWidth { get; internal set; }

    /// <summary><c>img_h</c>。</summary>
    public int ImageHeight { get; internal set; }

    /// <summary><c>szFileName</c>（已解析成绝对路径）。</summary>
    public string FileName { get; internal set; } = string.Empty;

    /// <summary><c>fStartTime</c>：spawn 时刻，<c>time</c> 变量从它算起。</summary>
    public float StartTime { get; internal set; }

    /// <summary><c>nStartFrame</c>。</summary>
    public long StartFrame { get; internal set; }

    /// <summary><c>nUserData</c>：spawn 时传进来的 sprite 编号（用于按编号 kill）。</summary>
    public int UserData { get; set; }

    /// <summary>init 代码原文（<c>init_1..N</c> 拼好、已剥注释）。</summary>
    public string InitCode { get; internal set; } = string.Empty;

    /// <summary>per-frame 代码原文（<c>code_1..N</c> 拼好、已剥注释）。</summary>
    public string FrameCodeText { get; internal set; } = string.Empty;

    /// <summary>已编译的 per-frame 代码（<c>m_codehandle</c>）；null = 没有代码或编译失败。</summary>
    public ICompiledExpression? FrameCode { get; internal set; }

    /// <summary>最近一次编译失败的信息（null = 正常）。</summary>
    public string? CodeError { get; internal set; }

    public bool IsActive => Texture is not null;

    /// <summary>按名字取变量（测试与绘制用）；名字不在表里则抛异常。</summary>
    public IEelVariable this[string name] => _vars[name];

    public double Get(string name) => _vars[name].Value;

    public void Set(string name, double value) => _vars[name].Value = value;

    /// <summary>
    /// 输出变量的默认值（<c>texmgr.cpp:216-231</c>）。只在 spawn 时写一次，
    /// 之后允许跨帧保持（这样 <c>sx+=0.01</c> 这类代码才有累积效果）。
    /// </summary>
    public void ApplyDefaults()
    {
        Set("x", 0.5);
        Set("y", 0.5);
        Set("sx", 1.0);
        Set("sy", 1.0);
        Set("repeatx", 1.0);
        Set("repeaty", 1.0);
        Set("rot", 0.0);
        Set("flipx", 0.0);
        Set("flipy", 0.0);
        Set("r", 1.0);
        Set("g", 1.0);
        Set("b", 1.0);
        Set("a", 1.0);
        Set("blendmode", 0.0);
        Set("done", 0.0);
        Set("burn", 1.0);
    }

    /// <summary>
    /// 每帧执行前的只读量（<c>milkdropfs.cpp:3479-3488</c>）。
    /// </summary>
    /// <param name="now">当前动画时间（<c>GetTime()</c>）。</param>
    /// <param name="fps">当前帧率。</param>
    /// <param name="mySound"><c>mysound</c>：<c>bass/mid/treb</c> 的相对电平。</param>
    /// <param name="progress">预设混合进度（<c>m_fBlendProgress</c>）。</param>
    /// <param name="frame">当前帧号。</param>
    public void SyncInputs(float now, float fps, MySoundData mySound, double progress, long frame)
    {
        Set("time", now - StartTime);
        Set("frame", frame - StartFrame);
        Set("fps", fps);
        Set("progress", progress);
        Set("bass", mySound.ImmRel[0]);
        Set("mid", mySound.ImmRel[1]);
        Set("treb", mySound.ImmRel[2]);
        Set("bass_att", mySound.AvgRel[0]);
        Set("mid_att", mySound.AvgRel[1]);
        Set("treb_att", mySound.AvgRel[2]);
    }

    /// <summary>执行 per-frame 代码（<c>milkdropfs.cpp:3490-3496</c>）。</summary>
    public void RunFrameCode() => FrameCode?.Execute();

    /// <summary>
    /// 编译 init 代码并执行一次（<c>texmgr.cpp:201-239</c>）。
    /// </summary>
    /// <remarks>
    /// 顺序必须是"重置变量 → 写默认值 → 编译 → 执行"：原版 <c>RegisterBuiltInVariables</c>
    /// 会重新注册（值归零），然后才写默认值，最后 <c>NSEEL_code_execute</c>。
    /// </remarks>
    public bool RunInitCode(string code, out ExpressionError? error)
    {
        InitCode = code;
        Vm.ResetVariables();
        ApplyDefaults();

        // 原版对"只有空格"的表达式会先清空再编译（texmgr.cpp:245-251）；空代码不算错误。
        if (string.IsNullOrWhiteSpace(code))
        {
            error = null;
            return true;
        }

        var program = Vm.Compile(code);
        if (program is null)
        {
            error = Vm.LastError;
            CodeError = error?.ToString();
            return false;
        }

        program.Execute();
        error = null;
        return true;
    }
}
