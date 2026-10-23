namespace ProjectMEvalSharp;

/// <summary>
/// 表达式树节点的求值函数签名，等价于 C 里的
/// <c>void (*)(prjm_eval_exptreenode_t*, PRJM_EVAL_F**)</c>。
/// </summary>
public delegate void EvalFunc(EvalNode node, ref EvalResult ret);

/// <summary>
/// 内置函数描述，对应 C 里的 <c>prjm_eval_function_def_t</c>。
/// </summary>
public sealed class FunctionDef
{
    public string Name;
    public EvalFunc Func;
    public int ArgCount;
    public bool IsConstEval;
    public bool IsStateChanging;

    public FunctionDef(string name, EvalFunc func, int argCount, bool isConstEval, bool isStateChanging)
    {
        Name = name;
        Func = func;
        ArgCount = argCount;
        IsConstEval = isConstEval;
        IsStateChanging = isStateChanging;
    }
}
