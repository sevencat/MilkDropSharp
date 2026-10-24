namespace MilkDrop.Eval;

/// <summary>编译期节点类型，对应 <c>prjm_eval_compiler_node_type_t</c>。</summary>
internal enum CompilerNodeType
{
    /// <summary>普通表达式节点。</summary>
    Expression,

    /// <summary><c>/*list*/</c> 指令列表节点。</summary>
    InstructionList,
}

/// <summary>
/// 编译期节点，对应 C 里的 <c>prjm_eval_compiler_node_t</c>。
/// 在生成 <see cref="EvalNode"/> 的同时携带常量折叠/状态变更标记，
/// 供指令列表优化（丢弃无副作用的中间表达式）使用。
/// </summary>
internal sealed class CompilerNode
{
    public CompilerNodeType Type;

    /// <summary>生成的表达式树节点；被移动到父节点后可能为 null。</summary>
    public EvalNode? TreeNode;

    /// <summary>本节点自身是否为常量表达式（不含变量、无副作用）。</summary>
    public bool InstrIsConstExpr;

    /// <summary>本节点自身是否会改变执行状态（写变量/内存）。</summary>
    public bool InstrIsStateChanging;

    /// <summary>整条指令列表是否全部由常量表达式组成。</summary>
    public bool ListIsConstExpr;

    /// <summary>整条指令列表中是否至少有一个节点会改变执行状态。</summary>
    public bool ListIsStateChanging;

    /// <summary>按函数定义初始化四个标记。</summary>
    public static CompilerNode FromFunction(EvalNode treeNode, FunctionDef function, CompilerNodeType type = CompilerNodeType.Expression)
    {
        return new CompilerNode
        {
            Type = type,
            TreeNode = treeNode,
            InstrIsConstExpr = function.IsConstEval,
            InstrIsStateChanging = function.IsStateChanging,
            ListIsConstExpr = function.IsConstEval,
            ListIsStateChanging = function.IsStateChanging,
        };
    }
}
