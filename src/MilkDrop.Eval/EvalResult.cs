namespace ProjectMEvalSharp;

/// <summary>
/// 表达式求值结果。对应 C 里通过 <c>PRJM_EVAL_F**</c> 传递的“值 / 引用”双重语义：
/// <list type="bullet">
/// <item>值语义：<see cref="Value"/> 保存一个临时数值。</item>
/// <item>引用语义（左值）：指向某个变量（<see cref="Var"/>）或某块内存（<see cref="MemArray"/> + <see cref="MemIndex"/>）。</item>
/// </list>
/// 通过 <c>ref EvalResult</c> 在函数间传递，等价于 C 里传递 <c>double**</c>。
/// </summary>
public struct EvalResult
{
    /// <summary>临时值（当没有任何引用目标时使用）。</summary>
    public double Value;

    /// <summary>变量引用目标，非空表示引用某个变量。</summary>
    public Variable? Var;

    /// <summary>内存引用目标块，非空表示引用某块内存。</summary>
    public double[]? MemArray;

    /// <summary>内存引用在块内的下标。</summary>
    public int MemIndex;

    public readonly bool IsRef => Var is not null || MemArray is not null;

    public readonly double Get()
    {
        if (Var is not null)
        {
            return Var.Value;
        }
        if (MemArray is not null)
        {
            return MemArray[MemIndex];
        }
        return Value;
    }

    public void Set(double value)
    {
        if (Var is not null)
        {
            Var.Value = value;
        }
        else if (MemArray is not null)
        {
            MemArray[MemIndex] = value;
        }
        else
        {
            Value = value;
        }
    }

    public static EvalResult Const(double value) => new() { Value = value };

    public static EvalResult VarRef(Variable variable) => new() { Var = variable };

    public static EvalResult MemRef(double[] array, int index) => new() { MemArray = array, MemIndex = index };
}
