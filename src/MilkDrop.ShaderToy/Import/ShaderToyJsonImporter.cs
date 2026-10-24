using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MilkDrop.ShaderToy.Pass;

namespace MilkDrop.ShaderToy.Import;

/// <summary>
/// Shadertoy 官方导出 JSON（<c>ver</c> / <c>info</c> / <c>renderpass</c>）的导入器。
/// 语义对齐上游 <c>viewport.cpp load_shadertoy_json()</c>（945-1200 行），但只做解析，不碰 GPU。
/// </summary>
/// <remarks>
/// 用 <see cref="JsonDocument"/>（DOM）而不是反射式反序列化：AOT 下不需要源生成元数据，
/// 也方便容忍字段缺失/类型混用（见 <c>shaderToyIssue.md</c> S7-2）。
/// </remarks>
public static class ShaderToyJsonImporter
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64,
    };

    /// <summary>
    /// 被换行/空格拆开的两字符运算符：<c>&gt; =</c> / <c>&lt; =</c> / <c>= =</c> / <c>! =</c> / <c>&amp; &amp;</c> / <c>| |</c> …
    /// </summary>
    /// <remarks>
    /// 刻意不含 <c>+ +</c> / <c>- -</c>：那两个分开写是合法代码（<c>a - -b</c>），合并会改变语义。
    /// 也不含 <c>&lt;&lt;</c> / <c>&gt;&gt;</c>：位移极少被这样拆开，而误合并的代价更大。
    /// </remarks>
    private static readonly Regex BrokenOperatorGap =
        new(@"([<>=!&|+*/%-])[ \t\r\n]+(=|&|\|)", RegexOptions.Compiled);

    /// <summary>读文件并解析。UTF-8 解不出来时回退 Latin1（S7-4）。</summary>
    public static ShaderToyProject LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(ReadAllTextTolerant(path));
    }

    /// <summary>解析 JSON 文本；<see cref="ShaderToyProject.RawJson"/> 保存传入的原文。</summary>
    /// <exception cref="JsonException">不是合法 JSON。</exception>
    /// <exception cref="ShaderToyImportException">是合法 JSON 但不是 Shadertoy 工程。</exception>
    public static ShaderToyProject Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        // 有些工具会写 BOM，JsonDocument 不接受字符串里的 U+FEFF
        string text = json.TrimStart('\uFEFF');

        List<string> repairNotes = [];
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(text, DocumentOptions);
        }
        catch (JsonException)
        {
            // 真实踩到过的样本（res/shadertoy/stpreset/flGyWc.json）：从网页/编辑器粘来的 JSON
            // 长字符串被硬换行，字符串里出现裸 CR/LF，严格解析直接报
            // "'0x0D' is invalid within a JSON string"；同一份文件里还有被空白打断的 `\ /`。
            // 这两类损坏都不损失语义，修一下即可。
            string candidate = RepairMangledJson(text, out bool escapedControls, out bool rejoinedEscapes);
            if (!escapedControls && !rejoinedEscapes)
            {
                throw;
            }

            document = JsonDocument.Parse(candidate, DocumentOptions);

            if (escapedControls)
            {
                repairNotes.Add("JSON 字符串里含裸控制字符（硬换行/制表），已按上下文修复后解析。");
            }

            if (rejoinedEscapes)
            {
                repairNotes.Add("JSON 里有被空白打断的转义序列（如 \"\\ /\"），已修复后解析。");
            }
        }

        ShaderToyProject project;
        using (document)
        {
            project = ParseRoot(document.RootElement);
        }

        if (repairNotes.Count > 0)
        {
            project.Warnings.InsertRange(0, repairNotes);
        }

        project.RawJson = json;
        return project;
    }

    /// <summary>
    /// 修复"被编辑器/查看器硬换行搞坏"的 JSON：字符串内的裸控制字符转义，被打断的转义序列重新粘合。
    /// </summary>
    /// <remarks>
    /// 只在字符串字面量内部动手，并且尊重已有转义，所以不会破坏结构。
    /// 被硬换行的 GLSL 代码修好后只是"多了几个换行"，对 GLSL 无影响（空白不敏感）。
    /// </remarks>
    private static string RepairMangledJson(
        string json,
        out bool escapedControlChars,
        out bool rejoinedEscapes)
    {
        var builder = new StringBuilder(json.Length + 64);
        bool inString = false;
        bool atLineStart = true;      // 解码后当前逻辑行还没出现实义字符
        bool lineIsDirective = false; // 当前逻辑行以 # 开头（#define 之类不能被硬换行截断）

        escapedControlChars = false;
        rejoinedEscapes = false;

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];

            if (!inString)
            {
                if (c == '"')
                {
                    inString = true;
                    // 每个字符串字面量都当成一条新的逻辑行：我们只关心"这段代码/这个键是不是以 # 开头"，
                    // 而键值之间的括号逗号不影响这个判断。
                    atLineStart = true;
                    lineIsDirective = false;
                }

                builder.Append(c);
                continue;
            }

            if (c == '"')
            {
                inString = false;
                builder.Append(c);
                continue;
            }

            if (c == '\\')
            {
                // 被硬换行/空格打断的转义：\ + 空白 + 目标字符（真实文件里 "\/" 写成了 "\ /"）
                int lookahead = i + 1;
                bool hadWhitespace = false;

                while (lookahead < json.Length && char.IsWhiteSpace(json[lookahead]))
                {
                    lookahead++;
                    hadWhitespace = true;
                }

                if (hadWhitespace && lookahead < json.Length)
                {
                    char escapedChar = json[lookahead];
                    builder.Append('\\').Append(escapedChar);
                    rejoinedEscapes = true;
                    i = lookahead;
                    TrackLine(escapedChar);
                    continue;
                }

                if (i + 1 < json.Length)
                {
                    char escapedChar = json[i + 1];
                    builder.Append('\\').Append(escapedChar);
                    i++;
                    TrackLine(escapedChar);
                }

                continue;
            }

            if (c < ' ')
            {
                escapedControlChars = true;

                if (c is '\r' or '\n')
                {
                    // CRLF 只留一个换行
                    if (c == '\r' && i + 1 < json.Length && json[i + 1] == '\n')
                    {
                        i++;
                    }

                    if (lineIsDirective)
                    {
                        // 原始单行 JSON 里这里本来就没有换行，是编辑器/查看器折行加进来的：
                        // 折成空格，否则 #define 会被拦腰截断（flGyWc.json 就是这么坏的）
                        builder.Append(' ');
                        while (i + 1 < json.Length && (json[i + 1] == ' ' || json[i + 1] == '\t'))
                        {
                            i++;
                        }
                    }
                    else
                    {
                        builder.Append("\\n");
                        atLineStart = true;
                        lineIsDirective = false;
                    }

                    continue;
                }

                builder.Append(c == '\t' ? "\\t" : $"\\u{(int)c:x4}");
                continue;
            }

            builder.Append(c);
            TrackLine(c);
        }

        return builder.ToString();

        void TrackLine(char decoded)
        {
            if (decoded == '\n')
            {
                atLineStart = true;
                lineIsDirective = false;
                return;
            }

            if (!atLineStart || decoded is ' ' or '\t' or '\r')
            {
                return;
            }

            lineIsDirective = decoded == '#';
            atLineStart = false;
        }
    }

    /// <summary>不抛异常的版本，失败时给出原因（UI 里直接显示）。</summary>
    public static bool TryParse(
        string json,
        [NotNullWhen(true)] out ShaderToyProject? project,
        out string? error)
    {
        try
        {
            project = Parse(json);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ShaderToyImportException or ArgumentException)
        {
            project = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 把 JSON 里"字面 <c>\n</c>"还原成真实换行。上游对 <c>common</c> 与每个 pass 的 code 各做一次
    /// （<c>viewport.cpp:979</c>、<c>1168</c>）。
    /// </summary>
    /// <remarks>
    /// 不能简单 <c>Replace("\\n", "\n")</c>：GLSL 里偶尔出现转义过的 <c>\\n</c>（例如写在字符串里），
    /// 直接替换会把两个反斜杠吃掉一个。这里按"从左到右扫描、成对消费"处理。
    /// </remarks>
    public static string UnescapeSource(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\' || i + 1 >= text.Length)
            {
                builder.Append(c);
                continue;
            }

            char next = text[i + 1];
            switch (next)
            {
                case 'n':
                    builder.Append('\n');
                    i++;
                    break;
                case 'r':
                    builder.Append('\r');
                    i++;
                    break;
                case '\\':
                    // "\\" 原样保留（其后若是 'n' 也保持字面，属于用户自己写的转义）
                    builder.Append('\\').Append('\\');
                    i++;
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    private static ShaderToyProject ParseRoot(JsonElement element)
    {
        // Shadertoy 的 API 返回的是数组（[ { "ver": "0.1", ... } ]），站点导出的是单个对象，两种都认。
        JsonElement root = element;
        var warnings = new List<string>();

        while (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0)
            {
                throw new ShaderToyImportException("Shadertoy JSON 的根节点是空数组。");
            }

            if (root.GetArrayLength() > 1)
            {
                warnings.Add($"根节点是含 {root.GetArrayLength()} 个元素的数组，只取第一个工程。");
            }

            root = root[0];
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ShaderToyImportException("Shadertoy JSON 的根节点必须是对象（或只含一个对象的数组）。");
        }

        var project = new ShaderToyProject
        {
            Version = ReadString(root, "ver") ?? ReadString(root, "version") ?? string.Empty,
        };

        foreach (string warning in warnings)
        {
            project.Warnings.Add(warning);
        }

        if (TryGetProperty(root, "info", JsonValueKind.Object, out JsonElement info))
        {
            project.Metadata = ParseMetadata(info);
        }
        else
        {
            project.Warnings.Add("缺少 info 节，元信息为空。");
        }

        // 官方导出用 renderpass；个别工具/中间格式写 passes，两种都认
        if (!TryGetProperty(root, "renderpass", JsonValueKind.Array, out JsonElement passes) &&
            !TryGetProperty(root, "passes", JsonValueKind.Array, out passes))
        {
            project.Warnings.Add("缺少 renderpass / passes 数组，工程里没有 pass。");
            return project;
        }

        foreach (JsonElement passElement in passes.EnumerateArray())
        {
            ShaderPass? pass = ParsePass(passElement, project.Warnings);
            if (pass is not null)
            {
                project.Passes.Add(pass);
            }
        }

        if (project.ImagePass is null)
        {
            project.Warnings.Add("没有 image pass，渲染时不会输出到屏幕。");
        }

        return project;
    }

    private static ShaderToyMetadata ParseMetadata(JsonElement info)
    {
        var metadata = new ShaderToyMetadata
        {
            Id = NormalizeId(ReadString(info, "id")),
            Name = ReadString(info, "name") ?? string.Empty,
            // 官方导出没有作者字段；API 里叫 username，个别工具写 author/user
            Author = ReadString(info, "author") ?? ReadString(info, "username") ?? ReadString(info, "user")
                ?? string.Empty,
            Description = ReadString(info, "description") ?? string.Empty,
        };

        if (TryGetProperty(info, "tags", JsonValueKind.Array, out JsonElement tags))
        {
            foreach (JsonElement tag in tags.EnumerateArray())
            {
                if (tag.ValueKind == JsonValueKind.String)
                {
                    string value = tag.GetString() ?? string.Empty;
                    if (value.Length > 0)
                    {
                        metadata.Tags.Add(value);
                    }
                }
            }
        }

        return metadata;
    }

    private static ShaderPass? ParsePass(JsonElement element, List<string> warnings)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            warnings.Add("renderpass 里出现非对象元素，已跳过。");
            return null;
        }

        string typeText = ReadString(element, "type") ?? ReadString(element, "pass_type") ?? string.Empty;
        ShaderPassType? type = ParsePassType(typeText);
        if (type is null)
        {
            warnings.Add($"跳过未知 pass 类型 \"{typeText}\"。");
            return null;
        }

        // 真实导出里有 " Image" 这种带前导空格的名字
        string name = (ReadString(element, "name") ?? string.Empty).Trim();
        string code = UnescapeSource(ReadString(element, "code") ?? string.Empty);

        // 被硬换行拆开的运算符（"z>\n=-2.6"）在这里合并：换行已经还原成真换行了，
        // 在 JSON 文本上做匹配看不到这种跨行情况。
        string joinedCode = BrokenOperatorGap.Replace(code, "$1$2");
        if (joinedCode != code)
        {
            warnings.Add($"pass \"{name}\" 的代码里有两字符运算符被换行/空格拆开，已合并。");
            code = joinedCode;
        }

        var pass = new ShaderPass
        {
            Type = type.Value,
            Name = name,
            Description = (ReadString(element, "description") ?? string.Empty).Trim(),
            Code = code,
        };

        if (TryGetProperty(element, "outputs", JsonValueKind.Array, out JsonElement outputs))
        {
            foreach (JsonElement output in outputs.EnumerateArray())
            {
                if (output.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                pass.Outputs.Add(new ShaderPassOutput
                {
                    Id = NormalizeId(ReadString(output, "id")),
                    Channel = ReadInt(output, "channel"),
                });
            }
        }

        if (pass.Outputs.Count == 0 && type != ShaderPassType.Common)
        {
            warnings.Add($"pass \"{pass.Name}\"（{typeText}）没有 outputs，无法被其他 pass 引用。");
        }

        if (TryGetProperty(element, "inputs", JsonValueKind.Array, out JsonElement inputs))
        {
            foreach (JsonElement input in inputs.EnumerateArray())
            {
                ShaderPassInput? parsed = ParseInput(input, typeText, warnings);
                if (parsed is not null)
                {
                    pass.Inputs.Add(parsed);
                }
            }
        }

        return pass;
    }

    private static ShaderPassInput? ParseInput(JsonElement element, string passTypeText, List<string> warnings)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            warnings.Add("inputs 里出现非对象元素，已跳过。");
            return null;
        }

        int channel = ReadInt(element, "channel", -1);
        if (channel is < 0 or > 3)
        {
            warnings.Add($"pass \"{passTypeText}\" 的 input 通道号 {channel} 越界（只支持 0..3），已跳过。");
            return null;
        }

        // 官方导出是 type；老一些的是 ctype（viewport.cpp:1079-1086）
        string typeText = ReadString(element, "type") ?? ReadString(element, "ctype") ?? string.Empty;
        ShaderInputType inputType = ParseInputType(typeText);
        if (inputType == ShaderInputType.None)
        {
            warnings.Add($"通道 {channel} 的输入类型 \"{typeText}\" 不受支持，已跳过。");
            return null;
        }

        var input = new ShaderPassInput
        {
            Channel = channel,
            Type = inputType,
            Id = NormalizeId(ReadString(element, "id")),
            Sampler = ParseSampler(element),
        };

        input.Path = inputType switch
        {
            // volume 的 id 就是资源 id（viewport.cpp:1112-1114）
            ShaderInputType.Volume => string.IsNullOrEmpty(input.Id) ? null : input.Id,
            ShaderInputType.Texture or ShaderInputType.Cubemap =>
                ReadString(element, "filepath") ?? ReadString(element, "src"),
            _ => null,
        };

        if (inputType is ShaderInputType.Texture or ShaderInputType.Cubemap or ShaderInputType.Volume
            && string.IsNullOrEmpty(input.Path))
        {
            warnings.Add($"通道 {channel}（{typeText}）没有 filepath/src，加载时会退化成占位纹理。");
        }

        return input;
    }

    private static ShaderInputSampler ParseSampler(JsonElement input)
    {
        // 官方导出把 filter/wrap/vflip 放在 sampler 对象里；更老的格式是平铺在 input 上
        bool hasSamplerObject = TryGetProperty(input, "sampler", JsonValueKind.Object, out JsonElement sampler);
        JsonElement scope = hasSamplerObject ? sampler : input;

        var result = new ShaderInputSampler
        {
            Filter = ParseFilter(ReadString(scope, "filter")),
            Wrap = ParseWrap(ReadString(scope, "wrap")),
            // 两种写法都可能出现，任一为真即翻转
            VFlip = ReadBool(scope, "vflip") || (hasSamplerObject && ReadBool(input, "vflip")),
        };

        return result;
    }

    // 注意：真实导出里 type 可能是 " image"（前导空格），一律 Trim 后再比较
    private static ShaderPassType? ParsePassType(string text) => text.Trim().ToLowerInvariant() switch
    {
        "common" => ShaderPassType.Common,
        "image" => ShaderPassType.Image,
        "buffer" => ShaderPassType.Buffer,
        "cubemap" or "cube" => ShaderPassType.Cubemap,
        "sound" => ShaderPassType.Sound,
        _ => null,
    };

    private static ShaderInputType ParseInputType(string text) => text.Trim().ToLowerInvariant() switch
    {
        // 上游 type_json_to_glsl_image_type() 把 image/texture 一律当 2D（viewport.cpp:1029-1038）
        "texture" or "image" => ShaderInputType.Texture,
        "buffer" => ShaderInputType.Buffer,
        "cubemap" => ShaderInputType.Cubemap,
        "volume" => ShaderInputType.Volume,
        "keyboard" => ShaderInputType.Keyboard,
        "music" => ShaderInputType.Music,
        "musicstream" => ShaderInputType.MusicStream,
        _ => ShaderInputType.None,
    };

    private static SamplerFilter ParseFilter(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "nearest" => SamplerFilter.Nearest,
        "mipmap" => SamplerFilter.Mipmap,
        _ => SamplerFilter.Linear,
    };

    private static SamplerWrap ParseWrap(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "repeat" => SamplerWrap.Repeat,
        _ => SamplerWrap.Clamp,
    };

    private static bool TryGetProperty(JsonElement element, string name, JsonValueKind kind, out JsonElement value)
    {
        if (!TryGetAny(element, name, out value))
        {
            value = default;
            return false;
        }

        return value.ValueKind == kind;
    }

    /// <summary>
    /// 按名字找属性：先精确匹配，再退化到"去掉键名空白 + 忽略大小写"。
    /// </summary>
    /// <remarks>
    /// 真实踩到过的样本（<c>res/shadertoy/stpreset/flGyWc.json</c>）里 Sound pass 的键名被写成了
    /// <c>" name"</c> / <c>" type"</c> / <c>" inputs"</c>（引号内多一个前导空格），
    /// 精确匹配会把整个 pass 当成"未知类型"丢掉。键名本来就是数据、这里只查已知名字，所以放宽是安全的。
    /// </remarks>
    private static bool TryGetAny(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>读字符串，数字/布尔也接受（<c>id</c> 在导出里可能是数字）。</summary>
    private static string? ReadString(JsonElement element, string name)
    {
        if (!TryGetAny(element, name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    /// <summary>读整数，容忍 <c>"1"</c> / <c>1.0</c> / <c>"01"</c>。</summary>
    private static int ReadInt(JsonElement element, string name, int fallback = 0)
    {
        if (!TryGetAny(element, name, out JsonElement value))
        {
            return fallback;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt32(out int number))
                {
                    return number;
                }

                return value.TryGetDouble(out double real) && !double.IsNaN(real) && !double.IsInfinity(real)
                    ? (int)Math.Round(real)
                    : fallback;

            case JsonValueKind.String:
                string? text = value.GetString()?.Trim();
                return int.TryParse(text, out int parsed) ? parsed : fallback;

            default:
                return fallback;
        }
    }

    /// <summary>读布尔，容忍 <c>"true"</c> / <c>1</c>（老导出的 vflip 是字符串）。</summary>
    private static bool ReadBool(JsonElement element, string name)
    {
        if (!TryGetAny(element, name, out JsonElement value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetDouble(out double number) && Math.Abs(number) > double.Epsilon,
            JsonValueKind.String => bool.TryParse(value.GetString()?.Trim(), out bool parsed)
                ? parsed
                : string.Equals(value.GetString()?.Trim(), "1", StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// 上游用 <c>nlohmann::to_string(output["id"])</c> 再删掉引号（<c>viewport.cpp:959-961</c>），
    /// 这里等价处理：去掉可能残留的首尾引号。
    /// </summary>
    private static string NormalizeId(string? id) =>
        string.IsNullOrEmpty(id) ? string.Empty : id.Trim().Trim('"');

    private static string ReadAllTextTolerant(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);

        try
        {
            // 严格 UTF-8：遇到非法字节直接抛，交给下面的 Latin1 兜底（S7-4）
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
