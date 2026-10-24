using Xunit;

namespace ProjectMEvalSharp.Tests;

/// <summary>
/// 对拍：同一个表达式分别用栈式字节码 VM 与原来的树遍历解释器执行，结果必须一致。
/// 只挑确定性表达式（不用 rand / gmegabuf：前者随机，后者是跨上下文共享的静态内存）。
/// </summary>
public class VmDifferentialTests
{
    public static TheoryData<string> Expressions =>
        new()
        {
            "1 + 2 * 3",
            "(1 + 2) * 3 - 4 / 2",
            "x = 3; y = x * x + 1;",
            "x = 5; x += 2; x -= 1; x *= 3; x /= 2; x %= 4;",
            "x = 1; y = 2; (x = y) + 1;",
            "-3 + abs(-2) * sqr(3)",
            "min(3, 5) + max(3, 5) + sign(-7) + int(2.7) + ceil(2.1)",
            "sqrt(16) + pow(2, 3) + exp(0) + log(1) + log10(100)",
            "sin(0.5) + cos(0.5) + tan(0.5) + atan(1) + atan2(1, 2)",
            "asin(0.5) + acos(0.5) + invsqrt(4) + sigmoid(1, 2)",
            "if(1, 10, 20) + if(0, 10, 20)",
            "x = 0; if(equal(1, 1), x = 5, x = 9); x;",
            "x = 1; y = 0; if(x, y = 2, y = 3); y;",
            "band(1, 1) + band(1, 0) + bor(0, 0) + bor(0, 1)",
            "x = 0; _and(0, x = 1); x;",                 // 短路：右侧不该执行
            "x = 0; _or(1, x = 1); x;",                  // 短路：右侧不该执行
            "x = 0; _and(1, x = 1); x;",
            "x = 0; _or(0, x = 1); x;",
            "bnot(0) + bnot(1) + (3 | 5) + (7 & 3)",
            "x = 7; x |= 8; x &= 12; x;",
            "below(1, 2) + above(1, 2) + _beleq(2, 2) + _aboeq(2, 2)",
            "equal(1, 1) + equal(1, 2) + _noteq(1, 2)",
            "x = 0; loop(5, x = x + 1); x;",
            "x = 0; loop(0, x = 99); x;",
            "x = 0; loop(3, loop(2, x = x + 1)); x;",
            "x = 0; while(x = x + 1; below(x, 5);); x;",
            "x = 0; while(x = x + 1); x;",
            "exec2(1 + 1, 42) + exec3(1, 2, 3)",
            "megabuf(0) = 7; megabuf(0) + megabuf(1);",
            "megabuf(10) = 1; megabuf(11) = 2; megabuf(10) + megabuf(11);",
            "x = 0; loop(4, megabuf(x) = x; x = x + 1;); megabuf(3);",
            "megabuf(0) = 3; megabuf(0) += 4; megabuf(0) *= 2; megabuf(0);",
            "memset(0, 5, 4); megabuf(0) + megabuf(3);",
            "megabuf(0) = 9; memcpy(1, 0, 1); megabuf(1);",
            "reg01 = 4; reg02 = reg01 * 2; reg01 + reg02;",
            "x = 2; x = x * x; x = x * x; x;",
            "1; 2; 3;",                                   // 指令列表：取最后一条
            "x = 1; (x = 2; x + 10;);",
            "freembuf(0); 5;",
            "x = 0; loop(2, loop(2, loop(2, x = x + 1))); x;",
        };

    [Theory]
    [MemberData(nameof(Expressions))]
    public void BytecodeVmMatchesTreeInterpreter(string code)
    {
        var vmProgram = new CompileContext().Compile(code);
        var treeProgram = new CompileContext().Compile(code);

        Assert.NotNull(vmProgram);
        Assert.NotNull(treeProgram);

        Assert.Equal(treeProgram!.ExecuteTree(), vmProgram!.Execute());
    }
}
