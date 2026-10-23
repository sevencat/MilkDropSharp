namespace HlslParser;

public enum NumericType
{
    Float,
    Bool,
    Int,
    Uint,
    Count,
    NaN,
}

/// <summary>
/// Port of <c>HLSLBaseType</c>. The numeric values are load bearing:
/// <see cref="BaseTypes.Descriptions"/> is indexed by this enum and the parser
/// uses the First/Last ranges for type promotion, so do not reorder.
/// Note <see cref="Auto"/> (37) has no entry in the C++ description table
/// either; the 38th element there is zero initialized.
/// </summary>
public enum HLSLBaseType
{
    Unknown = 0,
    Void = 1,

    Float = 2,
    FirstNumeric = Float,
    Float2 = 3,
    Float3 = 4,
    Float4 = 5,

    Float2x4 = 6,
    Float2x3 = 7,
    Float2x2 = 8,

    Float3x4 = 9,
    Float3x3 = 10,
    Float3x2 = 11,

    Float4x4 = 12,
    Float4x3 = 13,
    Float4x2 = 14,

    Bool = 15,
    FirstInteger = Bool,
    Bool2 = 16,
    Bool3 = 17,
    Bool4 = 18,

    Int = 19,
    Int2 = 20,
    Int3 = 21,
    Int4 = 22,

    Uint = 23,
    Uint2 = 24,
    Uint3 = 25,
    Uint4 = 26,
    LastInteger = Uint4,
    LastNumeric = Uint4,

    Texture = 27,
    Sampler = 28,
    Sampler2D = 29,
    Sampler3D = 30,
    SamplerCube = 31,
    Sampler2DShadow = 32,
    Sampler2DMS = 33,
    Sampler2DArray = 34,
    UserDefined = 35,
    Expression = 36,
    Auto = 37,

    Count = 38,
    NumericCount = LastNumeric - FirstNumeric + 1,
}

public readonly struct BaseTypeDescription
{
    public readonly string TypeName;
    public readonly NumericType NumericType;
    public readonly int NumComponents;
    public readonly int NumDimensions;
    public readonly int Height;
    public readonly int BinaryOpRank;

    public BaseTypeDescription(string typeName, NumericType numericType, int numComponents, int numDimensions, int height, int binaryOpRank)
    {
        TypeName = typeName;
        NumericType = numericType;
        NumComponents = numComponents;
        NumDimensions = numDimensions;
        Height = height;
        BinaryOpRank = binaryOpRank;
    }
}

/// <summary>
/// Port of the <c>baseTypeDescriptions</c> table and the type predicates from
/// HLSLTree.h. The table must stay in the same order as <see cref="HLSLBaseType"/>.
/// </summary>
public static class BaseTypes
{
    public static readonly BaseTypeDescription[] Descriptions =
    {
        new("unknown type", NumericType.NaN, 0, 0, 0, -1), // Unknown
        new("void", NumericType.NaN, 0, 0, 0, -1),         // Void
        new("float", NumericType.Float, 1, 0, 1, 0),       // Float
        new("float2", NumericType.Float, 2, 1, 1, 0),      // Float2
        new("float3", NumericType.Float, 3, 1, 1, 0),      // Float3
        new("float4", NumericType.Float, 4, 1, 1, 0),      // Float4

        new("float2x4", NumericType.Float, 2, 2, 4, 0),    // Float2x4
        new("float2x3", NumericType.Float, 2, 2, 3, 0),    // Float2x3
        new("float2x2", NumericType.Float, 2, 2, 2, 0),    // Float2x2

        new("float3x4", NumericType.Float, 3, 2, 4, 0),    // Float3x4
        new("float3x3", NumericType.Float, 3, 2, 3, 0),    // Float3x3
        new("float3x2", NumericType.Float, 3, 2, 2, 0),    // Float3x2

        new("float4x4", NumericType.Float, 4, 2, 4, 0),    // Float4x4
        new("float4x3", NumericType.Float, 4, 2, 3, 0),    // Float4x3
        new("float4x2", NumericType.Float, 4, 2, 2, 0),    // Float4x2

        new("bool", NumericType.Bool, 1, 0, 1, 4),         // Bool
        new("bool2", NumericType.Bool, 2, 1, 1, 4),        // Bool2
        new("bool3", NumericType.Bool, 3, 1, 1, 4),        // Bool3
        new("bool4", NumericType.Bool, 4, 1, 1, 4),        // Bool4

        new("int", NumericType.Int, 1, 0, 1, 3),           // Int
        new("int2", NumericType.Int, 2, 1, 1, 3),          // Int2
        new("int3", NumericType.Int, 3, 1, 1, 3),          // Int3
        new("int4", NumericType.Int, 4, 1, 1, 3),          // Int4

        new("uint", NumericType.Uint, 1, 0, 1, 2),         // Uint
        new("uint2", NumericType.Uint, 2, 1, 1, 2),        // Uint2
        new("uint3", NumericType.Uint, 3, 1, 1, 2),        // Uint3
        new("uint4", NumericType.Uint, 4, 1, 1, 2),        // Uint4

        new("texture", NumericType.NaN, 1, 0, 0, -1),      // Texture
        new("sampler", NumericType.NaN, 1, 0, 0, -1),      // Sampler
        new("sampler2D", NumericType.NaN, 1, 0, 0, -1),    // Sampler2D
        new("sampler3D", NumericType.NaN, 1, 0, 0, -1),    // Sampler3D
        new("samplerCUBE", NumericType.NaN, 1, 0, 0, -1),  // SamplerCube
        new("sampler2DShadow", NumericType.NaN, 1, 0, 0, -1), // Sampler2DShadow
        new("sampler2DMS", NumericType.NaN, 1, 0, 0, -1),  // Sampler2DMS
        new("sampler2DArray", NumericType.NaN, 1, 0, 0, -1), // Sampler2DArray
        new("user defined", NumericType.NaN, 1, 0, 0, -1), // UserDefined
        new("expression", NumericType.NaN, 1, 0, 0, -1),   // Expression

        // Auto has no initializer in C++ (value initialized): no name and
        // NumericType.Float (0). Kept as-is so lookups behave identically.
        new(null, NumericType.Float, 0, 0, 0, 0),
    };

    public static bool IsSamplerType(HLSLBaseType baseType)
    {
        return baseType == HLSLBaseType.Sampler ||
               baseType == HLSLBaseType.Sampler2D ||
               baseType == HLSLBaseType.Sampler3D ||
               baseType == HLSLBaseType.SamplerCube ||
               baseType == HLSLBaseType.Sampler2DShadow ||
               baseType == HLSLBaseType.Sampler2DMS ||
               baseType == HLSLBaseType.Sampler2DArray;
    }

    public static bool IsMatrixType(HLSLBaseType baseType)
    {
        return baseType == HLSLBaseType.Float2x4 || baseType == HLSLBaseType.Float2x3 || baseType == HLSLBaseType.Float2x2 ||
               baseType == HLSLBaseType.Float3x4 || baseType == HLSLBaseType.Float3x3 || baseType == HLSLBaseType.Float3x2 ||
               baseType == HLSLBaseType.Float4x4 || baseType == HLSLBaseType.Float4x3 || baseType == HLSLBaseType.Float4x2;
    }

    public static bool IsScalarType(HLSLBaseType baseType)
    {
        return baseType == HLSLBaseType.Float ||
               baseType == HLSLBaseType.Bool ||
               baseType == HLSLBaseType.Int ||
               baseType == HLSLBaseType.Uint;
    }

    public static bool IsVectorType(HLSLBaseType baseType)
    {
        return baseType == HLSLBaseType.Float2 ||
               baseType == HLSLBaseType.Float3 ||
               baseType == HLSLBaseType.Float4 ||
               baseType == HLSLBaseType.Bool2 ||
               baseType == HLSLBaseType.Bool3 ||
               baseType == HLSLBaseType.Bool4 ||
               baseType == HLSLBaseType.Int2 ||
               baseType == HLSLBaseType.Int3 ||
               baseType == HLSLBaseType.Int4 ||
               baseType == HLSLBaseType.Uint2 ||
               baseType == HLSLBaseType.Uint3 ||
               baseType == HLSLBaseType.Uint4;
    }
}
