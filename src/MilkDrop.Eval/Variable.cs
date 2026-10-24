namespace MilkDrop.Eval;

/// <summary>
/// 一个可寻址的数值存储单元，对应 C 里的 <c>prjm_eval_variable_t</c>。
/// 变量值实际存放在 <see cref="CompileContext"/> 的槽位数组里，变量对象只是「名字 + 槽号」的句柄，
/// 这样字节码 VM 可以按整数下标直接读写，不用每次解引用对象。
/// </summary>
public sealed class Variable
{
    public string Name;

    /// <summary>所属上下文的槽位数组（同上下文的所有变量共用一块连续存储）。</summary>
    internal double[] Storage;

    /// <summary>在 <see cref="Storage"/> 里的下标。</summary>
    internal int Slot;

    public Variable(string name)
    {
        Name = name;
        Storage = new double[1];
        Slot = 0;
    }

    internal Variable(string name, double[] storage, int slot)
    {
        Name = name;
        Storage = storage;
        Slot = slot;
    }

    public double Value
    {
        get => Storage[Slot];
        set => Storage[Slot] = value;
    }
}
