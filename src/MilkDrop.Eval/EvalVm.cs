namespace MilkDrop.Eval;

/// <summary>
/// 栈式字节码解释器。语义与 <see cref="IntrinsicFunctions"/> 里的树节点版本一一对应，
/// 但把「递归遍历 + 委托分派 + <see cref="EvalResult"/> 结构传递」换成了
/// 线性指令 + <c>double[]</c> 值栈，热路径（逐顶点 per-pixel）上快得多。
/// </summary>
/// <remarks>
/// 不是线程安全的：值栈与槽位数组按程序复用，与既有的树解释器约定一致。
/// </remarks>
internal sealed class EvalVm
{
    private const double Eps = IntrinsicFunctions.CompareCloseFactor;
    private const long MaxLoop = IntrinsicFunctions.MaxLoopCount;

    // 引用（左值）编码：高 8 位是种类，低位是下标。0 表示纯值。
    private const int RefShift = 56;
    private const long RefVar = 1L << RefShift;
    private const long RefReg = 2L << RefShift;
    private const long RefMega = 3L << RefShift;
    private const long RefGMega = 4L << RefShift;
    private const long RefMask = 0x00FF_FFFF_FFFF_FFFF;

    private readonly Instr[] _code;
    private readonly double[] _stack;
    private readonly long[] _refs;
    private readonly CompileContext _ctx;

    private double[] _vars = Array.Empty<double>();
    private double[] _regs = Array.Empty<double>();
    private int _sp;

    public EvalVm(CompiledCode code, CompileContext ctx)
    {
        _code = code.Code;
        _stack = new double[code.MaxStack + 8];
        _refs = new long[_stack.Length];
        _ctx = ctx;
    }

    /// <summary>执行整个程序，返回顶层表达式的值。</summary>
    public double Execute()
    {
        _vars = _ctx.Slots;
        _regs = _ctx.GlobalRegisters;
        _sp = 0;
        return Run(0, _code.Length);
    }

    /// <summary>
    /// 执行 <c>[pc, end)</c> 区间的指令。该区间必须恰好净压入 1 个值；
    /// 返回该值并把栈恢复到进入时的位置。
    /// </summary>
    private double Run(int pc, int end)
    {
        int baseSp = _sp;

        while (pc < end)
        {
            Instr instr = _code[pc++];

            switch (instr.Op)
            {
                // ---- 取值 ----
                case EvalOpCode.Const:
                    Push(instr.D, 0);
                    break;

                case EvalOpCode.LoadVar:
                    Push(_vars[instr.A], RefVar | (uint)instr.A);
                    break;

                case EvalOpCode.LoadReg:
                    Push(_regs[instr.A], RefReg | (uint)instr.A);
                    break;

                case EvalOpCode.LoadMega:
                    LoadMem(_ctx.LocalMemory, RefMega);
                    break;

                case EvalOpCode.LoadGMega:
                    LoadMem(CompileContext.GlobalMemory, RefGMega);
                    break;

                case EvalOpCode.Pop:
                    _sp--;
                    break;

                // ---- 算术 ----
                case EvalOpCode.Add:
                {
                    double b = _stack[--_sp];
                    _stack[_sp - 1] = _stack[_sp - 1] + b;
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Sub:
                {
                    double b = _stack[--_sp];
                    _stack[_sp - 1] = _stack[_sp - 1] - b;
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Mul:
                {
                    double b = _stack[--_sp];
                    _stack[_sp - 1] = _stack[_sp - 1] * b;
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Div:
                {
                    double b = _stack[--_sp];
                    double a = _stack[_sp - 1];
                    _stack[_sp - 1] = Math.Abs(b) < Eps ? 0.0 : a / b;
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Mod:
                {
                    double b = _stack[--_sp];
                    double a = _stack[_sp - 1];
                    long divisor = (long)b;
                    _stack[_sp - 1] = divisor == 0 ? 0.0 : (double)((long)a % divisor);
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Pow:
                {
                    double b = _stack[--_sp];
                    double a = _stack[_sp - 1];
                    double r = Math.Abs(a) < Eps && b < 0 ? 0.0 : Math.Pow(a, b);
                    _stack[_sp - 1] = double.IsNaN(r) ? 0.0 : r;
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Neg:
                    _stack[_sp - 1] = -_stack[_sp - 1];
                    _refs[_sp - 1] = 0;
                    break;

                // ---- 位运算 ----
                case EvalOpCode.BOr:
                {
                    double b = _stack[--_sp];
                    _stack[_sp - 1] = (double)((long)_stack[_sp - 1] | (long)b);
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.BAnd:
                {
                    double b = _stack[--_sp];
                    _stack[_sp - 1] = (double)((long)_stack[_sp - 1] & (long)b);
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.BNot:
                    _stack[_sp - 1] = Math.Abs(_stack[_sp - 1]) < Eps ? 1.0 : 0.0;
                    _refs[_sp - 1] = 0;
                    break;

                // ---- 比较与逻辑 ----
                case EvalOpCode.Equal:
                {
                    double b = _stack[--_sp];
                    _stack[_sp - 1] = Math.Abs(_stack[_sp - 1] - b) < Eps ? 1.0 : 0.0;
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.NotEqual:
                {
                    double b = _stack[--_sp];
                    _stack[_sp - 1] = Math.Abs(_stack[_sp - 1] - b) > Eps ? 1.0 : 0.0;
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Below:
                {
                    double b = _stack[--_sp];
                    _stack[_sp - 1] = _stack[_sp - 1] < b ? 1.0 : 0.0;
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Above:
                {
                    double b = _stack[--_sp];
                    _stack[_sp - 1] = _stack[_sp - 1] > b ? 1.0 : 0.0;
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.BelowEq:
                {
                    double b = _stack[--_sp];
                    _stack[_sp - 1] = _stack[_sp - 1] <= b ? 1.0 : 0.0;
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.AboveEq:
                {
                    double b = _stack[--_sp];
                    _stack[_sp - 1] = _stack[_sp - 1] >= b ? 1.0 : 0.0;
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.ToBool:
                    _stack[_sp - 1] = Math.Abs(_stack[_sp - 1]) > Eps ? 1.0 : 0.0;
                    _refs[_sp - 1] = 0;
                    break;

                case EvalOpCode.AndFunc:
                {
                    double b = _stack[--_sp];
                    double a = _stack[_sp - 1];
                    _stack[_sp - 1] = Math.Abs(a) > Eps && Math.Abs(b) > Eps ? 1.0 : 0.0;
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.OrFunc:
                {
                    double b = _stack[--_sp];
                    double a = _stack[_sp - 1];
                    _stack[_sp - 1] = Math.Abs(a) > Eps || Math.Abs(b) > Eps ? 1.0 : 0.0;
                    _refs[_sp - 1] = 0;
                    break;
                }

                // ---- 数学 ----
                case EvalOpCode.Min:
                {
                    double b = _stack[--_sp];
                    double a = _stack[_sp - 1];
                    _stack[_sp - 1] = a < b ? a : b;
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Max:
                {
                    double b = _stack[--_sp];
                    double a = _stack[_sp - 1];
                    _stack[_sp - 1] = a > b ? a : b;
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Atan2:
                {
                    double b = _stack[--_sp];
                    _stack[_sp - 1] = Math.Atan2(_stack[_sp - 1], b);
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Sigmoid:
                {
                    double b = _stack[--_sp];
                    double t = 1.0 + Math.Exp(-_stack[_sp - 1] * b);
                    _stack[_sp - 1] = Math.Abs(t) > Eps ? 1.0 / t : 0.0;
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Sign:
                {
                    double v = _stack[_sp - 1];
                    _stack[_sp - 1] = v == 0 ? 0.0 : (v < 0 ? -1.0 : 1.0);
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Floor:
                    _stack[_sp - 1] = Math.Floor(_stack[_sp - 1]);
                    _refs[_sp - 1] = 0;
                    break;

                case EvalOpCode.Ceil:
                    _stack[_sp - 1] = Math.Ceiling(_stack[_sp - 1]);
                    _refs[_sp - 1] = 0;
                    break;

                case EvalOpCode.Abs:
                    _stack[_sp - 1] = Math.Abs(_stack[_sp - 1]);
                    _refs[_sp - 1] = 0;
                    break;

                case EvalOpCode.Sqr:
                    _stack[_sp - 1] = _stack[_sp - 1] * _stack[_sp - 1];
                    _refs[_sp - 1] = 0;
                    break;

                case EvalOpCode.Sqrt:
                    _stack[_sp - 1] = Math.Sqrt(Math.Abs(_stack[_sp - 1]));
                    _refs[_sp - 1] = 0;
                    break;

                case EvalOpCode.Exp:
                    _stack[_sp - 1] = Math.Exp(_stack[_sp - 1]);
                    _refs[_sp - 1] = 0;
                    break;

                case EvalOpCode.Log:
                {
                    double v = _stack[_sp - 1];
                    _stack[_sp - 1] = v <= 0.0 ? 0.0 : Math.Log(v);
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Log10:
                {
                    double v = _stack[_sp - 1];
                    _stack[_sp - 1] = v <= 0.0 ? 0.0 : Math.Log10(v);
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Sin:
                    _stack[_sp - 1] = Math.Sin(_stack[_sp - 1]);
                    _refs[_sp - 1] = 0;
                    break;

                case EvalOpCode.Cos:
                    _stack[_sp - 1] = Math.Cos(_stack[_sp - 1]);
                    _refs[_sp - 1] = 0;
                    break;

                case EvalOpCode.Tan:
                    _stack[_sp - 1] = Math.Tan(_stack[_sp - 1]);
                    _refs[_sp - 1] = 0;
                    break;

                case EvalOpCode.Asin:
                {
                    double v = _stack[_sp - 1];
                    _stack[_sp - 1] = v < -1.0 || v > 1.0 ? 0.0 : Math.Asin(v);
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Acos:
                {
                    double v = _stack[_sp - 1];
                    _stack[_sp - 1] = v < -1.0 || v > 1.0 ? 0.0 : Math.Acos(v);
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Atan:
                    _stack[_sp - 1] = Math.Atan(_stack[_sp - 1]);
                    _refs[_sp - 1] = 0;
                    break;

                case EvalOpCode.InvSqrt:
                {
                    double v = _stack[_sp - 1];
                    double half = v * 0.5;
                    ulong bits = 0x5fe6eb50c7b537a9UL - ((ulong)BitConverter.DoubleToInt64Bits(v) >> 1);
                    double y = BitConverter.Int64BitsToDouble((long)bits);
                    y *= 1.5 - half * y * y;
                    _stack[_sp - 1] = double.IsNaN(y) ? 0.0 : y;
                    _refs[_sp - 1] = 0;
                    break;
                }

                case EvalOpCode.Rand:
                {
                    double max = Math.Floor(_stack[_sp - 1]);
                    if (max < 1.0)
                    {
                        max = 1.0;
                    }

                    _stack[_sp - 1] = IntrinsicFunctions.Mt.Next() * (1.0 / 0xFFFFFFFF) * max;
                    _refs[_sp - 1] = 0;
                    break;
                }

                // ---- 赋值（栈顶右值，次栈顶左值）----
                case EvalOpCode.Set:
                {
                    double v = _stack[--_sp];
                    int i = _sp - 1;
                    WriteRef(_refs[i], v);
                    _stack[i] = v;
                    break;
                }

                case EvalOpCode.AddOp:
                {
                    double v = _stack[--_sp];
                    int i = _sp - 1;
                    double r = _stack[i] + v;
                    WriteRef(_refs[i], r);
                    _stack[i] = r;
                    break;
                }

                case EvalOpCode.SubOp:
                {
                    double v = _stack[--_sp];
                    int i = _sp - 1;
                    double r = _stack[i] - v;
                    WriteRef(_refs[i], r);
                    _stack[i] = r;
                    break;
                }

                case EvalOpCode.MulOp:
                {
                    double v = _stack[--_sp];
                    int i = _sp - 1;
                    double r = _stack[i] * v;
                    WriteRef(_refs[i], r);
                    _stack[i] = r;
                    break;
                }

                case EvalOpCode.DivOp:
                {
                    double v = _stack[--_sp];
                    int i = _sp - 1;
                    double r = Math.Abs(v) < Eps ? 0.0 : _stack[i] / v;
                    WriteRef(_refs[i], r);
                    _stack[i] = r;
                    break;
                }

                case EvalOpCode.ModOp:
                {
                    double v = _stack[--_sp];
                    int i = _sp - 1;
                    long divisor = (long)v;
                    double r = divisor == 0 ? 0.0 : (double)((long)_stack[i] % divisor);
                    WriteRef(_refs[i], r);
                    _stack[i] = r;
                    break;
                }

                case EvalOpCode.BOrOp:
                {
                    double v = _stack[--_sp];
                    int i = _sp - 1;
                    double r = (double)((long)_stack[i] | (long)v);
                    WriteRef(_refs[i], r);
                    _stack[i] = r;
                    break;
                }

                case EvalOpCode.BAndOp:
                {
                    double v = _stack[--_sp];
                    int i = _sp - 1;
                    double r = (double)((long)_stack[i] & (long)v);
                    WriteRef(_refs[i], r);
                    _stack[i] = r;
                    break;
                }

                case EvalOpCode.PowOp:
                {
                    double v = _stack[--_sp];
                    int i = _sp - 1;
                    double a = _stack[i];
                    double r = Math.Abs(a) < Eps && v < 0 ? 0.0 : Math.Pow(a, v);
                    if (double.IsNaN(r))
                    {
                        r = 0.0;
                    }

                    WriteRef(_refs[i], r);
                    _stack[i] = r;
                    break;
                }

                // ---- 内存 ----
                case EvalOpCode.MemCpy:
                {
                    int count = (int)(_stack[--_sp] + 0.0001);
                    int src = (int)(_stack[--_sp] + 0.0001);
                    int dest = (int)(_stack[--_sp] + 0.0001);
                    _ctx.LocalMemory.Copy(dest, src, count);
                    Push(dest, 0);
                    break;
                }

                case EvalOpCode.MemSet:
                {
                    int count = (int)(_stack[--_sp] + 0.0001);
                    double value = _stack[--_sp];
                    int dest = (int)(_stack[--_sp] + 0.0001);
                    _ctx.LocalMemory.Set(dest, value, count);
                    Push(dest, 0);
                    break;
                }

                // ---- 控制 ----
                case EvalOpCode.Loop:
                {
                    double n = _stack[--_sp];
                    long nref = _refs[_sp];
                    int bodyStart = pc;
                    int bodyEnd = instr.A;

                    long count = (long)n;
                    if (count > MaxLoop)
                    {
                        count = MaxLoop;
                    }

                    double r = n;
                    long rref = nref;

                    for (long i = 0; i < count; i++)
                    {
                        _sp = baseSp;
                        r = Run(bodyStart, bodyEnd);
                        rref = _refs[baseSp];
                    }

                    Push(r, rref);
                    pc = bodyEnd;
                    break;
                }

                case EvalOpCode.While:
                {
                    int bodyStart = pc;
                    int bodyEnd = instr.A;
                    long remaining = MaxLoop;
                    double r;
                    long rref;

                    do
                    {
                        _sp = baseSp;
                        r = Run(bodyStart, bodyEnd);
                        rref = _refs[baseSp];
                    } while (Math.Abs(r) > Eps && --remaining > 0);

                    Push(r, rref);
                    pc = bodyEnd;
                    break;
                }

                case EvalOpCode.Jmp:
                    pc = instr.A;
                    break;

                case EvalOpCode.JzExact:
                {
                    if (_stack[--_sp] == 0.0)
                    {
                        pc = instr.A;
                    }

                    break;
                }

                case EvalOpCode.JzPush0:
                {
                    double v = _stack[--_sp];
                    if (Math.Abs(v) <= Eps)
                    {
                        Push(0.0, 0);
                        pc = instr.A;
                    }

                    break;
                }

                case EvalOpCode.JnzPush1:
                {
                    double v = _stack[--_sp];
                    if (Math.Abs(v) > Eps)
                    {
                        Push(1.0, 0);
                        pc = instr.A;
                    }

                    break;
                }

                default:
                    break;
            }
        }

        double result = _sp > baseSp ? _stack[_sp - 1] : 0.0;
        _sp = baseSp;
        return result;
    }

    private void Push(double value, long reference)
    {
        _stack[_sp] = value;
        _refs[_sp] = reference;
        _sp++;
    }

    /// <summary>megabuf / gmegabuf 取值：栈顶是下标，原地换成元素值（并带上引用）。</summary>
    private void LoadMem(MemoryBuffer memory, long kind)
    {
        int top = _sp - 1;
        int index = (int)(_stack[top] + 0.0001);

        var address = memory.Address(index);
        if (address is null)
        {
            _stack[top] = 0.0;
            _refs[top] = 0;
            return;
        }

        var (array, offset) = address.Value;
        _stack[top] = array[offset];
        _refs[top] = kind | (uint)index;
    }

    private void WriteRef(long reference, double value)
    {
        switch (reference >> RefShift)
        {
            case 1:
                _vars[(int)(reference & RefMask)] = value;
                break;
            case 2:
                _regs[(int)(reference & RefMask)] = value;
                break;
            case 3:
                _ctx.LocalMemory[(int)(reference & RefMask)] = value;
                break;
            case 4:
                CompileContext.GlobalMemory[(int)(reference & RefMask)] = value;
                break;
            default:
                break;
        }
    }
}
