namespace MilkDrop.Eval;

/// <summary>
/// 编译/执行上下文，对应 <c>prjm_eval_compiler_context_t</c>。
/// 持有函数表、变量表、本地/全局内存和全局寄存器 reg00~reg99。
/// </summary>
public sealed class CompileContext
{
    private static MemoryBuffer? _globalMemory;

    private readonly Dictionary<string, FunctionDef> _functions;
    private readonly Dictionary<string, Variable> _variables;

    /// <summary>
    /// 变量值的连续存储（<c>Variable</c> 只是它的句柄）。字节码 VM 直接按下标读写，
    /// 比逐对象访问缓存友好得多。扩容时会把已有变量重新指向新数组。
    /// </summary>
    private double[] _slots = new double[64];
    private int _slotCount;

    /// <summary>变量槽存储；供 <see cref="EvalVm"/> 使用。</summary>
    internal double[] Slots => _slots;

    /// <summary>全局内存（gmegabuf），所有上下文共享，对应 C 里的 static_global_memory。</summary>
    public static MemoryBuffer GlobalMemory => _globalMemory ??= new MemoryBuffer();

    /// <summary>本地内存（megabuf），每个上下文独有。</summary>
    public MemoryBuffer LocalMemory { get; } = new();

    /// <summary>全局寄存器 reg00~reg99，对应 C 里的 global_variables[100]。</summary>
    /// <remarks>
    /// 传入共享数组时，多个上下文共用同一份寄存器存储，与 C 版把同一个
    /// <c>global_variables</c> 指针传给所有上下文的行为一致。
    /// </remarks>
    public double[] GlobalRegisters { get; }

    public string? Error { get; set; }

    /// <summary>最近一次编译错误的起始行（1 起）。</summary>
    public int ErrorLine { get; set; }

    /// <summary>最近一次编译错误的起始列。</summary>
    public int ErrorColumnStart { get; set; }

    /// <summary>最近一次编译错误的结束列。</summary>
    public int ErrorColumnEnd { get; set; }

    /// <param name="globalRegisters">
    /// 可选的共享寄存器数组，长度必须为 100；为 null 时创建独立数组。
    /// </param>
    public CompileContext(double[]? globalRegisters = null)
    {
        GlobalRegisters = globalRegisters ?? new double[100];

        _functions = new Dictionary<string, FunctionDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var func in IntrinsicFunctions.CreateTable())
        {
            _functions[func.Name] = func;
        }
        _variables = new Dictionary<string, Variable>(StringComparer.OrdinalIgnoreCase);
    }

    public FunctionDef? FindFunction(string name) => _functions.GetValueOrDefault(name);

    public Variable? FindVariable(string name) => _variables.GetValueOrDefault(name);

    public Variable GetOrCreateVariable(string name)
    {
        if (_variables.TryGetValue(name, out var existing))
        {
            return existing;
        }
        if (_slotCount == _slots.Length)
        {
            GrowSlots();
        }

        var variable = new Variable(name, _slots, _slotCount);
        _slotCount++;
        _variables[name] = variable;
        return variable;
    }

    /// <summary>槽位数组扩容；已有变量句柄要重新指向新数组，否则读写会落到旧数组上。</summary>
    private void GrowSlots()
    {
        var slots = new double[_slots.Length * 2];
        Array.Copy(_slots, slots, _slotCount);

        foreach (var variable in _variables.Values)
        {
            variable.Storage = slots;
        }

        _slots = slots;
    }

    public void ResetVariables()
    {
        Array.Clear(_slots, 0, _slotCount);
    }

    /// <summary>
    /// 注册变量并返回其存储槽，对应 <c>prjm_eval_register_variable</c>。
    /// 名字形如 <c>regNN</c>（大小写不敏感）时返回全局寄存器槽，否则创建/复用普通变量。
    /// </summary>
    public VariableReference RegisterVariable(string name)
        => TryParseRegisterIndex(name, out int index)
            ? new VariableReference(GlobalRegisters, index)
            : new VariableReference(GetOrCreateVariable(name));

    /// <summary>为已注册的变量槽创建只读值节点。</summary>
    internal EvalNode VariableNode(VariableReference slot)
        => slot.IsRegister
            ? RegisterRef(slot.Index)
            : new EvalNode
            {
                Func = FuncOf("/*var*/"),
                Op = EvalOpCode.LoadVar,
                Slot = slot.Variable!.Slot,
                Var = slot.Variable,
                RefArray = slot.Variable.Storage,
                RefIndex = slot.Variable.Slot,
            };

    /// <summary>判断名字是否为 <c>regNN</c> 形式（5 个字符，前 3 个为 "reg"，后两位为数字）。</summary>
    private static bool TryParseRegisterIndex(string name, out int index)
    {
        index = 0;
        if (name.Length != 5
            || !name.AsSpan(0, 3).Equals("reg", StringComparison.OrdinalIgnoreCase)
            || !char.IsAsciiDigit(name[3])
            || !char.IsAsciiDigit(name[4]))
        {
            return false;
        }

        index = (name[3] - '0') * 10 + (name[4] - '0');
        return true;
    }

    // ---- 节点工厂 ----

    private EvalFunc FuncOf(string name) => _functions[name].Func;

    public EvalNode Constant(double value)
        => new() { Func = FuncOf("/*const*/"), Op = EvalOpCode.Const, Value = value };

    public EvalNode VariableRef(string name)
    {
        var variable = GetOrCreateVariable(name);
        return new EvalNode
        {
            Func = FuncOf("/*var*/"),
            Op = EvalOpCode.LoadVar,
            Slot = variable.Slot,
            Var = variable,
            RefArray = variable.Storage,
            RefIndex = variable.Slot,
        };
    }

    public EvalNode RegisterRef(int index)
        => new()
        {
            Func = FuncOf("/*var*/"),
            Op = EvalOpCode.LoadReg,
            Slot = index,
            RefArray = GlobalRegisters,
            RefIndex = index,
        };

    /// <summary>构建函数调用节点，自动为内存访问函数绑定本地/全局内存。</summary>
    public EvalNode Call(string name, params EvalNode[] args) => Call(_functions[name], args);

    /// <summary>按函数定义构建调用节点；内存绑定使用函数表中的规范名，避免大小写问题。</summary>
    public EvalNode Call(FunctionDef function, params EvalNode[] args)
    {
        var node = new EvalNode { Func = function.Func, Op = function.Op, Args = args };
        node.Memory = function.Name switch
        {
            "_gmem" or "gmegabuf" => GlobalMemory,
            "_mem" or "megabuf" or "freembuf" or "memcpy" or "memset" => LocalMemory,
            _ => null,
        };
        return node;
    }

    /// <summary>构建指令列表（<c>/*list*/</c>）节点。</summary>
    public EvalNode List(params EvalNode[] exprs)
    {
        var node = new EvalNode { Func = FuncOf("/*list*/"), Op = EvalOpCode.List };
        ListNode? head = null;
        for (int i = exprs.Length - 1; i >= 0; i--)
        {
            head = new ListNode { Expr = exprs[i], Next = head };
        }
        node.List = head;
        return node;
    }

    /// <summary>执行一棵节点树并返回最终数值。</summary>
    public double Evaluate(EvalNode node)
    {
        var ret = default(EvalResult);
        node.Eval(ref ret);
        return ret.Get();
    }

    /// <summary>
    /// 编译代码，对应 <c>projectm_eval_code_compile</c>。
    /// 失败时返回 null，并把错误信息写入 <see cref="Error"/> 与行列号属性。
    /// </summary>
    public EvalProgram? Compile(string code)
    {
        Error = null;
        ErrorLine = 0;
        ErrorColumnStart = 0;
        ErrorColumnEnd = 0;

        try
        {
            var parser = new Parser(this, code);
            return new EvalProgram(this, parser.ParseProgram());
        }
        catch (CompileException ex)
        {
            Error = ex.Message;
            ErrorLine = ex.Line;
            ErrorColumnStart = ex.Column;
            ErrorColumnEnd = ex.EndColumn;
            return null;
        }
    }
}
