using System.Diagnostics;

namespace HlslParser;

/// <summary>Port of the expression half of GLSLGenerator.cpp.</summary>
public sealed partial class GLSLGenerator
{
    private void OutputExpressionList(HLSLExpression expression, HLSLArgument argument = null)
    {
        int numExpressions = 0;
        while (expression != null)
        {
            if (numExpressions > 0)
            {
                _writer.Write(", ");
            }

            HLSLType? expectedType = null;
            if (argument != null)
            {
                expectedType = argument.Type;
                argument = argument.NextArgument;
            }

            OutputExpression(expression, expectedType);
            expression = expression.NextExpression;
            ++numExpressions;
        }
    }

    private void OutputExpression(HLSLExpression expression, HLSLType? dstType = null)
    {
        bool cast = dstType.HasValue && !GetCanImplicitCast(expression.ExpressionType, dstType.Value);
        if (expression.NodeType == HLSLNodeType.CastingExpression)
        {
            // No need to include a cast if the expression is already doing it.
            cast = false;
        }

        if (cast)
        {
            OutputCast(dstType.Value);
            _writer.Write("(");
        }

        HLSLBuffer bufferAccess = (_options.Flags & (int)Flags.Flag_EmulateConstantBuffer) != 0 ? GetBufferAccessExpression(expression) : null;

        if (bufferAccess != null)
        {
            OutputBufferAccessExpression(bufferAccess, expression, expression.ExpressionType, 0);
        }
        else if (expression.NodeType == HLSLNodeType.IdentifierExpression)
        {
            HLSLIdentifierExpression identifierExpression = (HLSLIdentifierExpression)expression;
            OutputIdentifier(identifierExpression.Name);
        }
        else if (expression.NodeType == HLSLNodeType.ConstructorExpression)
        {
            HLSLConstructorExpression constructorExpression = (HLSLConstructorExpression)expression;

            bool matrixCtorNeeded = false;
            if (BaseTypes.IsMatrixType(constructorExpression.Type.BaseType))
            {
                MatrixCtor ctor = MatrixCtor.Build(constructorExpression.Type, constructorExpression.Argument);
                if (_matrixCtors.Contains(ctor))
                {
                    matrixCtorNeeded = true;
                }
            }

            if (matrixCtorNeeded)
            {
                // Matrix contructors needs to be adapted since GLSL access a matrix as m[c][r] while HLSL is m[r][c]
                MatrixCtor ctor = MatrixCtor.Build(constructorExpression.Type, constructorExpression.Argument);
                _writer.Write("%s(", _matrixCtorsId[ctor]);
                OutputExpressionList(constructorExpression.Argument);
                _writer.Write(")");
            }
            else
            {
                _writer.Write("%s(", GetTypeName(constructorExpression.Type));
                OutputExpressionList(constructorExpression.Argument);
                _writer.Write(")");
            }
        }
        else if (expression.NodeType == HLSLNodeType.CastingExpression)
        {
            HLSLCastingExpression castingExpression = (HLSLCastingExpression)expression;
            OutputCast(castingExpression.Type);
            _writer.Write("(");
            OutputExpression(castingExpression.Expression);
            _writer.Write(")");
        }
        else if (expression.NodeType == HLSLNodeType.LiteralExpression)
        {
            HLSLLiteralExpression literalExpression = (HLSLLiteralExpression)expression;
            switch (literalExpression.Type)
            {
                case HLSLBaseType.Float:
                    {
                        // Don't use printf directly so that we don't use the system locale.
                        _writer.Write("%s", String_.FormatFloat(literalExpression.FValue));
                    }
                    break;
                case HLSLBaseType.Int:
                case HLSLBaseType.Uint:
                    _writer.Write("%d", literalExpression.IValue);
                    break;
                case HLSLBaseType.Bool:
                    _writer.Write("%s", literalExpression.BValue ? "true" : "false");
                    break;
                default:
                    Debug.Assert(false);
                    break;
            }
        }
        else if (expression.NodeType == HLSLNodeType.UnaryExpression)
        {
            HLSLUnaryExpression unaryExpression = (HLSLUnaryExpression)expression;
            string op = "?";
            bool pre = true;
            HLSLType? dstType2 = null;
            switch (unaryExpression.UnaryOp)
            {
                case HLSLUnaryOp.Negative: op = "-"; break;
                case HLSLUnaryOp.Positive: op = "+"; break;
                case HLSLUnaryOp.Not: op = "!"; dstType2 = unaryExpression.ExpressionType; break;
                case HLSLUnaryOp.PreIncrement: op = "++"; break;
                case HLSLUnaryOp.PreDecrement: op = "--"; break;
                case HLSLUnaryOp.PostIncrement: op = "++"; pre = false; break;
                case HLSLUnaryOp.PostDecrement: op = "--"; pre = false; break;
                case HLSLUnaryOp.BitNot: op = "~"; break;
            }
            _writer.Write("(");
            if (pre)
            {
                _writer.Write("%s", op);
                OutputExpression(unaryExpression.Expression, dstType2);
            }
            else
            {
                OutputExpression(unaryExpression.Expression, dstType2);
                _writer.Write("%s", op);
            }
            _writer.Write(")");
        }
        else if (expression.NodeType == HLSLNodeType.BinaryExpression)
        {
            HLSLBinaryExpression binaryExpression = (HLSLBinaryExpression)expression;
            string op = "?";
            HLSLType? dstType1 = null;
            HLSLType? dstType2 = null;

            bool vectorExpression = HLSLType.IsVectorType(binaryExpression.Expression1.ExpressionType) ||
                                    HLSLType.IsVectorType(binaryExpression.Expression2.ExpressionType);
            if (binaryExpression.BinaryOp == HLSLBinaryOp.Mod &&
                BaseTypes.Descriptions[(int)binaryExpression.ExpressionType.BaseType].NumericType == NumericType.Float)
            {
                // HLSL permits floating-point remainder; GLSL's % only accepts integers.
                // mod agrees with HLSL % in its defined domain (operands of the same sign).
                _writer.Write("mod(");
                OutputExpression(binaryExpression.Expression1, binaryExpression.ExpressionType);
                _writer.Write(", ");
                OutputExpression(binaryExpression.Expression2, binaryExpression.ExpressionType);
                _writer.Write(")");
            }
            else if (vectorExpression && binaryExpression.BinaryOp.IsCompareOp())
            {
                switch (binaryExpression.BinaryOp)
                {
                    case HLSLBinaryOp.Less: _writer.Write("lessThan("); break;
                    case HLSLBinaryOp.Greater: _writer.Write("greaterThan("); break;
                    case HLSLBinaryOp.LessEqual: _writer.Write("lessThanEqual("); break;
                    case HLSLBinaryOp.GreaterEqual: _writer.Write("greaterThanEqual("); break;
                    case HLSLBinaryOp.Equal: _writer.Write("equal("); break;
                    case HLSLBinaryOp.NotEqual: _writer.Write("notEqual("); break;
                    default:
                        Debug.Assert(false); // is so, check isCompareOp
                        break;
                }

                if (HLSLType.IsVectorType(binaryExpression.Expression1.ExpressionType) &&
                    HLSLType.IsScalarType(binaryExpression.Expression2.ExpressionType))
                {
                    dstType2 = binaryExpression.Expression1.ExpressionType;
                }
                else if (HLSLType.IsScalarType(binaryExpression.Expression1.ExpressionType) &&
                         HLSLType.IsVectorType(binaryExpression.Expression2.ExpressionType))
                {
                    dstType1 = binaryExpression.Expression2.ExpressionType;
                }
                // TODO if both expressions are vector but with different dimension handle it here or in parser?

                OutputExpression(binaryExpression.Expression1, dstType1);
                _writer.Write(", ");
                OutputExpression(binaryExpression.Expression2, dstType2);
                _writer.Write(")");
            }
            else
            {
                switch (binaryExpression.BinaryOp)
                {
                    case HLSLBinaryOp.Add: op = " + "; dstType1 = dstType2 = binaryExpression.ExpressionType; break;
                    case HLSLBinaryOp.Sub: op = " - "; dstType1 = dstType2 = binaryExpression.ExpressionType; break;
                    case HLSLBinaryOp.Mul: op = " * "; dstType1 = dstType2 = binaryExpression.ExpressionType; break;
                    case HLSLBinaryOp.Div: op = " / "; dstType1 = dstType2 = binaryExpression.ExpressionType; break;
                    case HLSLBinaryOp.Mod: op = " % "; dstType1 = dstType2 = binaryExpression.ExpressionType; break;
                    case HLSLBinaryOp.Less: op = " < "; dstType1 = dstType2 = CommonScalarType(binaryExpression.Expression1.ExpressionType, binaryExpression.Expression2.ExpressionType); break;
                    case HLSLBinaryOp.Greater: op = " > "; dstType1 = dstType2 = CommonScalarType(binaryExpression.Expression1.ExpressionType, binaryExpression.Expression2.ExpressionType); break;
                    case HLSLBinaryOp.LessEqual: op = " <= "; dstType1 = dstType2 = CommonScalarType(binaryExpression.Expression1.ExpressionType, binaryExpression.Expression2.ExpressionType); break;
                    case HLSLBinaryOp.GreaterEqual: op = " >= "; dstType1 = dstType2 = CommonScalarType(binaryExpression.Expression1.ExpressionType, binaryExpression.Expression2.ExpressionType); break;
                    case HLSLBinaryOp.Equal: op = " == "; dstType1 = dstType2 = CommonScalarType(binaryExpression.Expression1.ExpressionType, binaryExpression.Expression2.ExpressionType); break;
                    case HLSLBinaryOp.NotEqual: op = " != "; dstType1 = dstType2 = CommonScalarType(binaryExpression.Expression1.ExpressionType, binaryExpression.Expression2.ExpressionType); break;
                    case HLSLBinaryOp.Assign: op = " = "; dstType2 = binaryExpression.ExpressionType; break;
                    case HLSLBinaryOp.AddAssign: op = " += "; dstType2 = binaryExpression.ExpressionType; break;
                    case HLSLBinaryOp.SubAssign: op = " -= "; dstType2 = binaryExpression.ExpressionType; break;
                    case HLSLBinaryOp.MulAssign: op = " *= "; dstType2 = binaryExpression.ExpressionType; break;
                    case HLSLBinaryOp.DivAssign: op = " /= "; dstType2 = binaryExpression.ExpressionType; break;
                    case HLSLBinaryOp.And: op = " && "; dstType1 = dstType2 = binaryExpression.ExpressionType; break;
                    case HLSLBinaryOp.Or: op = " || "; dstType1 = dstType2 = binaryExpression.ExpressionType; break;
                    case HLSLBinaryOp.BitAnd: op = " & "; dstType1 = dstType2 = CommonScalarType(binaryExpression.Expression1.ExpressionType, binaryExpression.Expression2.ExpressionType); break;
                    case HLSLBinaryOp.BitOr: op = " | "; dstType1 = dstType2 = CommonScalarType(binaryExpression.Expression1.ExpressionType, binaryExpression.Expression2.ExpressionType); break;
                    case HLSLBinaryOp.BitXor: op = " ^ "; dstType1 = dstType2 = CommonScalarType(binaryExpression.Expression1.ExpressionType, binaryExpression.Expression2.ExpressionType); break;
                    default:
                        Debug.Assert(false);
                        break;
                }
                if ((_version == Version.Version_110 || _version == Version.Version_120 || _version == Version.Version_100_ES) &&
                    binaryExpression.BinaryOp == HLSLBinaryOp.Mod)
                {
                    // Legacy GLSL has no integer %, so use a component-wise float fallback.
                    HLSLType floatType = new(
                        (HLSLBaseType)((int)HLSLBaseType.Float +
                            BaseTypes.Descriptions[(int)binaryExpression.ExpressionType.BaseType].NumComponents - 1));
                    _writer.Write("%s(mod(", GetTypeName(binaryExpression.ExpressionType));
                    OutputExpression(binaryExpression.Expression1, floatType);
                    _writer.Write(", ");
                    OutputExpression(binaryExpression.Expression2, floatType);
                    _writer.Write("))");
                }
                else
                {
                    bool handled = false;
                    if ((_options.Flags & (int)Flags.Flag_AlternateNanPropagation) != 0)
                    {
                        if (binaryExpression.BinaryOp == HLSLBinaryOp.Mul)
                        {
                            // Punt to function that does not follow IEEE 754 NaN propagation rules
                            _writer.Write("%s(", _altMultFunction);
                            OutputExpression(binaryExpression.Expression1, dstType1);
                            _writer.Write(",", op);
                            OutputExpression(binaryExpression.Expression2, dstType2);
                            _writer.Write(")");
                            handled = true;
                        }
                    }

                    if (!handled)
                    {
                        _writer.Write("(");
                        OutputExpression(binaryExpression.Expression1, dstType1);
                        _writer.Write("%s", op);
                        OutputExpression(binaryExpression.Expression2, dstType2);
                        _writer.Write(")");
                    }
                }
            }
        }
        else if (expression.NodeType == HLSLNodeType.ConditionalExpression)
        {
            HLSLConditionalExpression conditionalExpression = (HLSLConditionalExpression)expression;
            if (HLSLType.IsVectorType(conditionalExpression.Condition.ExpressionType))
            {
                _writer.Write("%s", _bvecTernary);
                _writer.Write("( ");
                OutputExpression(conditionalExpression.Condition);
                _writer.Write(", ");
                OutputExpression(conditionalExpression.TrueExpression, conditionalExpression.ExpressionType);
                _writer.Write(", ");
                OutputExpression(conditionalExpression.FalseExpression, conditionalExpression.ExpressionType);
                _writer.Write(" )");
            }
            else
            {
                _writer.Write("((");
                OutputExpression(conditionalExpression.Condition, kBoolType);
                _writer.Write(")?(");
                OutputExpression(conditionalExpression.TrueExpression, dstType);
                _writer.Write("):(");
                OutputExpression(conditionalExpression.FalseExpression, dstType);
                _writer.Write("))");
            }
        }
        else if (expression.NodeType == HLSLNodeType.MemberAccess)
        {
            HLSLMemberAccess memberAccess = (HLSLMemberAccess)expression;

            if (memberAccess.Object.ExpressionType.BaseType == HLSLBaseType.Float ||
                memberAccess.Object.ExpressionType.BaseType == HLSLBaseType.Int ||
                memberAccess.Object.ExpressionType.BaseType == HLSLBaseType.Uint)
            {
                // Handle swizzling on scalar values.
                int swizzleLength = memberAccess.Field.Length;
                if (swizzleLength == 2)
                {
                    _writer.Write("%s", _scalarSwizzle2Function);
                }
                else if (swizzleLength == 3)
                {
                    _writer.Write("%s", _scalarSwizzle3Function);
                }
                else if (swizzleLength == 4)
                {
                    _writer.Write("%s", _scalarSwizzle4Function);
                }
                _writer.Write("(");
                OutputExpression(memberAccess.Object);
                _writer.Write(")");
            }
            else
            {
                _writer.Write("(");
                OutputExpression(memberAccess.Object);
                _writer.Write(")");

                if (BaseTypes.IsMatrixType(memberAccess.Object.ExpressionType.BaseType))
                {
                    // Handle HLSL matrix "swizzling".
                    // TODO: Properly handle multiple element selection such as _m00_m12
                    string n = memberAccess.Field;
                    int index = 0;
                    while (index < n.Length && n[index] != 0)
                    {
                        if (n[index] != '_')
                        {
                            Debug.Assert(false);
                            break;
                        }
                        ++index;
                        char baseChar = '1';
                        if (index < n.Length && n[index] == 'm')
                        {
                            baseChar = '0';
                            ++index;
                        }
                        if (index + 1 < n.Length && Ascii.IsDigit(n[index]) && Ascii.IsDigit(n[index + 1]))
                        {
                            _writer.Write("[%d][%d]", n[index + 1] - baseChar, n[index] - baseChar);
                            index += 2;
                        }
                        else
                        {
                            Debug.Assert(false);
                            break;
                        }
                    }
                }
                else
                {
                    _writer.Write(".%s", memberAccess.Field);
                }
            }
        }
        else if (expression.NodeType == HLSLNodeType.ArrayAccess)
        {
            HLSLArrayAccess arrayAccess = (HLSLArrayAccess)expression;

            if (!arrayAccess.Array.ExpressionType.Array &&
                BaseTypes.IsMatrixType(arrayAccess.Array.ExpressionType.BaseType))
            {
                // GLSL access a matrix as m[c][r] while HLSL is m[r][c], so use our
                // special row access function to convert.
                _writer.Write("%s(", _matrixRowFunction);
                OutputExpression(arrayAccess.Array);
                _writer.Write(",");
                OutputExpression(arrayAccess.Index);
                _writer.Write(")");
            }
            else
            {
                // Array subscript operator in GLSL requires an explicit int parameter
                HLSLType intType = new(HLSLBaseType.Int);
                OutputExpression(arrayAccess.Array);
                _writer.Write("[");
                OutputExpression(arrayAccess.Index, intType);
                _writer.Write("]");
            }
        }
        else if (expression.NodeType == HLSLNodeType.FunctionCall)
        {
            HLSLFunctionCall functionCall = (HLSLFunctionCall)expression;

            // Handle intrinsic funtions that are different between HLSL and GLSL.
            bool handled = false;
            string functionName = functionCall.Function.Name;

            if (String_.Equal(functionName, "mul"))
            {
                HLSLExpression[] argument = new HLSLExpression[2];
                if (GetFunctionArguments(functionCall, argument, 2) != 2)
                {
                    Error("mul expects 2 arguments");
                    return;
                }

                HLSLType type0 = functionCall.Function.Argument.Type;
                HLSLType type1 = functionCall.Function.Argument.NextArgument.Type;

                if (BaseTypes.IsVectorType(type0.BaseType) && BaseTypes.IsVectorType(type1.BaseType))
                {
                    _writer.Write("dot((");
                    OutputExpression(argument[0], type0);
                    _writer.Write("),(");
                    OutputExpression(argument[1], type1);
                    _writer.Write("))");
                }
                else
                {
                    _writer.Write("((");
                    OutputExpression(argument[0], type0);
                    _writer.Write(")*(");
                    OutputExpression(argument[1], type1);
                    _writer.Write("))");
                }

                handled = true;
            }
            else if (String_.Equal(functionName, "saturate"))
            {
                HLSLExpression[] argument = new HLSLExpression[1];
                if (GetFunctionArguments(functionCall, argument, 1) != 1)
                {
                    Error("saturate expects 1 argument");
                    return;
                }
                _writer.Write("clamp(");
                OutputExpression(argument[0]);
                HLSLBaseType baseType = argument[0].ExpressionType.BaseType;
                switch (baseType)
                {
                    case HLSLBaseType.Float:
                    case HLSLBaseType.Float2:
                    case HLSLBaseType.Float3:
                    case HLSLBaseType.Float4:
                        _writer.Write(", 0.0, 1.0)");
                        break;

                    case HLSLBaseType.Int:
                    case HLSLBaseType.Int2:
                    case HLSLBaseType.Int3:
                    case HLSLBaseType.Int4:
                    case HLSLBaseType.Uint:
                    case HLSLBaseType.Uint2:
                    case HLSLBaseType.Uint3:
                    case HLSLBaseType.Uint4:
                        _writer.Write(", 0, 1)");
                        break;

                    default:
                        Error("saturate unhandled type: %s", GetTypeName(argument[0].ExpressionType));
                        break;
                }
                handled = true;
            }
            else if (String_.Equal(functionName, "rsqrt"))
            {
                /* The documentation says that these functions return NaN for negative
                 * arguments. However, testing with DX9 shader model 3 shows that they
                 * most definitely do take the absolute value of the argument and do
                 * NOT return NaN.
                 * See https://github.com/projectM-visualizer/projectm/issues/724
                 */
                _writer.Write("inversesqrt(abs(");
                OutputExpressionList(functionCall.Argument, functionCall.Function.Argument);
                _writer.Write("))");
                handled = true;
            }
            else if (String_.Equal(functionName, "sqrt") ||
                String_.Equal(functionName, "log") ||
                String_.Equal(functionName, "log2"))
            {
                /* See rsqrt above */
                _writer.Write("%s(abs(", functionName);
                OutputExpressionList(functionCall.Argument, functionCall.Function.Argument);
                _writer.Write("))");
                handled = true;
            }
            else if (String_.Equal(functionName, "log10"))
            {
                /* See rsqrt above regarding abs(). */
                _writer.Write("(log(abs(");
                OutputExpressionList(functionCall.Argument, functionCall.Function.Argument);
                _writer.Write("))/log(10.0))");
                handled = true;
            }
            else if (String_.Equal(functionName, "pow"))
            {
                HLSLExpression[] argument = new HLSLExpression[2];
                if (GetFunctionArguments(functionCall, argument, 2) != 2)
                {
                    Error("%s expects 2 arguments", functionName);
                    return;
                }

                /* See rsqrt above regarding abs(). Note that this behaves
                 * as expected on some drivers but not others, so we add
                 * the abs() call for compatibility across drivers.
                 *
                 * There's one special case though: if the exponent is a literal "1" (or "1.0"),
                 * don't use pow() at all, just return the base (arg 1) unchanged, even if negative.
                 * This is probably due to an HLSL compiler optimization which does the same thing.
                 * When not optimized, pow(x, 1) with a negative value of x would return NaN instead.
                 */
                if (argument[1].NodeType == HLSLNodeType.LiteralExpression)
                {
                    HLSLLiteralExpression literalExpression = (HLSLLiteralExpression)argument[1];
                    float value = 0.0f;
                    bool found = false;
                    switch (literalExpression.Type)
                    {
                        case HLSLBaseType.Float:
                            value = literalExpression.FValue;
                            found = true;
                            break;
                        case HLSLBaseType.Int:
                        case HLSLBaseType.Uint:
                            value = literalExpression.IValue;
                            found = true;
                            break;
                        case HLSLBaseType.Bool:
                            value = literalExpression.BValue ? 1.0f : 0.0f;
                            found = true;
                            break;
                        default:
                            break;
                    }

                    // Replace the function call with just arg 1.
                    if (found && value == 1.0f)
                    {
                        _writer.Write("(");
                        OutputExpression(argument[0], functionCall.Function.ReturnType);
                        _writer.Write(")");
                        handled = true;
                    }
                }

                // Other cases, including variable exponent arguments, will still call pow().
                if (!handled)
                {
                    _writer.Write("pow(abs(");
                    OutputExpression(argument[0], functionCall.Function.ReturnType);
                    _writer.Write("),");
                    OutputExpression(argument[1], functionCall.Function.ReturnType);
                    _writer.Write(")");
                    handled = true;
                }
            }
            else if (String_.Equal(functionName, "ldexp"))
            {
                /* HLSL has the second argument as float, while GLSL only supports ints, so we simulate the HLSL behaviour
                 * by using the equivalent "x * exp2(exp)" expression. */
                HLSLExpression[] argument = new HLSLExpression[2];
                if (GetFunctionArguments(functionCall, argument, 2) != 2)
                {
                    Error("%s expects 2 arguments", functionName);
                    return;
                }
                _writer.Write("(");
                OutputExpression(argument[0], functionCall.Function.ReturnType);
                _writer.Write("*exp2(");
                OutputExpression(argument[1], functionCall.Function.ReturnType);
                _writer.Write("))");
                handled = true;
            }

            if (!handled)
            {
                OutputIdentifier(functionName);
                _writer.Write("(");
                OutputExpressionList(functionCall.Argument, functionCall.Function.Argument);
                _writer.Write(")");
            }
        }
        else
        {
            _writer.Write("<unknown expression>");
        }

        if (cast)
        {
            if (BaseTypes.IsVectorType(dstType.Value.BaseType) || BaseTypes.IsMatrixType(dstType.Value.BaseType))
            {
                CompleteConstructorArguments(expression, dstType.Value.BaseType);
            }

            _writer.Write(")");
        }
    }

    private void OutputIdentifier(string name)
    {
        // Remap intrinstic functions.
        if (String_.Equal(name, "tex2D"))
        {
            name = _versionLegacy ? "texture2D" : "texture";
        }
        else if (String_.Equal(name, "tex2Dproj"))
        {
            name = _versionLegacy ? "texture2DProj" : "textureProj";
        }
        else if (String_.Equal(name, "texCUBE"))
        {
            name = _versionLegacy ? "textureCube" : "texture";
        }
        else if (String_.Equal(name, "tex3D"))
        {
            name = _versionLegacy ? "texture3D" : "texture";
        }
        else if (String_.Equal(name, "clip"))
        {
            name = _clipFunction;
        }
        else if (String_.Equal(name, "tex2Dlod"))
        {
            name = _tex2DlodFunction;
        }
        else if (String_.Equal(name, "tex2Dbias"))
        {
            name = _tex2DbiasFunction;
        }
        else if (String_.Equal(name, "tex2Dgrad"))
        {
            name = _tex2DgradFunction;
        }
        else if (String_.Equal(name, "tex2DArray"))
        {
            name = "texture";
        }
        else if (String_.Equal(name, "texCUBEbias"))
        {
            name = _texCUBEbiasFunction;
        }
        else if (String_.Equal(name, "texCUBElod"))
        {
            name = _texCUBElodFunction;
        }
        else if (String_.Equal(name, "tex3Dlod"))
        {
            name = _tex3DlodFunction;
        }
        else if (String_.Equal(name, "tex3Dbias"))
        {
            name = _tex3DbiasFunction;
        }
        else if (String_.Equal(name, "atan2"))
        {
            name = "atan";
        }
        else if (String_.Equal(name, "sincos"))
        {
            name = _sinCosFunction;
        }
        else if (String_.Equal(name, "fmod"))
        {
            // mod is not the same as fmod if the parameter is negative!
            // The equivalent of fmod(x, y) is x - y * floor(x/y)
            // We use the mod version for performance.
            name = "mod";
        }
        else if (String_.Equal(name, "lerp"))
        {
            name = "mix";
        }
        else if (String_.Equal(name, "frac"))
        {
            name = "fract";
        }
        else if (String_.Equal(name, "ddx"))
        {
            name = "dFdx";
        }
        else if (String_.Equal(name, "ddy"))
        {
            name = "dFdy";
        }
        else if (String_.Equal(name, "modf"))
        {
            name = _modfFunction;
        }
        else if (String_.Equal(name, "acos"))
        {
            name = _acosFunction;
        }
        else if (String_.Equal(name, "asin"))
        {
            name = _asinFunction;
        }
        else
        {
            // The identifier could be a GLSL reserved word (if it's not also a HLSL reserved word).
            name = GetSafeIdentifierName(name);
        }
        _writer.Write("%s", name);
    }

    private void OutputCast(in HLSLType type)
    {
        if ((_version == Version.Version_110 || _version == Version.Version_120) && type.BaseType == HLSLBaseType.Float3x3)
        {
            _writer.Write("%s", _matrixCtorFunction);
        }
        else
        {
            OutputDeclaration(type, "");
        }
    }
}
