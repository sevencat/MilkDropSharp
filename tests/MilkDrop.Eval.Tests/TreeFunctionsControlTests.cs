using Xunit;

namespace ProjectMEvalSharp.Tests;

public class TreeFunctionsControlTests : TreeFunctionsTestBase
{
    [Fact]
    public void Constant()
    {
        var ctx = new CompileContext();
        var node = ctx.Constant(5.0);
        Assert.Equal(5.0, ctx.Evaluate(node));
    }

    [Fact]
    public void Variable()
    {
        var ctx = new CompileContext();
        var (node, var) = CreateVariableNode(ctx, "x", 5.0);
        var ret = default(EvalResult);
        node.Eval(ref ret);
        Assert.Equal(5.0, ret.Get());
        Assert.Same(var, ret.Var);
    }

    [Fact]
    public void ExecuteList()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var setNode1 = ctx.Call("_set", varNode1, ctx.Constant(-50.0));
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 123.0);
        var setNode2 = ctx.Call("_set", varNode2, ctx.Constant(50.0));

        var listNode = ctx.List(setNode1, setNode2);

        Assert.Equal(50.0, ctx.Evaluate(listNode));
        Assert.Equal(-50.0, var1.Value);
        Assert.Equal(50.0, var2.Value);
    }

    [Fact]
    public void ExecuteLoop()
    {
        var ctx = new CompileContext();
        var (varNodeX, varX) = CreateVariableNode(ctx, "x", 0.0);
        var incrementNode = ctx.Call("_addop", varNodeX, ctx.Constant(1.0));
        var loopNode = ctx.Call("loop", ctx.Constant(42.0), incrementNode);

        Assert.Equal(42.0, ctx.Evaluate(loopNode));
        Assert.Equal(42.0, varX.Value);
    }

    [Fact]
    public void ExecuteWhile()
    {
        var ctx = new CompileContext();
        var (varNodeX, varX) = CreateVariableNode(ctx, "x", 42.0);
        var decrementNode = ctx.Call("_subop", varNodeX, ctx.Constant(1.0));
        var whileNode = ctx.Call("while", decrementNode);

        Assert.Equal(0.0, ctx.Evaluate(whileNode));
        Assert.Equal(0.0, varX.Value);
    }

    [Fact]
    public void If()
    {
        var ctx = new CompileContext();
        var (varNodeX, varX) = CreateVariableNode(ctx, "x", 0.0);
        var comparisonNode = ctx.Call("below", varNodeX, ctx.Constant(10.0));
        var ifNode = ctx.Call("if", comparisonNode, ctx.Constant(42.0), ctx.Constant(999.0));

        Assert.Equal(42.0, ctx.Evaluate(ifNode));
        varX.Value = 1000.0;
        Assert.Equal(999.0, ctx.Evaluate(ifNode));
    }

    [Fact]
    public void IfWithReferenceReturn()
    {
        var ctx = new CompileContext();
        var (varNodeX, varX) = CreateVariableNode(ctx, "x", 0.0);
        var (varNodeY, varY) = CreateVariableNode(ctx, "y", 999.0);
        var comparisonNode = ctx.Call("below", varNodeX, ctx.Constant(10.0));
        var ifNode = ctx.Call("if", comparisonNode, ctx.Constant(42.0), varNodeY);
        var setNode = ctx.Call("_set", ifNode, ctx.Constant(666.0));

        Assert.Equal(666.0, ctx.Evaluate(setNode));
        Assert.Equal(999.0, varY.Value);

        varX.Value = 1000.0;
        Assert.Equal(666.0, ctx.Evaluate(setNode));
        Assert.Equal(666.0, varY.Value);
    }

    [Fact]
    public void Execute2()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var setNode1 = ctx.Call("_set", varNode1, ctx.Constant(-50.0));
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 123.0);
        var setNode2 = ctx.Call("_set", varNode2, ctx.Constant(50.0));

        var exec2Node = ctx.Call("exec2", setNode1, setNode2);

        Assert.Equal(50.0, ctx.Evaluate(exec2Node));
        Assert.Equal(-50.0, var1.Value);
        Assert.Equal(50.0, var2.Value);
    }

    [Fact]
    public void Execute3()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var setNode1 = ctx.Call("_set", varNode1, ctx.Constant(-50.0));
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 123.0);
        var setNode2 = ctx.Call("_set", varNode2, ctx.Constant(50.0));
        var (varNode3, var3) = CreateVariableNode(ctx, "z", 456.0);
        var setNode3 = ctx.Call("_set", varNode3, ctx.Constant(200.0));

        var exec3Node = ctx.Call("exec3", setNode1, setNode2, setNode3);

        Assert.Equal(200.0, ctx.Evaluate(exec3Node));
        Assert.Equal(-50.0, var1.Value);
        Assert.Equal(50.0, var2.Value);
        Assert.Equal(200.0, var3.Value);
    }

    [Fact]
    public void Assignment()
    {
        var ctx = new CompileContext();
        var (varNode1, var1) = CreateVariableNode(ctx, "x", 5.0);
        var (varNode2, var2) = CreateVariableNode(ctx, "y", 45.0);
        var setNode = ctx.Call("_set", varNode1, varNode2);

        var ret = default(EvalResult);
        setNode.Eval(ref ret);

        Assert.Same(var1, ret.Var);
        Assert.NotSame(var2, ret.Var);
        Assert.Equal(45.0, ret.Get());
        Assert.Equal(45.0, var1.Value);
    }
}
