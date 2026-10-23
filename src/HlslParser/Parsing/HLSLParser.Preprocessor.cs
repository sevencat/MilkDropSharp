using System.Text;

namespace HlslParser;

/// <summary>
/// Port of the text level preprocessor in HLSLParser.cpp.
///
/// It is not a real C preprocessor: four sequential passes collect #defines,
/// repeatedly substitute macro values inside other macro values, resolve simple
/// aliases and finally rewrite the source while honouring #if/#else/#endif.
/// Behaviour (including quirks) must match exactly, because the GLSL output is
/// compared byte for byte.
/// </summary>
public partial class HLSLParser
{
    public bool ApplyPreprocessor(string fileName, string buffer, out string sourcePreprocessed)
    {
        return ApplyPreprocessor(fileName, buffer, buffer?.Length ?? 0, out sourcePreprocessed);
    }

    public bool ApplyPreprocessor(string fileName, string buffer, int length, out string sourcePreprocessed)
    {
        sourcePreprocessed = string.Empty;

        if (buffer == null)
        {
            return false;
        }

        if (length >= 0 && length < buffer.Length)
        {
            buffer = buffer.Substring(0, length);
        }

        _tokenizer = new HLSLTokenizer(fileName, buffer, _diagnostics);

        // First pass: grab #define.
        while (_tokenizer.GetToken() != (int)HLSLToken.EndOfStream)
        {
            if (_tokenizer.GetToken() == (int)HLSLToken.PreprocessorDefine)
            {
                ParsePreprocessorDefine();
            }

            _tokenizer.Next();
        }

        // Second pass: propagate macro definitions across macro values.
        int index = 0;
        while (index < _macros.Count)
        {
            HLSLMacro macro = _macros[index];
            _tokenizer = new HLSLTokenizer(fileName, macro.Value ?? string.Empty, _diagnostics);
            var valueProcessed = new StringBuilder();

            while (_tokenizer.GetToken() != (int)HLSLToken.EndOfStream)
            {
                bool addOriginalSource = true;
                if (_tokenizer.GetToken() == (int)HLSLToken.Identifier)
                {
                    HLSLMacro matchedMacro = ProcessMacroFromIdentifier(valueProcessed, ref addOriginalSource);
                    if (matchedMacro != null)
                    {
                        // Check if macro is just an alias: the macro value must
                        // be equal to the matched macro name. C++ compares the
                        // pointers (both come from the string pool).
                        if (ReferenceEquals(macro.Value, matchedMacro.Name))
                        {
                            macro.MacroAliased = matchedMacro;
                        }
                    }
                }

                if (addOriginalSource)
                {
                    AppendTokenSource(valueProcessed, trimmed: true);
                }

                _tokenizer.Next();
            }

            string valueText = valueProcessed.ToString();
            if (valueText == "main")
            {
                valueText = "sampler_fw_main";
            }

            if (!String_.Equal(valueText, macro.Value))
            {
                // The macro value referenced another define which was replaced;
                // try again until there is no replacement.
                macro.Value = _tree.AddString(valueText);
            }
            else
            {
                index++;
            }
        }

        // Third pass: macro aliases get the original value and arguments.
        for (index = 0; index < _macros.Count; ++index)
        {
            HLSLMacro macro = _macros[index];
            if (macro.MacroAliased != null)
            {
                macro.Argument = macro.MacroAliased.Argument;
                macro.NumArguments = macro.MacroAliased.NumArguments;
                macro.Value = macro.MacroAliased.Value;
            }
        }

        // Fourth pass: search and replace preprocessor directives.
        var isCodeActive = new Stack<bool>();
        isCodeActive.Push(true);

        _tokenizer = new HLSLTokenizer(fileName, buffer, _diagnostics);
        var result = new StringBuilder();

        while (_tokenizer.GetToken() != (int)HLSLToken.EndOfStream && isCodeActive.Count > 0)
        {
            bool addOriginalSource = true;

            if (_tokenizer.GetToken() == (int)HLSLToken.PreprocessorIf)
            {
                while (_tokenizer.GetToken() != (int)HLSLToken.IntLiteral &&
                       _tokenizer.GetToken() != (int)HLSLToken.EndOfLine)
                {
                    _tokenizer.Next(false);
                }

                if (_tokenizer.GetToken() == (int)HLSLToken.IntLiteral)
                {
                    isCodeActive.Push(_tokenizer.GetInt() != 0);
                }
                else
                {
                    _tokenizer.Error("#if evaluation failed: not an integer");
                    return false;
                }
                addOriginalSource = false;
            }
            else if (_tokenizer.GetToken() == (int)HLSLToken.PreprocessorElse)
            {
                // Invert stack state.
                if (isCodeActive.Count > 0)
                {
                    bool state = isCodeActive.Pop();
                    isCodeActive.Push(!state);
                }
                addOriginalSource = false;
            }
            else if (_tokenizer.GetToken() == (int)HLSLToken.PreprocessorEndif)
            {
                if (isCodeActive.Count > 0)
                {
                    isCodeActive.Pop();
                }
                addOriginalSource = false;
            }
            else if (_tokenizer.GetToken() == (int)HLSLToken.PreprocessorDefine)
            {
                // Skip macro definitions.
                while (_tokenizer.GetToken() != (int)HLSLToken.EndOfLine)
                {
                    _tokenizer.Next(false);
                }
                addOriginalSource = false;
            }
            else if (_tokenizer.GetToken() == (int)HLSLToken.Identifier && isCodeActive.Peek())
            {
                ProcessMacroFromIdentifier(result, ref addOriginalSource);
            }
            else if (!isCodeActive.Peek())
            {
                addOriginalSource = false;
            }

            if (addOriginalSource)
            {
                AppendTokenSource(result, trimmed: false);
            }

            _tokenizer.Next();
        }

        sourcePreprocessed = result.ToString();
        return isCodeActive.Count == 1;
    }

    internal bool ParsePreprocessorDefine()
    {
        int line = GetLineNumber();
        string fileName = GetFileName();

        // Do not use AcceptIdentifier here: we need to know whether there is
        // whitespace after the define name, because both the name and the value
        // can contain parentheses.
        _tokenizer.Next();

        string macroName;
        if (_tokenizer.GetToken() == (int)HLSLToken.Identifier)
        {
            macroName = _tree.AddString(_tokenizer.GetIdentifier());
        }
        else
        {
            string near = _tokenizer.GetTokenName();
            _tokenizer.Error(CPrintf.Format("Syntax error: expected identifier near '%s'", near));
            return false;
        }

        bool macroWithArguments = !_tokenizer.NextIsWhitespace();

        // Macro declaration.
        var macro = _tree.AddNode<HLSLMacro>(fileName, line);
        macro.Name = macroName;
        _macros.Add(macro);

        // Prepare next token.
        _tokenizer.Next();

        var value = new StringBuilder();

        if (macroWithArguments)
        {
            // Macro with arguments.
            if (Accept('('))
            {
                int numArguments = 0;
                HLSLArgument lastArgument = null;

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
                    if (!ExpectIdentifier(out argument.Name))
                    {
                        return false;
                    }

                    if (lastArgument != null)
                    {
                        lastArgument.NextArgument = argument;
                    }
                    else
                    {
                        macro.Argument = argument;
                    }
                    lastArgument = argument;

                    ++numArguments;
                }

                macro.NumArguments = (uint)numArguments;
            }

            // Macro value.
            while (_tokenizer.GetToken() != (int)HLSLToken.EndOfLine)
            {
                bool addOriginalSource = true;

                if (_tokenizer.GetToken() == (int)HLSLToken.Identifier)
                {
                    // Search for macro arguments.
                    HLSLArgument argument = macro.Argument;
                    int argNum = 0;
                    while (argument != null)
                    {
                        if (String_.Equal(argument.Name, _tokenizer.GetIdentifier()))
                        {
                            value.Append('#').Append(argNum).Append('#');
                            addOriginalSource = false;
                            break;
                        }

                        argument = argument.NextArgument;
                        ++argNum;
                    }
                }

                if (addOriginalSource)
                {
                    AppendTokenSource(value, trimmed: true);
                }

                _tokenizer.Next(false);
            }
        }
        else
        {
            // Macro value.
            while (_tokenizer.GetToken() != (int)HLSLToken.EndOfLine)
            {
                AppendTokenSource(value, trimmed: true);
                _tokenizer.Next(false);
            }
        }

        // Remove extra parentheses.
        if (value.Length > 0 && value[0] == '(')
        {
            // C++ erases the last char and then the first one without a length
            // check; guard so malformed input cannot throw.
            if (value.Length >= 2)
            {
                value.Length -= 1;
                value.Remove(0, 1);
            }
        }

        macro.Value = _tree.AddString(value.ToString());

        return true;
    }

    internal HLSLMacro ProcessMacroFromIdentifier(StringBuilder output, ref bool addOriginalSource)
    {
        // Search for a matching define.
        for (int i = _macros.Count - 1; i >= 0; --i)
        {
            if (String_.Equal(_macros[i].Name, _tokenizer.GetIdentifier()))
            {
                if (_macros[i].Argument == null)
                {
                    // Macro without arguments.
                    if (ReferenceEquals(_macros[i].Name, _macros[i].Value))
                    {
                        // Macro name and value are identical (#define xyz xyz).
                        addOriginalSource = true;
                    }
                    else
                    {
                        output.Append('(').Append(_macros[i].Value).Append(')');
                        addOriginalSource = false;
                    }
                }
                else
                {
                    // Macro with arguments.
                    int savePos = _tokenizer.GetLastPos(false);

                    _tokenizer.Next();

                    if (ProcessMacroArguments(_macros[i], output))
                    {
                        // Macro replaced.
                        addOriginalSource = false;
                    }
                    else
                    {
                        // Macro signatures do not match: write back the read parts.
                        int end = _tokenizer.GetLastPos(false);
                        output.Append(_tokenizer.SourceSlice(savePos, end));

                        _tokenizer.ReturnToPos(end);

                        addOriginalSource = true;
                    }
                }

                return _macros[i];
            }
        }

        return null;
    }

    internal bool ProcessMacroArguments(HLSLMacro macro, StringBuilder output)
    {
        uint scopeLevel = 0;
        var argumentsValues = new List<string>();
        var argValue = new StringBuilder();
        bool firstToken = true;

        // Parse argument values.
        while (_tokenizer.GetToken() != (int)HLSLToken.EndOfStream)
        {
            bool addToValue = true;

            if (firstToken && _tokenizer.GetToken() != '(')
            {
                break;
            }
            else if (_tokenizer.GetToken() == '(')
            {
                ++scopeLevel;
                if (scopeLevel == 1)
                {
                    addToValue = false;
                }
            }
            else if (_tokenizer.GetToken() == ')')
            {
                --scopeLevel;
                if (scopeLevel == 0)
                {
                    argumentsValues.Add(argValue.ToString());
                    break;
                }
            }
            else if (_tokenizer.GetToken() == ',')
            {
                if (scopeLevel == 1)
                {
                    argumentsValues.Add(argValue.ToString());
                    argValue.Clear();
                    addToValue = false;
                }
            }
            else if (_tokenizer.GetToken() == (int)HLSLToken.Identifier)
            {
                ProcessMacroFromIdentifier(argValue, ref addToValue);
            }

            if (addToValue)
            {
                int start = _tokenizer.GetLastPos(false);
                int end = _tokenizer.GetCurrentPos();
                argValue.Append(_tokenizer.SourceSlice(start, end));
            }

            _tokenizer.Next();
            firstToken = false;
        }

        // Argument count does not match.
        if (argumentsValues.Count != (int)macro.NumArguments)
        {
            return false;
        }

        // Surround the macro definition with parentheses.
        output.Append('(');

        // Write argument values, substituting #n# placeholders.
        string value = macro.Value ?? string.Empty;
        int index = 0;
        var arg = new StringBuilder();
        bool argFound = false;

        while (index < value.Length)
        {
            char c = value[index];
            if (c == '#' && !argFound)
            {
                argFound = true;
                arg.Clear();
            }
            else if (c == '#' && argFound)
            {
                int indexArg = 0;
                int.TryParse(arg.ToString(), out indexArg);
                if (indexArg < argumentsValues.Count)
                {
                    output.Append(argumentsValues[indexArg]);
                }
                argFound = false;
            }
            else if (argFound)
            {
                arg.Append(c);
            }
            else
            {
                output.Append(c);
            }

            ++index;
        }

        // Surround the macro definition with parentheses.
        output.Append(')');

        return true;
    }

    private void AppendTokenSource(StringBuilder output, bool trimmed)
    {
        int start = _tokenizer.GetLastPos(trimmed);
        int end = _tokenizer.GetCurrentPos();
        output.Append(_tokenizer.SourceSlice(start, end));
    }
}
