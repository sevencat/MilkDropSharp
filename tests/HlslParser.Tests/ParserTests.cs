using HlslParser;

namespace HlslParser.Tests;

public class ParserTests
{
    private static bool TryParse(string code, out HLSLTree tree, out CollectingDiagnosticSink diagnostics)
    {
        var allocator = new Allocator();
        tree = new HLSLTree(allocator);
        diagnostics = new CollectingDiagnosticSink();
        var parser = new HLSLParser(allocator, tree, diagnostics);
        return parser.Parse("test.hlsl", code);
    }

    private static void AssertParses(string code, out HLSLTree tree)
    {
        bool ok = TryParse(code, out tree, out CollectingDiagnosticSink diagnostics);
        Assert.True(ok, "parse failed: " + string.Join("; ", diagnostics.Diagnostics.ConvertAll(d => $"{d.Line}: {d.Message}")));
    }

    // ------------------------------------------------------ issue #940 regressions

    [Theory]
    [InlineData("float scalar = 2.0;\nfloat2 var = (float2(1.0, 2.0)) * scalar;\n")]
    [InlineData("float2 var = (float2(1.0, 2.0)) + float2(3.0, 4.0);\n")]
    [InlineData("float2 var = ((float2(1.0, 2.0))) * 2.0;\n")]
    [InlineData("float2 var = (float2(1.0, 2.0)) * (float2(3.0, 4.0));\n")]
    [InlineData("float a = 1.0; float b = 2.0; float c = 3.0;\nfloat x = (a) * b + c;\n")]
    [InlineData("float2 var = float2(1.0, 2.0) * 2.0;\n")]
    public void ParenthesizedConstructorRegressions(string code)
    {
        AssertParses(code, out _);
    }

    // ------------------------------------------------------------ AST structure

    [Fact]
    public void ParsesSimpleFunction()
    {
        const string code = @"
float4 PS(float2 uv : TEXCOORD0) : COLOR
{
    return float4(uv, 0.0, 1.0);
}";
        AssertParses(code, out HLSLTree tree);

        var function = Assert.IsType<HLSLFunction>(tree.GetRoot().Statement);
        Assert.Equal("PS", function.Name);
        Assert.Equal(HLSLBaseType.Float4, function.ReturnType.BaseType);
        Assert.Equal("COLOR", function.Semantic);
        Assert.Equal(1, function.NumArguments);
        Assert.Equal("uv", function.Argument.Name);
        Assert.Equal("TEXCOORD0", function.Argument.Semantic);
        Assert.Equal(0, function.NumOutputArguments);

        var returnStatement = Assert.IsType<HLSLReturnStatement>(function.Statement);
        var constructor = Assert.IsType<HLSLConstructorExpression>(returnStatement.Expression);
        Assert.Equal(HLSLBaseType.Float4, constructor.Type.BaseType);
        Assert.True((constructor.ExpressionType.Flags & (int)HLSLTypeFlags.Const) != 0);
    }

    [Fact]
    public void ParsesGlobalsAndCommaDeclarations()
    {
        const string code = @"
uniform float4 color;
float x = 2.0, y = 3.0;
";
        AssertParses(code, out HLSLTree tree);

        var color = Assert.IsType<HLSLDeclaration>(tree.GetRoot().Statement);
        Assert.Equal("color", color.Name);
        Assert.Equal((int)HLSLTypeFlags.Uniform, color.Type.Flags & (int)HLSLTypeFlags.Uniform);

        var x = Assert.IsType<HLSLDeclaration>(color.NextStatement);
        Assert.Equal("x", x.Name);
        Assert.NotNull(x.NextDeclaration);
        HLSLDeclaration y = x.NextDeclaration;
        Assert.Equal("y", y.Name);
        Assert.Equal(HLSLBaseType.Float, y.Type.BaseType);
    }

    [Fact]
    public void ParsesStructAndUserDefinedType()
    {
        const string code = @"
struct S
{
    float3 a;
    float b;
};
S s;
";
        AssertParses(code, out HLSLTree tree);

        var structure = Assert.IsType<HLSLStruct>(tree.GetRoot().Statement);
        Assert.Equal("S", structure.Name);
        Assert.Equal("a", structure.Field.Name);
        Assert.Equal(HLSLBaseType.Float3, structure.Field.Type.BaseType);
        Assert.Equal("b", structure.Field.NextField.Name);

        var declaration = Assert.IsType<HLSLDeclaration>(structure.NextStatement);
        Assert.Equal(HLSLBaseType.UserDefined, declaration.Type.BaseType);
        Assert.Equal("S", declaration.Type.TypeName);
    }

    [Fact]
    public void ParsesControlFlow()
    {
        const string code = @"
float f(float a)
{
    float r = 0.0;
    for (int i = 0; i < 4; i++)
    {
        r += a;
    }
    while (r > 10.0)
    {
        r -= 1.0;
    }
    if (r > 1.0)
    {
        return r;
    }
    else
    {
        return 0.0;
    }
}";
        AssertParses(code, out HLSLTree tree);

        var function = Assert.IsType<HLSLFunction>(tree.GetRoot().Statement);
        var declaration = Assert.IsType<HLSLDeclaration>(function.Statement);
        Assert.Equal("r", declaration.Name);

        var forStatement = Assert.IsType<HLSLForStatement>(declaration.NextStatement);
        Assert.Equal("i", forStatement.Initialization.Name);
        Assert.IsType<HLSLBinaryExpression>(forStatement.Condition);
        Assert.IsType<HLSLUnaryExpression>(forStatement.Increment);

        // ParseStatementOrBlock consumes '{' and inlines the block contents
        // (no HLSLBlockStatement node), same as C++.
        Assert.IsType<HLSLExpressionStatement>(forStatement.Statement);

        var whileStatement = Assert.IsType<HLSLWhileStatement>(forStatement.NextStatement);
        Assert.IsType<HLSLBinaryExpression>(whileStatement.Condition);

        var ifStatement = Assert.IsType<HLSLIfStatement>(whileStatement.NextStatement);
        Assert.IsType<HLSLBinaryExpression>(ifStatement.Condition);
        Assert.NotNull(ifStatement.Statement);
        Assert.NotNull(ifStatement.ElseStatement);
    }

    [Fact]
    public void ParsesSwizzleAndMemberAccess()
    {
        const string code = @"
float4 c = float4(1, 2, 3, 4);
float3 rgb = c.rgb;
float m = c[2];
";
        AssertParses(code, out HLSLTree tree);

        var c = Assert.IsType<HLSLDeclaration>(tree.GetRoot().Statement);
        var rgb = Assert.IsType<HLSLDeclaration>(c.NextStatement);

        var access = Assert.IsType<HLSLMemberAccess>(rgb.Assignment);
        Assert.Equal("rgb", access.Field);
        Assert.True(access.Swizzle);
        Assert.Equal(HLSLBaseType.Float3, access.ExpressionType.BaseType);

        var index = Assert.IsType<HLSLDeclaration>(rgb.NextStatement);
        var arrayAccess = Assert.IsType<HLSLArrayAccess>(index.Assignment);
        Assert.Equal(HLSLBaseType.Float, arrayAccess.ExpressionType.BaseType);
    }

    [Fact]
    public void ParsesIntrinsicCalls()
    {
        const string code = @"
float4 a = lerp(float4(1, 1, 1, 1), float4(0, 0, 0, 0), 0.5);
float b = abs(-1.0);
float c = saturate(b);
";
        AssertParses(code, out HLSLTree tree);

        var a = Assert.IsType<HLSLDeclaration>(tree.GetRoot().Statement);
        var call = Assert.IsType<HLSLFunctionCall>(a.Assignment);
        Assert.Equal("lerp", call.Function.Name);
        Assert.Equal(HLSLBaseType.Float4, call.ExpressionType.BaseType);
        Assert.Equal(3, call.NumArguments);

        var b = Assert.IsType<HLSLDeclaration>(a.NextStatement);
        var absCall = Assert.IsType<HLSLFunctionCall>(b.Assignment);
        Assert.Equal("abs", absCall.Function.Name);
        Assert.Equal(HLSLBaseType.Float, absCall.ExpressionType.BaseType);
    }

    [Fact]
    public void ParsesOutArguments()
    {
        const string code = @"
void f(out float a, inout float b)
{
    a = b;
}
";
        AssertParses(code, out HLSLTree tree);

        var function = Assert.IsType<HLSLFunction>(tree.GetRoot().Statement);
        Assert.Equal(2, function.NumArguments);
        Assert.Equal(2, function.NumOutputArguments);
        Assert.Equal(HLSLArgumentModifier.Out, function.Argument.Modifier);
        Assert.Equal(HLSLArgumentModifier.Inout, function.Argument.NextArgument.Modifier);
    }

    // ---------------------------------------------------------------- errors

    [Fact]
    public void ReportsUndeclaredIdentifier()
    {
        bool ok = TryParse("float x = y;", out _, out CollectingDiagnosticSink diagnostics);

        Assert.False(ok);
        Assert.Contains(diagnostics.Diagnostics, d => d.Message.Contains("Undeclared identifier 'y'"));
    }

    [Fact]
    public void ReportsMissingSemicolon()
    {
        bool ok = TryParse("float x = 1.0", out _, out CollectingDiagnosticSink diagnostics);

        Assert.False(ok);
        Assert.NotEmpty(diagnostics.Diagnostics);
    }

    [Fact]
    public void ReportsUnknownType()
    {
        bool ok = TryParse("Bogus x;", out _, out CollectingDiagnosticSink diagnostics);

        Assert.False(ok);
        Assert.NotEmpty(diagnostics.Diagnostics);
    }
}
