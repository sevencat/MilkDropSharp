namespace MilkDrop.Eval;

/// <summary>
/// 编译器节点工厂，移植 <c>CompilerFunctions.c</c> 中的
/// <c>prjm_eval_compiler_create_*</c> / <c>prjm_eval_compiler_add_instruction</c>。
/// 负责生成表达式树、做常量折叠，并维护指令列表的副作用标记。
/// </summary>
internal sealed class CompilerBuilder
{
    private readonly CompileContext _ctx;
    private readonly FunctionDef _constFunc;
    private readonly FunctionDef _varFunc;
    private readonly FunctionDef _listFunc;

    public CompilerBuilder(CompileContext ctx)
    {
        _ctx = ctx;
        _constFunc = ctx.FindFunction("/*const*/")!;
        _varFunc = ctx.FindFunction("/*var*/")!;
        _listFunc = ctx.FindFunction("/*list*/")!;
    }

    public CompilerNode CreateConstant(double value)
        => CompilerNode.FromFunction(_ctx.Constant(value), _constFunc);

    /// <summary>
    /// 创建变量节点。名字形如 <c>regNN</c>（大小写不敏感）时映射到全局寄存器数组，
    /// 对应 <c>prjm_eval_register_variable</c> 的特殊处理。
    /// </summary>
    public CompilerNode CreateVariable(string name)
        => CompilerNode.FromFunction(_ctx.VariableNode(_ctx.RegisterVariable(name)), _varFunc);

    /// <summary>创建函数调用节点，并按参数与函数自身的标记推导常量/副作用属性。</summary>
    public CompilerNode CreateExpression(FunctionDef function, IReadOnlyList<CompilerNode> args)
    {
        var argNodes = new EvalNode[args.Count];
        bool argsAreConstEvaluable = true;
        bool argsAreStateChanging = false;

        for (int i = 0; i < args.Count; i++)
        {
            argNodes[i] = args[i].TreeNode!;
            argsAreConstEvaluable = argsAreConstEvaluable && args[i].ListIsConstExpr;
            argsAreStateChanging = argsAreStateChanging || args[i].ListIsStateChanging;
        }

        var expr = _ctx.Call(function, argNodes);

        var node = new CompilerNode
        {
            Type = CompilerNodeType.Expression,
            TreeNode = expr,
            InstrIsConstExpr = argsAreConstEvaluable && function.IsConstEval,
            InstrIsStateChanging = argsAreStateChanging || function.IsStateChanging,
        };
        node.ListIsConstExpr = node.InstrIsConstExpr;
        node.ListIsStateChanging = node.InstrIsStateChanging;

        // 常量折叠：编译期直接求值，替换为 /*const*/ 节点。
        if (node.InstrIsConstExpr && !node.InstrIsStateChanging)
        {
            var ret = default(EvalResult);
            expr.Eval(ref ret);

            node.TreeNode = _ctx.Constant(ret.Get());
            node.InstrIsConstExpr = _constFunc.IsConstEval;
            node.InstrIsStateChanging = _constFunc.IsStateChanging;
            node.ListIsConstExpr = _constFunc.IsConstEval;
            node.ListIsStateChanging = _constFunc.IsStateChanging;
        }

        return node;
    }

    /// <summary>创建函数调用节点；参数个数不匹配时抛出编译错误。</summary>
    public CompilerNode CreateFunction(string name, FunctionDef function, IReadOnlyList<CompilerNode> args)
    {
        if (function.ArgCount != args.Count)
        {
            throw new CompileException(
                $"Invalid argument count for function \"{name}\": Expected {function.ArgCount}, but {args.Count} given.");
        }

        return CreateExpression(function, args);
    }

    /// <summary>
    /// 向指令列表追加一条指令，移植 <c>prjm_eval_compiler_add_instruction</c>。
    /// 无副作用的非末位表达式会被丢弃。
    /// </summary>
    public CompilerNode? AddInstruction(CompilerNode? list, CompilerNode instruction)
    {
        if (list is null)
        {
            return null;
        }

        var node = list;

        if (list.Type != CompilerNodeType.InstructionList)
        {
            // 前一条指令无副作用，其值也没有意义，直接丢弃。
            if (!list.InstrIsStateChanging)
            {
                return instruction;
            }

            var listTree = new EvalNode { Func = _listFunc.Func, Op = EvalOpCode.List };
            listTree.List = new ListNode { Expr = list.TreeNode!, Next = null };

            var newList = new CompilerNode
            {
                Type = CompilerNodeType.InstructionList,
                TreeNode = listTree,
                InstrIsConstExpr = list.InstrIsConstExpr,
                InstrIsStateChanging = list.InstrIsStateChanging,
                ListIsConstExpr = list.ListIsConstExpr,
                ListIsStateChanging = list.ListIsStateChanging,
            };

            node = newList;
        }

        var item = node.TreeNode!.List!;
        while (item.Next is not null)
        {
            // 列表中最后一条指令无副作用时同样可以丢弃。
            if (!node.InstrIsStateChanging && item.Next.Next is null)
            {
                item.Next = null;
                break;
            }

            item = item.Next;
        }

        item.Next = new ListNode { Expr = instruction.TreeNode!, Next = null };

        node.InstrIsConstExpr = instruction.ListIsConstExpr;
        node.InstrIsStateChanging = instruction.ListIsStateChanging;
        node.ListIsConstExpr = node.ListIsConstExpr && instruction.ListIsConstExpr;
        node.ListIsStateChanging = node.ListIsStateChanging || instruction.ListIsStateChanging;

        return node;
    }
}
