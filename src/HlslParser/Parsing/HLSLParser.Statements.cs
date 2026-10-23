namespace HlslParser;

public partial class HLSLParser
{
    /// <summary>Entry point: tokenizes <paramref name="buffer"/> and parses all top level statements.</summary>
    public bool Parse(string fileName, string buffer)
    {
        HLSLRoot root = _tree.GetRoot();
        HLSLStatement lastStatement = null;

        _tokenizer = new HLSLTokenizer(fileName, buffer, _diagnostics);

        while (!Accept((int)HLSLToken.EndOfStream))
        {
            if (!ParseTopLevel(out HLSLStatement statement))
            {
                return false;
            }

            if (statement != null)
            {
                if (lastStatement == null)
                {
                    root.Statement = statement;
                }
                else
                {
                    lastStatement.NextStatement = statement;
                }

                lastStatement = statement;
                while (lastStatement.NextStatement != null)
                {
                    lastStatement = lastStatement.NextStatement;
                }
            }
        }

        return true;
    }

    internal bool ParseTopLevel(out HLSLStatement statement)
    {
        statement = null;

        ParseAttributeBlock(out HLSLAttribute attributes);

        int line = GetLineNumber();
        string fileName = GetFileName();

        var type = new HLSLType();
        bool doesNotExpectSemicolon = false;

        if (Accept((int)HLSLToken.Struct))
        {
            // Struct declaration.
            if (!ExpectIdentifier(out string structName))
            {
                return false;
            }
            if (FindUserDefinedType(structName) != null)
            {
                _tokenizer.Error(CPrintf.Format("struct %s already defined", structName));
                return false;
            }

            if (!Expect('{'))
            {
                return false;
            }

            var structure = _tree.AddNode<HLSLStruct>(fileName, line);
            structure.Name = structName;

            _userTypes.Add(structure);

            HLSLStructField lastField = null;

            // Add the struct to our list of user defined types.
            while (!Accept('}'))
            {
                if (CheckForUnexpectedEndOfStream('}'))
                {
                    return false;
                }

                if (!ParseFieldDeclaration(out HLSLStructField field))
                {
                    return false;
                }

                if (lastField == null)
                {
                    structure.Field = field;
                }
                else
                {
                    lastField.NextField = field;
                }
                lastField = field;
            }

            statement = structure;
        }
        else if (Accept((int)HLSLToken.CBuffer) || Accept((int)HLSLToken.TBuffer))
        {
            // cbuffer/tbuffer declaration.
            var buffer = _tree.AddNode<HLSLBuffer>(fileName, line);
            AcceptIdentifier(out buffer.Name);

            // Optional register assignment.
            if (Accept(':'))
            {
                if (!Expect((int)HLSLToken.Register) || !Expect('(') ||
                    !ExpectIdentifier(out buffer.RegisterName) || !Expect(')'))
                {
                    return false;
                }
                // TODO: Check that we aren't re-using a register.
            }

            // Fields.
            if (!Expect('{'))
            {
                return false;
            }

            HLSLDeclaration lastField = null;
            while (!Accept('}'))
            {
                if (CheckForUnexpectedEndOfStream('}'))
                {
                    return false;
                }

                if (!ParseDeclaration(out HLSLDeclaration field))
                {
                    _tokenizer.Error("Expected variable declaration");
                    return false;
                }

                DeclareVariable(field.Name, field.Type);

                field.Buffer = buffer;
                if (buffer.Field == null)
                {
                    buffer.Field = field;
                }
                else
                {
                    lastField.NextStatement = field;
                }
                lastField = field;

                if (!Expect(';'))
                {
                    return false;
                }
            }

            statement = buffer;
        }
        else if (AcceptType(true, ref type))
        {
            // Global declaration (uniform or function).
            if (!ExpectIdentifier(out string globalName))
            {
                return false;
            }

            if (Accept('('))
            {
                // Function declaration.
                var function = _tree.AddNode<HLSLFunction>(fileName, line);
                function.Name = globalName;
                function.ReturnType.BaseType = type.BaseType;
                function.ReturnType.TypeName = type.TypeName;
                function.Attributes = attributes;

                BeginScope();

                int numOutputArguments = 0;
                if (!ParseArgumentList(out function.Argument, out function.NumArguments, ref numOutputArguments))
                {
                    return false;
                }
                function.NumOutputArguments = numOutputArguments;

                HLSLFunction declaration = FindFunction(function);

                // Forward declaration.
                if (Accept(';'))
                {
                    // Add a function entry so that calls can refer to it.
                    if (declaration == null)
                    {
                        _functions.Add(function);
                        statement = function;
                    }
                    EndScope();
                    return true;
                }

                // Optional semantic.
                if (Accept(':') && !ExpectIdentifier(out function.Semantic))
                {
                    return false;
                }

                if (declaration != null)
                {
                    if (declaration.Forward != null || declaration.Statement != null)
                    {
                        _tokenizer.Error("Duplicate function definition");
                        return false;
                    }

                    declaration.Forward = function;
                }
                else
                {
                    _functions.Add(function);
                }

                if (!Expect('{') || !ParseBlock(out function.Statement, function.ReturnType))
                {
                    return false;
                }

                EndScope();

                // Note, no semi-colon at the end of a function declaration.
                statement = function;

                return true;
            }
            else
            {
                HLSLDeclaration firstDeclaration = null;
                HLSLDeclaration lastDeclaration = null;

                do
                {
                    if (firstDeclaration != null)
                    {
                        if (!ExpectIdentifier(out globalName))
                        {
                            return false;
                        }
                    }

                    // Uniform declaration.
                    var declaration = _tree.AddNode<HLSLDeclaration>(fileName, line);
                    declaration.Name = globalName;
                    declaration.Type = type;

                    // Handle array syntax.
                    if (Accept('['))
                    {
                        if (!Accept(']'))
                        {
                            if (!ParseExpression(out declaration.Type.ArraySize) || !Expect(']'))
                            {
                                return false;
                            }
                        }
                        declaration.Type.Array = true;
                    }

                    // Handle optional register.
                    if (Accept(':'))
                    {
                        // @@ Currently we support either a semantic or a
                        // register, but not both.
                        if (AcceptIdentifier(out declaration.Semantic))
                        {
                            // nothing
                        }
                        else if (!Expect((int)HLSLToken.Register) || !Expect('(') ||
                                 !ExpectIdentifier(out declaration.RegisterName) || !Expect(')'))
                        {
                            return false;
                        }
                    }

                    DeclareVariable(globalName, declaration.Type);

                    if (!ParseDeclarationAssignment(declaration))
                    {
                        return false;
                    }

                    if (firstDeclaration == null)
                    {
                        firstDeclaration = declaration;
                    }
                    if (lastDeclaration != null)
                    {
                        lastDeclaration.NextDeclaration = declaration;
                    }
                    lastDeclaration = declaration;

                } while (Accept(','));

                statement = firstDeclaration;
            }
        }
        else if (ParseTechnique(out statement))
        {
            doesNotExpectSemicolon = true;
        }
        else if (ParsePipeline(out statement))
        {
            doesNotExpectSemicolon = true;
        }
        else if (ParseStage(out statement))
        {
            doesNotExpectSemicolon = true;
        }

        if (statement != null)
        {
            statement.Attributes = attributes;
        }

        return doesNotExpectSemicolon || Expect(';');
    }

    internal bool ParseStatementOrBlock(out HLSLStatement firstStatement, in HLSLType returnType, bool scoped = true)
    {
        firstStatement = null;

        if (scoped)
        {
            BeginScope();
        }

        if (Accept('{'))
        {
            if (!ParseBlock(out firstStatement, returnType))
            {
                return false;
            }
        }
        else
        {
            if (!ParseStatement(out firstStatement, returnType))
            {
                return false;
            }
        }

        if (scoped)
        {
            EndScope();
        }

        return true;
    }

    internal bool ParseBlock(out HLSLStatement firstStatement, in HLSLType returnType)
    {
        firstStatement = null;
        HLSLStatement lastStatement = null;

        while (!Accept('}'))
        {
            if (CheckForUnexpectedEndOfStream('}'))
            {
                return false;
            }

            if (!ParseStatement(out HLSLStatement statement, returnType))
            {
                return false;
            }

            if (statement != null)
            {
                if (firstStatement == null)
                {
                    firstStatement = statement;
                }
                else
                {
                    lastStatement.NextStatement = statement;
                }

                lastStatement = statement;
                while (lastStatement.NextStatement != null)
                {
                    lastStatement = lastStatement.NextStatement;
                }
            }
        }

        return true;
    }

    internal bool ParseStatement(out HLSLStatement statement, in HLSLType returnType)
    {
        statement = null;

        string fileName = GetFileName();
        int line = GetLineNumber();

        // Empty statements.
        if (Accept(';'))
        {
            return true;
        }

        ParseAttributeBlock(out HLSLAttribute attributes);

        // If statement.
        if (Accept((int)HLSLToken.If))
        {
            var ifStatement = _tree.AddNode<HLSLIfStatement>(fileName, line);
            ifStatement.Attributes = attributes;
            if (!Expect('(') || !ParseExpression(out ifStatement.Condition) || !Expect(')'))
            {
                return false;
            }

            statement = ifStatement;
            if (!ParseStatementOrBlock(out ifStatement.Statement, returnType))
            {
                return false;
            }

            if (Accept((int)HLSLToken.Else))
            {
                return ParseStatementOrBlock(out ifStatement.ElseStatement, returnType);
            }
            return true;
        }

        // For statement.
        if (Accept((int)HLSLToken.For))
        {
            var forStatement = _tree.AddNode<HLSLForStatement>(fileName, line);
            forStatement.Attributes = attributes;
            if (!Expect('('))
            {
                return false;
            }

            BeginScope();
            if (!ParseDeclaration(out forStatement.Initialization) &&
                !ParseExpression(out forStatement.InitializationWithoutType))
            {
                return false;
            }
            if (!Expect(';'))
            {
                return false;
            }

            ParseExpression(out forStatement.Condition);
            if (!Expect(';'))
            {
                return false;
            }

            ParseExpression(out forStatement.Increment);
            if (!Expect(')'))
            {
                return false;
            }

            statement = forStatement;
            if (!ParseStatementOrBlock(out forStatement.Statement, returnType))
            {
                return false;
            }

            EndScope();
            return true;
        }

        // While statement.
        if (Accept((int)HLSLToken.While))
        {
            var whileStatement = _tree.AddNode<HLSLWhileStatement>(fileName, line);
            whileStatement.Attributes = attributes;
            if (!Expect('(') || !ParseExpression(out whileStatement.Condition) || !Expect(')'))
            {
                return false;
            }

            statement = whileStatement;
            if (!ParseStatementOrBlock(out whileStatement.Statement, returnType))
            {
                return false;
            }
            return true;
        }

        // Block statement.
        if (Accept('{'))
        {
            var blockStatement = _tree.AddNode<HLSLBlockStatement>(fileName, line);
            statement = blockStatement;
            BeginScope();
            bool success = ParseBlock(out blockStatement.Statement, returnType);
            EndScope();
            return success;
        }

        // Discard statement.
        if (Accept((int)HLSLToken.Discard))
        {
            statement = _tree.AddNode<HLSLDiscardStatement>(fileName, line);
            return Expect(';');
        }

        // Break statement.
        if (Accept((int)HLSLToken.Break))
        {
            statement = _tree.AddNode<HLSLBreakStatement>(fileName, line);
            return Expect(';');
        }

        // Continue statement.
        if (Accept((int)HLSLToken.Continue))
        {
            statement = _tree.AddNode<HLSLContinueStatement>(fileName, line);
            return Expect(';');
        }

        // Return statement.
        if (Accept((int)HLSLToken.Return))
        {
            var returnStatement = _tree.AddNode<HLSLReturnStatement>(fileName, line);
            if (!Accept(';') && !ParseExpression(out returnStatement.Expression))
            {
                return false;
            }

            // Check that the return expression can be cast to the return type.
            var voidType = new HLSLType(HLSLBaseType.Void);
            HLSLType expressionType = returnStatement.Expression != null ? returnStatement.Expression.ExpressionType : voidType;
            if (!CheckTypeCast(expressionType, returnType))
            {
                return false;
            }

            statement = returnStatement;
            return Expect(';');
        }

        if (ParseDeclaration(out HLSLDeclaration declaration))
        {
            statement = declaration;
        }
        else if (ParseExpression(out HLSLExpression expression))
        {
            var expressionStatement = _tree.AddNode<HLSLExpressionStatement>(fileName, line);
            expressionStatement.Expression = expression;
            statement = expressionStatement;
        }

        // Some statements are separated by a colon instead of a semicolon.
        if (Accept(','))
        {
            return true;
        }

        return Expect(';');
    }

    // Only used in block statements or within control flow statements, so it
    // doesn't support semantics or layout modifiers.
    internal bool ParseDeclaration(out HLSLDeclaration declaration)
    {
        declaration = null;

        string fileName = GetFileName();
        int line = GetLineNumber();

        var type = new HLSLType();
        if (!AcceptType(false, ref type))
        {
            return false;
        }

        bool allowUnsizedArray = true; // @@ Really?

        HLSLDeclaration firstDeclaration = null;
        HLSLDeclaration lastDeclaration = null;

        do
        {
            if (!ExpectIdentifier(out string name))
            {
                // TODO: false means we didn't accept a declaration and we had an error!
                return false;
            }

            // Handle array syntax.
            if (Accept('['))
            {
                type.Array = true;
                // Optionally allow no size to be specified for the array.
                if (Accept(']') && allowUnsizedArray)
                {
                    return true;
                }
                if (!ParseExpression(out type.ArraySize) || !Expect(']'))
                {
                    return false;
                }
            }

            var declaration2 = _tree.AddNode<HLSLDeclaration>(fileName, line);
            declaration2.Type = type;
            declaration2.Name = name;

            DeclareVariable(declaration2.Name, declaration2.Type);

            // Handle optional assignment of the declared variable(s).
            if (!ParseDeclarationAssignment(declaration2))
            {
                return false;
            }

            if (firstDeclaration == null)
            {
                firstDeclaration = declaration2;
            }
            if (lastDeclaration != null)
            {
                lastDeclaration.NextDeclaration = declaration2;
            }
            lastDeclaration = declaration2;

        } while (Accept(','));

        declaration = firstDeclaration;

        return true;
    }

    internal bool ParseDeclarationAssignment(HLSLDeclaration declaration)
    {
        if (Accept('='))
        {
            // Handle array initialization syntax.
            if (declaration.Type.Array)
            {
                if (!Expect('{') || !ParseExpressionList('}', true, out declaration.Assignment, out _))
                {
                    return false;
                }
            }
            else if (BaseTypes.IsSamplerType(declaration.Type.BaseType))
            {
                if (!ParseSamplerState(out declaration.Assignment))
                {
                    return false;
                }
            }
            else if (!ParseExpression(out declaration.Assignment))
            {
                return false;
            }
        }
        return true;
    }

    internal bool ParseFieldDeclaration(out HLSLStructField field)
    {
        field = _tree.AddNode<HLSLStructField>(GetFileName(), GetLineNumber());
        if (!ExpectDeclaration(false, ref field.Type, out field.Name))
        {
            return false;
        }

        // Handle optional semantics.
        if (Accept(':'))
        {
            if (!ExpectIdentifier(out field.Semantic))
            {
                return false;
            }
        }

        return Expect(';');
    }

    internal bool ParseAttributeList(ref HLSLAttribute firstAttribute)
    {
        string fileName = GetFileName();
        int line = GetLineNumber();

        do
        {
            if (!ExpectIdentifier(out string identifier))
            {
                return false;
            }

            var attribute = _tree.AddNode<HLSLAttribute>(fileName, line);

            if (String_.Equal(identifier, "unroll"))
            {
                attribute.AttributeType = HLSLAttributeType.Unroll;
            }
            else if (String_.Equal(identifier, "flatten"))
            {
                attribute.AttributeType = HLSLAttributeType.Flatten;
            }
            else if (String_.Equal(identifier, "branch"))
            {
                attribute.AttributeType = HLSLAttributeType.Branch;
            }
            else if (String_.Equal(identifier, "nofastmath"))
            {
                attribute.AttributeType = HLSLAttributeType.NoFastMath;
            }

            // @@ parse arguments, () not required if attribute constructor has no arguments.

            if (firstAttribute == null)
            {
                firstAttribute = attribute;
            }
            else
            {
                HLSLAttribute lastAttribute = firstAttribute;
                while (lastAttribute.NextAttribute != null)
                {
                    lastAttribute = lastAttribute.NextAttribute;
                }
                lastAttribute.NextAttribute = attribute;
            }

        } while (Accept(','));

        return true;
    }

    /// <summary>
    /// Attributes can have all these forms:
    ///   [A] statement; [A,B] statement; [A][B] statement;
    /// Not supported: [A] statement [B]; [A()] statement; [A(a)] statement;
    /// </summary>
    internal bool ParseAttributeBlock(out HLSLAttribute attribute)
    {
        attribute = null;

        if (!Accept('['))
        {
            return false;
        }

        // Parse list of attribute constructors.
        ParseAttributeList(ref attribute);

        if (!Expect(']'))
        {
            return false;
        }

        // Parse additional [] blocks.
        ParseAttributeBlock(out HLSLAttribute additional);
        if (additional != null)
        {
            if (attribute == null)
            {
                attribute = additional;
            }
            else
            {
                HLSLAttribute lastAttribute = attribute;
                while (lastAttribute.NextAttribute != null)
                {
                    lastAttribute = lastAttribute.NextAttribute;
                }
                lastAttribute.NextAttribute = additional;
            }
        }

        return true;
    }
}
