using System.Text;
using System.Text.Json;

namespace MilkDrop.ShaderToy.App;

/// <summary>
/// 网络设置（目前只有代理），存在 <c>%APPDATA%\MilkDropShaderToy\settings.json</c>。
/// </summary>
/// <remarks>
/// <para>
/// 优先级：命令行 <c>--proxy</c> &gt; 环境变量 <c>MILKDROP_PROXY</c> &gt; 这个文件 &gt; 内置默认
/// （<see cref="DefaultProxy"/>，即本地 socks5）。命令行显式给 <c>none</c> 表示不走代理。
/// </para>
/// <para>
/// 只读写一个字段，用 <see cref="JsonDocument"/>/<see cref="Utf8JsonWriter"/>（AOT 友好）；
/// 文件坏了/没权限都退回默认值，不影响启动。
/// </para>
/// </remarks>
internal static class HttpSettings
{
    /// <summary>默认代理：本机 socks5（shadertoy 的资源要走它才通）。</summary>
    public const string DefaultProxy = "socks5://127.0.0.1:1080";

    /// <summary>设置文件路径。</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MilkDropShaderToy",
        "settings.json");

    /// <summary>读代理设置（缺失/损坏时返回 <see cref="DefaultProxy"/>）。</summary>
    public static string LoadProxy()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return DefaultProxy;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(FilePath));

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("proxy", out JsonElement proxy)
                && proxy.ValueKind == JsonValueKind.String)
            {
                return proxy.GetString() ?? DefaultProxy;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 设置文件坏了不值得让程序起不来
        }

        return DefaultProxy;
    }

    /// <summary>保存代理设置（空字符串写成 <c>none</c>，表示直连）。</summary>
    public static bool TrySaveProxy(string proxy, out string? error)
    {
        error = null;

        try
        {
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
                writer.WriteString("proxy", string.IsNullOrWhiteSpace(proxy) ? "none" : proxy.Trim());
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
}
