namespace MilkDrop.Eval;

/// <summary>
/// 指令列表（<c>/*list*/</c>）节点使用的链表项，对应 <c>prjm_eval_exptreenode_list_item_t</c>。
/// </summary>
public sealed class ListNode
{
    public EvalNode Expr = null!;
    public ListNode? Next;
}

/// <summary>
/// 表达式树节点，对应 C 里的 <c>prjm_eval_exptreenode_t</c>。
/// 各字段按用途互斥（相当于 C 里的 union）：
/// <list type="bullet">
/// <item>常量节点：<see cref="Value"/>。</item>
/// <item>变量节点：<see cref="Var"/>。</item>
/// <item>列表节点：<see cref="List"/>。</item>
/// <item>内存访问节点：<see cref="Memory"/>。</item>
/// <item>普通函数节点：<see cref="Args"/>。</item>
/// </list>
/// </summary>
public sealed class EvalNode
{
    public EvalFunc Func = null!;

    /// <summary>
    /// 对应的字节码指令。由 <see cref="CompileContext"/> 在建节点时填好，
    /// <see cref="BytecodeCompiler"/> 据此把树降成指令；<see cref="EvalOpCode.None"/>
    /// 表示这是手工构造的节点，只能走树解释器。
    /// </summary>
    internal EvalOpCode Op;

    /// <summary><see cref="EvalOpCode.LoadVar"/> 的变量槽 / <see cref="EvalOpCode.LoadReg"/> 的寄存器下标。</summary>
    internal int Slot;

    public EvalNode?[] Args = Array.Empty<EvalNode>();

    public double Value;

    public Variable? Var;

    public double[]? RefArray;

    public int RefIndex;

    public ListNode? List;

    public MemoryBuffer? Memory;

    public void Eval(ref EvalResult ret) => Func(this, ref ret);
}
