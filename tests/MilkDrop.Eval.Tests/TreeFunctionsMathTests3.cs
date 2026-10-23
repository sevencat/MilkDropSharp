using Xunit;

namespace ProjectMEvalSharp.Tests;

public class TreeFunctionsMathTests3 : TreeFunctionsTestBase
{
    private const double Int32Max = 2147483647.0;

    [Fact]
    public void SigmoidalFunction()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 0.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 1.0);
        var node = ctx.Call("sigmoid", varNode1, varNode2);

        Assert.Equal(0.5, ctx.Evaluate(node));           // sigmoid(0, 1)
        var1.Value = 100000.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // sigmoid(100000, 1)
        var1.Value = -100000.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // sigmoid(-100000, 1)
    }

    [Fact]
    public void SquareFunction()
    {
        var ctx = new CompileContext();
        var (varNode, var) = CreateVariableNode(ctx, "x", 0.0);
        var node = ctx.Call("sqr", varNode);

        Assert.Equal(0.0, ctx.Evaluate(node));           // sqr(0)
        var.Value = 1.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // sqr(1)
        var.Value = -1.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // sqr(-1)
        var.Value = 2.0;
        Assert.Equal(4.0, ctx.Evaluate(node));           // sqr(2)
        var.Value = -2.0;
        Assert.Equal(4.0, ctx.Evaluate(node));           // sqr(-2)
        var.Value = 1000000.0;
        Assert.Equal(1e12, ctx.Evaluate(node));          // sqr(1000000)
        var.Value = -1000000.0;
        Assert.Equal(1e12, ctx.Evaluate(node));          // sqr(-1000000)
        var.Value = 9999999999999.0;
        Assert.Equal(9.9999999999980007e+25, ctx.Evaluate(node));
        var.Value = 1.7976931348623157E+300;
        Assert.Equal(double.PositiveInfinity, ctx.Evaluate(node));
    }

    [Fact]
    public void AbsoluteFunction()
    {
        var ctx = new CompileContext();
        var (varNode, var) = CreateVariableNode(ctx, "x", 0.0);
        var node = ctx.Call("abs", varNode);

        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = -1.5;
        Assert.Equal(1.5, ctx.Evaluate(node));
        var.Value = 1.5;
        Assert.Equal(1.5, ctx.Evaluate(node));
    }

    [Fact]
    public void MinimumFunction()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("min", varNode1, varNode2);

        Assert.Equal(2.0, ctx.Evaluate(node));
        var1.Value = 2.0; var2.Value = 5.0;
        Assert.Equal(2.0, ctx.Evaluate(node));
        var1.Value = -100000.0; var2.Value = 100000.0;
        Assert.Equal(-100000.0, ctx.Evaluate(node));
    }

    [Fact]
    public void MaximumFunction()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("max", varNode1, varNode2);

        Assert.Equal(5.0, ctx.Evaluate(node));
        var1.Value = 2.0; var2.Value = 5.0;
        Assert.Equal(5.0, ctx.Evaluate(node));
        var1.Value = -100000.0; var2.Value = 100000.0;
        Assert.Equal(100000.0, ctx.Evaluate(node));
    }

    [Fact]
    public void SignFunction()
    {
        var ctx = new CompileContext();
        var (varNode, var) = CreateVariableNode(ctx, "x", 0.0);
        var node = ctx.Call("sign", varNode);

        Assert.Equal(0.0, ctx.Evaluate(node));
        var.Value = -1.5;
        Assert.Equal(-1.0, ctx.Evaluate(node));
        var.Value = 1.5;
        Assert.Equal(1.0, ctx.Evaluate(node));
    }

    [Fact]
    public void RandFunction()
    {
        var ctx = new CompileContext();
        var (varNode, var) = CreateVariableNode(ctx, "x", 0.0);
        var node = ctx.Call("rand", varNode);

        void CheckRange(double value)
        {
            var.Value = value;
            double threshold = value / 2.0;
            bool oneValueOverThreshold = false;
            for (int i = 0; i < 100; i++)
            {
                double r = ctx.Evaluate(node);
                if (value == 0)
                {
                    Assert.True(r < 1.0);
                }
                else
                {
                    Assert.True(r < value);
                    if (r > threshold)
                    {
                        oneValueOverThreshold = true;
                    }
                }
            }
            if (value != 0)
            {
                Assert.True(oneValueOverThreshold);
            }
        }

        CheckRange(0.0);                                    // rand(0) < 1
        CheckRange(100.0);
        CheckRange(10000.0);
        CheckRange(1000000.0);
        CheckRange(100000000.0);
        CheckRange(Int32Max);
        CheckRange(Int32Max * 1000000000000000.0);
    }

    [Fact]
    public void InverseSquareRootFunction()
    {
        var ctx = new CompileContext();
        var (varNode, var) = CreateVariableNode(ctx, "x", 1.0);
        var node = ctx.Call("invsqrt", varNode);

        Assert.InRange(ctx.Evaluate(node), 0.99, 1.01);     // invsqrt(1)
        var.Value = 25.0;
        Assert.InRange(ctx.Evaluate(node), 0.19, 0.21);     // invsqrt(25)
        var.Value = -1.0;
        Assert.Equal(double.NegativeInfinity, ctx.Evaluate(node));
    }
}
