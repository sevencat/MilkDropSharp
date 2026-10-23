namespace ProjectMEvalSharp;

/// <summary>
/// 已编译程序句柄，对应 C 里的 <c>prjm_eval_program_t</c>。
/// 程序始终绑定到编译它的 <see cref="CompileContext"/>，上下文销毁后不可再使用。
/// </summary>
public sealed class EvalProgram
{
    internal EvalProgram(CompileContext context, EvalNode? program)
    {
        Context = context;
        Program = program;
    }

    public CompileContext Context { get; }

    /// <summary>编译结果；空程序为 null。</summary>
    internal EvalNode? Program { get; }

    /// <summary>执行程序，返回顶层指令列表最后一条表达式的结果。</summary>
    public double Execute()
        => Program is null ? 0.0 : Context.Evaluate(Program);
}
