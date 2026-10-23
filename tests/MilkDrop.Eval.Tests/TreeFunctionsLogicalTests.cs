using Xunit;

namespace ProjectMEvalSharp.Tests;

public class TreeFunctionsLogicalTests : TreeFunctionsTestBase
{
    [Fact]
    public void LogicalNotOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var node = ctx.Call("bnot", varNode1);

        Assert.Equal(0.0, ctx.Evaluate(node));           // !5
        var1.Value = 0.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // !0
        var1.Value = 0.0001;
        Assert.Equal(0.0, ctx.Evaluate(node));           // !0.0001
        var1.Value = 0.000009;
        Assert.Equal(1.0, ctx.Evaluate(node));           // !0.000009 (slightly below closefac)
        var1.Value = -1.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // !-1
    }

    [Fact]
    public void EqualOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("equal", varNode1, varNode2);

        Assert.Equal(0.0, ctx.Evaluate(node));           // 5 == 2
        var1.Value = 0.0; var2.Value = 0.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // 0 == 0
        var1.Value = 0.000000000000001; var2.Value = 0.000000000000001;
        Assert.Equal(1.0, ctx.Evaluate(node));
        var1.Value = -0.0; var2.Value = +0.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // -0 == 0
    }

    [Fact]
    public void NotEqualOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("_noteq", varNode1, varNode2);

        Assert.Equal(1.0, ctx.Evaluate(node));           // 5 != 2
        var1.Value = 0.0; var2.Value = 0.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // 0 != 0
        var1.Value = 0.000000000000001; var2.Value = 0.000000000000001;
        Assert.Equal(0.0, ctx.Evaluate(node));
        var1.Value = -0.0; var2.Value = +0.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // -0 != 0
    }

    [Fact]
    public void BelowOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("below", varNode1, varNode2);

        Assert.Equal(0.0, ctx.Evaluate(node));           // 5 < 2
        var1.Value = 2.0; var2.Value = 5.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // 2 < 5
        var1.Value = 0.000000000000001; var2.Value = 0.000000000000001;
        Assert.Equal(0.0, ctx.Evaluate(node));
        var1.Value = 0.0000000000000001; var2.Value = 0.000000000000001;
        Assert.Equal(1.0, ctx.Evaluate(node));
        var1.Value = -0.0; var2.Value = +0.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // -0 < 0
        var1.Value = -1.0; var2.Value = 1.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // -1 < 1
    }

    [Fact]
    public void AboveOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("above", varNode1, varNode2);

        Assert.Equal(1.0, ctx.Evaluate(node));           // 5 > 2
        var1.Value = 2.0; var2.Value = 5.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // 2 > 5
        var1.Value = 0.000000000000001; var2.Value = 0.000000000000001;
        Assert.Equal(0.0, ctx.Evaluate(node));
        var1.Value = 0.000000000000001; var2.Value = 0.0000000000000001;
        Assert.Equal(1.0, ctx.Evaluate(node));
        var1.Value = -0.0; var2.Value = +0.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // -0 > 0
        var1.Value = 1.0; var2.Value = -1.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // 1 > -1
    }

    [Fact]
    public void BelowOrEqualOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("_beleq", varNode1, varNode2);

        Assert.Equal(0.0, ctx.Evaluate(node));           // 5 <= 2
        var1.Value = 2.0; var2.Value = 5.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // 2 <= 5
        var1.Value = 0.000000000000001; var2.Value = 0.000000000000001;
        Assert.Equal(1.0, ctx.Evaluate(node));
        var1.Value = 0.0000000000000001; var2.Value = 0.000000000000001;
        Assert.Equal(1.0, ctx.Evaluate(node));
        var1.Value = -0.0; var2.Value = +0.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // -0 <= 0
        var1.Value = -1.0; var2.Value = 1.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // -1 <= 1
    }

    [Fact]
    public void AboveOrEqualOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("_aboeq", varNode1, varNode2);

        Assert.Equal(1.0, ctx.Evaluate(node));           // 5 >= 2
        var1.Value = 2.0; var2.Value = 5.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // 2 >= 5
        var1.Value = 0.000000000000001; var2.Value = 0.000000000000001;
        Assert.Equal(1.0, ctx.Evaluate(node));
        var1.Value = 0.000000000000001; var2.Value = 0.0000000000000001;
        Assert.Equal(1.0, ctx.Evaluate(node));
        var1.Value = -0.0; var2.Value = +0.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // -0 >= 0
        var1.Value = 1.0; var2.Value = -1.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // 1 >= -1
    }

    [Fact]
    public void AdditionOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("_add", varNode1, varNode2);

        Assert.Equal(7.0, ctx.Evaluate(node));           // 5 + 2
        var1.Value = -1.0; var2.Value = 1.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // -1 + 1
        var1.Value = 5.0; var2.Value = -5.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // 5 + -5
    }

    [Fact]
    public void SubtractionOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("_sub", varNode1, varNode2);

        Assert.Equal(3.0, ctx.Evaluate(node));           // 5 - 2
        var1.Value = -1.0; var2.Value = 1.0;
        Assert.Equal(-2.0, ctx.Evaluate(node));          // -1 - 1
        var1.Value = 5.0; var2.Value = -5.0;
        Assert.Equal(10.0, ctx.Evaluate(node));          // 5 - -5
    }

    [Fact]
    public void MultiplicationOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("_mul", varNode1, varNode2);

        Assert.Equal(10.0, ctx.Evaluate(node));          // 5 * 2
        var1.Value = -1.0; var2.Value = 1.0;
        Assert.Equal(-1.0, ctx.Evaluate(node));          // -1 * 1
        var1.Value = 5.0; var2.Value = -5.0;
        Assert.Equal(-25.0, ctx.Evaluate(node));         // 5 * -5
    }

    [Fact]
    public void DivisionOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("_div", varNode1, varNode2);

        Assert.Equal(2.5, ctx.Evaluate(node));           // 5 / 2
        var1.Value = 0.0; var2.Value = 5.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // 0 / 5
        var1.Value = 5.0; var2.Value = 0.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // 5 / 0 -> 0, not NaN
    }

    [Fact]
    public void ModuloOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("_mod", varNode1, varNode2);

        Assert.Equal(1.0, ctx.Evaluate(node));           // 5 % 2
        var1.Value = 0.0; var2.Value = 5.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // 0 % 5
        var1.Value = 5.0; var2.Value = 5.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // 5 % 5
        var1.Value = 5.0; var2.Value = 1.9;
        Assert.Equal(0.0, ctx.Evaluate(node));           // 5 % 1.9 -> 5 % 1
    }

    [Fact]
    public void LogicalAndOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("_and", varNode1, varNode2);

        Assert.Equal(1.0, ctx.Evaluate(node));           // 5 && 2
        var1.Value = -5.0; var2.Value = 5.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // -5 && 5
        var1.Value = 0.0; var2.Value = 5.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // 0 && 5
        var1.Value = 5.0; var2.Value = 0.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // 5 && 0
    }

    [Fact]
    public void LogicalAndOperatorSideEffect()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 5.0);
        var setNode = ctx.Call("_set", varNode2, ctx.Constant(2.0));
        var node = ctx.Call("_and", varNode1, setNode);

        Assert.Equal(1.0, ctx.Evaluate(node));           // 5 && (y=2)
        Assert.Equal(2.0, var2.Value);

        var1.Value = 0.0; var2.Value = 5.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // 0 && (y=2) 短路
        Assert.Equal(5.0, var2.Value);
    }

    [Fact]
    public void LogicalOrOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("_or", varNode1, varNode2);

        Assert.Equal(1.0, ctx.Evaluate(node));           // 5 || 2
        var1.Value = -5.0; var2.Value = 5.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // -5 || 5
        var1.Value = 0.0; var2.Value = 5.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // 0 || 5
        var1.Value = 5.0; var2.Value = 0.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // 5 || 0
        var1.Value = 0.0; var2.Value = 0.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // 0 || 0
    }

    [Fact]
    public void LogicalOrOperatorSideEffect()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 5.0);
        var setNode = ctx.Call("_set", varNode2, ctx.Constant(2.0));
        var node = ctx.Call("_or", varNode1, setNode);

        Assert.Equal(1.0, ctx.Evaluate(node));           // 5 || (y=2) 短路
        Assert.Equal(5.0, var2.Value);

        var1.Value = 0.0; var2.Value = 5.0;
        Assert.Equal(1.0, ctx.Evaluate(node));           // 0 || (y=2)
        Assert.Equal(2.0, var2.Value);
    }

    [Fact]
    public void LogicalAndFunction()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("band", varNode1, varNode2);

        Assert.Equal(1.0, ctx.Evaluate(node));           // band(5, 2)
        var1.Value = -5.0; var2.Value = 5.0;
        Assert.Equal(1.0, ctx.Evaluate(node));
        var1.Value = 0.0; var2.Value = 5.0;
        Assert.Equal(0.0, ctx.Evaluate(node));
        var1.Value = 5.0; var2.Value = 0.0;
        Assert.Equal(0.0, ctx.Evaluate(node));
    }

    [Fact]
    public void LogicalOrFunction()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 2.0);
        var node = ctx.Call("bor", varNode1, varNode2);

        Assert.Equal(1.0, ctx.Evaluate(node));           // bor(5, 2)
        var1.Value = -5.0; var2.Value = 5.0;
        Assert.Equal(1.0, ctx.Evaluate(node));
        var1.Value = 0.0; var2.Value = 5.0;
        Assert.Equal(1.0, ctx.Evaluate(node));
        var1.Value = 5.0; var2.Value = 0.0;
        Assert.Equal(1.0, ctx.Evaluate(node));
        var1.Value = 0.0; var2.Value = 0.0;
        Assert.Equal(0.0, ctx.Evaluate(node));
    }

    [Fact]
    public void NegationOperator()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var node = ctx.Call("_neg", varNode1);

        Assert.Equal(-5.0, ctx.Evaluate(node));          // -(5)
        var1.Value = -5.0;
        Assert.Equal(5.0, ctx.Evaluate(node));           // -(-5)
        var1.Value = 0.0;
        Assert.Equal(0.0, ctx.Evaluate(node));           // -(0)
    }
}
