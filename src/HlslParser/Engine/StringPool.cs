namespace HlslParser;

/// <summary>
/// Port of Engine/StringPool.cpp. C++ interns every string so that pointer
/// equality is equivalent to content equality; the dictionary reproduces that
/// (callers get the same instance for equal content).
/// </summary>
public sealed class StringPool
{
    private readonly List<string> _strings = new();
    private readonly Dictionary<string, string> _lookup = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Strings => _strings;

    public string AddString(string value)
    {
        if (value == null)
        {
            return null;
        }
        if (_lookup.TryGetValue(value, out string existing))
        {
            return existing;
        }
        _lookup.Add(value, value);
        _strings.Add(value);
        return value;
    }

    public string AddStringFormat(string format, params object[] args)
    {
        return AddString(CPrintf.Format(format, args));
    }

    public bool GetContainsString(string value)
    {
        return value != null && _lookup.ContainsKey(value);
    }
}
