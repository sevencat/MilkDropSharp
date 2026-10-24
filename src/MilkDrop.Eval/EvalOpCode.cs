namespace MilkDrop.Eval;

/// <summary>
/// 字节码指令。把 <see cref="EvalNode"/> 树降成线性指令序列后，
/// 由 <see cref="EvalVm"/> 用栈式解释器执行，替代原来的树遍历 + 委托分派。
/// </summary>
/// <remarks>
/// 全部在 <c>int</c> / <c>double</c> 上操作，不涉及任何运行时代码生成，
/// 因此与 Native AOT 兼容（本库标了 <c>IsAotCompatible</c>）。
/// </remarks>
internal enum EvalOpCode : byte
{
    None = 0,

    // ---- 取值 ----
    Const,          // D：常量值                       -> +1
    LoadVar,        // A：变量槽                       -> +1（带引用）
    LoadReg,        // A：reg00~99 下标                -> +1（带引用）
    LoadMega,       // megabuf：栈顶为下标              -> 原地换成值（带引用）
    LoadGMega,      // gmegabuf：同上
    Pop,            // 丢弃栈顶（语句序列里的非末位表达式）

    // ---- 算术 ----
    Add, Sub, Mul, Div, Mod, Neg, Pow,

    // ---- 位运算 ----
    BOr, BAnd, BNot,

    // ---- 比较与逻辑 ----
    Equal, NotEqual, Below, Above, BelowEq, AboveEq,
    ToBool,         // |x| > eps ? 1 : 0（_and / _or 的分支结果）
    AndShort,       // _and：短路的 &&（靠 JzPush0 实现，仅代码生成期可见）
    OrShort,        // _or：短路的 ||（靠 JnzPush1 实现，仅代码生成期可见）
    AndFunc,        // band
    OrFunc,         // bor

    // ---- 数学 ----
    Min, Max, Sign, Floor, Ceil, Abs, Sqr, Sqrt, InvSqrt, Exp, Log, Log10,
    Sin, Cos, Tan, Asin, Acos, Atan, Atan2, Sigmoid, Rand,

    // ---- 赋值（栈顶为右值，次栈顶为左值引用）----
    Set, AddOp, SubOp, MulOp, DivOp, ModOp, BOrOp, BAndOp, PowOp,

    // ---- 内存 ----
    MemCpy, MemSet, FreeMemBuf,   // FreeMemBuf 在 C 里是 no-op，这里只保留参数的求值结果

    // ---- 顺序执行 ----
    Exec2, Exec3,

    // ---- 控制（A = 循环体结束地址，左开区间）----
    Loop,           // 前面已压入次数
    While,          // 循环体即条件表达式
    Jmp,
    JzExact,        // 弹出，值为 0 则跳转（if 的语义：精确判 0）
    JzPush0,        // 弹出，|x| <= eps 则压 0 并跳转（_and）
    JnzPush1,       // 弹出，|x| > eps 则压 1 并跳转（_or）

    // ---- 仅用于代码生成期，不会出现在最终指令流里 ----
    List,
    If,
}

/// <summary>一条指令。<see cref="Op"/> 之外的两个字段按指令含义互斥。</summary>
internal struct Instr
{
    public EvalOpCode Op;
    public int A;
    public double D;

    public Instr(EvalOpCode op, int a, double d)
    {
        Op = op;
        A = a;
        D = d;
    }
}

/// <summary>代码生成产物。</summary>
internal sealed class CompiledCode
{
    public CompiledCode(Instr[] code, int maxStack)
    {
        Code = code;
        MaxStack = maxStack;
    }

    public Instr[] Code { get; }

    /// <summary>执行所需的最大栈深。</summary>
    public int MaxStack { get; }
}
