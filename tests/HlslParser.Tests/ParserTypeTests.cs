using HlslParser;

namespace HlslParser.Tests;

public class ParserTypeTests
{
    private static HLSLParser CreateParser(string source, out HLSLTokenizer tokenizer)
    {
        var parser = new HLSLParser(new Allocator(), new HLSLTree());
        tokenizer = new HLSLTokenizer("test.hlsl", source);
        parser.Tokenizer = tokenizer;
        return parser;
    }

    [Fact]
    public void ParsesScalarAndVectorTypes()
    {
        AssertParsedType("float4 x;", HLSLBaseType.Float4, "x");
        AssertParsedType("float1x1 x;", HLSLBaseType.Float, "x");
        AssertParsedType("half2 x;", HLSLBaseType.Float2, "x");
        AssertParsedType("double3 x;", HLSLBaseType.Float3, "x");
        AssertParsedType("bool4 x;", HLSLBaseType.Bool4, "x");
        AssertParsedType("uint3 x;", HLSLBaseType.Uint3, "x");
        AssertParsedType("float4x4 x;", HLSLBaseType.Float4x4, "x");
        AssertParsedType("float3x2 x;", HLSLBaseType.Float3x2, "x");
    }

    [Fact]
    public void ParsesSamplerTypes()
    {
        AssertParsedType("sampler2D s;", HLSLBaseType.Sampler2D, "s");
        AssertParsedType("samplerCUBE s;", HLSLBaseType.SamplerCube, "s");

        // Generic "sampler" becomes sampler2D (C++ parity).
        AssertParsedType("sampler s;", HLSLBaseType.Sampler2D, "s");

        var parser = CreateParser("sampler2D<float> s;", out HLSLTokenizer tokenizer);
        var type = new HLSLType();
        Assert.True(parser.AcceptType(false, ref type));
        Assert.Equal(HLSLBaseType.Sampler2D, type.BaseType);
        Assert.Equal(HLSLBaseType.Float, type.SamplerType);
        Assert.Equal("s", tokenizer.GetIdentifier());
    }

    [Fact]
    public void ParsesModifiersAndInterpolationFlags()
    {
        var parser = CreateParser("const uniform linear float3 p;", out HLSLTokenizer tokenizer);
        var type = new HLSLType();

        Assert.True(parser.AcceptType(false, ref type));
        Assert.Equal(HLSLBaseType.Float3, type.BaseType);
        Assert.Equal((int)HLSLTypeFlags.Const, type.Flags & (int)HLSLTypeFlags.Const);
        Assert.Equal((int)HLSLTypeFlags.Uniform, type.Flags & (int)HLSLTypeFlags.Uniform);
        Assert.Equal((int)HLSLTypeFlags.Linear, type.Flags & (int)HLSLTypeFlags.Linear);
        Assert.Equal("p", tokenizer.GetIdentifier());
    }

    [Fact]
    public void VoidNeedsExplicitPermission()
    {
        var parser = CreateParser("void f;", out HLSLTokenizer tokenizer);
        var type = new HLSLType();
        Assert.False(parser.AcceptType(false, ref type));
        Assert.Equal((int)HLSLToken.Void, tokenizer.GetToken());

        type = new HLSLType();
        Assert.True(parser.AcceptType(true, ref type));
        Assert.Equal(HLSLBaseType.Void, type.BaseType);
    }

    [Fact]
    public void UnknownIdentifierIsNotAType()
    {
        var parser = CreateParser("MyType x;", out _);
        var type = new HLSLType();
        Assert.False(parser.AcceptType(false, ref type));
    }

    [Fact]
    public void TypeCastRanksMatchCppEncoding()
    {
        var tree = new HLSLTree();

        // 0 conversion rank + dimension promotion bit (1 << 2).
        Assert.Equal(4, HLSLParser.GetTypeCastRank(tree, new HLSLType(HLSLBaseType.Float), new HLSLType(HLSLBaseType.Float4)));

        // Truncation bit (1 << 6).
        Assert.Equal(64, HLSLParser.GetTypeCastRank(tree, new HLSLType(HLSLBaseType.Float4), new HLSLType(HLSLBaseType.Float)));
        Assert.Equal(64, HLSLParser.GetTypeCastRank(tree, new HLSLType(HLSLBaseType.Float4), new HLSLType(HLSLBaseType.Float3)));

        // float -> int conversion rank is 4, int -> float is 2.
        Assert.Equal(4 << 3, HLSLParser.GetTypeCastRank(tree, new HLSLType(HLSLBaseType.Float), new HLSLType(HLSLBaseType.Int)));
        Assert.Equal(2 << 3, HLSLParser.GetTypeCastRank(tree, new HLSLType(HLSLBaseType.Int), new HLSLType(HLSLBaseType.Float)));

        // Exact match.
        Assert.Equal(0, HLSLParser.GetTypeCastRank(tree, new HLSLType(HLSLBaseType.Float3), new HLSLType(HLSLBaseType.Float3)));

        // Sampler precision must match.
        var samplerFloat = new HLSLType(HLSLBaseType.Sampler2D) { SamplerType = HLSLBaseType.Float };
        var samplerHalf = new HLSLType(HLSLBaseType.Sampler2D) { SamplerType = HLSLBaseType.Float2 };
        Assert.Equal(0, HLSLParser.GetTypeCastRank(tree, samplerFloat, samplerFloat));
        Assert.Equal(-1, HLSLParser.GetTypeCastRank(tree, samplerFloat, samplerHalf));
    }

    [Fact]
    public void BinaryOpResultTypeUsesCppLookupTable()
    {
        Assert.True(HLSLParser.GetBinaryOpResultType(
            HLSLBinaryOp.Add,
            new HLSLType(HLSLBaseType.Float3),
            new HLSLType(HLSLBaseType.Float),
            out HLSLType result));
        Assert.Equal(HLSLBaseType.Float3, result.BaseType);

        // int + uint -> uint (C++ table quirk).
        Assert.True(HLSLParser.GetBinaryOpResultType(
            HLSLBinaryOp.Add,
            new HLSLType(HLSLBaseType.Int),
            new HLSLType(HLSLBaseType.Uint),
            out result));
        Assert.Equal(HLSLBaseType.Uint, result.BaseType);

        // Comparisons produce bool vectors sized by the widest operand.
        Assert.True(HLSLParser.GetBinaryOpResultType(
            HLSLBinaryOp.Less,
            new HLSLType(HLSLBaseType.Float3),
            new HLSLType(HLSLBaseType.Float),
            out result));
        Assert.Equal(HLSLBaseType.Bool3, result.BaseType);

        // Sampling types are not numeric.
        Assert.False(HLSLParser.GetBinaryOpResultType(
            HLSLBinaryOp.Add,
            new HLSLType(HLSLBaseType.Sampler2D),
            new HLSLType(HLSLBaseType.Float),
            out _));
    }

    [Fact]
    public void GeneratedTablesHaveExpectedShape()
    {
        Assert.Equal(312, ParserTables.Intrinsics.Length);
        Assert.Equal(25, ParserTables.BinaryOpTypeLookup.Length);
        Assert.All(ParserTables.BinaryOpTypeLookup, row => Assert.Equal(25, row.Length));
        Assert.Equal(16, ParserTables.BinaryOpPriority.Length);
        Assert.Equal(1, ParserTables.ConditionalOpPriority);

        // Spot checks against the C++ table.
        int index = (int)HLSLBaseType.Float3 - (int)HLSLBaseType.FirstNumeric;
        int column = (int)HLSLBaseType.Float - (int)HLSLBaseType.FirstNumeric;
        Assert.Equal(HLSLBaseType.Float3, ParserTables.BinaryOpTypeLookup[index][column]);

        bool absFound = false;
        foreach (Intrinsic intrinsic in ParserTables.Intrinsics)
        {
            if (intrinsic.Function.Name == "abs" && intrinsic.Function.ReturnType.BaseType == HLSLBaseType.Float4)
            {
                absFound = true;
                Assert.Equal(1, intrinsic.Function.NumArguments);
                Assert.Equal((int)HLSLTypeFlags.Const, intrinsic.Function.Argument.Type.Flags);
                break;
            }
        }
        Assert.True(absFound, "abs(float4) intrinsic missing");
    }

    private static void AssertParsedType(string source, HLSLBaseType expected, string expectedNextIdentifier)
    {
        var parser = CreateParser(source, out HLSLTokenizer tokenizer);
        var type = new HLSLType();

        Assert.True(parser.AcceptType(false, ref type));
        Assert.Equal(expected, type.BaseType);
        Assert.Equal((int)HLSLToken.Identifier, tokenizer.GetToken());
        Assert.Equal(expectedNextIdentifier, tokenizer.GetIdentifier());
    }
}
