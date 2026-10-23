namespace HlslParser;

/// <summary>
/// Base class for all nodes in the HLSL AST (port of <c>HLSLNode</c>).
/// Each concrete node sets its <see cref="NodeType"/> in its constructor, which
/// replaces the C++ <c>static const HLSLNodeType s_type</c> + template trick.
/// </summary>
public class HLSLNode
{
    public HLSLNodeType NodeType;
    public string FileName;
    public int Line;

    protected HLSLNode(HLSLNodeType nodeType)
    {
        NodeType = nodeType;
    }
}

public class HLSLRoot : HLSLNode
{
    public HLSLRoot()
        : base(HLSLNodeType.Root)
    {
    }

    /// <summary>First statement.</summary>
    public HLSLStatement Statement;
}

public class HLSLStatement : HLSLNode
{
    protected HLSLStatement(HLSLNodeType nodeType)
        : base(nodeType)
    {
    }

    /// <summary>Next statement in the block.</summary>
    public HLSLStatement NextStatement;

    public HLSLAttribute Attributes;

    public bool Hidden;
}

/// <summary>Base class for all types of expressions.</summary>
public class HLSLExpression : HLSLNode
{
    public HLSLExpression()
        : base(HLSLNodeType.Expression)
    {
    }

    protected HLSLExpression(HLSLNodeType nodeType)
        : base(nodeType)
    {
    }

    public HLSLType ExpressionType = new();

    /// <summary>Used when the expression is part of a list, like in a function call.</summary>
    public HLSLExpression NextExpression;
}
