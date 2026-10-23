using Xunit;

namespace ProjectMEvalSharp.Tests;

public class CompilerTests : TreeFunctionsTestBase
{
    private static double CompileAndExecute(CompileContext ctx, string code)
    {
        var program = ctx.Compile(code);
        Assert.NotNull(program);
        return program!.Execute();
    }

    // ---- 语法与字面量（SyntaxTest）----

    [Theory]
    [InlineData("1.0", 1.0)]
    [InlineData("2.0", 2.0)]
    [InlineData(".1", 0.1)]
    [InlineData("0.2", 0.2)]
    [InlineData("1e1", 10.0)]
    [InlineData("1E2", 100.0)]
    [InlineData("2.0e1", 20.0)]
    [InlineData("2.0E2", 200.0)]
    [InlineData("100", 100.0)]
    [InlineData("2147483648", (double)int.MinValue)]
    [InlineData("2147483648.0", 2147483648.0)]
    public void ParsesNumericLiterals(string code, double expected)
    {
        var ctx = new CompileContext();
        AssertAlmostEqual(expected, CompileAndExecute(ctx, code));
    }

    [Fact]
    public void ReportsSyntaxErrorForUnexpectedToken()
    {
        var ctx = new CompileContext();
        var program = ctx.Compile("k1 =  is_\nbeat*equal(index%2,0);");

        Assert.Null(program);
        Assert.Equal("syntax error, unexpected VAR", ctx.Error);
    }

    [Fact]
    public void EmptyProgramCompilesAndEvaluatesToZero()
    {
        var ctx = new CompileContext();
        AssertAlmostEqual(0.0, CompileAndExecute(ctx, ""));
        AssertAlmostEqual(0.0, CompileAndExecute(ctx, "   /* comment */  "));
    }

    [Fact]
    public void RejectsEmptyFunctionArgumentList()
    {
        var ctx = new CompileContext();
        Assert.Null(ctx.Compile("sin()"));
        Assert.Equal("syntax error, unexpected ')'", ctx.Error);
    }

    [Fact]
    public void ReportsInvalidArgumentCount()
    {
        var ctx = new CompileContext();
        Assert.Null(ctx.Compile("sin(1,2)"));
        Assert.Equal("Invalid argument count for function \"sin\": Expected 1, but 2 given.", ctx.Error);
    }

    [Fact]
    public void AllowsTrailingSemicolonsAndEmptyParentheses()
    {
        var ctx = new CompileContext();
        AssertAlmostEqual(2.0, CompileAndExecute(ctx, "1;2;"));
        AssertAlmostEqual(1.0, CompileAndExecute(ctx, "1;()"));
    }

    // ---- 优先级（PrecedenceTest）----

    [Fact]
    public void DivisionAndMultiplicationAreLeftAssociative()
    {
        var ctx = new CompileContext();
        double r1 = CompileAndExecute(ctx, "3.0 * 2.0 / 5.0 * 7.0;");
        double r2 = CompileAndExecute(ctx, "2.0 / 5.0 * 3.0 * 7.0;");
        double r3 = CompileAndExecute(ctx, "3.0 * 7.0 * 2.0 / 5.0;");

        AssertAlmostEqual(8.4, r1);
        AssertAlmostEqual(r1, r2);
        AssertAlmostEqual(r2, r3);
    }

    [Fact]
    public void ModuloBindsTighterThanMultiplication()
    {
        var ctx = new CompileContext();
        double r1 = CompileAndExecute(ctx, "3.0 * 2.0 % 5.0 * 7.0");
        double r2 = CompileAndExecute(ctx, "2.0 % 5.0 * 3.0 * 7.0");
        double r3 = CompileAndExecute(ctx, "3.0 * 7.0 * 2.0 % 5.0");

        AssertAlmostEqual(42.0, r1);
        AssertAlmostEqual(r1, r2);
        AssertAlmostEqual(r2, r3);
    }

    [Theory]
    [InlineData("2^10", 1024.0)]
    [InlineData("2^3^2", 64.0)]       // '^' 左结合
    [InlineData("5%3", 2.0)]
    [InlineData("-2^2", 4.0)]         // 一元负号优先级高于 '^'
    [InlineData("2^-2", 0.25)]
    [InlineData("+2*3", 6.0)]
    [InlineData("1<2", 1.0)]
    [InlineData("2<1", 0.0)]
    [InlineData("2>1", 1.0)]
    [InlineData("1<=1", 1.0)]
    [InlineData("1>=2", 0.0)]
    [InlineData("1==1", 1.0)]
    [InlineData("1!=1", 0.0)]
    [InlineData("6|3", 7.0)]
    [InlineData("6&3", 2.0)]
    [InlineData("1||0", 1.0)]
    [InlineData("0||0", 0.0)]
    [InlineData("1&&0", 0.0)]
    [InlineData("1&&1", 1.0)]
    [InlineData("!0", 1.0)]
    [InlineData("!5", 0.0)]
    [InlineData("!1+1", 1.0)]         // '!' 优先于 '+'
    [InlineData("!1^0", 0.0)]         // '^' 优先于 '!'
    [InlineData("1?2:3", 2.0)]
    [InlineData("0?2:3", 3.0)]
    [InlineData("1||0?5:6", 5.0)]     // '||' 优先于 '?:'
    [InlineData("0?1:0?2:3", 3.0)]    // '?:' 右结合
    public void OperatorPrecedence(string code, double expected)
    {
        var ctx = new CompileContext();
        AssertAlmostEqual(expected, CompileAndExecute(ctx, code));
    }

    [Fact]
    public void AssignmentIsRightAssociative()
    {
        var ctx = new CompileContext();
        AssertAlmostEqual(7.0, CompileAndExecute(ctx, "a = b = 7; a"));
        AssertAlmostEqual(7.0, CompileAndExecute(ctx, "b"));
    }

    [Theory]
    [InlineData("x = 3; x += 4; x", 7.0)]
    [InlineData("x = 3; x -= 4; x", -1.0)]
    [InlineData("x = 3; x *= 4; x", 12.0)]
    [InlineData("x = 12; x /= 4; x", 3.0)]
    [InlineData("x = 7; x %= 4; x", 3.0)]
    [InlineData("x = 6; x |= 3; x", 7.0)]
    [InlineData("x = 6; x &= 3; x", 2.0)]
    [InlineData("x = 2; x ^= 3; x", 8.0)]
    [InlineData("x = 1; x += 2 * 3; x", 7.0)]
    public void CompoundAssignment(string code, double expected)
    {
        var ctx = new CompileContext();
        AssertAlmostEqual(expected, CompileAndExecute(ctx, code));
    }

    [Fact]
    public void CompoundAssignmentBindsTighterThanPlainAssignment()
    {
        var ctx = new CompileContext();
        AssertAlmostEqual(14.0, CompileAndExecute(ctx, "x = 4; y = 5; x += y += 0; x + y"));
    }

    // ---- 指令列表（InstructionListTest）----

    [Fact]
    public void TopLevelInstructionList()
    {
        var ctx = new CompileContext();
        double result = CompileAndExecute(ctx, "x = 1; 2; 3; y = 4; sin(4); gmem[100] = 300; 5");

        AssertAlmostEqual(5.0, result);
        AssertAlmostEqual(1.0, ctx.FindVariable("x")!.Value);
        AssertAlmostEqual(4.0, ctx.FindVariable("y")!.Value);
        AssertAlmostEqual(300.0, ctx.Compile("gmem[100]")!.Execute());
    }

    [Fact]
    public void InstructionListInsideWhile()
    {
        var ctx = new CompileContext();
        double result = CompileAndExecute(ctx, "x = 1; 2; while(y = 4; sin(5); 0); gmem[100] = 300; 5");

        AssertAlmostEqual(5.0, result);
        AssertAlmostEqual(1.0, ctx.FindVariable("x")!.Value);
        AssertAlmostEqual(4.0, ctx.FindVariable("y")!.Value);
    }

    [Fact]
    public void InstructionListInsideParentheses()
    {
        var ctx = new CompileContext();
        double result = CompileAndExecute(ctx, "x = 1; 2; y = (z = 6; sin(5); 4); gmem[100] = 300; 5");

        AssertAlmostEqual(5.0, result);
        AssertAlmostEqual(1.0, ctx.FindVariable("x")!.Value);
        AssertAlmostEqual(4.0, ctx.FindVariable("y")!.Value);
        AssertAlmostEqual(6.0, ctx.FindVariable("z")!.Value);
    }

    // ---- 变量、寄存器与内存 ----

    [Theory]
    [InlineData("reg00", 0)]
    [InlineData("reg42", 42)]
    [InlineData("REG99", 99)]
    public void RegistersMapToGlobalRegisterArray(string name, int index)
    {
        var ctx = new CompileContext();
        AssertAlmostEqual(9.0, CompileAndExecute(ctx, $"{name} = 9; {name}"));
        AssertAlmostEqual(9.0, ctx.GlobalRegisters[index]);
    }

    [Fact]
    public void MemoryIndexAndOffsetForm()
    {
        var ctx = new CompileContext();
        AssertAlmostEqual(5.0, CompileAndExecute(ctx, "gmem[0] = 5; gmem[0]"));
        // "x[]"：以 x 的值作为本地内存地址。
        AssertAlmostEqual(6.0, CompileAndExecute(ctx, "100[] = 6; 100[]"));
        // "x[y]"：本地内存地址 x + y。
        AssertAlmostEqual(8.0, CompileAndExecute(ctx, "100[2] = 8; 100[2]"));
        AssertAlmostEqual(9.0, CompileAndExecute(ctx, "megabuf(3) = 9; megabuf(3)"));
    }

    [Fact]
    public void GmemKeywordIsCaseInsensitive()
    {
        var ctx = new CompileContext();
        AssertAlmostEqual(3.0, CompileAndExecute(ctx, "GMEM[10] = 3; GMem[10]"));
    }

    [Fact]
    public void ConstantsAreFoldedAtCompileTime()
    {
        var ctx = new CompileContext();
        AssertAlmostEqual(Math.Sin(0.5) + 1.0, CompileAndExecute(ctx, "sin(0.5) + 1"));
    }

    [Theory]
    [InlineData("gmem 5", "syntax error, unexpected NUM, expecting '['")]
    [InlineData("gmem", "syntax error, unexpected end of file, expecting '['")]
    [InlineData("sin", "syntax error, unexpected end of file, expecting '('")]
    [InlineData("sin 1", "syntax error, unexpected NUM, expecting '('")]
    public void ReportsExpectedTokenWhereBisonWould(string code, string expected)
    {
        var ctx = new CompileContext();
        Assert.Null(ctx.Compile(code));
        Assert.Equal(expected, ctx.Error);
    }

    [Fact]
    public void RegisterVariableSupportsPlainAndRegisterNames()
    {
        var ctx = new CompileContext();

        var alpha = ctx.RegisterVariable("alpha");
        var alphaAgain = ctx.RegisterVariable("ALPHA");
        Assert.False(alpha.IsRegister);
        Assert.Same(alpha.Variable, alphaAgain.Variable);

        alpha.Value = 3.5;
        AssertAlmostEqual(3.5, ctx.FindVariable("alpha")!.Value);
        AssertAlmostEqual(3.5, CompileAndExecute(ctx, "alpha + 0"));

        var register = ctx.RegisterVariable("reg07");
        Assert.True(register.IsRegister);
        Assert.Equal(7, register.Index);

        register.Value = 2.0;
        AssertAlmostEqual(2.0, ctx.GlobalRegisters[7]);
        AssertAlmostEqual(2.0, CompileAndExecute(ctx, "reg07"));
    }

    [Fact]
    public void FailedCompileKeepsContextUsable()
    {
        var ctx = new CompileContext();
        Assert.Null(ctx.Compile("1 +"));
        Assert.Equal("syntax error, unexpected end of file", ctx.Error);
        AssertAlmostEqual(3.0, CompileAndExecute(ctx, "1 + 2"));
        Assert.Null(ctx.Error);
    }
}
