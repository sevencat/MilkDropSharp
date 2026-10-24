namespace MilkDrop.Eval;

public static partial class IntrinsicFunctions
{
    // ---- 内存访问 ----

    private static void Mem(EvalNode node, ref EvalResult ret)
    {
        int index = (int)(ArgValue(node, 0) + 0.0001);
        var addr = node.Memory!.Address(index);
        ret = addr is { } a ? EvalResult.MemRef(a.Arr, a.Offset) : EvalResult.Const(0.0);
    }

    private static void FreeMemBuf(EvalNode node, ref EvalResult ret)
    {
        node.Args[0]!.Eval(ref ret);
        // prjm_eval_memory_free_block 在 C 里是 no-op。
    }

    private static void MemCpy(EvalNode node, ref EvalResult ret)
    {
        int dest = (int)(ArgValue(node, 0) + 0.0001);
        int src = (int)(ArgValue(node, 1) + 0.0001);
        int count = (int)(ArgValue(node, 2) + 0.0001);
        node.Memory!.Copy(dest, src, count);
        // C 版返回 dest 索引值的引用（dest_index_ptr），此处以值语义返回索引。
        ret = EvalResult.Const(dest);
    }

    private static void MemSet(EvalNode node, ref EvalResult ret)
    {
        int dest = (int)(ArgValue(node, 0) + 0.0001);
        double value = ArgValue(node, 1);
        int count = (int)(ArgValue(node, 2) + 0.0001);
        node.Memory!.Set(dest, value, count);
        ret = EvalResult.Const(dest);
    }

    // ---- 比较与逻辑 ----

    private static void BNot(EvalNode node, ref EvalResult ret)
        => ret = EvalResult.Const(Math.Abs(ArgValue(node, 0)) < CompareCloseFactor ? 1.0 : 0.0);

    private static void Equal(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        ret = EvalResult.Const(Math.Abs(a - b) < CompareCloseFactor ? 1.0 : 0.0);
    }

    private static void NotEqual(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        ret = EvalResult.Const(Math.Abs(a - b) > CompareCloseFactor ? 1.0 : 0.0);
    }

    private static void Below(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        ret = EvalResult.Const(a < b ? 1.0 : 0.0);
    }

    private static void Above(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        ret = EvalResult.Const(a > b ? 1.0 : 0.0);
    }

    private static void BelowEq(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        ret = EvalResult.Const(a <= b ? 1.0 : 0.0);
    }

    private static void AboveEq(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        ret = EvalResult.Const(a >= b ? 1.0 : 0.0);
    }

    private static void BooleanAndOp(EvalNode node, ref EvalResult ret)
    {
        if (Math.Abs(ArgValue(node, 0)) > CompareCloseFactor)
        {
            ret = EvalResult.Const(Math.Abs(ArgValue(node, 1)) > CompareCloseFactor ? 1.0 : 0.0);
        }
        else
        {
            ret = EvalResult.Const(0.0);
        }
    }

    private static void BooleanOrOp(EvalNode node, ref EvalResult ret)
    {
        if (Math.Abs(ArgValue(node, 0)) < CompareCloseFactor)
        {
            ret = EvalResult.Const(Math.Abs(ArgValue(node, 1)) > CompareCloseFactor ? 1.0 : 0.0);
        }
        else
        {
            ret = EvalResult.Const(1.0);
        }
    }

    private static void BooleanAndFunc(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        ret = EvalResult.Const(Math.Abs(a) > CompareCloseFactor && Math.Abs(b) > CompareCloseFactor ? 1.0 : 0.0);
    }

    private static void BooleanOrFunc(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        ret = EvalResult.Const(Math.Abs(a) > CompareCloseFactor || Math.Abs(b) > CompareCloseFactor ? 1.0 : 0.0);
    }

    // ---- 算术 ----

    private static void Add(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        ret = EvalResult.Const(a + b);
    }

    private static void Sub(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        ret = EvalResult.Const(a - b);
    }

    private static void Mul(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        ret = EvalResult.Const(a * b);
    }

    private static void Div(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        ret = EvalResult.Const(Math.Abs(b) < CompareCloseFactor ? 0.0 : a / b);
    }

    private static void Mod(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        long divisor = ToInt(b);
        ret = EvalResult.Const(divisor == 0 ? 0.0 : (double)(ToInt(a) % divisor));
    }

    private static void BitwiseOr(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        ret = EvalResult.Const((double)(ToInt(a) | ToInt(b)));
    }

    private static void BitwiseAnd(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        ret = EvalResult.Const((double)(ToInt(a) & ToInt(b)));
    }

    private static void Neg(EvalNode node, ref EvalResult ret)
        => ret = EvalResult.Const(-ArgValue(node, 0));

    // ---- 复合赋值（第一个参数为左值引用）----

    private static void AddOp(EvalNode node, ref EvalResult ret)
    {
        var left = default(EvalResult);
        node.Args[0]!.Eval(ref left);
        double result = left.Get() + ArgValue(node, 1);
        left.Set(result);
        ret = left.IsRef ? left : EvalResult.Const(result);
    }

    private static void SubOp(EvalNode node, ref EvalResult ret)
    {
        var left = default(EvalResult);
        node.Args[0]!.Eval(ref left);
        double result = left.Get() - ArgValue(node, 1);
        left.Set(result);
        ret = left.IsRef ? left : EvalResult.Const(result);
    }

    private static void MulOp(EvalNode node, ref EvalResult ret)
    {
        var left = default(EvalResult);
        node.Args[0]!.Eval(ref left);
        double result = left.Get() * ArgValue(node, 1);
        left.Set(result);
        ret = left.IsRef ? left : EvalResult.Const(result);
    }

    private static void DivOp(EvalNode node, ref EvalResult ret)
    {
        var left = default(EvalResult);
        node.Args[0]!.Eval(ref left);
        double b = ArgValue(node, 1);
        double result = Math.Abs(b) < CompareCloseFactor ? 0.0 : left.Get() / b;
        left.Set(result);
        ret = left.IsRef ? left : EvalResult.Const(result);
    }

    private static void ModOp(EvalNode node, ref EvalResult ret)
    {
        var left = default(EvalResult);
        node.Args[0]!.Eval(ref left);
        long divisor = ToInt(ArgValue(node, 1));
        double result = divisor == 0 ? 0.0 : (double)(ToInt(left.Get()) % divisor);
        left.Set(result);
        ret = left.IsRef ? left : EvalResult.Const(result);
    }

    private static void BitwiseOrOp(EvalNode node, ref EvalResult ret)
    {
        var left = default(EvalResult);
        node.Args[0]!.Eval(ref left);
        double result = (double)(ToInt(left.Get()) | ToInt(ArgValue(node, 1)));
        left.Set(result);
        ret = left.IsRef ? left : EvalResult.Const(result);
    }

    private static void BitwiseAndOp(EvalNode node, ref EvalResult ret)
    {
        var left = default(EvalResult);
        node.Args[0]!.Eval(ref left);
        double result = (double)(ToInt(left.Get()) & ToInt(ArgValue(node, 1)));
        left.Set(result);
        ret = left.IsRef ? left : EvalResult.Const(result);
    }

    private static void PowOp(EvalNode node, ref EvalResult ret)
    {
        var left = default(EvalResult);
        node.Args[0]!.Eval(ref left);
        double a = left.Get();
        double b = ArgValue(node, 1);
        double result;
        if (Math.Abs(a) < CompareCloseFactor && b < 0)
        {
            result = 0.0;
        }
        else
        {
            result = Math.Pow(a, b);
            if (double.IsNaN(result))
            {
                result = 0.0;
            }
        }
        left.Set(result);
        ret = left.IsRef ? left : EvalResult.Const(result);
    }
}
