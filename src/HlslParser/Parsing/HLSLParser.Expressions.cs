namespace HlslParser;

public partial class HLSLParser
{
    internal bool ParseExpression(out HLSLExpression expression)
    {
        if (!ParseBinaryExpression(0, out expression))
        {
            return false;
        }

        if (AcceptAssign(out HLSLBinaryOp assignOp))
        {
            if (!ParseExpression(out HLSLExpression expression2))
            {
                return false;
            }

            var binaryExpression = _tree.AddNode<HLSLBinaryExpression>(expression.FileName, expression.Line);
            binaryExpression.BinaryOp = assignOp;
            binaryExpression.Expression1 = expression;
            binaryExpression.Expression2 = expression2;
            // This type is not strictly correct, since the type should be a
            // reference. However, for our usage of the types it is sufficient.
            binaryExpression.ExpressionType = expression.ExpressionType;

            if (!CheckTypeCast(expression2.ExpressionType, expression.ExpressionType))
            {
                string srcTypeName = GetTypeName(expression2.ExpressionType);
                string dstTypeName = GetTypeName(expression.ExpressionType);
                _tokenizer.Error(CPrintf.Format("Cannot implicitly convert from '%s' to '%s'", srcTypeName, dstTypeName));
                return false;
            }

            expression = binaryExpression;
        }

        return true;
    }

    internal bool AcceptBinaryOperator(int priority, out HLSLBinaryOp binaryOp)
    {
        int token = _tokenizer.GetToken();
        switch (token)
        {
            case (int)HLSLToken.AndAnd: binaryOp = HLSLBinaryOp.And; break;
            case (int)HLSLToken.BarBar: binaryOp = HLSLBinaryOp.Or; break;
            case '+': binaryOp = HLSLBinaryOp.Add; break;
            case '-': binaryOp = HLSLBinaryOp.Sub; break;
            case '*': binaryOp = HLSLBinaryOp.Mul; break;
            case '/': binaryOp = HLSLBinaryOp.Div; break;
            case '%': binaryOp = HLSLBinaryOp.Mod; break;
            case '<': binaryOp = HLSLBinaryOp.Less; break;
            case '>': binaryOp = HLSLBinaryOp.Greater; break;
            case (int)HLSLToken.LessEqual: binaryOp = HLSLBinaryOp.LessEqual; break;
            case (int)HLSLToken.GreaterEqual: binaryOp = HLSLBinaryOp.GreaterEqual; break;
            case (int)HLSLToken.EqualEqual: binaryOp = HLSLBinaryOp.Equal; break;
            case (int)HLSLToken.NotEqual: binaryOp = HLSLBinaryOp.NotEqual; break;
            case '&': binaryOp = HLSLBinaryOp.BitAnd; break;
            case '|': binaryOp = HLSLBinaryOp.BitOr; break;
            case '^': binaryOp = HLSLBinaryOp.BitXor; break;
            default:
                binaryOp = HLSLBinaryOp.Add;
                return false;
        }

        if (ParserTables.BinaryOpPriority[(int)binaryOp] > priority)
        {
            _tokenizer.Next();
            return true;
        }

        return false;
    }

    internal bool AcceptUnaryOperator(bool pre, out HLSLUnaryOp unaryOp)
    {
        int token = _tokenizer.GetToken();
        if (token == (int)HLSLToken.PlusPlus)
        {
            unaryOp = pre ? HLSLUnaryOp.PreIncrement : HLSLUnaryOp.PostIncrement;
        }
        else if (token == (int)HLSLToken.MinusMinus)
        {
            unaryOp = pre ? HLSLUnaryOp.PreDecrement : HLSLUnaryOp.PostDecrement;
        }
        else if (pre && token == '-')
        {
            unaryOp = HLSLUnaryOp.Negative;
        }
        else if (pre && token == '+')
        {
            unaryOp = HLSLUnaryOp.Positive;
        }
        else if (pre && token == '!')
        {
            unaryOp = HLSLUnaryOp.Not;
        }
        else if (pre && token == '~')
        {
            // C++ parity: '~' is mapped to HLSLUnaryOp_Not here (sic), but the
            // BitNot validation below still runs because of the separate check
            // in ParseTerminalExpression... which in C++ never triggers since
            // BitNot is never produced. Kept verbatim.
            unaryOp = HLSLUnaryOp.Not;
        }
        else
        {
            unaryOp = HLSLUnaryOp.Not;
            return false;
        }

        _tokenizer.Next();
        return true;
    }

    internal bool AcceptAssign(out HLSLBinaryOp binaryOp)
    {
        if (Accept('='))
        {
            binaryOp = HLSLBinaryOp.Assign;
        }
        else if (Accept((int)HLSLToken.PlusEqual))
        {
            binaryOp = HLSLBinaryOp.AddAssign;
        }
        else if (Accept((int)HLSLToken.MinusEqual))
        {
            binaryOp = HLSLBinaryOp.SubAssign;
        }
        else if (Accept((int)HLSLToken.TimesEqual))
        {
            binaryOp = HLSLBinaryOp.MulAssign;
        }
        else if (Accept((int)HLSLToken.DivideEqual))
        {
            binaryOp = HLSLBinaryOp.DivAssign;
        }
        else
        {
            binaryOp = HLSLBinaryOp.Assign;
            return false;
        }
        return true;
    }

    internal bool ParseBinaryExpression(int priority, out HLSLExpression expression)
    {
        string fileName = GetFileName();
        int line = GetLineNumber();

        if (!ParseTerminalExpression(out expression, out char needsExpressionEndChar))
        {
            return false;
        }

        // Reset priority because of an opened parenthesis.
        if (needsExpressionEndChar != 0)
        {
            priority = 0;
        }

        bool acceptBinaryOp = false;
        HLSLBinaryOp binaryOp = HLSLBinaryOp.Add;

        while (true)
        {
            if (acceptBinaryOp || AcceptBinaryOperator(priority, out binaryOp))
            {
                acceptBinaryOp = false;
                if (!ParseBinaryExpression(ParserTables.BinaryOpPriority[(int)binaryOp], out HLSLExpression expression2))
                {
                    return false;
                }

                var binaryExpression = _tree.AddNode<HLSLBinaryExpression>(fileName, line);
                binaryExpression.BinaryOp = binaryOp;
                binaryExpression.Expression1 = expression;
                binaryExpression.Expression2 = expression2;

                if (!GetBinaryOpResultType(binaryOp, expression.ExpressionType, expression2.ExpressionType, out HLSLType resultType))
                {
                    string typeName1 = GetTypeName(binaryExpression.Expression1.ExpressionType);
                    string typeName2 = GetTypeName(binaryExpression.Expression2.ExpressionType);
                    _tokenizer.Error(CPrintf.Format(
                        "binary '%s' : no global operator found which takes types '%s' and '%s' (or there is no acceptable conversion)",
                        GetBinaryOpName(binaryOp), typeName1, typeName2));
                    return false;
                }

                binaryExpression.ExpressionType = resultType;

                // Propagate constness.
                binaryExpression.ExpressionType.Flags =
                    (expression.ExpressionType.Flags | expression2.ExpressionType.Flags) & (int)HLSLTypeFlags.Const;

                expression = binaryExpression;
            }
            else if (ParserTables.ConditionalOpPriority > priority && Accept('?'))
            {
                var conditionalExpression = _tree.AddNode<HLSLConditionalExpression>(fileName, line);
                conditionalExpression.Condition = expression;

                if (!ParseBinaryExpression(ParserTables.ConditionalOpPriority, out HLSLExpression expression1) ||
                    !Expect(':') ||
                    !ParseBinaryExpression(ParserTables.ConditionalOpPriority, out HLSLExpression expression2))
                {
                    return false;
                }

                // Make sure both cases have compatible types.
                if (GetTypeCastRank(_tree, expression1.ExpressionType, expression2.ExpressionType) == -1)
                {
                    string srcTypeName = GetTypeName(expression2.ExpressionType);
                    string dstTypeName = GetTypeName(expression1.ExpressionType);
                    _tokenizer.Error(CPrintf.Format("':' no possible conversion from '%s' to '%s'", srcTypeName, dstTypeName));
                    return false;
                }

                conditionalExpression.TrueExpression = expression1;
                conditionalExpression.FalseExpression = expression2;
                conditionalExpression.ExpressionType = expression1.ExpressionType;

                expression = conditionalExpression;
            }
            else
            {
                // Before breaking, consume end char if needed and check for more operators.
                if (needsExpressionEndChar != 0)
                {
                    if (!Expect(needsExpressionEndChar))
                    {
                        return false;
                    }
                    needsExpressionEndChar = (char)0;

                    // After consuming end char, check if there is a binary operator to continue.
                    if (AcceptBinaryOperator(priority, out binaryOp))
                    {
                        acceptBinaryOp = true;
                        continue;
                    }
                }
                break;
            }

            // First: try if next is a binary op before exiting the loop.
            if (AcceptBinaryOperator(priority, out binaryOp))
            {
                acceptBinaryOp = true;
                continue;
            }

            if (needsExpressionEndChar != 0)
            {
                if (!Expect(needsExpressionEndChar))
                {
                    return false;
                }
                needsExpressionEndChar = (char)0;
            }
        }

        return needsExpressionEndChar == 0 || Expect(needsExpressionEndChar);
    }

    internal bool ParsePartialConstructor(out HLSLExpression expression, HLSLBaseType type, string typeName)
    {
        string fileName = GetFileName();
        int line = GetLineNumber();

        var constructorExpression = _tree.AddNode<HLSLConstructorExpression>(fileName, line);
        constructorExpression.Type.BaseType = type;
        constructorExpression.Type.TypeName = typeName;

        if (!ParseExpressionList(')', false, out constructorExpression.Argument, out _))
        {
            expression = null;
            return false;
        }

        constructorExpression.ExpressionType = constructorExpression.Type;
        constructorExpression.ExpressionType.Flags = (int)HLSLTypeFlags.Const;
        expression = constructorExpression;
        return true;
    }

    internal bool ParseTerminalExpression(out HLSLExpression expression, out char needsExpressionEndChar)
    {
        string fileName = GetFileName();
        int line = GetLineNumber();

        expression = null;
        needsExpressionEndChar = (char)0;

        if (AcceptUnaryOperator(true, out HLSLUnaryOp unaryOp))
        {
            var unaryExpression = _tree.AddNode<HLSLUnaryExpression>(fileName, line);
            unaryExpression.UnaryOp = unaryOp;
            if (!ParseTerminalExpression(out unaryExpression.Expression, out needsExpressionEndChar))
            {
                return false;
            }

            if (unaryOp == HLSLUnaryOp.BitNot)
            {
                if (unaryExpression.Expression.ExpressionType.BaseType < HLSLBaseType.FirstInteger ||
                    unaryExpression.Expression.ExpressionType.BaseType > HLSLBaseType.LastInteger)
                {
                    string typeName = GetTypeName(unaryExpression.Expression.ExpressionType);
                    _tokenizer.Error(CPrintf.Format(
                        "unary '~' : no global operator found which takes type '%s' (or there is no acceptable conversion)", typeName));
                    return false;
                }
            }

            if (unaryOp == HLSLUnaryOp.Not)
            {
                if (unaryExpression.Expression.ExpressionType.BaseType != HLSLBaseType.Bool)
                {
                    // Insert a cast to bool.
                    var castingExpression = _tree.AddNode<HLSLCastingExpression>(fileName, line);
                    castingExpression.Type.BaseType = HLSLBaseType.Bool;
                    castingExpression.Expression = unaryExpression.Expression;
                    castingExpression.ExpressionType.BaseType = HLSLBaseType.Bool;

                    unaryExpression.Expression = castingExpression;
                    expression = unaryExpression;
                }

                unaryExpression.ExpressionType = new HLSLType(HLSLBaseType.Bool);

                // Propagate constness.
                unaryExpression.ExpressionType.Flags =
                    unaryExpression.Expression.ExpressionType.Flags & (int)HLSLTypeFlags.Const;
            }

            if (unaryOp == HLSLUnaryOp.Negative || unaryOp == HLSLUnaryOp.Positive)
            {
                if (unaryExpression.Expression.ExpressionType.BaseType <= HLSLBaseType.Bool4 &&
                    unaryExpression.Expression.ExpressionType.BaseType >= HLSLBaseType.Bool)
                {
                    // Insert a cast bool to int.
                    int numComponents = BaseTypes.Descriptions[(int)unaryExpression.Expression.ExpressionType.BaseType].NumComponents;
                    HLSLBaseType baseType = (HLSLBaseType)((int)HLSLBaseType.Int + numComponents - 1);

                    unaryExpression.ExpressionType = unaryExpression.Expression.ExpressionType;

                    var castingExpression = _tree.AddNode<HLSLCastingExpression>(fileName, line);
                    castingExpression.Type.BaseType = baseType;
                    castingExpression.Expression = unaryExpression.Expression;
                    castingExpression.ExpressionType.BaseType = baseType;

                    unaryExpression.Expression = castingExpression;
                    expression = unaryExpression;
                }
                else
                {
                    unaryExpression.ExpressionType = unaryExpression.Expression.ExpressionType;
                }
            }
            else
            {
                unaryExpression.ExpressionType = unaryExpression.Expression.ExpressionType;
            }

            expression = unaryExpression;
            return true;
        }

        // Expressions inside parenthesis or casts.
        char expressionEndChar = (char)0;
        if (Accept('('))
        {
            expressionEndChar = ')';
        }
        else if (Accept('{'))
        {
            expressionEndChar = '}';
        }

        if (expressionEndChar != 0)
        {
            // Check for a casting operator.
            var type = new HLSLType();
            if (AcceptType(false, ref type))
            {
                // This is actually a type constructor like (float2(...
                if (Accept('('))
                {
                    needsExpressionEndChar = expressionEndChar;
                    return ParsePartialConstructor(out expression, type.BaseType, type.TypeName);
                }

                var castingExpression = _tree.AddNode<HLSLCastingExpression>(fileName, line);
                castingExpression.Type = type;
                expression = castingExpression;
                castingExpression.ExpressionType = type;
                return Expect(')') && ParseExpression(out castingExpression.Expression);
            }

            if (!ParseExpressionList(expressionEndChar, false, out expression, out _))
            {
                return false;
            }

            if (expressionEndChar == ')')
            {
                // Allows comma separated expression lists; the HLSL compiler
                // keeps the last one.
                HLSLExpression lastExpression = expression;
                while (lastExpression.NextExpression != null)
                {
                    lastExpression = lastExpression.NextExpression;
                }
            }
        }
        else
        {
            // Terminal values.
            if (AcceptFloat(out float fValue))
            {
                var literalExpression = _tree.AddNode<HLSLLiteralExpression>(fileName, line);
                literalExpression.Type = HLSLBaseType.Float;
                literalExpression.FValue = fValue;
                literalExpression.ExpressionType.BaseType = literalExpression.Type;
                literalExpression.ExpressionType.Flags = (int)HLSLTypeFlags.Const;
                expression = literalExpression;
                return true;
            }

            if (AcceptInt(out int iValue))
            {
                var literalExpression = _tree.AddNode<HLSLLiteralExpression>(fileName, line);
                literalExpression.Type = HLSLBaseType.Int;
                literalExpression.IValue = iValue;
                literalExpression.ExpressionType.BaseType = literalExpression.Type;
                literalExpression.ExpressionType.Flags = (int)HLSLTypeFlags.Const;
                expression = literalExpression;
                return true;
            }

            if (Accept((int)HLSLToken.True))
            {
                var literalExpression = _tree.AddNode<HLSLLiteralExpression>(fileName, line);
                literalExpression.Type = HLSLBaseType.Bool;
                literalExpression.BValue = true;
                literalExpression.ExpressionType.BaseType = literalExpression.Type;
                literalExpression.ExpressionType.Flags = (int)HLSLTypeFlags.Const;
                expression = literalExpression;
                return true;
            }

            if (Accept((int)HLSLToken.False))
            {
                var literalExpression = _tree.AddNode<HLSLLiteralExpression>(fileName, line);
                literalExpression.Type = HLSLBaseType.Bool;
                literalExpression.BValue = false;
                literalExpression.ExpressionType.BaseType = literalExpression.Type;
                literalExpression.ExpressionType.Flags = (int)HLSLTypeFlags.Const;
                expression = literalExpression;
                return true;
            }

            // Type constructor.
            var type = new HLSLType();
            if (AcceptType(false, ref type))
            {
                Expect('(');
                if (!ParsePartialConstructor(out expression, type.BaseType, type.TypeName))
                {
                    return false;
                }
            }
            else
            {
                var identifierExpression = _tree.AddNode<HLSLIdentifierExpression>(fileName, line);
                if (!ExpectIdentifier(out identifierExpression.Name))
                {
                    return false;
                }

                bool undeclaredIdentifier = false;

                if (FindVariable(identifierExpression.Name, out HLSLType identifierType, out identifierExpression.Global))
                {
                    identifierExpression.ExpressionType = identifierType;
                }
                else
                {
                    if (GetIsFunction(identifierExpression.Name))
                    {
                        // Functions are always global scope.
                        identifierExpression.Global = true;
                    }
                    else
                    {
                        undeclaredIdentifier = true;
                    }
                }

                if (undeclaredIdentifier)
                {
                    if (_allowUndeclaredIdentifiers)
                    {
                        var literalExpression = _tree.AddNode<HLSLLiteralExpression>(fileName, line);
                        literalExpression.BValue = false;
                        literalExpression.Type = HLSLBaseType.Bool;
                        literalExpression.ExpressionType.BaseType = literalExpression.Type;
                        literalExpression.ExpressionType.Flags = (int)HLSLTypeFlags.Const;
                        expression = literalExpression;
                    }
                    else
                    {
                        _tokenizer.Error(CPrintf.Format("Undeclared identifier '%s'", identifierExpression.Name));
                        return false;
                    }
                }
                else
                {
                    expression = identifierExpression;
                }
            }
        }

        bool done = false;
        while (!done)
        {
            done = true;

            // Post fix unary operator.
            while (AcceptUnaryOperator(false, out HLSLUnaryOp unaryOp2))
            {
                var unaryExpression = _tree.AddNode<HLSLUnaryExpression>(fileName, line);
                unaryExpression.UnaryOp = unaryOp2;
                unaryExpression.Expression = expression;
                unaryExpression.ExpressionType = unaryExpression.Expression.ExpressionType;
                expression = unaryExpression;
                done = false;
            }

            // Member access operator.
            while (Accept('.'))
            {
                var memberAccess = _tree.AddNode<HLSLMemberAccess>(fileName, line);
                memberAccess.Object = expression;
                if (!ExpectIdentifier(out memberAccess.Field))
                {
                    return false;
                }
                if (!GetMemberType(expression.ExpressionType, memberAccess))
                {
                    _tokenizer.Error(CPrintf.Format("Couldn't access '%s'", memberAccess.Field));
                    return false;
                }
                expression = memberAccess;
                done = false;
            }

            // Handle array access.
            while (Accept('['))
            {
                var arrayAccess = _tree.AddNode<HLSLArrayAccess>(fileName, line);
                arrayAccess.Array = expression;
                if (!ParseExpression(out arrayAccess.Index) || !Expect(']'))
                {
                    return false;
                }

                if (expression.ExpressionType.Array)
                {
                    arrayAccess.ExpressionType = expression.ExpressionType;
                    arrayAccess.ExpressionType.Array = false;
                    arrayAccess.ExpressionType.ArraySize = null;
                }
                else
                {
                    switch (expression.ExpressionType.BaseType)
                    {
                        case HLSLBaseType.Float2:
                        case HLSLBaseType.Float3:
                        case HLSLBaseType.Float4:
                            arrayAccess.ExpressionType.BaseType = HLSLBaseType.Float;
                            break;

                        case HLSLBaseType.Float4x4:
                        case HLSLBaseType.Float3x4:
                        case HLSLBaseType.Float2x4:
                            arrayAccess.ExpressionType.BaseType = HLSLBaseType.Float4;
                            break;

                        case HLSLBaseType.Float4x3:
                        case HLSLBaseType.Float3x3:
                        case HLSLBaseType.Float2x3:
                            arrayAccess.ExpressionType.BaseType = HLSLBaseType.Float3;
                            break;

                        case HLSLBaseType.Float4x2:
                        case HLSLBaseType.Float3x2:
                        case HLSLBaseType.Float2x2:
                            arrayAccess.ExpressionType.BaseType = HLSLBaseType.Float2;
                            break;

                        case HLSLBaseType.Bool2:
                        case HLSLBaseType.Bool3:
                        case HLSLBaseType.Bool4:
                            arrayAccess.ExpressionType.BaseType = HLSLBaseType.Bool;
                            break;

                        case HLSLBaseType.Int2:
                        case HLSLBaseType.Int3:
                        case HLSLBaseType.Int4:
                            arrayAccess.ExpressionType.BaseType = HLSLBaseType.Int;
                            break;

                        case HLSLBaseType.Uint2:
                        case HLSLBaseType.Uint3:
                        case HLSLBaseType.Uint4:
                            arrayAccess.ExpressionType.BaseType = HLSLBaseType.Uint;
                            break;

                        default:
                            _tokenizer.Error("array, matrix, vector, or indexable object type expected in index expression");
                            return false;
                    }
                }

                expression = arrayAccess;
                done = false;
            }

            // Handle function calls. Note, HLSL functions aren't like C
            // function pointers -- we can only directly call on an identifier,
            // not on an expression.
            if (Accept('('))
            {
                var functionCall = _tree.AddNode<HLSLFunctionCall>(fileName, line);
                done = false;
                if (!ParseExpressionList(')', false, out functionCall.Argument, out functionCall.NumArguments))
                {
                    return false;
                }

                if (expression.NodeType != HLSLNodeType.IdentifierExpression)
                {
                    _tokenizer.Error("Expected function identifier");
                    return false;
                }

                var identifierExpression = (HLSLIdentifierExpression)expression;
                HLSLFunction function = MatchFunctionCall(functionCall, identifierExpression.Name);
                if (function == null)
                {
                    return false;
                }

                functionCall.Function = function;
                functionCall.ExpressionType = function.ReturnType;
                expression = functionCall;
            }
        }

        return true;
    }

    internal bool ParseExpressionList(int endToken, bool allowEmptyEnd, out HLSLExpression firstExpression, out int numExpressions)
    {
        numExpressions = 0;
        firstExpression = null;
        HLSLExpression lastExpression = null;

        while (!Accept(endToken))
        {
            if (CheckForUnexpectedEndOfStream(endToken))
            {
                return false;
            }
            if (numExpressions > 0 && !Expect(','))
            {
                return false;
            }
            // It is acceptable for the final element in an initialization list
            // to have a trailing comma in some cases, like {1, 2, 3,}.
            if (allowEmptyEnd && Accept(endToken))
            {
                break;
            }

            if (!ParseExpression(out HLSLExpression expression))
            {
                return false;
            }

            if (firstExpression == null)
            {
                firstExpression = expression;
            }
            else
            {
                lastExpression.NextExpression = expression;
            }
            lastExpression = expression;
            ++numExpressions;
        }

        return true;
    }

    internal bool ParseArgumentList(out HLSLArgument firstArgument, out int numArguments, ref int numOutputArguments)
    {
        string fileName = GetFileName();
        int line = GetLineNumber();

        firstArgument = null;
        HLSLArgument lastArgument = null;
        numArguments = 0;

        while (!Accept(')'))
        {
            if (CheckForUnexpectedEndOfStream(')'))
            {
                return false;
            }
            if (numArguments > 0 && !Expect(','))
            {
                return false;
            }

            var argument = _tree.AddNode<HLSLArgument>(fileName, line);

            if (Accept((int)HLSLToken.Uniform))
            {
                argument.Modifier = HLSLArgumentModifier.Uniform;
            }
            else if (Accept((int)HLSLToken.In))
            {
                argument.Modifier = HLSLArgumentModifier.In;
            }
            else if (Accept((int)HLSLToken.Out))
            {
                argument.Modifier = HLSLArgumentModifier.Out;
            }
            else if (Accept((int)HLSLToken.InOut))
            {
                argument.Modifier = HLSLArgumentModifier.Inout;
            }
            else if (Accept((int)HLSLToken.Const))
            {
                argument.Modifier = HLSLArgumentModifier.Const;
            }

            if (!ExpectDeclaration(true, ref argument.Type, out argument.Name))
            {
                return false;
            }

            DeclareVariable(argument.Name, argument.Type);

            // Optional semantic.
            if (Accept(':') && !ExpectIdentifier(out argument.Semantic))
            {
                return false;
            }

            if (Accept('=') && !ParseExpression(out argument.DefaultValue))
            {
                return false;
            }

            if (lastArgument != null)
            {
                lastArgument.NextArgument = argument;
            }
            else
            {
                firstArgument = argument;
            }
            lastArgument = argument;

            ++numArguments;
            if (argument.Modifier == HLSLArgumentModifier.Out || argument.Modifier == HLSLArgumentModifier.Inout)
            {
                ++numOutputArguments;
            }
        }

        return true;
    }

    internal bool GetMemberType(in HLSLType objectType, HLSLMemberAccess memberAccess)
    {
        string fieldName = memberAccess.Field;

        if (objectType.BaseType == HLSLBaseType.UserDefined)
        {
            HLSLStruct structure = FindUserDefinedType(objectType.TypeName);

            if (structure != null)
            {
                HLSLStructField field = structure.Field;
                while (field != null)
                {
                    if (String_.Equal(field.Name, fieldName))
                    {
                        memberAccess.ExpressionType = field.Type;
                        return true;
                    }
                    field = field.NextField;
                }
            }

            return false;
        }

        if (BaseTypes.Descriptions[(int)objectType.BaseType].NumericType == NumericType.NaN)
        {
            // Currently there are no non-numeric types that allow member access.
            return false;
        }

        int swizzleLength = 0;

        if (BaseTypes.Descriptions[(int)objectType.BaseType].NumDimensions <= 1)
        {
            // Check for a swizzle on the scalar/vector types.
            for (int i = 0; i < fieldName.Length; ++i)
            {
                char c = fieldName[i];
                if (c != 'x' && c != 'y' && c != 'z' && c != 'w' &&
                    c != 'r' && c != 'g' && c != 'b' && c != 'a')
                {
                    _tokenizer.Error(CPrintf.Format("Invalid swizzle '%s'", fieldName));
                    return false;
                }
                ++swizzleLength;
            }
        }
        else
        {
            // Check for a matrix element access (e.g. _m00 or _11).
            int index = 0;
            while (index < fieldName.Length && fieldName[index] == '_')
            {
                ++index;
                int baseIndex = 1;
                if (index < fieldName.Length && fieldName[index] == 'm')
                {
                    baseIndex = 0;
                    ++index;
                }

                char rowChar = index < fieldName.Length ? fieldName[index] : '\0';
                char columnChar = index + 1 < fieldName.Length ? fieldName[index + 1] : '\0';
                if (!Ascii.IsDigit(rowChar) || !Ascii.IsDigit(columnChar))
                {
                    return false;
                }

                int row = (rowChar - '0') - baseIndex;
                int column = (columnChar - '0') - baseIndex;
                if (row >= BaseTypes.Descriptions[(int)objectType.BaseType].Height ||
                    column >= BaseTypes.Descriptions[(int)objectType.BaseType].NumComponents)
                {
                    return false;
                }
                ++swizzleLength;
                index += 2;
            }

            if (index != fieldName.Length)
            {
                return false;
            }
        }

        if (swizzleLength > 4)
        {
            _tokenizer.Error(CPrintf.Format("Invalid swizzle '%s'", fieldName));
            return false;
        }

        if (swizzleLength == 0)
        {
            // C++ asserts here; fail cleanly instead.
            return false;
        }

        HLSLBaseType[] floatType = { HLSLBaseType.Float, HLSLBaseType.Float2, HLSLBaseType.Float3, HLSLBaseType.Float4 };
        HLSLBaseType[] intType = { HLSLBaseType.Int, HLSLBaseType.Int2, HLSLBaseType.Int3, HLSLBaseType.Int4 };
        HLSLBaseType[] uintType = { HLSLBaseType.Uint, HLSLBaseType.Uint2, HLSLBaseType.Uint3, HLSLBaseType.Uint4 };
        HLSLBaseType[] boolType = { HLSLBaseType.Bool, HLSLBaseType.Bool2, HLSLBaseType.Bool3, HLSLBaseType.Bool4 };

        switch (BaseTypes.Descriptions[(int)objectType.BaseType].NumericType)
        {
            case NumericType.Float:
                memberAccess.ExpressionType.BaseType = floatType[swizzleLength - 1];
                break;
            case NumericType.Int:
                memberAccess.ExpressionType.BaseType = intType[swizzleLength - 1];
                break;
            case NumericType.Uint:
                memberAccess.ExpressionType.BaseType = uintType[swizzleLength - 1];
                break;
            case NumericType.Bool:
                memberAccess.ExpressionType.BaseType = boolType[swizzleLength - 1];
                break;
            default:
                return false;
        }

        memberAccess.Swizzle = true;
        return true;
    }

    internal HLSLFunction MatchFunctionCall(HLSLFunctionCall functionCall, string name)
    {
        HLSLFunction matchedFunction = null;

        int numMatchedOverloads = 0;
        bool nameMatches = false;

        // User defined functions with the specified name.
        for (int i = 0; i < _functions.Count; ++i)
        {
            HLSLFunction function = _functions[i];
            if (String_.Equal(function.Name, name))
            {
                nameMatches = true;

                CompareFunctionsResult result = CompareFunctions(_tree, functionCall, function, matchedFunction);
                if (result == CompareFunctionsResult.Function1Better)
                {
                    matchedFunction = function;
                    numMatchedOverloads = 1;
                }
                else if (result == CompareFunctionsResult.FunctionsEqual)
                {
                    ++numMatchedOverloads;
                }
            }
        }

        // Intrinsic functions with the specified name.
        Intrinsic[] intrinsics = ParserTables.Intrinsics;
        for (int i = 0; i < intrinsics.Length; ++i)
        {
            HLSLFunction function = intrinsics[i].Function;
            if (String_.Equal(function.Name, name))
            {
                nameMatches = true;

                CompareFunctionsResult result = CompareFunctions(_tree, functionCall, function, matchedFunction);
                if (result == CompareFunctionsResult.Function1Better)
                {
                    matchedFunction = function;
                    numMatchedOverloads = 1;
                }
                else if (result == CompareFunctionsResult.FunctionsEqual)
                {
                    ++numMatchedOverloads;
                }
            }
        }

        if (matchedFunction != null && numMatchedOverloads > 1)
        {
            // Multiple overloads match.
            _tokenizer.Error(CPrintf.Format("'%s' %d overloads have similar conversions", name, numMatchedOverloads));
            return null;
        }
        if (matchedFunction == null)
        {
            if (nameMatches)
            {
                _tokenizer.Error(CPrintf.Format("'%s' no overloaded function matched all of the arguments", name));
            }
            else
            {
                _tokenizer.Error(CPrintf.Format("Undeclared identifier '%s'", name));
            }
        }

        return matchedFunction;
    }
}
