using ProjectMEvalSharp;

namespace MilkDrop.Eval;

/// <summary>
/// 基于 <c>ProjectMEvalSharp</c>（projectm-eval 的纯托管移植）的引擎实现。
/// 语义与 ns-eel2 对齐情况见 <c>cbplan.md</c> 附录 A。
/// </summary>
public sealed class ProjectMEvalEngine : IExpressionEngine
{
    public string Name => "ProjectMEvalSharp";

    public IExpressionVm CreateVm(string name) => new ProjectMEvalVm(name);
}

/// <summary>变量槽：直接持有 <see cref="Variable"/>，逐顶点写入时只做一次字段赋值。</summary>
public sealed class ProjectMEvalVariable : IEelVariable
{
    private readonly VariableReference _reference;

    internal ProjectMEvalVariable(string name, VariableReference reference)
    {
        Name = name;
        _reference = reference;
        Handle = reference.Variable;
    }

    public string Name { get; }

    /// <summary>普通变量对象；<c>regNN</c> 寄存器槽为 null。</summary>
    public Variable? Handle { get; }

    public double Value
    {
        get => Handle?.Value ?? _reference.Value;
        set
        {
            if (Handle is not null)
            {
                Handle.Value = value;
            }
            else
            {
                _reference.Value = value;
            }
        }
    }
}

public sealed class ProjectMEvalVm : IExpressionVm
{
    private readonly CompileContext _context = new();

    public ProjectMEvalVm(string name) => Name = name;

    public string Name { get; }

    public ExpressionError? LastError { get; private set; }

    public CompileContext Context => _context;

    public IEelVariable RegisterVariable(string name) => new ProjectMEvalVariable(name, _context.RegisterVariable(name));

    /// <summary>批量注册（逐顶点热路径用），返回具体类型以便直接写字段。</summary>
    public ProjectMEvalVariable[] RegisterVariables(params string[] names)
    {
        var result = new ProjectMEvalVariable[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            result[i] = new ProjectMEvalVariable(names[i], _context.RegisterVariable(names[i]));
        }

        return result;
    }

    public void ResetVariables() => _context.ResetVariables();

    public ICompiledExpression? Compile(string code)
    {
        LastError = null;
        var program = _context.Compile(code);
        if (program is null)
        {
            LastError = new ExpressionError(
                _context.Error ?? "unknown compile error",
                _context.ErrorLine,
                _context.ErrorColumnStart,
                _context.ErrorColumnEnd);
            return null;
        }

        return new ProjectMEvalProgram(program);
    }

    private sealed class ProjectMEvalProgram : ICompiledExpression
    {
        private readonly EvalProgram _program;

        public ProjectMEvalProgram(EvalProgram program) => _program = program;

        public double Execute() => _program.Execute();
    }
}
