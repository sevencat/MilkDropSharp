namespace HlslParser;

/// <summary>
/// Port of <c>matrixCtor</c>. The C++ code uses <c>std::vector</c> plus
/// <c>operator==</c>/<c>operator&lt;</c> for lookup in <c>std::find</c> and
/// <c>std::map</c>; here the array is compared element-wise and the struct is
/// usable as a dictionary key.
/// </summary>
public readonly struct MatrixCtor : IEquatable<MatrixCtor>
{
    public readonly HLSLBaseType MatrixType;
    public readonly HLSLBaseType[] ArgumentTypes;

    public MatrixCtor(HLSLBaseType matrixType, HLSLBaseType[] argumentTypes)
    {
        MatrixType = matrixType;
        ArgumentTypes = argumentTypes ?? Array.Empty<HLSLBaseType>();
    }

    public bool Equals(MatrixCtor other)
    {
        if (MatrixType != other.MatrixType)
        {
            return false;
        }

        HLSLBaseType[] a = ArgumentTypes ?? Array.Empty<HLSLBaseType>();
        HLSLBaseType[] b = other.ArgumentTypes ?? Array.Empty<HLSLBaseType>();
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; ++i)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object obj)
    {
        return obj is MatrixCtor other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add((int)MatrixType);
        HLSLBaseType[] types = ArgumentTypes ?? Array.Empty<HLSLBaseType>();
        for (int i = 0; i < types.Length; ++i)
        {
            hash.Add((int)types[i]);
        }
        return hash.ToHashCode();
    }

    /// <summary>Port of the free function <c>matrixCtorBuilder</c>.</summary>
    public static MatrixCtor Build(HLSLType type, HLSLExpression arguments)
    {
        var argumentTypes = new List<HLSLBaseType>();
        HLSLExpression argument = arguments;
        while (argument != null)
        {
            argumentTypes.Add(argument.ExpressionType.BaseType);
            argument = argument.NextExpression;
        }
        return new MatrixCtor(type.BaseType, argumentTypes.ToArray());
    }
}
