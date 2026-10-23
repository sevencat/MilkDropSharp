using Xunit;

namespace ProjectMEvalSharp.Tests;

public class TreeFunctionsMathTests : TreeFunctionsTestBase
{
    private static readonly double PI = Math.PI;
    private static readonly double PI_2 = Math.PI / 2;
    private static readonly double PI_4 = Math.PI / 4;

    [Fact]
    public void SineFunction()
    {
        var ctx = new CompileContext();
        var (varNode, var) = CreateVariableNode(ctx, "x", 0.0);
        var node = ctx.Call("sin", varNode);

        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = PI_2;
        Assert.Equal(1.0, ctx.Evaluate(node));
        var.Value = -PI_2;
        Assert.Equal(-1.0, ctx.Evaluate(node));
    }

    [Fact]
    public void CosineFunction()
    {
        var ctx = new CompileContext();
        var (varNode, var) = CreateVariableNode(ctx, "x", 0.0);
        var node = ctx.Call("cos", varNode);

        Assert.Equal(1.0, ctx.Evaluate(node));
        var.Value = PI;
        Assert.Equal(-1.0, ctx.Evaluate(node));
        var.Value = -PI;
        Assert.Equal(-1.0, ctx.Evaluate(node));
    }

    [Fact]
    public void TangentFunction()
    {
        var ctx = new CompileContext();
        var (varNode, var) = CreateVariableNode(ctx, "x", 0.0);
        var node = ctx.Call("tan", varNode);

        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = PI_4;
        AssertAlmostEqual(1.0, ctx.Evaluate(node));
        var.Value = -PI_4;
        AssertAlmostEqual(-1.0, ctx.Evaluate(node));
    }

    [Fact]
    public void ArcSineFunction()
    {
        var ctx = new CompileContext();
        var (varNode, var) = CreateVariableNode(ctx, "x", 0.0);
        var node = ctx.Call("asin", varNode);

        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = 1.0;
        Assert.Equal(PI_2, ctx.Evaluate(node));
        var.Value = -1.0;
        Assert.Equal(-PI_2, ctx.Evaluate(node));
        var.Value = 2.0;
        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = -2.0;
        Assert.Equal(0.0, ctx.Evaluate(node));
    }

    [Fact]
    public void ArcCosineFunction()
    {
        var ctx = new CompileContext();
        var (varNode, var) = CreateVariableNode(ctx, "x", 0.0);
        var node = ctx.Call("acos", varNode);

        Assert.Equal(PI_2, ctx.Evaluate(node));
        var.Value = 1.0;
        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = -1.0;
        Assert.Equal(PI, ctx.Evaluate(node));
        var.Value = 2.0;
        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = -2.0;
        Assert.Equal(0.0, ctx.Evaluate(node));
    }

    [Fact]
    public void ArcTangentFunction()
    {
        var ctx = new CompileContext();
        var (varNode, var) = CreateVariableNode(ctx, "x", 0.0);
        var node = ctx.Call("atan", varNode);

        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = 1.0;
        Assert.Equal(PI_4, ctx.Evaluate(node));
        var.Value = -1.0;
        Assert.Equal(-PI_4, ctx.Evaluate(node));
        var.Value = 1e26;
        Assert.Equal(PI_2, ctx.Evaluate(node));
        var.Value = -1e26;
        Assert.Equal(-PI_2, ctx.Evaluate(node));
    }

    [Fact]
    public void ArcTangent2Function()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 0.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 1.0);
        var node = ctx.Call("atan2", varNode1, varNode2);

        Assert.Equal(0.0, ctx.Evaluate(node));
        var1.Value = 1.0;
        Assert.Equal(PI_4, ctx.Evaluate(node));
        var1.Value = -1.0;
        Assert.Equal(-PI_4, ctx.Evaluate(node));
        var1.Value = 1e26;
        Assert.Equal(PI_2, ctx.Evaluate(node));
        var1.Value = -1e26;
        Assert.Equal(-PI_2, ctx.Evaluate(node));
    }
}
