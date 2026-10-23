namespace HlslParser;

/// <summary>
/// Port of <c>HLSLNodeType</c>. Order matches the C++ enum (values are only
/// used for dispatch, but keeping the order makes diffs easier).
/// </summary>
public enum HLSLNodeType
{
    Root,
    Declaration,
    Struct,
    StructField,
    Buffer,
    BufferField,
    Function,
    Argument,
    Macro,
    ExpressionStatement,
    Expression,
    ReturnStatement,
    DiscardStatement,
    BreakStatement,
    ContinueStatement,
    IfStatement,
    ForStatement,
    WhileStatement,
    BlockStatement,
    UnaryExpression,
    BinaryExpression,
    ConditionalExpression,
    CastingExpression,
    LiteralExpression,
    IdentifierExpression,
    ConstructorExpression,
    MemberAccess,
    ArrayAccess,
    FunctionCall,
    StateAssignment,
    SamplerState,
    Pass,
    Technique,
    Attribute,
    Pipeline,
    Stage,
}

public enum HLSLBinaryOp
{
    And,
    Or,
    Add,
    Sub,
    Mul,
    Div,
    Mod,
    Less,
    Greater,
    LessEqual,
    GreaterEqual,
    Equal,
    NotEqual,
    BitAnd,
    BitOr,
    BitXor,
    Assign,
    AddAssign,
    SubAssign,
    MulAssign,
    DivAssign,
}

public enum HLSLUnaryOp
{
    Negative,       // -x
    Positive,       // +x
    Not,            // !x
    PreIncrement,   // ++x
    PreDecrement,   // --x
    PostIncrement,  // x++
    PostDecrement,  // x--
    BitNot,         // ~x
}

public enum HLSLArgumentModifier
{
    None,
    In,
    Out,
    Inout,
    Uniform,
    Const,
}

public enum HLSLAttributeType
{
    Unknown,
    Unroll,
    Branch,
    Flatten,
    NoFastMath,
}

public static class HLSLBinaryOpExtensions
{
    public static bool IsCompareOp(this HLSLBinaryOp op)
    {
        return op == HLSLBinaryOp.Less ||
               op == HLSLBinaryOp.Greater ||
               op == HLSLBinaryOp.LessEqual ||
               op == HLSLBinaryOp.GreaterEqual ||
               op == HLSLBinaryOp.Equal ||
               op == HLSLBinaryOp.NotEqual;
    }

    public static bool IsArithmeticOp(this HLSLBinaryOp op)
    {
        return op == HLSLBinaryOp.Add ||
               op == HLSLBinaryOp.Sub ||
               op == HLSLBinaryOp.Mul ||
               op == HLSLBinaryOp.Div ||
               op == HLSLBinaryOp.Mod;
    }

    public static bool IsLogicOp(this HLSLBinaryOp op)
    {
        return op == HLSLBinaryOp.And ||
               op == HLSLBinaryOp.Or;
    }

    public static bool IsAssignOp(this HLSLBinaryOp op)
    {
        return op == HLSLBinaryOp.Assign ||
               op == HLSLBinaryOp.AddAssign ||
               op == HLSLBinaryOp.SubAssign ||
               op == HLSLBinaryOp.MulAssign ||
               op == HLSLBinaryOp.DivAssign;
    }
}
