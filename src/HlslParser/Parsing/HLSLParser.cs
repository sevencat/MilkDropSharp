namespace HlslParser;

/// <summary>
/// Port of HLSLParser.cpp. The parser is a hand written recursive descent
/// parser over <see cref="HLSLTokenizer"/> that builds an <see cref="HLSLTree"/>.
///
/// This file holds the scanner helpers, symbol tables and scope handling;
/// declarations/statements/expressions live in the partial files.
/// </summary>
public partial class HLSLParser
{
    private struct Variable
    {
        public string Name;
        public HLSLType Type;
    }

    private readonly List<HLSLStruct> _userTypes = new();
    private readonly List<Variable> _variables = new();
    private readonly List<HLSLFunction> _functions = new();
    private readonly List<HLSLMacro> _macros = new();

    private int _numGlobals;
    private readonly HLSLTree _tree;

    /// <summary>Only set by the disabled @if support in C++; kept for parity.</summary>
    private bool _allowUndeclaredIdentifiers;

    private HLSLTokenizer _tokenizer = new();
    private readonly IDiagnosticSink _diagnostics;

    public HLSLParser(Allocator allocator, HLSLTree tree)
        : this(allocator, tree, null)
    {
    }

    public HLSLParser(Allocator allocator, HLSLTree tree, IDiagnosticSink diagnostics)
    {
        _numGlobals = 0;
        _tree = tree;
        _allowUndeclaredIdentifiers = false;
        _diagnostics = diagnostics ?? NullDiagnosticSink.Instance;
    }

    /// <summary>Exposed for tests (the C++ class keeps the tokenizer private).</summary>
    internal HLSLTokenizer Tokenizer
    {
        get => _tokenizer;
        set => _tokenizer = value;
    }

    internal HLSLTree Tree => _tree;

    internal int NumGlobals => _numGlobals;

    internal IReadOnlyList<HLSLFunction> Functions => _functions;

    internal IReadOnlyList<HLSLStruct> UserTypes => _userTypes;

    // ---------------------------------------------------------------- scanning

    internal bool Accept(int token)
    {
        if (_tokenizer.GetToken() == token)
        {
            _tokenizer.Next();
            return true;
        }
        return false;
    }

    internal bool Accept(string token)
    {
        if (_tokenizer.GetToken() == (int)HLSLToken.Identifier &&
            String_.Equal(token, _tokenizer.GetIdentifier()))
        {
            _tokenizer.Next();
            return true;
        }
        return false;
    }

    internal bool Expect(int token)
    {
        if (!Accept(token))
        {
            string want = HLSLTokenizer.GetTokenName(token);
            string near = _tokenizer.GetTokenName();
            _tokenizer.Error(CPrintf.Format("Syntax error: expected '%s' near '%s'", want, near));
            return false;
        }
        return true;
    }

    internal bool Expect(string token)
    {
        if (!Accept(token))
        {
            string want = token;
            string near = _tokenizer.GetTokenName();
            _tokenizer.Error(CPrintf.Format("Syntax error: expected '%s' near '%s'", want, near));
            return false;
        }
        return true;
    }

    internal bool AcceptIdentifier(out string identifier)
    {
        if (_tokenizer.GetToken() == (int)HLSLToken.Identifier)
        {
            identifier = _tree.AddString(_tokenizer.GetIdentifier());
            _tokenizer.Next();
            return true;
        }
        identifier = null;
        return false;
    }

    internal bool ExpectIdentifier(out string identifier)
    {
        if (!AcceptIdentifier(out identifier))
        {
            string near = _tokenizer.GetTokenName();
            _tokenizer.Error(CPrintf.Format("Syntax error: expected identifier near '%s'", near));
            identifier = string.Empty;
            return false;
        }
        return true;
    }

    internal bool AcceptFloat(out float value)
    {
        if (_tokenizer.GetToken() == (int)HLSLToken.FloatLiteral)
        {
            value = _tokenizer.GetFloat();
            _tokenizer.Next();
            return true;
        }
        value = 0.0f;
        return false;
    }

    internal bool AcceptInt(out int value)
    {
        if (_tokenizer.GetToken() == (int)HLSLToken.IntLiteral)
        {
            value = _tokenizer.GetInt();
            _tokenizer.Next();
            return true;
        }
        value = 0;
        return false;
    }

    internal bool CheckForUnexpectedEndOfStream(int endToken)
    {
        if (Accept((int)HLSLToken.EndOfStream))
        {
            string what = HLSLTokenizer.GetTokenName(endToken);
            _tokenizer.Error(CPrintf.Format("Unexpected end of file while looking for '%s'", what));
            return true;
        }
        return false;
    }

    internal int GetLineNumber()
    {
        return _tokenizer.GetLineNumber();
    }

    internal string GetFileName()
    {
        return _tree.AddString(_tokenizer.GetFileName());
    }

    // ------------------------------------------------------------------ scopes

    internal void BeginScope()
    {
        // NULL name is the sentinel that starts a new scope level.
        _variables.Add(new Variable { Name = null });
    }

    internal void EndScope()
    {
        int numVariables = _variables.Count - 1;
        while (numVariables >= 0 && _variables[numVariables].Name != null)
        {
            --numVariables;
        }

        if (numVariables < 0)
        {
            _variables.Clear();
            return;
        }

        _variables.RemoveRange(numVariables, _variables.Count - numVariables);
    }

    internal void DeclareVariable(string name, in HLSLType type)
    {
        if (_variables.Count == _numGlobals)
        {
            ++_numGlobals;
        }

        _variables.Add(new Variable { Name = name, Type = type });
    }

    /// <summary>Port of <c>FindVariable</c>; returns false when the name is unknown.</summary>
    internal bool FindVariable(string name, out HLSLType type, out bool global)
    {
        for (int i = _variables.Count - 1; i >= 0; --i)
        {
            if (String_.Equal(_variables[i].Name, name))
            {
                type = _variables[i].Type;
                global = i < _numGlobals;
                return true;
            }
        }

        type = default;
        global = false;
        return false;
    }

    internal HLSLStruct FindUserDefinedType(string name)
    {
        // Pointer comparison is sufficient in C++ because of the string pool.
        for (int i = 0; i < _userTypes.Count; ++i)
        {
            if (String_.Equal(_userTypes[i].Name, name))
            {
                return _userTypes[i];
            }
        }
        return null;
    }

    internal HLSLFunction FindFunction(string name)
    {
        for (int i = 0; i < _functions.Count; ++i)
        {
            if (String_.Equal(_functions[i].Name, name))
            {
                return _functions[i];
            }
        }
        return null;
    }

    internal HLSLFunction FindFunction(HLSLFunction function)
    {
        for (int i = 0; i < _functions.Count; ++i)
        {
            if (String_.Equal(_functions[i].Name, function.Name) &&
                AreTypesEqual(_tree, _functions[i].ReturnType, function.ReturnType) &&
                AreArgumentListsEqual(_tree, _functions[i].Argument, function.Argument))
            {
                return _functions[i];
            }
        }
        return null;
    }

    internal bool GetIsFunction(string name)
    {
        for (int i = 0; i < _functions.Count; ++i)
        {
            if (String_.Equal(_functions[i].Name, name))
            {
                return true;
            }
        }

        Intrinsic[] intrinsics = ParserTables.Intrinsics;
        for (int i = 0; i < intrinsics.Length; ++i)
        {
            if (String_.Equal(name, intrinsics[i].Function.Name))
            {
                return true;
            }
        }

        return false;
    }

    // -------------------------------------------------------- type utilities

    internal static string GetTypeName(in HLSLType type)
    {
        if (type.BaseType == HLSLBaseType.UserDefined)
        {
            return type.TypeName;
        }
        return BaseTypes.Descriptions[(int)type.BaseType].TypeName;
    }

    internal static string GetBinaryOpName(HLSLBinaryOp binaryOp)
    {
        switch (binaryOp)
        {
            case HLSLBinaryOp.And: return "&&";
            case HLSLBinaryOp.Or: return "||";
            case HLSLBinaryOp.Add: return "+";
            case HLSLBinaryOp.Sub: return "-";
            case HLSLBinaryOp.Mul: return "*";
            case HLSLBinaryOp.Div: return "/";
            case HLSLBinaryOp.Mod: return "%";
            case HLSLBinaryOp.Less: return "<";
            case HLSLBinaryOp.Greater: return ">";
            case HLSLBinaryOp.LessEqual: return "<=";
            case HLSLBinaryOp.GreaterEqual: return ">=";
            case HLSLBinaryOp.Equal: return "==";
            case HLSLBinaryOp.NotEqual: return "!=";
            case HLSLBinaryOp.BitAnd: return "&";
            case HLSLBinaryOp.BitOr: return "|";
            case HLSLBinaryOp.BitXor: return "^";
            case HLSLBinaryOp.Assign: return "=";
            case HLSLBinaryOp.AddAssign: return "+=";
            case HLSLBinaryOp.SubAssign: return "-=";
            case HLSLBinaryOp.MulAssign: return "*=";
            case HLSLBinaryOp.DivAssign: return "/=";
            default: return "???";
        }
    }

    /// <summary>
    /// 1) Match 2) Scalar dimension promotion 3) Conversion
    /// 4) Conversion + scalar dimension promotion 5) Truncation
    /// 6) Conversion + truncation
    /// </summary>
    internal static int GetTypeCastRank(HLSLTree tree, in HLSLType srcType, in HLSLType dstType)
    {
        if (srcType.Array != dstType.Array)
        {
            return -1;
        }

        if (srcType.Array)
        {
            int srcArraySize = -1;
            int dstArraySize = -1;

            if (srcType.ArraySize != null)
            {
                tree.GetExpressionValue(srcType.ArraySize, out srcArraySize);
            }
            if (dstType.ArraySize != null)
            {
                tree.GetExpressionValue(dstType.ArraySize, out dstArraySize);
            }

            if (srcArraySize != dstArraySize)
            {
                return -1;
            }
        }

        if (srcType.BaseType == HLSLBaseType.UserDefined && dstType.BaseType == HLSLBaseType.UserDefined)
        {
            return String_.Equal(srcType.TypeName, dstType.TypeName) ? 0 : -1;
        }

        if (srcType.BaseType == dstType.BaseType)
        {
            if (BaseTypes.IsSamplerType(srcType.BaseType))
            {
                return srcType.SamplerType == dstType.SamplerType ? 0 : -1;
            }

            return 0;
        }

        BaseTypeDescription srcDesc = BaseTypes.Descriptions[(int)srcType.BaseType];
        BaseTypeDescription dstDesc = BaseTypes.Descriptions[(int)dstType.BaseType];
        if (srcDesc.NumericType == NumericType.NaN || dstDesc.NumericType == NumericType.NaN)
        {
            return -1;
        }

        // Result bits: T R R R P H C
        // (T = truncation, R = conversion rank, P = dimension promotion,
        //  H = height promotion, C = component promotion)
        int result = ParserTables.NumberTypeRank[(int)srcDesc.NumericType, (int)dstDesc.NumericType] << 3;

        if (srcDesc.NumDimensions == 0 && dstDesc.NumDimensions > 0)
        {
            // Scalar dimension promotion.
            result |= 1 << 2;
        }
        else if ((srcDesc.NumDimensions == dstDesc.NumDimensions &&
                  (srcDesc.NumComponents > dstDesc.NumComponents || srcDesc.Height > dstDesc.Height)) ||
                 (srcDesc.NumDimensions > 0 && dstDesc.NumDimensions == 0))
        {
            // Truncation.
            result |= 1 << 6;
        }
        else if (srcDesc.NumDimensions != dstDesc.NumDimensions)
        {
            // Can't convert.
            return -1;
        }
        else if (srcDesc.Height != dstDesc.Height)
        {
            // Scalar height promotion.
            result |= 1 << 1;
        }
        else if (srcDesc.NumComponents != dstDesc.NumComponents)
        {
            // Scalar components promotion.
            result |= 1 << 0;
        }

        return result;
    }

    internal bool CheckTypeCast(in HLSLType srcType, in HLSLType dstType)
    {
        if (GetTypeCastRank(_tree, srcType, dstType) == -1)
        {
            string srcTypeName = GetTypeName(srcType);
            string dstTypeName = GetTypeName(dstType);
            _tokenizer.Error(CPrintf.Format("Cannot implicitly convert from '%s' to '%s'", srcTypeName, dstTypeName));
            return false;
        }
        return true;
    }

    internal enum CompareFunctionsResult
    {
        FunctionsEqual,
        Function1Better,
        Function2Better,
    }

    internal static bool GetFunctionCallCastRanks(HLSLTree tree, HLSLFunctionCall call, HLSLFunction function, int[] rankBuffer)
    {
        if (function == null || function.NumArguments < call.NumArguments)
        {
            return false;
        }

        HLSLExpression expression = call.Argument;
        HLSLArgument argument = function.Argument;

        for (int i = 0; i < call.NumArguments; ++i)
        {
            int rank = GetTypeCastRank(tree, expression.ExpressionType, argument.Type);
            if (rank == -1)
            {
                return false;
            }

            rankBuffer[i] = rank;

            argument = argument.NextArgument;
            expression = expression.NextExpression;
        }

        for (int i = call.NumArguments; i < function.NumArguments; ++i)
        {
            if (argument.DefaultValue == null)
            {
                return false;
            }
            argument = argument.NextArgument;
        }

        return true;
    }

    internal static CompareFunctionsResult CompareFunctions(HLSLTree tree, HLSLFunctionCall call, HLSLFunction function1, HLSLFunction function2)
    {
        var function1Ranks = new int[call.NumArguments];
        var function2Ranks = new int[call.NumArguments];

        bool function1Viable = GetFunctionCallCastRanks(tree, call, function1, function1Ranks);
        bool function2Viable = GetFunctionCallCastRanks(tree, call, function2, function2Ranks);

        // Both functions have to be viable to be able to compare them.
        if (!(function1Viable && function2Viable))
        {
            if (function1Viable)
            {
                return CompareFunctionsResult.Function1Better;
            }
            if (function2Viable)
            {
                return CompareFunctionsResult.Function2Better;
            }
            return CompareFunctionsResult.FunctionsEqual;
        }

        Array.Sort(function1Ranks, static (a, b) => b.CompareTo(a));
        Array.Sort(function2Ranks, static (a, b) => b.CompareTo(a));

        for (int i = 0; i < call.NumArguments; ++i)
        {
            if (function1Ranks[i] < function2Ranks[i])
            {
                return CompareFunctionsResult.Function1Better;
            }
            if (function2Ranks[i] < function1Ranks[i])
            {
                return CompareFunctionsResult.Function2Better;
            }
        }

        return CompareFunctionsResult.FunctionsEqual;
    }

    private static bool AreTypesEqual(HLSLTree tree, in HLSLType lhs, in HLSLType rhs)
    {
        return GetTypeCastRank(tree, lhs, rhs) == 0;
    }

    private static bool AreArgumentListsEqual(HLSLTree tree, HLSLArgument lhs, HLSLArgument rhs)
    {
        while (lhs != null && rhs != null)
        {
            if (!AreTypesEqual(tree, lhs.Type, rhs.Type))
            {
                return false;
            }
            if (lhs.Modifier != rhs.Modifier)
            {
                return false;
            }
            if (!String_.Equal(lhs.Semantic, rhs.Semantic) || !String_.Equal(lhs.SvSemantic, rhs.SvSemantic))
            {
                return false;
            }

            lhs = lhs.NextArgument;
            rhs = rhs.NextArgument;
        }

        return lhs == null && rhs == null;
    }

    internal static bool GetBinaryOpResultType(HLSLBinaryOp binaryOp, in HLSLType type1, in HLSLType type2, out HLSLType result)
    {
        result = new HLSLType();

        if (type1.BaseType < HLSLBaseType.FirstNumeric || type1.BaseType > HLSLBaseType.LastNumeric || type1.Array ||
            type2.BaseType < HLSLBaseType.FirstNumeric || type2.BaseType > HLSLBaseType.LastNumeric || type2.Array)
        {
            return false;
        }

        if (binaryOp == HLSLBinaryOp.BitAnd || binaryOp == HLSLBinaryOp.BitOr || binaryOp == HLSLBinaryOp.BitXor)
        {
            if (type1.BaseType < HLSLBaseType.FirstInteger || type1.BaseType > HLSLBaseType.LastInteger)
            {
                return false;
            }
        }

        switch (binaryOp)
        {
            case HLSLBinaryOp.And:
            case HLSLBinaryOp.Or:
            case HLSLBinaryOp.Less:
            case HLSLBinaryOp.Greater:
            case HLSLBinaryOp.LessEqual:
            case HLSLBinaryOp.GreaterEqual:
            case HLSLBinaryOp.Equal:
            case HLSLBinaryOp.NotEqual:
            {
                int numComponents = Math.Max(
                    BaseTypes.Descriptions[(int)type1.BaseType].NumComponents,
                    BaseTypes.Descriptions[(int)type2.BaseType].NumComponents);
                result.BaseType = (HLSLBaseType)((int)HLSLBaseType.Bool + numComponents - 1);
                break;
            }
            default:
                result.BaseType = ParserTables.BinaryOpTypeLookup[(int)type1.BaseType - (int)HLSLBaseType.FirstNumeric]
                                                                  [(int)type2.BaseType - (int)HLSLBaseType.FirstNumeric];
                break;
        }

        result.TypeName = null;
        result.Array = false;
        result.ArraySize = null;
        result.Flags = (type1.Flags & type2.Flags) & (int)HLSLTypeFlags.Const; // Propagate constness.

        return result.BaseType != HLSLBaseType.Unknown;
    }

    private void Error(string format, params object[] args)
    {
        _tokenizer.Error(CPrintf.Format(format, args));
    }
}
