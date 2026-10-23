using HlslParser;

namespace HlslParser.Tests;

public class AstTests
{
    [Fact]
    public void BaseTypeEnumAndDescriptionTableStayInSync()
    {
        Assert.Equal(38, (int)HLSLBaseType.Count);
        Assert.Equal(38, BaseTypes.Descriptions.Length);

        Assert.Equal(2, (int)HLSLBaseType.Float);
        Assert.Equal(5, (int)HLSLBaseType.Float4);
        Assert.Equal(15, (int)HLSLBaseType.Bool);
        Assert.Equal(26, (int)HLSLBaseType.Uint4);
        Assert.Equal(35, (int)HLSLBaseType.UserDefined);
        Assert.Equal(37, (int)HLSLBaseType.Auto);

        Assert.Equal("float4", BaseTypes.Descriptions[(int)HLSLBaseType.Float4].TypeName);
        Assert.Equal("sampler2DArray", BaseTypes.Descriptions[(int)HLSLBaseType.Sampler2DArray].TypeName);
        Assert.Null(BaseTypes.Descriptions[(int)HLSLBaseType.Auto].TypeName);

        Assert.Equal(25, (int)HLSLBaseType.NumericCount);
    }

    [Fact]
    public void HLSLTypeHasValueSemantics()
    {
        var original = new HLSLType(HLSLBaseType.Float3) { Flags = (int)HLSLTypeFlags.Const };
        HLSLType copy = original;
        copy.Flags = (int)HLSLTypeFlags.Uniform;
        copy.BaseType = HLSLBaseType.Float2;

        Assert.Equal(HLSLBaseType.Float3, original.BaseType);
        Assert.Equal((int)HLSLTypeFlags.Const, original.Flags);
        Assert.Equal(HLSLBaseType.Float, original.SamplerType);
    }

    [Fact]
    public void MatrixCtorComparesArgumentsElementWise()
    {
        var a = new MatrixCtor(HLSLBaseType.Float4x4, new[] { HLSLBaseType.Int, HLSLBaseType.Int, HLSLBaseType.Int, HLSLBaseType.Int });
        var b = new MatrixCtor(HLSLBaseType.Float4x4, new[] { HLSLBaseType.Int, HLSLBaseType.Int, HLSLBaseType.Int, HLSLBaseType.Int });
        var c = new MatrixCtor(HLSLBaseType.Float4x4, new[] { HLSLBaseType.Float, HLSLBaseType.Float, HLSLBaseType.Float, HLSLBaseType.Float });
        var d = new MatrixCtor(HLSLBaseType.Float3x3, new[] { HLSLBaseType.Int, HLSLBaseType.Int, HLSLBaseType.Int, HLSLBaseType.Int });

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
    }

    [Fact]
    public void TreeInternsStrings()
    {
        var tree = new HLSLTree();
        string first = tree.AddString("uniformValue");
        string second = tree.AddString("uniformValue");

        Assert.Same(first, second);
        Assert.True(tree.GetContainsString("uniformValue"));
        Assert.False(tree.GetContainsString("otherValue"));
    }

    [Fact]
    public void GetExpressionValueFoldsConstants()
    {
        var tree = new HLSLTree();
        HLSLRoot root = tree.GetRoot();

        var declaration = tree.AddNode<HLSLDeclaration>("t", 1);
        declaration.Name = tree.AddString("N");
        declaration.Type = new HLSLType(HLSLBaseType.Int) { Flags = (int)HLSLTypeFlags.Const };

        var left = MakeIntLiteral(tree, 2);
        var right = MakeIntLiteral(tree, 3);
        var add = tree.AddNode<HLSLBinaryExpression>("t", 1);
        add.BinaryOp = HLSLBinaryOp.Add;
        add.Expression1 = left;
        add.Expression2 = right;
        add.ExpressionType = new HLSLType(HLSLBaseType.Int) { Flags = (int)HLSLTypeFlags.Const };

        declaration.Assignment = add;
        root.Statement = declaration;

        Assert.True(tree.GetExpressionValue(add, out int value));
        Assert.Equal(5, value);

        var identifier = tree.AddNode<HLSLIdentifierExpression>("t", 2);
        identifier.Name = tree.AddString("N");
        identifier.ExpressionType = new HLSLType(HLSLBaseType.Int) { Flags = (int)HLSLTypeFlags.Const };

        Assert.True(tree.GetExpressionValue(identifier, out int fromIdentifier));
        Assert.Equal(5, fromIdentifier);
    }

    [Fact]
    public void ReplaceUniformsAssignmentsRenamesWrittenUniform()
    {
        var tree = new HLSLTree();
        HLSLRoot root = tree.GetRoot();

        var uniform = tree.AddNode<HLSLDeclaration>("t", 1);
        uniform.Name = tree.AddString("u");
        uniform.Type = new HLSLType(HLSLBaseType.Float4) { Flags = (int)HLSLTypeFlags.Uniform };
        root.Statement = uniform;

        var function = tree.AddNode<HLSLFunction>("t", 2);
        function.Name = tree.AddString("PS");
        uniform.NextStatement = function;

        var identifier = tree.AddNode<HLSLIdentifierExpression>("t", 3);
        identifier.Name = tree.AddString("u");
        identifier.ExpressionType = new HLSLType(HLSLBaseType.Float4);

        var literal = tree.AddNode<HLSLLiteralExpression>("t", 3);
        literal.Type = HLSLBaseType.Float;
        literal.FValue = 1.0f;
        literal.ExpressionType = new HLSLType(HLSLBaseType.Float);

        var assignment = tree.AddNode<HLSLBinaryExpression>("t", 3);
        assignment.BinaryOp = HLSLBinaryOp.Assign;
        assignment.Expression1 = identifier;
        assignment.Expression2 = literal;

        var statement = tree.AddNode<HLSLExpressionStatement>("t", 3);
        statement.Expression = assignment;
        function.Statement = statement;

        Assert.True(tree.ReplaceUniformsAssignments());

        Assert.Equal(HLSLNodeType.Declaration, function.Statement.NodeType);
        var replacement = (HLSLDeclaration)function.Statement;
        Assert.Equal("newu", replacement.Name);
        Assert.Equal(HLSLBaseType.Float4, replacement.Type.BaseType);
        Assert.Same(statement, replacement.NextStatement);
        Assert.Equal("newu", identifier.Name);
    }

    [Fact]
    public void EnumerateMatrixCtorsDeduplicates()
    {
        var tree = new HLSLTree();
        HLSLRoot root = tree.GetRoot();

        var declaration = tree.AddNode<HLSLDeclaration>("t", 1);
        declaration.Name = tree.AddString("m");
        declaration.Type = new HLSLType(HLSLBaseType.Float4x4);

        var argument = tree.AddNode<HLSLLiteralExpression>("t", 1);
        argument.ExpressionType = new HLSLType(HLSLBaseType.Int);

        var constructor = tree.AddNode<HLSLConstructorExpression>("t", 1);
        constructor.Type = declaration.Type;
        constructor.ExpressionType = declaration.Type;
        constructor.Argument = argument;

        declaration.Assignment = constructor;
        root.Statement = declaration;

        var ctors = new List<MatrixCtor>();
        tree.EnumerateMatrixCtorsNeeded(ctors);

        Assert.Single(ctors);
        Assert.Equal(HLSLBaseType.Float4x4, ctors[0].MatrixType);
        Assert.Equal(new[] { HLSLBaseType.Int }, ctors[0].ArgumentTypes);
    }

    [Fact]
    public void NeedsFunctionUsesStringPoolAndFunctionCalls()
    {
        var tree = new HLSLTree();
        Assert.False(tree.NeedsFunction("clip"));

        HLSLRoot root = tree.GetRoot();
        var function = tree.AddNode<HLSLFunction>("t", 1);
        function.Name = tree.AddString("PS");
        root.Statement = function;

        var callee = tree.AddNode<HLSLFunction>("t", 2);
        callee.Name = tree.AddString("clip");
        function.NextStatement = callee;

        var call = tree.AddNode<HLSLFunctionCall>("t", 3);
        call.Function = callee;

        var statement = tree.AddNode<HLSLExpressionStatement>("t", 3);
        statement.Expression = call;
        function.Statement = statement;

        // clip was added to the pool by the callee name above.
        Assert.True(tree.GetContainsString("clip"));
        Assert.True(tree.NeedsFunction("clip"));
        Assert.False(tree.NeedsFunction("tex2Dlod"));
    }

    private static HLSLLiteralExpression MakeIntLiteral(HLSLTree tree, int value)
    {
        var literal = tree.AddNode<HLSLLiteralExpression>("t", 1);
        literal.Type = HLSLBaseType.Int;
        literal.IValue = value;
        literal.ExpressionType = new HLSLType(HLSLBaseType.Int) { Flags = (int)HLSLTypeFlags.Const };
        return literal;
    }
}
