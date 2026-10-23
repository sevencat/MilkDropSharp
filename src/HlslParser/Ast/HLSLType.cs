namespace HlslParser;

[Flags]
public enum HLSLTypeFlags
{
    None = 0,
    Const = 0x01,
    Static = 0x02,
    Uniform = 0x04,

    Input = 0x100,
    Output = 0x200,

    // Interpolation modifiers.
    Linear = 0x10000,
    Centroid = 0x20000,
    NoInterpolation = 0x40000,
    NoPerspective = 0x80000,
    Sample = 0x100000,

    // Misc.
    NoPromote = 0x200000,
}

public enum HLSLAddressSpace
{
    Undefined,
    Constant,
    Device,
    Thread,
    Shared,
}

/// <summary>
/// Port of <c>HLSLType</c>.
///
/// Kept as a mutable struct on purpose: the C++ code relies on value semantics
/// (assignment copies, functions take <c>const HLSLType&amp;</c> or
/// <c>HLSLType&amp;</c>). Every field is either a primitive or a reference, so
/// the shallow copy is equivalent.
/// </summary>
public struct HLSLType
{
    public HLSLBaseType BaseType;
    public HLSLBaseType SamplerType;    // Half or Float
    public string TypeName;             // For user defined types.
    public bool Array;
    public HLSLExpression ArraySize;
    public int Flags;
    public HLSLAddressSpace AddressSpace;

    public HLSLType()
        : this(HLSLBaseType.Unknown)
    {
    }

    public HLSLType(HLSLBaseType baseType)
    {
        BaseType = baseType;
        SamplerType = HLSLBaseType.Float;
        TypeName = null;
        Array = false;
        ArraySize = null;
        Flags = 0;
        AddressSpace = HLSLAddressSpace.Undefined;
    }

    public static bool IsSamplerType(in HLSLType type)
    {
        return BaseTypes.IsSamplerType(type.BaseType);
    }

    public static bool IsScalarType(in HLSLType type)
    {
        return BaseTypes.IsScalarType(type.BaseType);
    }

    public static bool IsVectorType(in HLSLType type)
    {
        return BaseTypes.IsVectorType(type.BaseType);
    }
}
