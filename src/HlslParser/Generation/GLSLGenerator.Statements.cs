using System.Diagnostics;

namespace HlslParser;

/// <summary>Port of the statement/declaration/attribute half of GLSLGenerator.cpp.</summary>
public sealed partial class GLSLGenerator
{
    private void OutputArguments(HLSLArgument argument)
    {
        int numArgs = 0;
        while (argument != null)
        {
            if (numArgs > 0)
            {
                _writer.Write(", ");
            }

            switch (argument.Modifier)
            {
                case HLSLArgumentModifier.In:
                    _writer.Write("in ");
                    break;
                case HLSLArgumentModifier.Out:
                    _writer.Write("out ");
                    break;
                case HLSLArgumentModifier.Inout:
                    _writer.Write("inout ");
                    break;
                default:
                    break;
            }

            OutputDeclaration(argument.Type, argument.Name);
            argument = argument.NextArgument;
            ++numArgs;
        }
    }

    private void OutputStatements(int indent, HLSLStatement statement, HLSLType? returnType = null)
    {
        while (statement != null)
        {
            if (statement.Hidden)
            {
                statement = statement.NextStatement;
                continue;
            }

            if (statement.NodeType == HLSLNodeType.Declaration)
            {
                HLSLDeclaration declaration = (HLSLDeclaration)statement;

                // GLSL doesn't seem have texture uniforms, so just ignore them.
                if (declaration.Type.BaseType != HLSLBaseType.Texture)
                {
                    bool skipAssignment = true;
                    if (indent != 0)
                    {
                        skipAssignment = false;
                    }

                    _writer.BeginLine(indent, declaration.FileName, declaration.Line);
                    if (indent == 0 && (declaration.Type.Flags & (int)HLSLTypeFlags.Uniform) != 0)
                    {
                        // At the top level, we need the "uniform" keyword.
                        _writer.Write("uniform ");
                        skipAssignment = false;
                    }
                    OutputDeclaration(declaration, skipAssignment);
                    _writer.EndLine(";");
                }
            }
            else if (statement.NodeType == HLSLNodeType.Struct)
            {
                HLSLStruct structure = (HLSLStruct)statement;
                _writer.WriteLine(indent, "struct %s {", structure.Name);
                HLSLStructField field = structure.Field;
                while (field != null)
                {
                    _writer.BeginLine(indent + 1, field.FileName, field.Line);
                    OutputDeclaration(field.Type, field.Name);
                    _writer.Write(";");
                    _writer.EndLine();
                    field = field.NextField;
                }
                _writer.WriteLine(indent, "};");
            }
            else if (statement.NodeType == HLSLNodeType.Buffer)
            {
                HLSLBuffer buffer = (HLSLBuffer)statement;
                OutputBuffer(indent, buffer);
            }
            else if (statement.NodeType == HLSLNodeType.Function)
            {
                HLSLFunction function = (HLSLFunction)statement;

                // Use an alternate name for the function which is supposed to be entry point
                // so that we can supply our own function which will be the actual entry point.
                string functionName = GetSafeIdentifierName(function.Name);
                string returnTypeName = GetTypeName(function.ReturnType);

                _writer.BeginLine(indent, function.FileName, function.Line);
                _writer.Write("%s %s(", returnTypeName, functionName);

                OutputArguments(function.Argument);

                if (function.Forward != null)
                {
                    _writer.WriteLine(indent, ");");
                }
                else
                {
                    _writer.Write(") {");
                    _writer.EndLine();

                    OutputStatements(indent + 1, function.Statement, function.ReturnType);
                    _writer.WriteLine(indent, "}");
                }
            }
            else if (statement.NodeType == HLSLNodeType.ExpressionStatement)
            {
                HLSLExpressionStatement expressionStatement = (HLSLExpressionStatement)statement;
                _writer.BeginLine(indent, statement.FileName, statement.Line);
                OutputExpression(expressionStatement.Expression);
                _writer.EndLine(";");
            }
            else if (statement.NodeType == HLSLNodeType.ReturnStatement)
            {
                HLSLReturnStatement returnStatement = (HLSLReturnStatement)statement;
                if (returnStatement.Expression != null)
                {
                    _writer.BeginLine(indent, returnStatement.FileName, returnStatement.Line);
                    _writer.Write("return ");
                    OutputExpression(returnStatement.Expression, returnType);
                    _writer.EndLine(";");
                }
                else
                {
                    _writer.WriteLineTagged(indent, returnStatement.FileName, returnStatement.Line, "return;");
                }
            }
            else if (statement.NodeType == HLSLNodeType.DiscardStatement)
            {
                HLSLDiscardStatement discardStatement = (HLSLDiscardStatement)statement;
                if (_target == Target.Target_FragmentShader)
                {
                    _writer.WriteLineTagged(indent, discardStatement.FileName, discardStatement.Line, "discard;");
                }
            }
            else if (statement.NodeType == HLSLNodeType.BreakStatement)
            {
                HLSLBreakStatement breakStatement = (HLSLBreakStatement)statement;
                _writer.WriteLineTagged(indent, breakStatement.FileName, breakStatement.Line, "break;");
            }
            else if (statement.NodeType == HLSLNodeType.ContinueStatement)
            {
                HLSLContinueStatement continueStatement = (HLSLContinueStatement)statement;
                _writer.WriteLineTagged(indent, continueStatement.FileName, continueStatement.Line, "continue;");
            }
            else if (statement.NodeType == HLSLNodeType.IfStatement)
            {
                HLSLIfStatement ifStatement = (HLSLIfStatement)statement;
                _writer.BeginLine(indent, ifStatement.FileName, ifStatement.Line);
                _writer.Write("if (");
                OutputExpression(ifStatement.Condition, kBoolType);
                _writer.Write(") {");
                _writer.EndLine();
                OutputStatements(indent + 1, ifStatement.Statement, returnType);
                _writer.WriteLine(indent, "}");
                if (ifStatement.ElseStatement != null)
                {
                    _writer.WriteLine(indent, "else {");
                    OutputStatements(indent + 1, ifStatement.ElseStatement, returnType);
                    _writer.WriteLine(indent, "}");
                }
            }
            else if (statement.NodeType == HLSLNodeType.ForStatement)
            {
                HLSLForStatement forStatement = (HLSLForStatement)statement;
                _writer.BeginLine(indent, forStatement.FileName, forStatement.Line);
                _writer.Write("for (");
                if (forStatement.Initialization != null)
                {
                    OutputDeclaration(forStatement.Initialization, false);
                }
                else
                {
                    OutputExpression(forStatement.InitializationWithoutType);
                }
                _writer.Write("; ");
                OutputExpression(forStatement.Condition, kBoolType);
                _writer.Write("; ");
                OutputExpression(forStatement.Increment);
                _writer.Write(") {");
                _writer.EndLine();
                OutputStatements(indent + 1, forStatement.Statement, returnType);
                _writer.WriteLine(indent, "}");
            }
            else if (statement.NodeType == HLSLNodeType.WhileStatement)
            {
                HLSLWhileStatement whileStatement = (HLSLWhileStatement)statement;
                _writer.BeginLine(indent, whileStatement.FileName, whileStatement.Line);
                _writer.Write("while (");
                OutputExpression(whileStatement.Condition, kBoolType);
                _writer.Write(") {");
                _writer.EndLine();
                OutputStatements(indent + 1, whileStatement.Statement, returnType);
                _writer.WriteLine(indent, "}");
            }
            else if (statement.NodeType == HLSLNodeType.BlockStatement)
            {
                HLSLBlockStatement blockStatement = (HLSLBlockStatement)statement;
                _writer.WriteLine(indent, "{");
                OutputStatements(indent + 1, blockStatement.Statement, returnType);
                _writer.WriteLine(indent, "}");
            }
            else
            {
                // Unhanded statement type.
                Debug.Assert(false);
            }

            statement = statement.NextStatement;
        }
    }

    private string GetAttribQualifier(AttributeModifier modifier)
    {
        if (_versionLegacy)
        {
            if (_target == Target.Target_VertexShader)
            {
                return (modifier == AttributeModifier.AttributeModifier_In) ? "attribute" : "varying";
            }
            return (modifier == AttributeModifier.AttributeModifier_In) ? "varying" : "out";
        }

        return (modifier == AttributeModifier.AttributeModifier_In) ? "in" : "out";
    }

    private void OutputAttribute(in HLSLType type, string semantic, AttributeModifier modifier)
    {
        string qualifier = GetAttribQualifier(modifier);
        string prefix = (modifier == AttributeModifier.AttributeModifier_In) ? _inAttribPrefix : _outAttribPrefix;

        HLSLRoot root = _tree.GetRoot();
        if (type.BaseType == HLSLBaseType.UserDefined)
        {
            // If the argument is a struct with semantics specified, we need to
            // grab them.
            HLSLStruct structDeclaration = _tree.FindGlobalStruct(type.TypeName);
            Debug.Assert(structDeclaration != null);
            HLSLStructField field = structDeclaration.Field;
            while (field != null)
            {
                if (field.Semantic != null && GetBuiltInSemantic(field.Semantic, modifier) == null)
                {
                    _writer.Write("%s ", qualifier);
                    string attribName = CPrintf.Format("%s%s", prefix, field.Semantic);
                    OutputDeclaration(field.Type, attribName);
                    _writer.EndLine(";");
                }
                field = field.NextField;
            }
        }
        else if (semantic != null && GetBuiltInSemantic(semantic, modifier) == null)
        {
            _writer.Write("%s ", qualifier);
            string attribName = CPrintf.Format("%s%s", prefix, semantic);
            OutputDeclaration(type, attribName);
            _writer.EndLine(";");
        }
    }

    private void OutputAttributes(HLSLFunction entryFunction)
    {
        // Write out the input/output attributes to the shader.
        HLSLArgument argument = entryFunction.Argument;
        while (argument != null)
        {
            if (argument.Modifier == HLSLArgumentModifier.None || argument.Modifier == HLSLArgumentModifier.In)
            {
                OutputAttribute(argument.Type, argument.Semantic, AttributeModifier.AttributeModifier_In);
            }
            if (argument.Modifier == HLSLArgumentModifier.Out)
            {
                OutputAttribute(argument.Type, argument.Semantic, AttributeModifier.AttributeModifier_Out);
            }

            argument = argument.NextArgument;
        }

        // Write out the output attributes from the shader.
        OutputAttribute(entryFunction.ReturnType, entryFunction.Semantic, AttributeModifier.AttributeModifier_Out);
    }

    private void OutputSetOutAttribute(string semantic, string resultName)
    {
        string builtInSemantic = GetBuiltInSemantic(semantic, AttributeModifier.AttributeModifier_Out, out int outputIndex);
        if (builtInSemantic != null)
        {
            if (String_.Equal(builtInSemantic, "gl_Position"))
            {
                if ((_options.Flags & (int)Flags.Flag_FlipPositionOutput) != 0)
                {
                    // Mirror the y-coordinate when we're outputing from the
                    // vertex shader so that we match the D3D texture
                    // coordinate origin convention in render-to-texture
                    // operations.
                    // We also need to convert the normalized device
                    // coordinates from the D3D convention of 0 to 1 to the
                    // OpenGL convention of -1 to 1.
                    _writer.WriteLine(1, "vec4 temp = %s;", resultName);
                    _writer.WriteLine(1, "%s = temp * vec4(1,-1,2,1) - vec4(0,0,temp.w,0);", builtInSemantic);
                }
                else
                {
                    _writer.WriteLine(1, "%s = %s;", builtInSemantic, resultName);
                }

                _outputPosition = true;
            }
            else if (String_.Equal(builtInSemantic, "gl_FragDepth"))
            {
                // If the value goes outside of the 0 to 1 range, the
                // fragment will be rejected unlike in D3D, so clamp it.
                _writer.WriteLine(1, "%s = clamp(float(%s), 0.0, 1.0);", builtInSemantic, resultName);
            }
            else if (outputIndex >= 0)
            {
                _writer.WriteLine(1, "%s[%d] = %s;", builtInSemantic, outputIndex, resultName);
            }
            else
            {
                _writer.WriteLine(1, "%s = %s;", builtInSemantic, resultName);
            }
        }
        else if (_target == Target.Target_FragmentShader)
        {
            Error("Output attribute %s does not map to any built-ins", semantic);
        }
        else
        {
            _writer.WriteLine(1, "%s%s = %s;", _outAttribPrefix, semantic, resultName);
        }
    }

    private void OutputEntryCaller(HLSLFunction entryFunction)
    {
        HLSLRoot root = _tree.GetRoot();

        _writer.WriteLine(0, "void main() {");

        // Create local variables for each of the parameters we'll need to pass
        // into the entry point function.
        HLSLArgument argument = entryFunction.Argument;
        while (argument != null)
        {
            _writer.BeginLine(1);
            OutputDeclaration(argument.Type, argument.Name);
            _writer.EndLine(";");

            if (argument.Modifier != HLSLArgumentModifier.Out)
            {
                // Set the value for the local variable.
                if (argument.Type.BaseType == HLSLBaseType.UserDefined)
                {
                    HLSLStruct structDeclaration = _tree.FindGlobalStruct(argument.Type.TypeName);
                    Debug.Assert(structDeclaration != null);
                    HLSLStructField field = structDeclaration.Field;
                    while (field != null)
                    {
                        if (field.Semantic != null)
                        {
                            string builtInSemantic = GetBuiltInSemantic(field.Semantic, AttributeModifier.AttributeModifier_In);
                            if (builtInSemantic != null)
                            {
                                _writer.WriteLine(1, "%s.%s = %s;", GetSafeIdentifierName(argument.Name), GetSafeIdentifierName(field.Name), builtInSemantic);
                            }
                            else
                            {
                                _writer.WriteLine(1, "%s.%s = %s%s;", GetSafeIdentifierName(argument.Name), GetSafeIdentifierName(field.Name), _inAttribPrefix, field.Semantic);
                            }
                        }
                        field = field.NextField;
                    }
                }
                else if (argument.Semantic != null)
                {
                    string builtInSemantic = GetBuiltInSemantic(argument.Semantic, AttributeModifier.AttributeModifier_In);
                    if (builtInSemantic != null)
                    {
                        _writer.WriteLine(1, "%s = %s;", GetSafeIdentifierName(argument.Name), builtInSemantic);
                    }
                    else
                    {
                        _writer.WriteLine(1, "%s = %s%s;", GetSafeIdentifierName(argument.Name), _inAttribPrefix, argument.Semantic);
                    }
                }
            }

            argument = argument.NextArgument;
        }

        // Initialize global variables
        foreach (HLSLDeclaration declaration in _globalVarsAssignments)
        {
            _writer.BeginLine(1, declaration.FileName, declaration.Line);
            OutputDeclarationBody(declaration.Type, GetSafeIdentifierName(declaration.Name));

            OutputDeclarationAssignment(declaration);
            _writer.EndLine(";");
        }

        const string resultName = "result";

        // Call the original entry function.
        _writer.BeginLine(1);
        if (entryFunction.ReturnType.BaseType != HLSLBaseType.Void)
        {
            _writer.Write("%s %s = ", GetTypeName(entryFunction.ReturnType), resultName);
        }
        _writer.Write("%s(", _entryName);

        int numArgs = 0;
        argument = entryFunction.Argument;
        while (argument != null)
        {
            if (numArgs > 0)
            {
                _writer.Write(", ");
            }

            _writer.Write("%s", GetSafeIdentifierName(argument.Name));

            argument = argument.NextArgument;
            ++numArgs;
        }
        _writer.EndLine(");");

        // Copy values from the result into the out attributes as necessary.
        argument = entryFunction.Argument;
        while (argument != null)
        {
            if (argument.Modifier == HLSLArgumentModifier.Out && argument.Semantic != null)
            {
                OutputSetOutAttribute(argument.Semantic, GetSafeIdentifierName(argument.Name));
            }

            argument = argument.NextArgument;
        }

        if (entryFunction.ReturnType.BaseType == HLSLBaseType.UserDefined)
        {
            HLSLStruct structDeclaration = _tree.FindGlobalStruct(entryFunction.ReturnType.TypeName);
            Debug.Assert(structDeclaration != null);
            HLSLStructField field = structDeclaration.Field;
            while (field != null)
            {
                string fieldResultName = CPrintf.Format("%s.%s", resultName, field.Name);
                OutputSetOutAttribute(field.Semantic, fieldResultName);
                field = field.NextField;
            }
        }
        else if (entryFunction.Semantic != null)
        {
            OutputSetOutAttribute(entryFunction.Semantic, resultName);
        }

        _writer.WriteLine(0, "}");
    }

    private void OutputDeclaration(HLSLDeclaration declaration, bool skipAssignment)
    {
        OutputDeclarationType(declaration.Type);

        HLSLDeclaration lastDecl = null;
        while (declaration != null)
        {
            if (lastDecl != null)
            {
                _writer.Write(", ");
            }

            OutputDeclarationBody(declaration.Type, GetSafeIdentifierName(declaration.Name));

            if (declaration.Assignment != null)
            {
                if (!skipAssignment)
                {
                    OutputDeclarationAssignment(declaration);
                }
                else
                {
                    _globalVarsAssignments.Add(declaration);
                }
            }

            lastDecl = declaration;
            declaration = declaration.NextDeclaration;
        }
    }

    private void OutputDeclarationAssignment(HLSLDeclaration declaration)
    {
        _writer.Write(" = ");
        if (declaration.Type.Array)
        {
            _writer.Write("%s[]( ", GetTypeName(declaration.Type));
            OutputExpressionList(declaration.Assignment);
            _writer.Write(" )");
        }
        else
        {
            bool matrixCtorNeeded = false;
            if (BaseTypes.IsMatrixType(declaration.Type.BaseType))
            {
                MatrixCtor ctor = MatrixCtor.Build(declaration.Type, declaration.Assignment);
                if (_matrixCtors.Contains(ctor))
                {
                    matrixCtorNeeded = true;
                }
            }

            if (matrixCtorNeeded)
            {
                // Matrix contructors needs to be adapted since GLSL access a matrix as m[c][r] while HLSL is m[r][c]
                MatrixCtor ctor = MatrixCtor.Build(declaration.Type, declaration.Assignment);
                _writer.Write("%s(", _matrixCtorsId[ctor]);
                OutputExpressionList(declaration.Assignment);
                _writer.Write(")");
            }
            else
            {
                _writer.Write("%s( ", GetTypeName(declaration.Type));
                OutputExpressionList(declaration.Assignment);
                _writer.Write(" )");
            }
        }
    }

    private void OutputDeclaration(in HLSLType type, string name)
    {
        OutputDeclarationType(type);
        OutputDeclarationBody(type, name);
    }

    private void OutputDeclarationType(in HLSLType type)
    {
        _writer.Write("%s ", GetTypeName(type));
    }

    private void OutputDeclarationBody(in HLSLType type, string name)
    {
        if (!type.Array)
        {
            _writer.Write("%s", GetSafeIdentifierName(name));
        }
        else
        {
            _writer.Write("%s[", GetSafeIdentifierName(name));
            if (type.ArraySize != null)
            {
                OutputExpression(type.ArraySize);
            }
            _writer.Write("]");
        }
    }
}
