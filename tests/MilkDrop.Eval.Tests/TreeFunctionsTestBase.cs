using Xunit;

namespace ProjectMEvalSharp.Tests;

public abstract class TreeFunctionsTestBase
{
    /// <summary>创建带初始值的变量节点，返回 (节点, 变量对象)。</summary>
    protected static (EvalNode Node, Variable Var) CreateVariableNode(
        CompileContext ctx, string name, double initialValue)
    {
        var node = ctx.VariableRef(name);
        var variable = ctx.FindVariable(name)!;
        variable.Value = initialValue;
        return (node, variable);
    }

    /// <summary>近似相等断言，对齐 C 版 EXPECT_DOUBLE_EQ（4 ULP）的容差语义。</summary>
    protected static void AssertAlmostEqual(double expected, double actual)
        => Assert.True(Math.Abs(actual - expected) < 1e-12, $"Expected: {expected}, Actual: {actual}");

}
