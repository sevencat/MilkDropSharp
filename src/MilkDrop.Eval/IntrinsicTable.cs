namespace ProjectMEvalSharp;

public static partial class IntrinsicFunctions
{
    /// <summary>内置函数表，与 C 的 <c>intrinsic_function_table</c> 一致。</summary>
    public static FunctionDef[] CreateTable()
    {
        return new[]
        {
            new FunctionDef("/*const*/", Const, 0, true, false),
            new FunctionDef("/*var*/", Var, 0, false, false),
            new FunctionDef("/*list*/", ExecuteList, 1, true, false),
            new FunctionDef("/*or*/", BitwiseOr, 2, true, false),
            new FunctionDef("/*and*/", BitwiseAnd, 2, true, false),

            new FunctionDef("if", If, 3, true, false),
            new FunctionDef("_if", If, 3, true, false),
            new FunctionDef("_and", BooleanAndOp, 2, true, false),
            new FunctionDef("_or", BooleanOrOp, 2, true, false),
            new FunctionDef("loop", ExecuteLoop, 2, true, false),
            new FunctionDef("while", ExecuteWhile, 1, true, false),

            new FunctionDef("_not", BNot, 1, true, false),
            new FunctionDef("bnot", BNot, 1, true, false),
            new FunctionDef("_equal", Equal, 2, true, false),
            new FunctionDef("equal", Equal, 2, true, false),
            new FunctionDef("_noteq", NotEqual, 2, true, false),
            new FunctionDef("_below", Below, 2, true, false),
            new FunctionDef("below", Below, 2, true, false),
            new FunctionDef("_above", Above, 2, true, false),
            new FunctionDef("above", Above, 2, true, false),
            new FunctionDef("_beleq", BelowEq, 2, true, false),
            new FunctionDef("_aboeq", AboveEq, 2, true, false),

            new FunctionDef("_set", Set, 2, false, true),
            new FunctionDef("assign", Set, 2, false, true),
            new FunctionDef("_add", Add, 2, true, false),
            new FunctionDef("_sub", Sub, 2, true, false),
            new FunctionDef("_mul", Mul, 2, true, false),
            new FunctionDef("_div", Div, 2, true, false),
            new FunctionDef("_mod", Mod, 2, true, false),
            new FunctionDef("_mulop", MulOp, 2, false, true),
            new FunctionDef("_divop", DivOp, 2, false, true),
            new FunctionDef("_orop", BitwiseOrOp, 2, false, true),
            new FunctionDef("_andop", BitwiseAndOp, 2, false, true),
            new FunctionDef("_addop", AddOp, 2, false, true),
            new FunctionDef("_subop", SubOp, 2, false, true),
            new FunctionDef("_modop", ModOp, 2, false, true),

            new FunctionDef("sin", Sin, 1, true, false),
            new FunctionDef("cos", Cos, 1, true, false),
            new FunctionDef("tan", Tan, 1, true, false),
            new FunctionDef("asin", Asin, 1, true, false),
            new FunctionDef("acos", Acos, 1, true, false),
            new FunctionDef("atan", Atan, 1, true, false),
            new FunctionDef("atan2", Atan2, 2, true, false),
            new FunctionDef("sqr", Sqr, 1, true, false),
            new FunctionDef("sqrt", Sqrt, 1, true, false),
            new FunctionDef("pow", Pow, 2, true, false),
            new FunctionDef("_powop", PowOp, 2, false, true),
            new FunctionDef("exp", Exp, 1, true, false),
            new FunctionDef("_neg", Neg, 1, true, false),

            new FunctionDef("log", Log, 1, true, false),
            new FunctionDef("log10", Log10, 1, true, false),
            new FunctionDef("abs", Abs, 1, true, false),
            new FunctionDef("min", Min, 2, true, false),
            new FunctionDef("max", Max, 2, true, false),
            new FunctionDef("sign", Sign, 1, true, false),
            new FunctionDef("rand", Rand, 1, false, false),
            new FunctionDef("floor", Floor, 1, true, false),
            new FunctionDef("int", Floor, 1, true, false),
            new FunctionDef("ceil", Ceil, 1, true, false),
            new FunctionDef("invsqrt", InvSqrt, 1, true, false),
            new FunctionDef("sigmoid", Sigmoid, 2, true, false),

            new FunctionDef("band", BooleanAndFunc, 2, true, false),
            new FunctionDef("bor", BooleanOrFunc, 2, true, false),

            new FunctionDef("exec2", Exec2, 2, true, false),
            new FunctionDef("exec3", Exec3, 3, true, false),
            new FunctionDef("_mem", Mem, 1, false, true),
            new FunctionDef("megabuf", Mem, 1, false, true),
            new FunctionDef("_gmem", Mem, 1, false, true),
            new FunctionDef("gmegabuf", Mem, 1, false, true),
            new FunctionDef("freembuf", FreeMemBuf, 1, false, true),
            new FunctionDef("memcpy", MemCpy, 3, false, true),
            new FunctionDef("memset", MemSet, 3, false, true),
        };
    }
}
