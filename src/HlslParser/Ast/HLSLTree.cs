namespace HlslParser;

/// <summary>
/// Abstract syntax tree for parsed HLSL code (port of <c>HLSLTree</c>).
///
/// The C++ version bump-allocates every node from pages and interns every
/// string; here nodes are plain GC objects and the string pool keeps the
/// interning semantics (equal content returns the same instance).
/// </summary>
public sealed class HLSLTree
{
    private readonly StringPool _stringPool = new();
    private readonly HLSLRoot _root;

    public HLSLTree()
        : this(null)
    {
    }

    public HLSLTree(Allocator allocator)
    {
        _root = AddNode<HLSLRoot>(null, 1);
    }

    /// <summary>Adds a string to the string pool used by the tree.</summary>
    public string AddString(string value)
    {
        return _stringPool.AddString(value);
    }

    public string AddStringFormat(string format, params object[] args)
    {
        return _stringPool.AddStringFormat(format, args);
    }

    /// <summary>Returns true if the string is contained within the tree.</summary>
    public bool GetContainsString(string value)
    {
        return _stringPool.GetContainsString(value);
    }

    /// <summary>Returns the root block in the tree.</summary>
    public HLSLRoot GetRoot()
    {
        return _root;
    }

    /// <summary>Adds a new node to the tree.</summary>
    public T AddNode<T>(string fileName, int line)
        where T : HLSLNode, new()
    {
        T node = new T();
        node.FileName = fileName;
        node.Line = line;
        return node;
    }

    // @@ This doesn't do any parameter matching. Simply returns the first
    // function with that name (same as C++).
    public HLSLFunction FindFunction(string name)
    {
        HLSLStatement statement = _root.Statement;
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

    public HLSLDeclaration FindGlobalDeclaration(string name)
    {
        return FindGlobalDeclaration(name, out _);
    }

    public HLSLDeclaration FindGlobalDeclaration(string name, out HLSLBuffer buffer)
    {
        buffer = null;
        HLSLStatement statement = _root.Statement;
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
            else if (statement.NodeType == HLSLNodeType.Buffer)
            {
                var currentBuffer = (HLSLBuffer)statement;

                HLSLDeclaration field = currentBuffer.Field;
                while (field != null)
                {
                    if (String_.Equal(name, field.Name))
                    {
                        buffer = currentBuffer;
                        return field;
                    }
                    field = (HLSLDeclaration)field.NextStatement;
                }
            }

            statement = statement.NextStatement;
        }
        return null;
    }

    public HLSLStruct FindGlobalStruct(string name)
    {
        HLSLStatement statement = _root.Statement;
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

    public HLSLTechnique FindTechnique(string name)
    {
        HLSLStatement statement = _root.Statement;
        while (statement != null)
        {
            if (statement.NodeType == HLSLNodeType.Technique)
            {
                var technique = (HLSLTechnique)statement;
                if (String_.Equal(name, technique.Name))
                {
                    return technique;
                }
            }
            statement = statement.NextStatement;
        }
        return null;
    }

    public HLSLPipeline FindFirstPipeline()
    {
        return FindNextPipeline(null);
    }

    public HLSLPipeline FindNextPipeline(HLSLPipeline current)
    {
        HLSLStatement statement = current != null ? (HLSLStatement)current : _root.Statement;
        while (statement != null)
        {
            if (statement.NodeType == HLSLNodeType.Pipeline)
            {
                return (HLSLPipeline)statement;
            }
            statement = statement.NextStatement;
        }
        return null;
    }

    public HLSLPipeline FindPipeline(string name)
    {
        HLSLStatement statement = _root.Statement;
        while (statement != null)
        {
            if (statement.NodeType == HLSLNodeType.Pipeline)
            {
                var pipeline = (HLSLPipeline)statement;
                if (String_.Equal(name, pipeline.Name))
                {
                    return pipeline;
                }
            }
            statement = statement.NextStatement;
        }
        return null;
    }

    public HLSLBuffer FindBuffer(string name)
    {
        HLSLStatement statement = _root.Statement;
        while (statement != null)
        {
            if (statement.NodeType == HLSLNodeType.Buffer)
            {
                var buffer = (HLSLBuffer)statement;
                if (String_.Equal(name, buffer.Name))
                {
                    return buffer;
                }
            }
            statement = statement.NextStatement;
        }
        return null;
    }

    public bool GetExpressionValue(HLSLExpression expression, out int value)
    {
        value = 0;

        // Expression must be constant.
        if ((expression.ExpressionType.Flags & (int)HLSLTypeFlags.Const) == 0)
        {
            return false;
        }

        // We are expecting an integer scalar. @@ Add support for type conversion from other scalar types.
        if (expression.ExpressionType.BaseType != HLSLBaseType.Int &&
            expression.ExpressionType.BaseType != HLSLBaseType.Bool)
        {
            return false;
        }

        if (expression.ExpressionType.Array)
        {
            return false;
        }

        if (expression.NodeType == HLSLNodeType.BinaryExpression)
        {
            var binaryExpression = (HLSLBinaryExpression)expression;

            if (!GetExpressionValue(binaryExpression.Expression1, out int value1) ||
                !GetExpressionValue(binaryExpression.Expression2, out int value2))
            {
                return false;
            }

            switch (binaryExpression.BinaryOp)
            {
                case HLSLBinaryOp.And: value = value1 != 0 && value2 != 0 ? 1 : 0; return true;
                case HLSLBinaryOp.Or: value = value1 != 0 || value2 != 0 ? 1 : 0; return true;
                case HLSLBinaryOp.Add: value = value1 + value2; return true;
                case HLSLBinaryOp.Sub: value = value1 - value2; return true;
                case HLSLBinaryOp.Mul: value = value1 * value2; return true;
                case HLSLBinaryOp.Div: value = value1 / value2; return true;
                case HLSLBinaryOp.Mod: value = value1 % value2; return true;
                case HLSLBinaryOp.Less: value = value1 < value2 ? 1 : 0; return true;
                case HLSLBinaryOp.Greater: value = value1 > value2 ? 1 : 0; return true;
                case HLSLBinaryOp.LessEqual: value = value1 <= value2 ? 1 : 0; return true;
                case HLSLBinaryOp.GreaterEqual: value = value1 >= value2 ? 1 : 0; return true;
                case HLSLBinaryOp.Equal: value = value1 == value2 ? 1 : 0; return true;
                case HLSLBinaryOp.NotEqual: value = value1 != value2 ? 1 : 0; return true;
                case HLSLBinaryOp.BitAnd: value = value1 & value2; return true;
                case HLSLBinaryOp.BitOr: value = value1 | value2; return true;
                case HLSLBinaryOp.BitXor: value = value1 ^ value2; return true;
                case HLSLBinaryOp.Assign:
                case HLSLBinaryOp.AddAssign:
                case HLSLBinaryOp.SubAssign:
                case HLSLBinaryOp.MulAssign:
                case HLSLBinaryOp.DivAssign:
                    // Not valid on non-constant expressions.
                    return false;
            }
        }
        else if (expression.NodeType == HLSLNodeType.UnaryExpression)
        {
            var unaryExpression = (HLSLUnaryExpression)expression;

            if (!GetExpressionValue(unaryExpression.Expression, out value))
            {
                return false;
            }

            switch (unaryExpression.UnaryOp)
            {
                case HLSLUnaryOp.Negative: value = -value; return true;
                case HLSLUnaryOp.Positive: return true;
                case HLSLUnaryOp.Not: value = value == 0 ? 1 : 0; return true;
                case HLSLUnaryOp.BitNot: value = ~value; return true;
                case HLSLUnaryOp.PostDecrement:
                case HLSLUnaryOp.PostIncrement:
                case HLSLUnaryOp.PreDecrement:
                case HLSLUnaryOp.PreIncrement:
                    return false;
            }
        }
        else if (expression.NodeType == HLSLNodeType.IdentifierExpression)
        {
            var identifier = (HLSLIdentifierExpression)expression;

            HLSLDeclaration declaration = FindGlobalDeclaration(identifier.Name);
            if (declaration == null)
            {
                return false;
            }
            if ((declaration.Type.Flags & (int)HLSLTypeFlags.Const) == 0)
            {
                return false;
            }
            if (declaration.Assignment == null)
            {
                // C++ would dereference NULL here; fail instead of crashing.
                return false;
            }

            return GetExpressionValue(declaration.Assignment, out value);
        }
        else if (expression.NodeType == HLSLNodeType.LiteralExpression)
        {
            var literal = (HLSLLiteralExpression)expression;

            if (literal.ExpressionType.BaseType == HLSLBaseType.Int)
            {
                value = literal.IValue;
            }
            else if (literal.ExpressionType.BaseType == HLSLBaseType.Bool)
            {
                value = literal.BValue ? 1 : 0;
            }
            else
            {
                return false;
            }

            return true;
        }

        return false;
    }

    /// <summary>Returns dimension, 0 if invalid.</summary>
    public int GetExpressionValue(HLSLExpression expression, float[] values)
    {
        // Expression must be constant.
        if ((expression.ExpressionType.Flags & (int)HLSLTypeFlags.Const) == 0)
        {
            return 0;
        }

        if (expression.ExpressionType.BaseType == HLSLBaseType.Int ||
            expression.ExpressionType.BaseType == HLSLBaseType.Bool)
        {
            if (GetExpressionValue(expression, out int intValue))
            {
                for (int i = 0; i < 4; ++i)
                {
                    values[i] = intValue; // @@ Warn if conversion is not exact.
                }
                return 1;
            }

            return 0;
        }
        if (expression.ExpressionType.BaseType >= HLSLBaseType.FirstInteger &&
            expression.ExpressionType.BaseType <= HLSLBaseType.LastInteger)
        {
            // @@ Add support for uints?
            // @@ Add support for int vectors?
            return 0;
        }
        if (expression.ExpressionType.BaseType > HLSLBaseType.LastNumeric)
        {
            return 0;
        }

        if (expression.ExpressionType.Array)
        {
            return 0;
        }

        if (expression.NodeType == HLSLNodeType.BinaryExpression)
        {
            var binaryExpression = (HLSLBinaryExpression)expression;
            int dim = GetVectorDimension(binaryExpression.ExpressionType);

            var values1 = new float[4];
            var values2 = new float[4];
            int dim1 = GetExpressionValue(binaryExpression.Expression1, values1);
            int dim2 = GetExpressionValue(binaryExpression.Expression2, values2);

            if (dim1 == 0 || dim2 == 0)
            {
                return 0;
            }

            if (dim1 != dim2)
            {
                // Broadcast scalar to vector size.
                if (dim1 == 1)
                {
                    for (int i = 1; i < dim2; ++i)
                    {
                        values1[i] = values1[0];
                    }
                    dim1 = dim2;
                }
                else if (dim2 == 1)
                {
                    for (int i = 1; i < dim1; ++i)
                    {
                        values2[i] = values2[0];
                    }
                    dim2 = dim1;
                }
                else
                {
                    return 0;
                }
            }

            switch (binaryExpression.BinaryOp)
            {
                case HLSLBinaryOp.Add:
                    for (int i = 0; i < dim; ++i) values[i] = values1[i] + values2[i];
                    return dim;
                case HLSLBinaryOp.Sub:
                    for (int i = 0; i < dim; ++i) values[i] = values1[i] - values2[i];
                    return dim;
                case HLSLBinaryOp.Mul:
                    for (int i = 0; i < dim; ++i) values[i] = values1[i] * values2[i];
                    return dim;
                case HLSLBinaryOp.Div:
                    for (int i = 0; i < dim; ++i) values[i] = values1[i] / values2[i];
                    return dim;
                case HLSLBinaryOp.Mod:
                    for (int i = 0; i < dim; ++i) values[i] = (int)values1[i] % (int)values2[i];
                    return dim;
                default:
                    return 0;
            }
        }
        else if (expression.NodeType == HLSLNodeType.UnaryExpression)
        {
            var unaryExpression = (HLSLUnaryExpression)expression;
            int dim = GetVectorDimension(unaryExpression.ExpressionType);

            int dim1 = GetExpressionValue(unaryExpression.Expression, values);
            if (dim1 == 0)
            {
                return 0;
            }

            switch (unaryExpression.UnaryOp)
            {
                case HLSLUnaryOp.Negative:
                    for (int i = 0; i < dim; ++i) values[i] = -values[i];
                    return dim;
                case HLSLUnaryOp.Positive:
                    return dim;
                default:
                    return 0;
            }
        }
        else if (expression.NodeType == HLSLNodeType.ConstructorExpression)
        {
            var constructor = (HLSLConstructorExpression)expression;

            int dim = GetVectorDimension(constructor.ExpressionType);

            int index = 0;
            HLSLExpression argument = constructor.Argument;
            while (argument != null)
            {
                var tmp = new float[4];
                int n = GetExpressionValue(argument, tmp);
                for (int i = 0; i < n; ++i)
                {
                    values[index + i] = tmp[i];
                }
                index += n;

                argument = argument.NextExpression;
            }

            return dim;
        }
        else if (expression.NodeType == HLSLNodeType.IdentifierExpression)
        {
            var identifier = (HLSLIdentifierExpression)expression;

            HLSLDeclaration declaration = FindGlobalDeclaration(identifier.Name);
            if (declaration == null)
            {
                return 0;
            }
            if ((declaration.Type.Flags & (int)HLSLTypeFlags.Const) == 0)
            {
                return 0;
            }
            if (declaration.Assignment == null)
            {
                return 0;
            }

            return GetExpressionValue(declaration.Assignment, values);
        }
        else if (expression.NodeType == HLSLNodeType.LiteralExpression)
        {
            var literal = (HLSLLiteralExpression)expression;

            if (literal.ExpressionType.BaseType == HLSLBaseType.Float)
            {
                values[0] = literal.FValue;
            }
            else if (literal.ExpressionType.BaseType == HLSLBaseType.Bool)
            {
                values[0] = literal.BValue ? 1.0f : 0.0f;
            }
            else if (literal.ExpressionType.BaseType == HLSLBaseType.Int)
            {
                values[0] = literal.IValue;
            }
            else
            {
                return 0;
            }

            return 1;
        }

        return 0;
    }

    /// <summary>Early outs on the string pool, then walks the tree for a call to <paramref name="name"/>.</summary>
    public bool NeedsFunction(string name)
    {
        if (!GetContainsString(name))
        {
            return false;
        }

        var visitor = new NeedsFunctionVisitor
        {
            Name = name,
        };
        visitor.VisitRoot(_root);
        return visitor.Result;
    }

    /// <summary>
    /// Renames uniforms that are written to inside a function into local
    /// variables (e.g. an assignment to a uniform gets a "new"-prefixed copy).
    /// </summary>
    public bool ReplaceUniformsAssignments()
    {
        var visitor = new ReplaceUniformsAssignmentsVisitor(this);
        visitor.VisitRoot(_root);
        return true;
    }

    public void EnumerateMatrixCtorsNeeded(List<MatrixCtor> matrixCtors)
    {
        var visitor = new EnumerateMatrixCtorsVisitor();
        visitor.VisitRoot(_root);
        matrixCtors.Clear();
        matrixCtors.AddRange(visitor.MatrixCtorsNeeded);
    }

    internal static int GetVectorDimension(in HLSLType type)
    {
        if (type.BaseType >= HLSLBaseType.FirstNumeric &&
            type.BaseType <= HLSLBaseType.LastNumeric)
        {
            if (type.BaseType == HLSLBaseType.Float) return 1;
            if (type.BaseType == HLSLBaseType.Float2) return 2;
            if (type.BaseType == HLSLBaseType.Float3) return 3;
            if (type.BaseType == HLSLBaseType.Float4) return 4;
        }
        return 0;
    }

    private sealed class NeedsFunctionVisitor : HLSLTreeVisitor
    {
        public string Name;
        public bool Result;

        public override void VisitTopLevelStatement(HLSLStatement node)
        {
            if (!node.Hidden)
            {
                base.VisitTopLevelStatement(node);
            }
        }

        public override void VisitFunctionCall(HLSLFunctionCall node)
        {
            Result = Result || (node.Function != null && String_.Equal(Name, node.Function.Name));
            base.VisitFunctionCall(node);
        }
    }

    private sealed class ReplaceUniformsAssignmentsVisitor : HLSLTreeVisitor
    {
        private readonly HLSLTree _tree;
        private readonly Dictionary<string, HLSLDeclaration> _uniforms = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, string> _uniformsReplaced = new(StringComparer.Ordinal);
        private bool _withinAssignment;

        public ReplaceUniformsAssignmentsVisitor(HLSLTree tree)
        {
            _tree = tree;
        }

        public override void VisitDeclaration(HLSLDeclaration node)
        {
            base.VisitDeclaration(node);

            // Enumerate uniforms.
            if ((node.Type.Flags & (int)HLSLTypeFlags.Uniform) != 0 && node.Name != null)
            {
                _uniforms[node.Name] = node;
            }
        }

        public override void VisitFunction(HLSLFunction node)
        {
            _uniformsReplaced.Clear();

            // Detect uniforms assignments.
            base.VisitFunction(node);

            // Declare uniforms replacements, in key order (std::map ordering).
            foreach (KeyValuePair<string, string> pair in _uniformsReplaced)
            {
                _uniforms.TryGetValue(pair.Key, out HLSLDeclaration uniformDeclaration);
                if (uniformDeclaration == null)
                {
                    continue;
                }

                HLSLDeclaration declaration = _tree.AddNode<HLSLDeclaration>(node.FileName, node.Line);
                declaration.Name = _tree.AddString(pair.Value);
                declaration.Type = uniformDeclaration.Type;

                // Add declaration within function statements.
                declaration.NextStatement = node.Statement;
                node.Statement = declaration;
            }
        }

        public override void VisitBinaryExpression(HLSLBinaryExpression node)
        {
            // Visit expression 2 first to not replace possible uniform reading.
            VisitExpression(node.Expression2);

            if (node.BinaryOp.IsAssignOp())
            {
                _withinAssignment = true;
            }

            VisitExpression(node.Expression1);

            _withinAssignment = false;
        }

        public override void VisitIdentifierExpression(HLSLIdentifierExpression node)
        {
            if (_withinAssignment)
            {
                // Check if variable is a uniform.
                if (node.Name != null && _uniforms.ContainsKey(node.Name))
                {
                    // Check if variable is not already replaced.
                    if (!_uniformsReplaced.ContainsKey(node.Name))
                    {
                        string newName = node.Name;
                        do
                        {
                            newName = "new" + newName;
                        }
                        while (_tree.GetContainsString(newName));

                        _uniformsReplaced[node.Name] = newName;
                    }
                }
            }

            // Check if variable needs to be replaced.
            if (node.Name != null && _uniformsReplaced.TryGetValue(node.Name, out string replacement))
            {
                node.Name = _tree.AddString(replacement);
            }
        }
    }

    private sealed class EnumerateMatrixCtorsVisitor : HLSLTreeVisitor
    {
        public readonly List<MatrixCtor> MatrixCtorsNeeded = new();

        public override void VisitConstructorExpression(HLSLConstructorExpression node)
        {
            if (BaseTypes.IsMatrixType(node.ExpressionType.BaseType))
            {
                MatrixCtor ctor = MatrixCtor.Build(node.ExpressionType, node.Argument);

                if (!MatrixCtorsNeeded.Contains(ctor))
                {
                    MatrixCtorsNeeded.Add(ctor);
                }
            }

            base.VisitConstructorExpression(node);
        }

        public override void VisitDeclaration(HLSLDeclaration node)
        {
            if (BaseTypes.IsMatrixType(node.Type.BaseType) &&
                (node.Type.Flags & (int)HLSLArgumentModifier.Uniform) == 0)
            {
                MatrixCtor ctor = MatrixCtor.Build(node.Type, node.Assignment);

                // No special constructor needed if it is already a matrix.
                bool matrixArgument = false;
                for (int i = 0; i < ctor.ArgumentTypes.Length; ++i)
                {
                    if (BaseTypes.IsMatrixType(ctor.ArgumentTypes[i]))
                    {
                        matrixArgument = true;
                        break;
                    }
                }

                if (!matrixArgument && !MatrixCtorsNeeded.Contains(ctor))
                {
                    MatrixCtorsNeeded.Add(ctor);
                }
            }

            base.VisitDeclaration(node);
        }
    }
}
