namespace MilkDrop.ProjectM;

/// <summary>预设工厂相关的错误（对应 <c>PresetFactoryException</c>）。</summary>
public sealed class PresetFactoryException : Exception
{
    public PresetFactoryException(string message)
        : base(message)
    {
    }

    public PresetFactoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 按扩展名把加载请求分派给对应的 <see cref="PresetFactory"/>。
/// </summary>
/// <remarks>对应 <c>PresetFactoryManager.hpp:31-90</c> 与 <c>.cpp</c>。</remarks>
public sealed class PresetFactoryManager
{
    /// <summary>扩展名 → 工厂（key 带前导点，例如 <c>.milk</c>）。</summary>
    private readonly Dictionary<string, PresetFactory> _factories = new(StringComparer.Ordinal);

    private readonly List<PresetFactory> _factoryList = [];

    /// <summary>登记内置工厂（目前只有 Milkdrop）。</summary>
    public void Initialize()
    {
        ClearFactories();

        var milkdrop = new MilkdropPreset.MilkdropPresetFactory();
        RegisterFactory(milkdrop.SupportedExtensions, milkdrop);
    }

    /// <summary>扩展名是否已注册（key 带前导点）。</summary>
    public bool ExtensionHandled(string extension) => _factories.ContainsKey(extension);

    /// <summary>已注册的扩展名列表。</summary>
    public IReadOnlyList<string> ExtensionsHandled() => _factories.Keys.ToArray();

    /// <summary>按扩展名取工厂。</summary>
    /// <exception cref="PresetFactoryException">扩展名没有对应工厂。</exception>
    public PresetFactory Factory(string extension)
    {
        if (!ExtensionHandled(extension))
        {
            throw new PresetFactoryException($"No preset factory associated with \"{extension}\".");
        }

        return _factories[extension];
    }

    /// <summary>按文件名/URL 载入预设。</summary>
    /// <exception cref="PresetFactoryException">没有对应工厂，或加载过程中抛了别的异常。</exception>
    public Preset CreatePresetFromFile(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);

        try
        {
            string extension = "." + ParseExtension(filename);
            return Factory(extension).LoadPresetFromFile(filename)
                   ?? throw new PresetFactoryException($"No preset loaded from \"{filename}\".");
        }
        catch (PresetFactoryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PresetFactoryException(ex.Message, ex);
        }
    }

    /// <summary>按扩展名从流载入预设。</summary>
    /// <exception cref="PresetFactoryException">没有对应工厂，或加载过程中抛了别的异常。</exception>
    public Preset CreatePresetFromStream(string extension, Stream data)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(data);

        try
        {
            return Factory(extension).LoadPresetFromStream(data);
        }
        catch (PresetFactoryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PresetFactoryException(ex.Message, ex);
        }
    }

    /// <summary>取小写扩展名（不含点）；没有扩展名时返回空串。</summary>
    /// <remarks>对应 <c>PresetFactoryManager::ParseExtension</c>。</remarks>
    private static string ParseExtension(string filename)
    {
        int start = filename.LastIndexOf('.');

        if (start < 0 || start >= filename.Length - 1)
        {
            return string.Empty;
        }

        return Utils.ToLower(filename[(start + 1)..]);
    }

    private void RegisterFactory(string extensions, PresetFactory factory)
    {
        _factoryList.Add(factory);

        foreach (string extension in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!_factories.TryAdd(extension, factory))
            {
                Console.Error.WriteLine(
                    $"[PresetFactoryManager] Warning: extension \"{extension}\" already has a factory. New factory handler ignored.");
            }
        }
    }

    private void ClearFactories()
    {
        _factories.Clear();
        _factoryList.Clear();
    }
}
