namespace HlslParser;

/// <summary>
/// Port of <c>HLSLTreeVisitor</c>. Dispatch happens on <see cref="HLSLNode.NodeType"/>
/// just like the C++, so traversal order and the set of visited nodes stay
/// identical (several transforms depend on that order).
/// </summary>
public class HLSLTreeVisitor
{
    public virtual void VisitType(ref HLSLType type)
    {
    }

    public virtual void VisitRoot(HLSLRoot root)
    {
        HLSLStatement statement = root.Statement;
        while (statement != null)
        {
            VisitTopLevelStatement(statement);
            statement = statement.NextStatement;
        }
    }

    public virtual void VisitTopLevelStatement(HLSLStatement node)
    {
        switch (node.NodeType)
        {
            case HLSLNodeType.Declaration:
                VisitDeclaration((HLSLDeclaration)node);
                break;
            case HLSLNodeType.Struct:
                VisitStruct((HLSLStruct)node);
                break;
            case HLSLNodeType.Buffer:
                VisitBuffer((HLSLBuffer)node);
                break;
            case HLSLNodeType.Function:
                VisitFunction((HLSLFunction)node);
                break;
            case HLSLNodeType.Technique:
                VisitTechnique((HLSLTechnique)node);
                break;
            case HLSLNodeType.Pipeline:
                VisitPipeline((HLSLPipeline)node);
                break;
            default:
                // ASSERT(0) is compiled out in the C++ build; ignore.
                break;
        }
    }

    public virtual void VisitStatements(HLSLStatement statement)
    {
        while (statement != null)
        {
            VisitStatement(statement);
            statement = statement.NextStatement;
        }
    }

    public virtual void VisitStatement(HLSLStatement node)
    {
        switch (node.NodeType)
        {
            case HLSLNodeType.Declaration:
                VisitDeclaration((HLSLDeclaration)node);
                break;
            case HLSLNodeType.ExpressionStatement:
                VisitExpressionStatement((HLSLExpressionStatement)node);
                break;
            case HLSLNodeType.ReturnStatement:
                VisitReturnStatement((HLSLReturnStatement)node);
                break;
            case HLSLNodeType.DiscardStatement:
                VisitDiscardStatement((HLSLDiscardStatement)node);
                break;
            case HLSLNodeType.BreakStatement:
                VisitBreakStatement((HLSLBreakStatement)node);
                break;
            case HLSLNodeType.ContinueStatement:
                VisitContinueStatement((HLSLContinueStatement)node);
                break;
            case HLSLNodeType.IfStatement:
                VisitIfStatement((HLSLIfStatement)node);
                break;
            case HLSLNodeType.ForStatement:
                VisitForStatement((HLSLForStatement)node);
                break;
            case HLSLNodeType.WhileStatement:
                VisitWhileStatement((HLSLWhileStatement)node);
                break;
            case HLSLNodeType.BlockStatement:
                VisitBlockStatement((HLSLBlockStatement)node);
                break;
            default:
                break;
        }
    }

    public virtual void VisitDeclaration(HLSLDeclaration node)
    {
        VisitType(ref node.Type);
        if (node.Assignment != null)
        {
            VisitExpression(node.Assignment);
        }
        if (node.NextDeclaration != null)
        {
            VisitDeclaration(node.NextDeclaration);
        }
    }

    public virtual void VisitStruct(HLSLStruct node)
    {
        HLSLStructField field = node.Field;
        while (field != null)
        {
            VisitStructField(field);
            field = field.NextField;
        }
    }

    public virtual void VisitStructField(HLSLStructField node)
    {
        VisitType(ref node.Type);
    }

    public virtual void VisitBuffer(HLSLBuffer node)
    {
        HLSLDeclaration field = node.Field;
        while (field != null)
        {
            VisitDeclaration(field);
            field = (HLSLDeclaration)field.NextStatement;
        }
    }

    public virtual void VisitFunction(HLSLFunction node)
    {
        VisitType(ref node.ReturnType);

        HLSLArgument argument = node.Argument;
        while (argument != null)
        {
            VisitArgument(argument);
            argument = argument.NextArgument;
        }

        VisitStatements(node.Statement);
    }

    public virtual void VisitArgument(HLSLArgument node)
    {
        VisitType(ref node.Type);
        if (node.DefaultValue != null)
        {
            VisitExpression(node.DefaultValue);
        }
    }

    public virtual void VisitExpressionStatement(HLSLExpressionStatement node)
    {
        VisitExpression(node.Expression);
    }

    public virtual void VisitExpression(HLSLExpression node)
    {
        VisitType(ref node.ExpressionType);

        switch (node.NodeType)
        {
            case HLSLNodeType.UnaryExpression:
                VisitUnaryExpression((HLSLUnaryExpression)node);
                break;
            case HLSLNodeType.BinaryExpression:
                VisitBinaryExpression((HLSLBinaryExpression)node);
                break;
            case HLSLNodeType.ConditionalExpression:
                VisitConditionalExpression((HLSLConditionalExpression)node);
                break;
            case HLSLNodeType.CastingExpression:
                VisitCastingExpression((HLSLCastingExpression)node);
                break;
            case HLSLNodeType.LiteralExpression:
                VisitLiteralExpression((HLSLLiteralExpression)node);
                break;
            case HLSLNodeType.IdentifierExpression:
                VisitIdentifierExpression((HLSLIdentifierExpression)node);
                break;
            case HLSLNodeType.ConstructorExpression:
                VisitConstructorExpression((HLSLConstructorExpression)node);
                break;
            case HLSLNodeType.MemberAccess:
                VisitMemberAccess((HLSLMemberAccess)node);
                break;
            case HLSLNodeType.ArrayAccess:
                VisitArrayAccess((HLSLArrayAccess)node);
                break;
            case HLSLNodeType.FunctionCall:
                VisitFunctionCall((HLSLFunctionCall)node);
                break;
            case HLSLNodeType.SamplerState:
                VisitSamplerState((HLSLSamplerState)node);
                break;
            default:
                break;
        }
    }

    public virtual void VisitReturnStatement(HLSLReturnStatement node)
    {
        VisitExpression(node.Expression);
    }

    public virtual void VisitDiscardStatement(HLSLDiscardStatement node)
    {
    }

    public virtual void VisitBreakStatement(HLSLBreakStatement node)
    {
    }

    public virtual void VisitContinueStatement(HLSLContinueStatement node)
    {
    }

    public virtual void VisitIfStatement(HLSLIfStatement node)
    {
        VisitExpression(node.Condition);
        VisitStatements(node.Statement);
        if (node.ElseStatement != null)
        {
            VisitStatements(node.ElseStatement);
        }
    }

    public virtual void VisitForStatement(HLSLForStatement node)
    {
        if (node.Initialization != null)
        {
            VisitDeclaration(node.Initialization);
        }
        if (node.Condition != null)
        {
            VisitExpression(node.Condition);
        }
        if (node.Increment != null)
        {
            VisitExpression(node.Increment);
        }
        VisitStatements(node.Statement);
    }

    public virtual void VisitWhileStatement(HLSLWhileStatement node)
    {
        if (node.Condition != null)
        {
            VisitExpression(node.Condition);
        }
        VisitStatements(node.Statement);
    }

    public virtual void VisitBlockStatement(HLSLBlockStatement node)
    {
        VisitStatements(node.Statement);
    }

    public virtual void VisitUnaryExpression(HLSLUnaryExpression node)
    {
        VisitExpression(node.Expression);
    }

    public virtual void VisitBinaryExpression(HLSLBinaryExpression node)
    {
        VisitExpression(node.Expression1);
        VisitExpression(node.Expression2);
    }

    public virtual void VisitConditionalExpression(HLSLConditionalExpression node)
    {
        VisitExpression(node.Condition);
        VisitExpression(node.FalseExpression);
        VisitExpression(node.TrueExpression);
    }

    public virtual void VisitCastingExpression(HLSLCastingExpression node)
    {
        VisitType(ref node.Type);
        VisitExpression(node.Expression);
    }

    public virtual void VisitLiteralExpression(HLSLLiteralExpression node)
    {
    }

    public virtual void VisitIdentifierExpression(HLSLIdentifierExpression node)
    {
    }

    public virtual void VisitConstructorExpression(HLSLConstructorExpression node)
    {
        HLSLExpression argument = node.Argument;
        while (argument != null)
        {
            VisitExpression(argument);
            argument = argument.NextExpression;
        }
    }

    public virtual void VisitMemberAccess(HLSLMemberAccess node)
    {
        VisitExpression(node.Object);
    }

    public virtual void VisitArrayAccess(HLSLArrayAccess node)
    {
        VisitExpression(node.Array);
        VisitExpression(node.Index);
    }

    public virtual void VisitFunctionCall(HLSLFunctionCall node)
    {
        HLSLExpression argument = node.Argument;
        while (argument != null)
        {
            VisitExpression(argument);
            argument = argument.NextExpression;
        }
    }

    public virtual void VisitStateAssignment(HLSLStateAssignment node)
    {
    }

    public virtual void VisitSamplerState(HLSLSamplerState node)
    {
        HLSLStateAssignment stateAssignment = node.StateAssignments;
        while (stateAssignment != null)
        {
            VisitStateAssignment(stateAssignment);
            stateAssignment = stateAssignment.NextStateAssignment;
        }
    }

    public virtual void VisitPass(HLSLPass node)
    {
        HLSLStateAssignment stateAssignment = node.StateAssignments;
        while (stateAssignment != null)
        {
            VisitStateAssignment(stateAssignment);
            stateAssignment = stateAssignment.NextStateAssignment;
        }
    }

    public virtual void VisitTechnique(HLSLTechnique node)
    {
        HLSLPass pass = node.Passes;
        while (pass != null)
        {
            VisitPass(pass);
            pass = pass.NextPass;
        }
    }

    public virtual void VisitPipeline(HLSLPipeline node)
    {
        // @@ ? (same as C++)
    }

    public virtual void VisitFunctions(HLSLRoot root)
    {
        HLSLStatement statement = root.Statement;
        while (statement != null)
        {
            if (statement.NodeType == HLSLNodeType.Function)
            {
                VisitFunction((HLSLFunction)statement);
            }
            statement = statement.NextStatement;
        }
    }

    public virtual void VisitParameters(HLSLRoot root)
    {
        HLSLStatement statement = root.Statement;
        while (statement != null)
        {
            if (statement.NodeType == HLSLNodeType.Declaration)
            {
                VisitDeclaration((HLSLDeclaration)statement);
            }
            statement = statement.NextStatement;
        }
    }

    public HLSLFunction FindFunction(HLSLRoot root, string name)
    {
        HLSLStatement statement = root.Statement;
        while (statement != null)
        {
            if (statement.NodeType == HLSLNodeType.Function)
            {
                var function = (HLSLFunction)statement;
                if (String_.Equal(name, function.Name))
                {
                    return function;
                }
            }
            statement = statement.NextStatement;
        }
        return null;
    }

    public HLSLDeclaration FindGlobalDeclaration(HLSLRoot root, string name)
    {
        HLSLStatement statement = root.Statement;
        while (statement != null)
        {
            if (statement.NodeType == HLSLNodeType.Declaration)
            {
                var declaration = (HLSLDeclaration)statement;
                if (String_.Equal(name, declaration.Name))
                {
                    return declaration;
                }
            }
            statement = statement.NextStatement;
        }
        return null;
    }

    public HLSLStruct FindGlobalStruct(HLSLRoot root, string name)
    {
        HLSLStatement statement = root.Statement;
        while (statement != null)
        {
            if (statement.NodeType == HLSLNodeType.Struct)
            {
                var declaration = (HLSLStruct)statement;
                if (String_.Equal(name, declaration.Name))
                {
                    return declaration;
                }
            }
            statement = statement.NextStatement;
        }
        return null;
    }
}
