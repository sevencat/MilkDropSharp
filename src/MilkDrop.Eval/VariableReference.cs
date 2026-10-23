namespace ProjectMEvalSharp;

/// <summary>
/// 变量槽句柄，等价于 C 里 <c>prjm_eval_register_variable</c> 返回的 <c>PRJM_EVAL_F*</c>。
/// 普通变量指向 <see cref="ProjectMEvalSharp.Variable"/> 对象，<c>regNN</c> 指向全局寄存器数组的某个元素。
/// 句柄是值类型，但内部只持有引用，因此复制后仍然指向同一存储。
/// </summary>
public readonly struct VariableReference
{
    private readonly Variable? _variable;
    private readonly double[]? _storage;
    private readonly int _index;

    internal VariableReference(Variable variable)
    {
        _variable = variable;
        _storage = null;
        _index = 0;
    }

    internal VariableReference(double[] storage, int index)
    {
        _variable = null;
        _storage = storage;
        _index = index;
    }

    /// <summary>是否为全局寄存器（<c>regNN</c>）槽。</summary>
    public bool IsRegister => _variable is null;

    /// <summary>普通变量对象；寄存器槽为 null。</summary>
    public Variable? Variable => _variable;

    /// <summary>寄存器下标；普通变量为 0。</summary>
    public int Index => _index;

    public double Value
    {
        get => _variable is not null ? _variable.Value : _storage![_index];
        set
        {
            if (_variable is not null)
            {
                _variable.Value = value;
            }
            else
            {
                _storage![_index] = value;
            }
        }
    }
}
