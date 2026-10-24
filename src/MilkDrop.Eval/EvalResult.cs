using System.Runtime.CompilerServices;

namespace MilkDrop.Eval;

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

    /// <summary>
    /// <see cref="Var"/> / <see cref="MemArray"/> 是否有意义（左值语义）。
    /// 绝大多数节点是纯值，靠这个标志位让 <see cref="Get"/>/<see cref="Set"/>
    /// 只判一次 bool，不用每次做两次 null 检查。
    /// </summary>
    public bool HasRef;

    public readonly bool IsRef => HasRef;

    /// <summary>读值：纯值节点走最短路（解释器里每个节点每次求值都要走一次）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly double Get()
        => HasRef
            ? (Var is not null ? Var.Value : MemArray![MemIndex])
            : Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(double value)
    {
        if (!HasRef)
        {
            Value = value;
        }
        else if (Var is not null)
        {
            Var.Value = value;
        }
        else
        {
            MemArray![MemIndex] = value;
        }
    }

    public static EvalResult Const(double value) => new() { Value = value, HasRef = false };

    public static EvalResult VarRef(Variable variable) => new() { Var = variable, HasRef = true };

    public static EvalResult MemRef(double[] array, int index) => new() { MemArray = array, MemIndex = index, HasRef = true };
}
