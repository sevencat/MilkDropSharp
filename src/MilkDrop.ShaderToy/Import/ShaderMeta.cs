using System.Globalization;
using System.Text.Json;

namespace MilkDrop.ShaderToy.Import;

/// <summary>
/// 下载时没拿到的资源（URL + 原因）。写进 <c>meta.json</c>，UI 里标"缺资源"。
/// </summary>
/// <param name="Url">原始下载地址。</param>
/// <param name="Error">失败原因（<c>404 Not Found</c> / 超时……）。</param>
public sealed record MissingResource(string Url, string? Error);

/// <summary>
/// 本地作品的私有元信息（<c>meta.json</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>project.json</c> 严格分工：<c>project.json</c> 是上游格式，只改写资源路径、其余原样保真；
/// 来源 URL、下载时间、收藏、最近播放这些"我们自己的东西"全部写在 meta 里
/// （见 <c>shaderToyIssue.md</c> S8-3）。
/// </para>
/// <para>
/// 读写都走 <see cref="JsonDocument"/>/<see cref="Utf8JsonWriter"/>，不依赖反射序列化（AOT，S7-2）。
/// 手工拷进来的作品没有 meta.json，扫描时按默认值显示、**不回写**（S8-6）。
/// </para>
/// </remarks>
public sealed class ShaderMeta
{
    /// <summary>文件名（作品目录下）。</summary>
    public const string FileName = "meta.json";

    /// <summary>Shadertoy 的短 ID（目录名以它的小写形式命名）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>标题（可能被用户改过名，所以不直接取 project.json 的 info.name）。</summary>
    public string Title { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    /// <summary>来源页面（<c>https://www.shadertoy.com/view/&lt;id&gt;</c>）。手工导入的作品为空。</summary>
    public string SourceUrl { get; set; } = string.Empty;

    public DateTimeOffset? DownloadedAt { get; set; }

    /// <summary>最近一次播放时间（列表按它排序）。</summary>
    public DateTimeOffset? LastPlayed { get; set; }

    public List<string> Tags { get; } = [];

    public bool Favorite { get; set; }

    /// <summary>上次下载时失败的资源。</summary>
    public List<MissingResource> MissingResources { get; } = [];

    /// <summary><paramref name="directory"/> 下的 meta.json 路径。</summary>
    public static string PathFor(string directory) => Path.Combine(directory, FileName);

    /// <summary>读目录下的 meta.json；不存在/坏掉返回 null（不影响作品本身）。</summary>
    public static ShaderMeta? TryLoad(string directory)
    {
        string path = PathFor(directory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return TryParse(File.ReadAllText(path), out ShaderMeta? meta) ? meta : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>解析 meta.json 文本。</summary>
    public static bool TryParse(string json, out ShaderMeta? meta)
    {
        meta = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var result = new ShaderMeta
            {
                Id = ReadString(root, "id") ?? string.Empty,
                Title = ReadString(root, "title") ?? ReadString(root, "name") ?? string.Empty,
                Author = ReadString(root, "author") ?? string.Empty,
                SourceUrl = ReadString(root, "sourceUrl") ?? string.Empty,
                DownloadedAt = ReadTime(root, "downloadedAt"),
                LastPlayed = ReadTime(root, "lastPlayed"),
                Favorite = ReadBool(root, "favorite"),
            };

            if (root.TryGetProperty("tags", out JsonElement tags) && tags.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement tag in tags.EnumerateArray())
                {
                    if (tag.ValueKind == JsonValueKind.String && tag.GetString() is { Length: > 0 } value)
                    {
                        result.Tags.Add(value);
                    }
                }
            }

            if (root.TryGetProperty("missingResources", out JsonElement missing) && missing.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in missing.EnumerateArray())
                {
                    string? url = item.ValueKind == JsonValueKind.String
                        ? item.GetString()
                        : ReadString(item, "url");

                    if (!string.IsNullOrEmpty(url))
                    {
                        result.MissingResources.Add(new MissingResource(
                            url,
                            ReadString(item, "error")));
                    }
                }
            }

            meta = result;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>序列化成 meta.json 的文本（缩进、UTF-8 友好）。</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               {
                   Indented = true,
                   // 标题/作者常有中文，别写成 \uXXXX（这个文件只给我们自己读，不必 HTML 安全转义）
                   Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            writer.WriteStartObject();
            writer.WriteString("id", Id);
            writer.WriteString("title", Title);
            writer.WriteString("author", Author);
            writer.WriteString("sourceUrl", SourceUrl);

            if (DownloadedAt is { } downloaded)
            {
                writer.WriteString("downloadedAt", downloaded.ToString("O", CultureInfo.InvariantCulture));
            }

            if (LastPlayed is { } played)
            {
                writer.WriteString("lastPlayed", played.ToString("O", CultureInfo.InvariantCulture));
            }

            writer.WriteBoolean("favorite", Favorite);

            writer.WriteStartArray("tags");
            foreach (string tag in Tags)
            {
                writer.WriteStringValue(tag);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("missingResources");
            foreach (MissingResource resource in MissingResources)
            {
                writer.WriteStartObject();
                writer.WriteString("url", resource.Url);
                if (resource.Error is { Length: > 0 } error)
                {
                    writer.WriteString("error", error);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>原子性够用的保存：先写临时文件再替换，避免半截 meta.json。</summary>
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = PathFor(directory);
        string temp = path + ".tmp";

        File.WriteAllText(temp, ToJson(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>记录一次播放（只更新 meta.json，不存在时由调用方决定是否创建）。</summary>
    public void MarkPlayed(DateTimeOffset now) => LastPlayed = now;

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? ReadTime(JsonElement element, string name)
    {
        string? text = ReadString(element, name);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : null;
    }
}
