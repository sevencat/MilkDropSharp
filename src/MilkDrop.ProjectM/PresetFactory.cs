using System.Diagnostics.CodeAnalysis;

namespace MilkDrop.ProjectM;

/// <summary>
/// 预设工厂：把一个文件或流变成 <see cref="Preset"/>。
/// </summary>
/// <remarks>对应 <c>PresetFactory.hpp:9-49</c> 与 <c>PresetFactory.cpp</c>。</remarks>
public abstract class PresetFactory
{
    /// <summary>
    /// 拆出 URL 里的协议段：<c>file:///x/y.milk</c> → (<c>file</c>, <c>/x/y.milk</c>)。
    /// 没有 <c>://</c> 时协议为空串，<paramref name="path"/> 原样返回。
    /// </summary>
    /// <remarks>对应 <c>PresetFactory::Protocol</c>。</remarks>
    public static string Protocol(string url, out string path)
    {
        ArgumentNullException.ThrowIfNull(url);

        int position = url.IndexOf("://", StringComparison.Ordinal);
        if (position < 0)
        {
            path = url;
            return string.Empty;
        }

        path = url[(position + 3)..];
        return url[..position];
    }

    /// <summary>从本地文件创建预设。</summary>
    public abstract Preset? LoadPresetFromFile(string filename);

    /// <summary>从数据流创建预设。</summary>
    public abstract Preset LoadPresetFromStream(Stream data);

    /// <summary>支持的扩展名（空格分隔）。</summary>
    public abstract string SupportedExtensions { get; }

    /// <summary>把 <c>"milk"</c>/<c>".milk"</c> 统一成扩展名判断用的小写串。</summary>
    [return: NotNullIfNotNull(nameof(extension))]
    protected static string? NormalizeExtension(string? extension)
        => extension?.TrimStart('.').ToLowerInvariant();
}
