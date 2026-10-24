namespace MilkDrop.Eval;

public static partial class IntrinsicFunctions
{
    // ---- 数学函数 ----

    private static void Sin(EvalNode node, ref EvalResult ret) => ret = EvalResult.Const(Math.Sin(ArgValue(node, 0)));
    private static void Cos(EvalNode node, ref EvalResult ret) => ret = EvalResult.Const(Math.Cos(ArgValue(node, 0)));
    private static void Tan(EvalNode node, ref EvalResult ret) => ret = EvalResult.Const(Math.Tan(ArgValue(node, 0)));
    private static void Atan(EvalNode node, ref EvalResult ret) => ret = EvalResult.Const(Math.Atan(ArgValue(node, 0)));
    private static void Exp(EvalNode node, ref EvalResult ret) => ret = EvalResult.Const(Math.Exp(ArgValue(node, 0)));
    private static void Floor(EvalNode node, ref EvalResult ret) => ret = EvalResult.Const(Math.Floor(ArgValue(node, 0)));
    private static void Ceil(EvalNode node, ref EvalResult ret) => ret = EvalResult.Const(Math.Ceiling(ArgValue(node, 0)));
    private static void Abs(EvalNode node, ref EvalResult ret) => ret = EvalResult.Const(Math.Abs(ArgValue(node, 0)));

    private static void Sqr(EvalNode node, ref EvalResult ret)
    {
        double v = ArgValue(node, 0);
        ret = EvalResult.Const(v * v);
    }

    private static void Sqrt(EvalNode node, ref EvalResult ret)
        => ret = EvalResult.Const(Math.Sqrt(Math.Abs(ArgValue(node, 0))));

    private static void Asin(EvalNode node, ref EvalResult ret)
    {
        double v = ArgValue(node, 0);
        ret = EvalResult.Const(v < -1.0 || v > 1.0 ? 0.0 : Math.Asin(v));
    }

    private static void Acos(EvalNode node, ref EvalResult ret)
    {
        double v = ArgValue(node, 0);
        ret = EvalResult.Const(v < -1.0 || v > 1.0 ? 0.0 : Math.Acos(v));
    }

    private static void Atan2(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        ret = EvalResult.Const(Math.Atan2(a, b));
    }

    private static void Log(EvalNode node, ref EvalResult ret)
    {
        double v = ArgValue(node, 0);
        ret = EvalResult.Const(v <= 0.0 ? 0.0 : Math.Log(v));
    }

    private static void Log10(EvalNode node, ref EvalResult ret)
    {
        double v = ArgValue(node, 0);
        ret = EvalResult.Const(v <= 0.0 ? 0.0 : Math.Log10(v));
    }

    private static void Pow(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        if (Math.Abs(a) < CompareCloseFactor && b < 0)
        {
            ret = EvalResult.Const(0.0);
            return;
        }
        double r = Math.Pow(a, b);
        ret = EvalResult.Const(double.IsNaN(r) ? 0.0 : r);
    }

    private static void Sigmoid(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        double t = 1 + Math.Exp(-a * b);
        ret = EvalResult.Const(Math.Abs(t) > CompareCloseFactor ? 1.0 / t : 0.0);
    }

    private static void Min(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        ret = EvalResult.Const(a < b ? a : b);
    }

    private static void Max(EvalNode node, ref EvalResult ret)
    {
        var (a, b) = TwoArgs(node);
        ret = EvalResult.Const(a > b ? a : b);
    }

    private static void Sign(EvalNode node, ref EvalResult ret)
    {
        double v = ArgValue(node, 0);
        ret = EvalResult.Const(v == 0 ? 0.0 : (v < 0 ? -1.0 : 1.0));
    }

    private static void Rand(EvalNode node, ref EvalResult ret)
    {
        double max = Math.Floor(ArgValue(node, 0));
        if (max < 1.0)
        {
            max = 1.0;
        }
        ret = EvalResult.Const(Mt.Next() * (1.0 / 0xFFFFFFFF) * max);
    }

    private static void InvSqrt(EvalNode node, ref EvalResult ret)
    {
        double v = ArgValue(node, 0);
        double num2 = v * 0.5;
        ulong bits = (ulong)BitConverter.DoubleToInt64Bits(v);
        bits = 0x5fe6eb50c7b537a9UL - (bits >> 1);
        double y = BitConverter.Int64BitsToDouble((long)bits);
        y = y * (1.5 - num2 * y * y);
        ret = EvalResult.Const(double.IsNaN(y) ? 0.0 : y);
    }
}
