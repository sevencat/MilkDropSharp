namespace HlslParser;

public class HLSLUnaryExpression : HLSLExpression
{
    public HLSLUnaryExpression()
        : base(HLSLNodeType.UnaryExpression)
    {
    }

    public HLSLUnaryOp UnaryOp;
    public HLSLExpression Expression;
}

public class HLSLBinaryExpression : HLSLExpression
{
    public HLSLBinaryExpression()
        : base(HLSLNodeType.BinaryExpression)
    {
    }

    public HLSLBinaryOp BinaryOp;
    public HLSLExpression Expression1;
    public HLSLExpression Expression2;
}

/// <summary>? : construct.</summary>
public class HLSLConditionalExpression : HLSLExpression
{
    public HLSLConditionalExpression()
        : base(HLSLNodeType.ConditionalExpression)
    {
    }

    public HLSLExpression Condition;
    public HLSLExpression TrueExpression;
    public HLSLExpression FalseExpression;
}

public class HLSLCastingExpression : HLSLExpression
{
    public HLSLCastingExpression()
        : base(HLSLNodeType.CastingExpression)
    {
    }

    public HLSLType Type = new();
    public HLSLExpression Expression;
}

/// <summary>Float, integer, boolean, etc. literal constant.</summary>
public class HLSLLiteralExpression : HLSLExpression
{
    public HLSLLiteralExpression()
        : base(HLSLNodeType.LiteralExpression)
    {
    }

    /// <summary>Note, not all types can be literals. This mirrors HLSL literal's own type field.</summary>
    public HLSLBaseType Type;

    // The C++ code uses a union; keep three fields and rely on Type.
    public bool BValue;
    public float FValue;
    public int IValue;
}

/// <summary>An identifier, typically a variable name or structure field name.</summary>
public class HLSLIdentifierExpression : HLSLExpression
{
    public HLSLIdentifierExpression()
        : base(HLSLNodeType.IdentifierExpression)
    {
    }

    public string Name;

    /// <summary>This is a global variable.</summary>
    public bool Global;
}

/// <summary>float2(1, 2)</summary>
public class HLSLConstructorExpression : HLSLExpression
{
    public HLSLConstructorExpression()
        : base(HLSLNodeType.ConstructorExpression)
    {
    }

    public HLSLType Type = new();
    public HLSLExpression Argument;
}

/// <summary>object.member</summary>
public class HLSLMemberAccess : HLSLExpression
{
    public HLSLMemberAccess()
        : base(HLSLNodeType.MemberAccess)
    {
    }

    public HLSLExpression Object;
    public string Field;
    public bool Swizzle;
}

/// <summary>array[index]</summary>
public class HLSLArrayAccess : HLSLExpression
{
    public HLSLArrayAccess()
        : base(HLSLNodeType.ArrayAccess)
    {
    }

    public HLSLExpression Array;
    public HLSLExpression Index;
}

public class HLSLFunctionCall : HLSLExpression
{
    public HLSLFunctionCall()
        : base(HLSLNodeType.FunctionCall)
    {
    }

    public HLSLFunction Function;
    public HLSLExpression Argument;
    public int NumArguments;
}

public class HLSLSamplerState : HLSLExpression
{
    public HLSLSamplerState()
        : base(HLSLNodeType.SamplerState)
    {
    }

    public int NumStateAssignments;
    public HLSLStateAssignment StateAssignments;
}
