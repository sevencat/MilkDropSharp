namespace ProjectMEvalSharp;

/// <summary>
/// 一个可寻址的数值存储单元，对应 C 里的 <c>prjm_eval_variable_t</c>。
/// 变量是独立对象，因此对它持有的引用不会因为容器扩容而失效。
/// </summary>
public sealed class Variable
{
    public string Name;
    public double Value;

    public Variable(string name)
    {
        Name = name;
    }
}
