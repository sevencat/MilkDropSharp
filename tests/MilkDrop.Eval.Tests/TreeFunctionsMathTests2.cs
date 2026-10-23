using Xunit;

namespace ProjectMEvalSharp.Tests;

public class TreeFunctionsMathTests2 : TreeFunctionsTestBase
{
    private static readonly double E = Math.E;

    [Fact]
    public void SquareRootFunction()
    {
        var ctx = new CompileContext();
        var (varNode, var) = CreateVariableNode(ctx, "x", 0.0);
        var node = ctx.Call("sqrt", varNode);

        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = 1.0;
        Assert.Equal(1.0, ctx.Evaluate(node));
        var.Value = -1.0;
        Assert.Equal(1.0, ctx.Evaluate(node));
        var.Value = 25.0;
        Assert.Equal(5.0, ctx.Evaluate(node));
        var.Value = -25.0;
        Assert.Equal(5.0, ctx.Evaluate(node));
    }

    [Fact]
    public void PowerFunction()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("pow", varNode1, varNode2);

        Assert.Equal(25.0, ctx.Evaluate(node));
        var1.Value = 2.0; var2.Value = -5.0;
        Assert.Equal(0.03125, ctx.Evaluate(node));
        var1.Value = 0.0; var2.Value = -5.0;
        Assert.Equal(0.0, ctx.Evaluate(node));
    }

    [Fact]
    public void ExponentialFunction()
    {
        var ctx = new CompileContext();
        var (varNode, var) = CreateVariableNode(ctx, "x", 1.0);
        var node = ctx.Call("exp", varNode);

        Assert.Equal(E, ctx.Evaluate(node));
        var.Value = 2.0;
        AssertAlmostEqual(E * E, ctx.Evaluate(node));
    }

    [Fact]
    public void NaturalLogarithmFunction()
    {
        var ctx = new CompileContext();
        var (varNode, var) = CreateVariableNode(ctx, "x", 0.0);
        var node = ctx.Call("log", varNode);

        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = -1.0;
        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = 1.0;
        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = E;
        Assert.Equal(1.0, ctx.Evaluate(node));
        var.Value = E * E;
        Assert.Equal(2.0, ctx.Evaluate(node));
    }

    [Fact]
    public void Logarithm10Function()
    {
        var ctx = new CompileContext();
        var (varNode, var) = CreateVariableNode(ctx, "x", 0.0);
        var node = ctx.Call("log10", varNode);

        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = -1.0;
        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = 1.0;
        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = 10.0;
        Assert.Equal(1.0, ctx.Evaluate(node));
        var.Value = 100.0;
        Assert.Equal(2.0, ctx.Evaluate(node));
    }

    [Fact]
    public void FloorFunction()
    {
        var ctx = new CompileContext();
        var (varNode, var) = CreateVariableNode(ctx, "x", 0.5);
        var node = ctx.Call("floor", varNode);

        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = -1.5;
        Assert.Equal(-2.0, ctx.Evaluate(node));
        var.Value = 0.0;
        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = 1.9999999999;
        Assert.Equal(1.0, ctx.Evaluate(node));
    }

    [Fact]
    public void CeilingFunction()
    {
        var ctx = new CompileContext();
        var (varNode, var) = CreateVariableNode(ctx, "x", 0.5);
        var node = ctx.Call("ceil", varNode);

        Assert.Equal(1.0, ctx.Evaluate(node));
        var.Value = -1.5;
        Assert.Equal(-1.0, ctx.Evaluate(node));
        var.Value = 0.0;
        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = 1.000000001;
        Assert.Equal(2.0, ctx.Evaluate(node));
    }
}
