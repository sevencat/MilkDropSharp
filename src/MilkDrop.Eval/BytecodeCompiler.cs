namespace MilkDrop.Eval;

/// <summary>
/// 把 <see cref="EvalNode"/> 表达式树降成 <see cref="EvalVm"/> 可执行的线性指令。
/// 只在编译期跑一次，因此这里用递归换取实现简单；热路径上跑的是降完的指令。
/// </summary>
internal static class BytecodeCompiler
{
    public static CompiledCode Compile(EvalNode program)
    {
        var emitter = new Emitter();
        emitter.Emit(program);
        return new CompiledCode(emitter.ToArray(), emitter.MaxStack);
    }

    private sealed class Emitter
    {
        private readonly List<Instr> _code = new();

        private int _depth;
        private int _max;

        public int MaxStack => _max;

        public Instr[] ToArray() => _code.ToArray();

        public void Emit(EvalNode node)
        {
            switch (node.Op)
            {
                case EvalOpCode.Const:
                    Emit(EvalOpCode.Const, 0, node.Value);
                    break;

                case EvalOpCode.LoadVar:
                    Emit(EvalOpCode.LoadVar, node.Slot);
                    break;

                case EvalOpCode.LoadReg:
                    Emit(EvalOpCode.LoadReg, node.Slot);
                    break;

                case EvalOpCode.List:
                    EmitList(node);
                    break;

                case EvalOpCode.LoadMega:
                case EvalOpCode.LoadGMega:
                    Emit(node.Args[0]!);
                    Emit(node.Op);
                    break;

                case EvalOpCode.If:
                    EmitIf(node);
                    break;

                case EvalOpCode.Loop:
                    EmitLoop(node);
                    break;

                case EvalOpCode.While:
                    EmitWhile(node);
                    break;

                case EvalOpCode.AndShort:
                case EvalOpCode.OrShort:
                    // _and / _or 是短路的，必须用跳转实现，不能把两个参数都压栈。
                    EmitShortCircuit(node, node.Op == EvalOpCode.OrShort);
                    break;

                case EvalOpCode.Exec2:
                    Emit(node.Args[0]!);
                    Emit(EvalOpCode.Pop);
                    Emit(node.Args[1]!);
                    break;

                case EvalOpCode.Exec3:
                    Emit(node.Args[0]!);
                    Emit(EvalOpCode.Pop);
                    Emit(node.Args[1]!);
                    Emit(EvalOpCode.Pop);
                    Emit(node.Args[2]!);
                    break;

                case EvalOpCode.FreeMemBuf:
                    // C 版是 no-op：只保留参数的求值结果（含引用）。
                    Emit(node.Args[0]!);
                    break;

                default:
                    // 普通函数：按序压参数，再发指令本身。
                    foreach (var arg in node.Args)
                    {
                        Emit(arg!);
                    }

                    Emit(node.Op);
                    break;
            }
        }

        private void EmitList(EvalNode node)
        {
            ListNode? item = node.List;
            if (item is null)
            {
                Emit(EvalOpCode.Const, 0, 0.0);
                return;
            }

            while (item.Next is not null)
            {
                Emit(item.Expr);
                Emit(EvalOpCode.Pop);
                item = item.Next;
            }

            Emit(item.Expr);
        }

        private void EmitIf(EvalNode node)
        {
            Emit(node.Args[0]!);
            int jumpElse = Emit(EvalOpCode.JzExact);
            Emit(node.Args[1]!);
            int jumpEnd = Emit(EvalOpCode.Jmp);
            int elseStart = _code.Count;
            Emit(node.Args[2]!);
            Patch(jumpElse, elseStart);
            Patch(jumpEnd, _code.Count);
        }

        private void EmitLoop(EvalNode node)
        {
            Emit(node.Args[0]!);
            int loopIndex = Emit(EvalOpCode.Loop);
            Emit(node.Args[1]!);
            Patch(loopIndex, _code.Count);
        }

        private void EmitWhile(EvalNode node)
        {
            int whileIndex = Emit(EvalOpCode.While);
            Emit(node.Args[0]!);
            Patch(whileIndex, _code.Count);
        }

        private void EmitShortCircuit(EvalNode node, bool isOr)
        {
            Emit(node.Args[0]!);
            int jump = Emit(isOr ? EvalOpCode.JnzPush1 : EvalOpCode.JzPush0);
            Emit(node.Args[1]!);
            Emit(EvalOpCode.ToBool);
            Patch(jump, _code.Count);
        }

        private int Emit(EvalOpCode op, int a = 0, double d = 0.0)
        {
            _code.Add(new Instr(op, a, d));
            Adjust(Delta(op));
            return _code.Count - 1;
        }

        private void Patch(int index, int target)
        {
            Instr instr = _code[index];
            _code[index] = new Instr(instr.Op, target, instr.D);
        }

        private void Adjust(int delta)
        {
            _depth += delta;
            if (_depth > _max)
            {
                _max = _depth;
            }
        }

        /// <summary>指令的栈深净变化。</summary>
        private static int Delta(EvalOpCode op) => op switch
        {
            EvalOpCode.Const or EvalOpCode.LoadVar or EvalOpCode.LoadReg => 1,
            EvalOpCode.Pop or EvalOpCode.JzExact => -1,
            EvalOpCode.MemCpy or EvalOpCode.MemSet => -2,
            EvalOpCode.Loop => -1,
            EvalOpCode.Add or EvalOpCode.Sub or EvalOpCode.Mul or EvalOpCode.Div or EvalOpCode.Mod
                or EvalOpCode.Pow or EvalOpCode.BOr or EvalOpCode.BAnd or EvalOpCode.Equal
                or EvalOpCode.NotEqual or EvalOpCode.Below or EvalOpCode.Above or EvalOpCode.BelowEq
                or EvalOpCode.AboveEq or EvalOpCode.AndFunc or EvalOpCode.OrFunc or EvalOpCode.Min
                or EvalOpCode.Max or EvalOpCode.Atan2 or EvalOpCode.Sigmoid or EvalOpCode.Set
                or EvalOpCode.AddOp or EvalOpCode.SubOp or EvalOpCode.MulOp or EvalOpCode.DivOp
                or EvalOpCode.ModOp or EvalOpCode.BOrOp or EvalOpCode.BAndOp or EvalOpCode.PowOp => -1,
            _ => 0,
        };
    }
}
