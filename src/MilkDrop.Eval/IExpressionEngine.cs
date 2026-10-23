namespace MilkDrop.Eval;

/// <summary>编译错误（含行列号，便于在 UI 里高亮预设表达式）。</summary>
public readonly record struct ExpressionError(string Message, int Line, int ColumnStart, int ColumnEnd)
{
    public override string ToString()
        => Line > 0 ? $"{Message} (line {Line}, col {ColumnStart}-{ColumnEnd})" : Message;
}

/// <summary>
/// 可写的 EEL 变量槽。热路径上（逐顶点）应持有具体实现类型，避免接口分派。
/// </summary>
public interface IEelVariable
{
    string Name { get; }

    double Value { get; set; }
}

/// <summary>一次编译产物，可反复执行且变量状态在多次执行之间保持。</summary>
public interface ICompiledExpression
{
    /// <summary>执行并返回最后一个表达式的值。</summary>
    double Execute();
}

/// <summary>
/// 一个 EEL 虚拟机（对应 <c>NSEEL_VMCTX</c>）。MilkDrop 需要 4 套：per-frame、per-vertex、
/// wave/shape、sprite（见 <c>state.cpp</c> 的变量注册）。
/// </summary>
public interface IExpressionVm
{
    string Name { get; }

    ExpressionError? LastError { get; }

    IEelVariable RegisterVariable(string name);

    void ResetVariables();

    /// <summary>编译代码；失败返回 null，错误见 <see cref="LastError"/>。</summary>
    ICompiledExpression? Compile(string code);
}

public interface IExpressionEngine
{
    string Name { get; }

    IExpressionVm CreateVm(string name);
}
