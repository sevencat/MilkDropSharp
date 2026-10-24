namespace MilkDrop.Eval;

/// <summary>内置函数库，对应 <c>TreeFunctions.c</c>。拆分为多个 partial 文件。</summary>
public static partial class IntrinsicFunctions
{
    public const double CompareCloseFactor = 0.00001;
    public const long MaxLoopCount = 1048576;

    private static long ToInt(double v) => (long)v;

    private static double ArgValue(EvalNode node, int index)
    {
        var r = default(EvalResult);
        node.Args[index]!.Eval(ref r);
        return r.Get();
    }

    private static (double A, double B) TwoArgs(EvalNode node)
    {
        var r1 = default(EvalResult);
        var r2 = default(EvalResult);
        node.Args[0]!.Eval(ref r1);
        node.Args[1]!.Eval(ref r2);
        return (r1.Get(), r2.Get());
    }

    /// <summary>MilkDrop 原始 Mersenne-Twister（MT19937），种子 0x4141f00d。</summary>
    internal static class Mt
    {
        private const int N = 624;
        private const int M = 397;
        private const uint MatrixA = 0x9908b0df;
        private const uint UpperMask = 0x80000000;
        private const uint LowerMask = 0x7fffffff;

        private static readonly uint[] State = new uint[N];
        private static int _mti;

        public static uint Next()
        {
            if (_mti == 0)
            {
                uint s = 0x4141f00d;
                State[0] = s;
                for (_mti = 1; _mti < N; _mti++)
                {
                    State[_mti] = (uint)(1812433253UL * (State[_mti - 1] ^ (State[_mti - 1] >> 30)) + (uint)_mti);
                }
            }

            if (_mti >= N)
            {
                uint y;
                int kk;
                for (kk = 0; kk < N - M; kk++)
                {
                    y = (State[kk] & UpperMask) | (State[kk + 1] & LowerMask);
                    State[kk] = State[kk + M] ^ (y >> 1) ^ ((y & 1) != 0 ? MatrixA : 0);
                }
                for (; kk < N - 1; kk++)
                {
                    y = (State[kk] & UpperMask) | (State[kk + 1] & LowerMask);
                    State[kk] = State[kk + (M - N)] ^ (y >> 1) ^ ((y & 1) != 0 ? MatrixA : 0);
                }
                y = (State[N - 1] & UpperMask) | (State[0] & LowerMask);
                State[N - 1] = State[M - 1] ^ (y >> 1) ^ ((y & 1) != 0 ? MatrixA : 0);
                _mti = 0;
            }

            uint result = State[_mti++];
            result ^= result >> 11;
            result ^= (result << 7) & 0x9d2c5680;
            result ^= (result << 15) & 0xefc60000;
            result ^= result >> 18;
            return result;
        }
    }

    // ---- 常量、变量、控制结构 ----

    private static void Const(EvalNode node, ref EvalResult ret) => ret = EvalResult.Const(node.Value);

    private static void Var(EvalNode node, ref EvalResult ret)
    {
        if (node.Var is not null)
        {
            ret = EvalResult.VarRef(node.Var);
        }
        else if (node.RefArray is not null)
        {
            ret = EvalResult.MemRef(node.RefArray, node.RefIndex);
        }
        else
        {
            ret = EvalResult.Const(0.0);
        }
    }

    private static void ExecuteList(EvalNode node, ref EvalResult ret)
    {
        EvalResult last = default;
        var item = node.List;
        while (item is not null)
        {
            last = default;
            item.Expr.Eval(ref last);
            item = item.Next;
        }
        ret = last;
    }

    private static void ExecuteLoop(EvalNode node, ref EvalResult ret)
    {
        EvalResult result = default;
        node.Args[0]!.Eval(ref result);
        long count = (long)result.Get();
        if (count > MaxLoopCount)
        {
            count = MaxLoopCount;
        }
        for (long i = 0; i < count; i++)
        {
            result = default;
            node.Args[1]!.Eval(ref result);
        }
        ret = result;
    }

    private static void ExecuteWhile(EvalNode node, ref EvalResult ret)
    {
        long remaining = MaxLoopCount;
        EvalResult result = default;
        do
        {
            result = default;
            node.Args[0]!.Eval(ref result);
        } while (Math.Abs(result.Get()) > CompareCloseFactor && --remaining > 0);
        ret = result;
    }

    private static void If(EvalNode node, ref EvalResult ret)
    {
        if (ArgValue(node, 0) != 0)
        {
            node.Args[1]!.Eval(ref ret);
        }
        else
        {
            node.Args[2]!.Eval(ref ret);
        }
    }

    private static void Exec2(EvalNode node, ref EvalResult ret)
    {
        var tmp = default(EvalResult);
        node.Args[0]!.Eval(ref tmp);
        node.Args[1]!.Eval(ref ret);
    }

    private static void Exec3(EvalNode node, ref EvalResult ret)
    {
        var tmp = default(EvalResult);
        node.Args[0]!.Eval(ref tmp);
        node.Args[1]!.Eval(ref tmp);
        node.Args[2]!.Eval(ref ret);
    }

    private static void Set(EvalNode node, ref EvalResult ret)
    {
        var left = default(EvalResult);
        node.Args[0]!.Eval(ref left);
        var right = default(EvalResult);
        node.Args[1]!.Eval(ref right);

        double value = right.Get();
        left.Set(value);
        ret = left.IsRef ? left : EvalResult.Const(value);
    }
}
