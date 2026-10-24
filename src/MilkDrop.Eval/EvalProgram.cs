namespace MilkDrop.Eval;

/// <summary>
/// 已编译程序句柄，对应 C 里的 <c>prjm_eval_program_t</c>。
/// 程序始终绑定到编译它的 <see cref="CompileContext"/>，上下文销毁后不可再使用。
/// </summary>
/// <remarks>
/// 默认走 <see cref="EvalVm"/>（栈式字节码）；<see cref="ExecuteTree"/> 保留原来的树遍历解释器，
/// 供对拍测试与回归排查使用。
/// </remarks>
public sealed class EvalProgram
{
    private readonly EvalVm? _vm;

    internal EvalProgram(CompileContext context, EvalNode? program)
    {
        Context = context;
        Program = program;
        _vm = program is null ? null : new EvalVm(BytecodeCompiler.Compile(program), context);
    }

    public CompileContext Context { get; }

    /// <summary>编译结果；空程序为 null。</summary>
    internal EvalNode? Program { get; }

    /// <summary>执行程序，返回顶层指令列表最后一条表达式的结果。</summary>
    public double Execute()
        => _vm is null ? 0.0 : _vm.Execute();

    /// <summary>用树遍历解释器执行；语义应与 <see cref="Execute"/> 完全一致。</summary>
    internal double ExecuteTree()
        => Program is null ? 0.0 : Context.Evaluate(Program);
}
