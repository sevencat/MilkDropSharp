namespace MilkDrop.Eval;

/// <summary>
/// 编译期错误，对应 C 里写入 <c>prjm_eval_compiler_error_t</c> 的信息。
/// <see cref="CompileContext.Compile"/> 会捕获该异常并保存到上下文中，与 C 版行为一致。
/// </summary>
public sealed class CompileException : Exception
{
    public int Line { get; }

    public int Column { get; }

    public int EndColumn { get; }

    public CompileException(string message, int line = 0, int column = 0, int endColumn = 0)
        : base(message)
    {
        Line = line;
        Column = column;
        EndColumn = endColumn;
    }
}
