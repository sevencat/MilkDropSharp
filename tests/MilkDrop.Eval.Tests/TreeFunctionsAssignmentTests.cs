using Xunit;

namespace ProjectMEvalSharp.Tests;

public class TreeFunctionsAssignmentTests : TreeFunctionsTestBase
{
    [Fact]
    public void AdditionAssignmentOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("_addop", varNode1, varNode2);

        Assert.Equal(7.0, ctx.Evaluate(node));           // 5 += 2
        Assert.Equal(7.0, var1.Value);

        var1.Value = 0.0; var2.Value = -5.0;
        Assert.Equal(-5.0, ctx.Evaluate(node));          // 0 += -5
        Assert.Equal(-5.0, var1.Value);
    }

    [Fact]
    public void SubtractionAssignmentOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("_subop", varNode1, varNode2);

        Assert.Equal(3.0, ctx.Evaluate(node));           // 5 -= 2
        Assert.Equal(3.0, var1.Value);

        var1.Value = 0.0; var2.Value = -5.0;
        Assert.Equal(5.0, ctx.Evaluate(node));           // 0 -= -5
        Assert.Equal(5.0, var1.Value);
    }

    [Fact]
    public void MultiplicationAssignmentOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("_mulop", varNode1, varNode2);

        Assert.Equal(10.0, ctx.Evaluate(node));          // 5 *= 2
        Assert.Equal(10.0, var1.Value);

        var1.Value = 2.0; var2.Value = -5.0;
        Assert.Equal(-10.0, ctx.Evaluate(node));         // 2 *= -5
        Assert.Equal(-10.0, var1.Value);
    }

    [Fact]
    public void DivisionAssignmentOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("_divop", varNode1, varNode2);

        Assert.Equal(2.5, ctx.Evaluate(node));           // 5 /= 2
        Assert.Equal(2.5, var1.Value);

        var1.Value = 2.0; var2.Value = -5.0;
        Assert.Equal(-0.4, ctx.Evaluate(node));          // 2 /= -5
        Assert.Equal(-0.4, var1.Value);

        var1.Value = 2.0; var2.Value = 0.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // 2 /= 0 (not NaN)
        Assert.Equal(0.0, var1.Value);
    }

    [Fact]
    public void BitwiseOrAssignmentOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 2.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 7.0);
        var node = ctx.Call("_orop", varNode1, varNode2);

        Assert.Equal(7.0, ctx.Evaluate(node));           // 2 |= 7
        Assert.Equal(7.0, var1.Value);

        var1.Value = -1.0; var2.Value = 2.0;
        Assert.Equal(-1.0, ctx.Evaluate(node));          // -1 |= 2
        Assert.Equal(-1.0, var1.Value);
    }

    [Fact]
    public void BitwiseOrOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 2.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 7.0);
        var node = ctx.Call("/*or*/", varNode1, varNode2);

        Assert.Equal(7.0, ctx.Evaluate(node));           // 2 | 7

        var1.Value = -1.0; var2.Value = 2.0;
        Assert.Equal(-1.0, ctx.Evaluate(node));          // -1 | 2
    }

    [Fact]
    public void BitwiseAndAssignmentOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 2.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 7.0);
        var node = ctx.Call("_andop", varNode1, varNode2);

        Assert.Equal(2.0, ctx.Evaluate(node));           // 2 &= 7
        Assert.Equal(2.0, var1.Value);

        var1.Value = -1.0; var2.Value = 2.0;
        Assert.Equal(2.0, ctx.Evaluate(node));           // -1 &= 2
        Assert.Equal(2.0, var1.Value);
    }

    [Fact]
    public void BitwiseAndOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 2.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 7.0);
        var node = ctx.Call("/*and*/", varNode1, varNode2);

        Assert.Equal(2.0, ctx.Evaluate(node));           // 2 & 7

        var1.Value = -1.0; var2.Value = 2.0;
        Assert.Equal(2.0, ctx.Evaluate(node));           // -1 & 2
    }

    [Fact]
    public void ModuloAssignmentOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("_modop", varNode1, varNode2);

        Assert.Equal(1.0, ctx.Evaluate(node));           // 5 %= 2
        Assert.Equal(1.0, var1.Value);

        var1.Value = 2.0; var2.Value = -5.0;
        Assert.Equal(2.0, ctx.Evaluate(node));           // 2 %= -5
        Assert.Equal(2.0, var1.Value);
    }

    [Fact]
    public void PowerAssignmentOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("_powop", varNode1, varNode2);

        Assert.Equal(25.0, ctx.Evaluate(node));          // 5 ^= 2
        Assert.Equal(25.0, var1.Value);

        var1.Value = 2.0; var2.Value = -5.0;
        Assert.Equal(0.03125, ctx.Evaluate(node));       // 2 ^= -5
        Assert.Equal(0.03125, var1.Value);

        var1.Value = 0.0; var2.Value = -5.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // 0 ^= -5 (not NaN)
        Assert.Equal(0.0, var1.Value);
    }
}
