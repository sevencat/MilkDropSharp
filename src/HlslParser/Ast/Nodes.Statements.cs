namespace HlslParser;

public class HLSLDeclaration : HLSLStatement
{
    public HLSLDeclaration()
        : base(HLSLNodeType.Declaration)
    {
    }

    public string Name;
    public HLSLType Type = new();
    public string RegisterName;   // @@ Store register index?
    public string Semantic;

    /// <summary>If multiple variables declared on a line.</summary>
    public HLSLDeclaration NextDeclaration;

    public HLSLExpression Assignment;
    public HLSLBuffer Buffer;
}

public class HLSLStruct : HLSLStatement
{
    public HLSLStruct()
        : base(HLSLNodeType.Struct)
    {
    }

    public string Name;

    /// <summary>First field in the structure.</summary>
    public HLSLStructField Field;
}

public class HLSLStructField : HLSLNode
{
    public HLSLStructField()
        : base(HLSLNodeType.StructField)
    {
    }

    public string Name;
    public HLSLType Type = new();
    public string Semantic;
    public string SvSemantic;

    /// <summary>Next field in the structure.</summary>
    public HLSLStructField NextField;

    public bool Hidden;
}

/// <summary>A cbuffer or tbuffer declaration.</summary>
public class HLSLBuffer : HLSLStatement
{
    public HLSLBuffer()
        : base(HLSLNodeType.Buffer)
    {
    }

    public string Name;
    public string RegisterName;
    public HLSLDeclaration Field;
}

/// <summary>Function declaration.</summary>
public class HLSLFunction : HLSLStatement
{
    public HLSLFunction()
        : base(HLSLNodeType.Function)
    {
    }

    public string Name;
    public HLSLType ReturnType = new();
    public string Semantic;
    public string SvSemantic;
    public int NumArguments;
    public int NumOutputArguments;  // Includes out and inout arguments.
    public HLSLArgument Argument;
    public HLSLStatement Statement;

    /// <summary>Which HLSLFunction this one forward-declares.</summary>
    public HLSLFunction Forward;
}

/// <summary>Declaration of an argument to a function.</summary>
public class HLSLArgument : HLSLNode
{
    public HLSLArgument()
        : base(HLSLNodeType.Argument)
    {
    }

    public string Name;
    public HLSLArgumentModifier Modifier;
    public HLSLType Type = new();
    public string Semantic;
    public string SvSemantic;
    public HLSLExpression DefaultValue;
    public HLSLArgument NextArgument;
    public bool Hidden;
}

/// <summary>Macro declaration (projectM fork).</summary>
public class HLSLMacro : HLSLStatement
{
    public HLSLMacro()
        : base(HLSLNodeType.Macro)
    {
    }

    public string Name;
    public HLSLArgument Argument;
    public uint NumArguments;
    public string Value;
    public HLSLMacro MacroAliased;
}

/// <summary>An expression which forms a complete statement.</summary>
public class HLSLExpressionStatement : HLSLStatement
{
    public HLSLExpressionStatement()
        : base(HLSLNodeType.ExpressionStatement)
    {
    }

    public HLSLExpression Expression;
}

public class HLSLReturnStatement : HLSLStatement
{
    public HLSLReturnStatement()
        : base(HLSLNodeType.ReturnStatement)
    {
    }

    public HLSLExpression Expression;
}

public class HLSLDiscardStatement : HLSLStatement
{
    public HLSLDiscardStatement()
        : base(HLSLNodeType.DiscardStatement)
    {
    }
}

public class HLSLBreakStatement : HLSLStatement
{
    public HLSLBreakStatement()
        : base(HLSLNodeType.BreakStatement)
    {
    }
}

public class HLSLContinueStatement : HLSLStatement
{
    public HLSLContinueStatement()
        : base(HLSLNodeType.ContinueStatement)
    {
    }
}

public class HLSLIfStatement : HLSLStatement
{
    public HLSLIfStatement()
        : base(HLSLNodeType.IfStatement)
    {
    }

    public HLSLExpression Condition;
    public HLSLStatement Statement;
    public HLSLStatement ElseStatement;
    public bool IsStatic;
}

public class HLSLForStatement : HLSLStatement
{
    public HLSLForStatement()
        : base(HLSLNodeType.ForStatement)
    {
    }

    public HLSLDeclaration Initialization;
    public HLSLExpression InitializationWithoutType;
    public HLSLExpression Condition;
    public HLSLExpression Increment;
    public HLSLStatement Statement;
}

public class HLSLWhileStatement : HLSLStatement
{
    public HLSLWhileStatement()
        : base(HLSLNodeType.WhileStatement)
    {
    }

    public HLSLExpression Condition;
    public HLSLStatement Statement;
}

public class HLSLBlockStatement : HLSLStatement
{
    public HLSLBlockStatement()
        : base(HLSLNodeType.BlockStatement)
    {
    }

    public HLSLStatement Statement;
}

public class HLSLAttribute : HLSLNode
{
    public HLSLAttribute()
        : base(HLSLNodeType.Attribute)
    {
    }

    public HLSLAttributeType AttributeType;
    public HLSLExpression Argument;
    public HLSLAttribute NextAttribute;
}
