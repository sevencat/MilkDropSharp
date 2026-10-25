using System.Text;
using System.Text.Json;

namespace MilkDrop.ShaderToy.App;

/// <summary>
/// 应用设置：**exe 目录下的 <c>shadertoy.json</c>**。
/// 字段：<c>dataDir</c>（数据根目录）、<c>gpu</c>（用哪块显卡）、<c>proxy</c>（下载资源时的代理，当前只有写侧工具用）。
/// </summary>
/// <remarks>
/// <para>
/// 文件放在 exe 旁边（不是在 <c>%APPDATA%</c> 里）：整个程序目录拷走/打包时配置跟着走，
/// 也方便手工改。示例：
/// </para>
/// <code>
/// {
///   "dataDir": "F:\\code\\ShaderToySnap\\output",
///   "gpu": "nvidia",
///   "proxy": "socks5://127.0.0.1:1080"
/// }
/// </code>
/// <para>
/// <c>gpu</c> 的写法和命令行 <c>--gpu</c> 一致（编号 / 名字片段 / <c>luid:xxxx</c>），
/// 命令行给了就以命令行为准；都不给则用系统默认卡（跟随 Windows 图形设置）。
/// </para>
/// <para>
/// 数据目录优先级：命令行 <c>--data</c> &gt; 这个文件的 <c>dataDir</c> &gt; exe 旁的 <c>stdata</c>（见 <see cref="DataPaths"/>）。
/// 相对路径按 **exe 所在目录** 解析，方便随程序整体搬走。
/// </para>
/// <para>
/// 兼容：老版本把同样的内容放在 <c>%APPDATA%\MilkDropShaderToy\settings.json</c>，
/// 新位置的文件不存在时会去读那份（只读，不写回），免得一升级就"丢了数据目录"。
/// </para>
/// <para>
/// 只有几个字符串字段，用 <see cref="JsonDocument"/>/<see cref="Utf8JsonWriter"/>（AOT 友好）；
/// 文件坏了/没权限都退回默认值，不影响启动；写回时保留不认识的字段。
/// </para>
/// </remarks>
internal static class AppSettings
{
    /// <summary>默认代理：本机 socks5（shadertoy 的资源要走它才通）。</summary>
    public const string DefaultProxy = "socks5://127.0.0.1:1080";

    /// <summary>代理字段名。</summary>
    public const string ProxyKey = "proxy";

    /// <summary>数据根目录字段名。</summary>
    public const string DataDirKey = "dataDir";

    /// <summary>显卡选择字段名。</summary>
    public const string GpuKey = "gpu";

    /// <summary>设置文件名（放在 exe 目录下）。</summary>
    public const string FileName = "shadertoy.json";

    /// <summary>设置文件路径：<c>&lt;exe 目录&gt;\shadertoy.json</c>。</summary>
    public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>老版本的位置（只读兼容）：<c>%APPDATA%\MilkDropShaderToy\settings.json</c>。</summary>
    private static string LegacyFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MilkDropShaderToy",
        "settings.json");

    /// <summary>读代理设置（缺失/损坏时返回 <see cref="DefaultProxy"/>）。</summary>
    public static string LoadProxy() => Read(ProxyKey) ?? DefaultProxy;

    /// <summary>读数据根目录（没有配置时返回 null，由 <see cref="DataPaths"/> 走默认值）。</summary>
    public static string? LoadDataDir() => Read(DataDirKey);

    /// <summary>读显卡选择（编号 / 名字片段 / <c>luid:xxxx</c>；没配返回 null = 系统默认）。</summary>
    public static string? LoadGpu() => Read(GpuKey);

    /// <summary>保存代理设置（空字符串写成 <c>none</c>，表示直连）。</summary>
    public static bool TrySaveProxy(string proxy, out string? error) =>
        Write(ProxyKey, string.IsNullOrWhiteSpace(proxy) ? "none" : proxy.Trim(), out error);

    /// <summary>保存数据根目录。</summary>
    public static bool TrySaveDataDir(string directory, out string? error)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            error = "数据目录不能为空。";
            return false;
        }

        return Write(DataDirKey, directory.Trim(), out error);
    }

    /// <summary>读一个字符串字段（缺失/损坏/不是字符串都返回 null；新位置没有时读老位置）。</summary>
    private static string? Read(string key)
    {
        // exe 旁的 shadertoy.json 优先；没有就退回老版本放在 %APPDATA% 的那份
        string? value = ReadFrom(FilePath, key);
        return value ?? ReadFrom(LegacyFilePath, key);
    }

    private static string? ReadFrom(string path, string key)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(key, out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 设置文件坏了不值得让程序起不来
        }

        return null;
    }

    /// <summary>写一个字符串字段（其余字段原样保留）。</summary>
    private static bool Write(string key, string value, out string? error)
    {
        error = null;

        try
        {
            Dictionary<string, string> values = ReadAllStringValues();
            values[key] = value;

            string? directory = Path.GetDirectoryName(FilePath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                   {
                       Indented = true,
                       Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
            {
                writer.WriteStartObject();
                foreach ((string name, string text) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WriteString(name, text);
                }

                writer.WriteEndObject();
            }

            File.WriteAllText(
                FilePath,
                Encoding.UTF8.GetString(stream.ToArray()),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 现有设置里的所有字符串字段（写回时保留不认识的键）。新位置没有内容时读老位置，顺手完成迁移。
    /// </summary>
    private static Dictionary<string, string> ReadAllStringValues()
    {
        Dictionary<string, string> values = ReadRawValues(FilePath);
        return values.Count > 0 ? values : ReadRawValues(LegacyFilePath);
    }

    private static Dictionary<string, string> ReadRawValues(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            if (!File.Exists(path))
            {
                return values;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return values;
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } text)
                {
                    values[property.Name] = text;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 读不出来就当空文件重写（宁可丢字段，也不要起不来）
        }

        return values;
    }
}
