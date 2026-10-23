namespace HlslParser;

/// <summary>
/// Port of the C++ <c>Intrinsic</c> helper: a HLSLFunction-shaped descriptor
/// for a built-in function. The generated <see cref="ParserTables.Intrinsics"/>
/// table is built from these.
/// </summary>
internal sealed class Intrinsic
{
    public readonly HLSLFunction Function;

    private Intrinsic(string name, HLSLBaseType returnType, HLSLBaseType[] argumentTypes)
    {
        var function = new HLSLFunction();
        function.Name = name;
        function.ReturnType = new HLSLType(returnType);
        function.NumArguments = argumentTypes.Length;

        HLSLArgument first = null;
        HLSLArgument previous = null;
        for (int i = 0; i < argumentTypes.Length; ++i)
        {
            var argument = new HLSLArgument();
            argument.Type = new HLSLType(argumentTypes[i]) { Flags = (int)HLSLTypeFlags.Const };

            if (previous == null)
            {
                first = argument;
            }
            else
            {
                previous.NextArgument = argument;
            }
            previous = argument;
        }

        function.Argument = first;
        Function = function;
    }

    public static Intrinsic Create(string name, HLSLBaseType returnType, HLSLBaseType[] argumentTypes)
    {
        return new Intrinsic(name, returnType, argumentTypes);
    }

    /// <summary>Port of <c>SamplerIntrinsic</c>: the first argument carries the sampler type.</summary>
    public static Intrinsic CreateSampler(string name, HLSLBaseType returnType, HLSLBaseType arg1, HLSLBaseType samplerType, HLSLBaseType arg2)
    {
        var intrinsic = new Intrinsic(name, returnType, new[] { arg1, arg2 });
        intrinsic.Function.Argument.Type.SamplerType = samplerType;
        return intrinsic;
    }
}
