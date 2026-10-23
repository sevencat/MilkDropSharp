using ProjectMEvalSharp;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// per-pixel 代码的表达式上下文：warp 网格的**每个顶点**执行一次，
/// 用来逐点改变 zoom/rot/warp 等变换参数。
/// </summary>
/// <remarks>
/// <para>对应 <c>MilkdropPreset/PerPixelContext.hpp:11-95</c> 与 <c>PerPixelContext.cpp</c>。</para>
/// <para>
/// 与 <see cref="PerFrameContext"/> 一样共享 <c>reg00..reg99</c>，但变量表独立；
/// <c>q1..q32</c> 每帧从 per-frame 上下文拷过来。
/// </para>
/// </remarks>
public sealed class PerPixelContext
{
    private readonly CompileContext _context;
    private readonly VariableReference[] _qVars = new VariableReference[Constants.QVarCount];

    private EvalProgram? _perPixelCodeHandle;

    /// <param name="globalRegisters">与 per-frame 上下文共享的 <c>reg00</c>..<c>reg99</c> 存储。</param>
    public PerPixelContext(double[] globalRegisters)
    {
        ArgumentNullException.ThrowIfNull(globalRegisters);
        _context = new CompileContext(globalRegisters);
    }

    /// <summary>底层编译/执行上下文。</summary>
    public CompileContext Context => _context;

    /// <summary>已编译的 per-pixel 代码（null 表示预设没有 per-pixel 代码）。</summary>
    public EvalProgram? PerPixelCodeHandle => _perPixelCodeHandle;

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
    public VariableReference X;
    public VariableReference Y;
    public VariableReference Rad;
    public VariableReference Ang;
    public VariableReference Frame;
    public VariableReference Progress;
    public VariableReference Meshx;
    public VariableReference Meshy;
    public VariableReference Pixelsx;
    public VariableReference Pixelsy;
    public VariableReference Aspectx;
    public VariableReference Aspecty;

    /// <summary>某个 Q 变量的槽位（<paramref name="index"/> 从 0 开始）。</summary>
    public VariableReference QVar(int index) => _qVars[index];

    /// <summary>注册所有内建变量（对应 <c>PerPixelContext::RegisterBuiltinVariables</c>）。</summary>
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
        X = Reg("x");
        Y = Reg("y");
        Rad = Reg("rad");
        Ang = Reg("ang");

        for (int q = 0; q < Constants.QVarCount; q++)
        {
            _qVars[q] = Reg("q" + (q + 1));
        }

        Progress = Reg("progress");
        Meshx = Reg("meshx");
        Meshy = Reg("meshy");
        Pixelsx = Reg("pixelsx");
        Pixelsy = Reg("pixelsy");
        Aspectx = Reg("aspectx");
        Aspecty = Reg("aspecty");
    }

    /// <summary>
    /// 装载每帧只读的变量（对应 <c>LoadStateReadOnlyVariables</c>）。
    /// </summary>
    /// <remarks>
    /// 这些值名义上是只读的，但表达式引擎没有只读标记，预设可以逐顶点改它们；
    /// 为了兼容性这里同样每帧重装一次。
    /// </remarks>
    public void LoadStateReadOnlyVariables(PresetState state, PerFrameContext perFrameContext)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(perFrameContext);

        Time.Value = perFrameContext.Time.Value;
        Fps.Value = perFrameContext.Fps.Value;
        Frame.Value = perFrameContext.Frame.Value;
        Progress.Value = perFrameContext.Progress.Value;
        Bass.Value = perFrameContext.Bass.Value;
        Mid.Value = perFrameContext.Mid.Value;
        Treb.Value = perFrameContext.Treb.Value;
        BassAtt.Value = perFrameContext.BassAtt.Value;
        MidAtt.Value = perFrameContext.MidAtt.Value;
        TrebAtt.Value = perFrameContext.TrebAtt.Value;

        Meshx.Value = state.RenderContext.PerPixelMeshX;
        Meshy.Value = state.RenderContext.PerPixelMeshY;
        Pixelsx.Value = state.RenderContext.ViewportSizeX;
        Pixelsy.Value = state.RenderContext.ViewportSizeY;
        Aspectx.Value = state.RenderContext.AspectX;
        Aspecty.Value = state.RenderContext.AspectY;
    }

    /// <summary>把 per-frame 执行后的 Q 变量拷进 per-pixel 上下文（对应 <c>LoadPerFrameQVariables</c>）。</summary>
    public void LoadPerFrameQVariables(PresetState state, PerFrameContext perFrameContext)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(perFrameContext);

        for (int q = 0; q < Constants.QVarCount; q++)
        {
            double value = perFrameContext.QVar(q).Value;
            state.FrameQVariables[q] = value;
            _qVars[q].Value = value;
        }
    }

    /// <summary>编译 per-pixel 代码（空代码跳过）。</summary>
    /// <exception cref="MilkdropCompileException">编译失败。</exception>
    public void CompilePerPixelCode(string perPixelCode)
    {
        ArgumentNullException.ThrowIfNull(perPixelCode);

        if (perPixelCode.Length == 0)
        {
            return;
        }

        _perPixelCodeHandle = _context.Compile(perPixelCode);
        if (_perPixelCodeHandle is null)
        {
            throw new MilkdropCompileException(
                "Could not compile per-pixel code: " + _context.Error + " (L" + _context.ErrorLine + ")");
        }
    }

    /// <summary>执行已编译的 per-pixel 代码（对应 <c>ExecutePerPixelCode</c>）。</summary>
    public void ExecutePerPixelCode() => _perPixelCodeHandle?.Execute();

    private VariableReference Reg(string name) => _context.RegisterVariable(name);
}
