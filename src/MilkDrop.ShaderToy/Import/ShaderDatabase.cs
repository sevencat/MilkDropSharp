using System.Globalization;
using MilkDrop.ShaderToy.Pass;

namespace MilkDrop.ShaderToy.Import;

/// <summary><c>shaders</c> 表的一行（列表用，只含"名字那几列"）。</summary>
/// <param name="Id">Shadertoy 短 ID（<c>ShaderId</c>）。</param>
/// <param name="Title"><c>Name</c>。</param>
/// <param name="Author"><c>UserName</c>。</param>
/// <param name="CapturedAt"><c>CapturedAt</c>（列表排序用）。</param>
/// <remarks>
/// 列表只要"能显示、能排序"的列：pass 数、缺图、标签这些细节不在这里查，
/// 等真正打开作品时再由 <see cref="ShaderDatabase.TryReadDetails"/> / <see cref="ShaderDatabase.TryLoadProject"/> 去取。
/// </remarks>
public sealed record ShaderDatabaseRecord(
    string Id,
    string Title,
    string Author,
    DateTime CapturedAt);

/// <summary>作品的细节（列表里不查，打开时才查）。</summary>
/// <param name="PassCount"><c>renderpasses</c> 里的 pass 数。</param>
/// <param name="MissingMedia">还没下到本地的图片资源数（<c>LocalFilePath</c> 为空）。</param>
/// <param name="Tags"><c>Tags</c>（逗号分隔切成数组）。</param>
public sealed record ShaderDatabaseDetails(
    int PassCount,
    int MissingMedia,
    IReadOnlyList<string> Tags);

/// <summary>
/// 只读访问 ShaderToySnap 抓下来的 <c>shadertoy.db</c>：作品列表 + 完整工程。
/// </summary>
/// <remarks>
/// <para>
/// 表结构（见 ShaderToySnap 的 <c>Data/Entities.cs</c>）：
/// <c>shaders</c>（info 节 + <c>RawJson</c>）、<c>renderpasses</c>（各 pass 的代码）、
/// <c>shader_inputs</c> / <c>shader_outputs</c>（通道绑定）。
/// </para>
/// <para>
/// 工程优先用 <c>shaders.RawJson</c>（就是 shadertoy 导出的原文，<see cref="ShaderToyJsonImporter"/>
/// 能直接吃）；原文缺失/坏掉时退回按 <c>renderpasses</c> + inputs/outputs 三张表重建。
/// 两个来源都在，所以**pass 代码以 <c>renderpasses.Code</c> 为准**（在库里改过代码能立刻生效）。
/// </para>
/// <para>
/// 只读：不写库、不改别人的表结构。图片路径原样保留站点形式（<c>/media/a/x.png</c>），
/// 由管线在数据根的 <c>media/</c> 目录里找。
/// </para>
/// </remarks>
public sealed class ShaderDatabase : IDisposable
{
    /// <summary>数据库文件名（放在数据根目录下）。</summary>
    public const string FileName = "shadertoy.db";

    private readonly SqliteDatabase? _sqlite;

    /// <param name="path"><c>shadertoy.db</c> 的路径。</param>
    public ShaderDatabase(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        _sqlite = SqliteDatabase.TryOpen(Path, create: false, out string? error);
        Error = error;
    }

    /// <summary>数据库文件的完整路径。</summary>
    public string Path { get; }

    /// <summary>是否成功打开。</summary>
    public bool IsOpen => _sqlite is not null;

    /// <summary>打不开的原因。</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// 读作品列表（按 <c>Id</c> 倒序，即最近抓的在前）—— **只查名字那几列**。
    /// </summary>
    /// <remarks>
    /// 列表在打开程序时就要出来，所以这里不碰 <c>renderpasses</c> / <c>shader_inputs</c>
    /// （不数 pass、不数缺图、不读标签、不读代码），也就不会因为表很大而卡住。
    /// </remarks>
    public List<ShaderDatabaseRecord> ReadShaders()
    {
        var records = new List<ShaderDatabaseRecord>();

        if (_sqlite is null)
        {
            return records;
        }

        List<string?[]> rows = _sqlite.Query(SqlShaders);

        if (_sqlite.LastError is { Length: > 0 } error)
        {
            Error = error;
        }

        foreach (string?[] row in rows)
        {
            string id = row[0] ?? string.Empty;
            if (id.Length == 0)
            {
                continue;
            }

            records.Add(new ShaderDatabaseRecord(
                id,
                row[1] ?? string.Empty,
                row[2] ?? string.Empty,
                ParseTime(row[3])));
        }

        return records;
    }

    /// <summary>
    /// 读一个作品的细节（pass 数 / 缺图数 / 标签）—— 打开作品时才调，列表不调。
    /// </summary>
    /// <returns>ID 不存在时返回 null。</returns>
    public ShaderDatabaseDetails? TryReadDetails(string shaderId)
    {
        if (_sqlite is null)
        {
            return null;
        }

        List<string?[]> rows = _sqlite.Query(SqlShaderDetails, shaderId);

        if (rows.Count == 0)
        {
            return null;
        }

        string?[] row = rows[0];

        return new ShaderDatabaseDetails(
            ParseInt(row[1]),
            ParseInt(row[2]),
            SplitTags(row[0]));
    }

    /// <summary>
    /// 读出作品各 pass 的类型与代码（<c>PassIndex</c> → (type, code)），用于覆盖 <c>RawJson</c> 里的代码。
    /// </summary>
    public Dictionary<int, (ShaderPassType Type, string Code)> ReadPassCode(string shaderId)
    {
        var result = new Dictionary<int, (ShaderPassType Type, string Code)>();

        if (_sqlite is null)
        {
            return result;
        }

        foreach (string?[] row in _sqlite.Query(SqlPassCode, shaderId))
        {
            string code = row[2] ?? string.Empty;
            if (code.Length > 0)
            {
                result[ParseInt(row[0])] = (ParsePassType(row[1]), code);
            }
        }

        return result;
    }

    /// <summary>
    /// 从库里读出一个完整工程。
    /// </summary>
    /// <returns>读不到（ID 不存在 / JSON 坏了 / 表缺数据）时返回 null，原因写进 <paramref name="error"/>。</returns>
    public ShaderToyProject? TryLoadProject(string shaderId, out string? error)
    {
        error = null;

        if (_sqlite is null)
        {
            error = Error ?? "数据库没有打开。";
            return null;
        }

        List<string?[]> rows = _sqlite.Query(SqlShaderRow, shaderId);

        if (rows.Count == 0)
        {
            error = $"数据库里没有作品 {shaderId}。";
            return null;
        }

        string?[] shader = rows[0];
        string rawJson = shader[0] ?? string.Empty;

        ShaderToyProject? project = null;

        if (rawJson.Length > 0)
        {
            if (ShaderToyJsonImporter.TryParse(rawJson, out ShaderToyProject? parsed, out string? parseError))
            {
                project = parsed;
            }
            else
            {
                // 原文坏了不至于播不了：退回按表重建，原因当提示带上
                error = $"raw_json 解析失败（{parseError}），改按表重建。";
            }
        }

        if (project is null)
        {
            project = BuildFromTables(shaderId, shader, out string? buildError);

            if (project is null)
            {
                error = buildError ?? error ?? "工程数据不完整。";
                return null;
            }
        }

        ApplyPassCodeFromTables(project, shaderId);
        return project;
    }

    public void Dispose() => _sqlite?.Dispose();

    /// <summary>执行一段 SQL（建库/测试用；正常播放路径不会写库）。</summary>
    public static bool TryExecute(string path, string sql, out string? error)
    {
        error = null;

        using SqliteDatabase? database = SqliteDatabase.TryOpen(path, create: true, out error);
        if (database is null)
        {
            return false;
        }

        if (database.Execute(sql))
        {
            return true;
        }

        error = database.LastError;
        return false;
    }

    private const string SqlShaders = """
        select s.ShaderId, coalesce(s.Name, ''), coalesce(s.UserName, ''), s.CapturedAt
          from shaders s
         order by s.Id desc
        """;

    private const string SqlShaderDetails = """
        select coalesce(s.Tags, ''),
               (select count(*) from renderpasses r where r.ShaderId = s.ShaderId),
               (select count(*) from shader_inputs i
                 where i.ShaderId = s.ShaderId
                   and lower(coalesce(i.Type, '')) in ('texture', 'cubemap', 'cube', 'volume')
                   and coalesce(trim(i.FilePath), '') <> ''
                   and coalesce(trim(i.LocalFilePath), '') = '')
          from shaders s
         where s.ShaderId = ?
         limit 1
        """;

    private const string SqlShaderRow = """
        select coalesce(s.RawJson, ''), coalesce(s.Ver, ''), coalesce(s.Name, ''),
               coalesce(s.UserName, ''), coalesce(s.Description, ''), coalesce(s.Tags, '')
          from shaders s
         where s.ShaderId = ?
         limit 1
        """;

    private const string SqlPassCode = """
        select PassIndex, coalesce(Type, ''), coalesce(Code, '')
          from renderpasses
         where ShaderId = ?
         order by PassIndex
        """;

    private const string SqlPasses = """
        select PassIndex, coalesce(Name, ''), coalesce(Description, ''), coalesce(Type, ''), coalesce(Code, '')
          from renderpasses
         where ShaderId = ?
         order by PassIndex
        """;

    private const string SqlInputs = """
        select PassIndex, Channel, coalesce(InputId, ''), coalesce(Type, ''), coalesce(FilePath, ''),
               coalesce(Filter, ''), coalesce(Wrap, ''), coalesce(VFlip, '')
          from shader_inputs
         where ShaderId = ?
         order by PassIndex, Channel
        """;

    private const string SqlOutputs = """
        select PassIndex, Channel, coalesce(OutputId, '')
          from shader_outputs
         where ShaderId = ?
         order by PassIndex, Channel
        """;

    /// <summary>
    /// <c>RawJson</c> 不能用时：按 <c>renderpasses</c> + inputs/outputs 重建工程。
    /// </summary>
    private ShaderToyProject? BuildFromTables(string shaderId, string?[] shader, out string? error)
    {
        error = null;

        if (_sqlite is null)
        {
            error = "数据库没有打开。";
            return null;
        }

        List<string?[]> passes = _sqlite.Query(SqlPasses, shaderId);
        if (passes.Count == 0)
        {
            error = $"作品 {shaderId} 在 renderpasses 里没有记录。";
            return null;
        }

        var project = new ShaderToyProject { Version = shader[1] ?? string.Empty };
        project.Metadata.Id = shaderId;
        project.Metadata.Name = shader[2] ?? string.Empty;
        project.Metadata.Author = shader[3] ?? string.Empty;
        project.Metadata.Description = shader[4] ?? string.Empty;

        foreach (string tag in SplitTags(shader[5]))
        {
            project.Metadata.Tags.Add(tag);
        }

        var inputsByPass = GroupByPass(_sqlite.Query(SqlInputs, shaderId));
        var outputsByPass = GroupByPass(_sqlite.Query(SqlOutputs, shaderId));

        foreach (string?[] row in passes)
        {
            int index = ParseInt(row[0]);

            var pass = new ShaderPass
            {
                Type = ParsePassType(row[3]),
                Name = row[1] ?? string.Empty,
                Description = row[2] ?? string.Empty,
                Code = row[4] ?? string.Empty,
            };

            if (outputsByPass.TryGetValue(index, out List<string?[]>? outputs) && outputs is not null)
            {
                foreach (string?[] output in outputs)
                {
                    pass.Outputs.Add(new ShaderPassOutput
                    {
                        Id = output[2] ?? string.Empty,
                        Channel = ParseInt(output[1]),
                    });
                }
            }

            if (inputsByPass.TryGetValue(index, out List<string?[]>? inputs) && inputs is not null)
            {
                foreach (string?[] input in inputs)
                {
                    pass.Inputs.Add(BuildInput(input));
                }
            }

            project.Passes.Add(pass);
        }

        project.Warnings.Add("工程 JSON 不可用，已按数据库表重建（字段比原文少，属正常）。");
        return project;
    }

    private static ShaderPassInput BuildInput(string?[] row)
    {
        ShaderInputType type = ParseInputType(row[3]);
        string inputId = row[2] ?? string.Empty;
        string filePath = row[4] ?? string.Empty;

        var input = new ShaderPassInput
        {
            Channel = ParseInt(row[1]),
            Type = type,
        };

        switch (type)
        {
            case ShaderInputType.Buffer:
                // buffer 引用的是别的 pass（FilePath 里那行是站点预览图，不参与采样）
                input.Id = inputId;
                break;

            case ShaderInputType.Texture:
            case ShaderInputType.Cubemap:
                input.Path = filePath.Length > 0 ? filePath : null;
                input.Id = inputId.Length > 0 ? inputId : null;
                break;

            case ShaderInputType.Volume:
                input.Id = inputId.Length > 0 ? inputId : null;
                break;
        }

        input.Sampler.Filter = ParseFilter(row[5]);
        input.Sampler.Wrap = ParseWrap(row[6]);
        input.Sampler.VFlip = IsTrue(row[7]);

        return input;
    }

    /// <summary>
    /// 库里的 pass 代码覆盖 JSON 里的代码（同一个 pass 以库里为准）。
    /// </summary>
    /// <remarks>
    /// 只按 <c>PassIndex</c> 对应，并且要求 **类型也一致** —— 万一库和 JSON 的 pass 顺序不同
    /// （表被手工改过、JSON 是旧版本），宁可用 JSON 里的代码，也不能把 Buffer 的代码塞给 Image。
    /// </remarks>
    private void ApplyPassCodeFromTables(ShaderToyProject project, string shaderId)
    {
        foreach ((int index, (ShaderPassType type, string code)) in ReadPassCode(shaderId))
        {
            if (index < 0 || index >= project.Passes.Count)
            {
                continue;
            }

            ShaderPass pass = project.Passes[index];

            if (pass.Type != type || string.Equals(pass.Code, code, StringComparison.Ordinal))
            {
                continue;
            }

            pass.Code = code;
        }
    }

    private static Dictionary<int, List<string?[]>> GroupByPass(List<string?[]> rows)
    {
        var result = new Dictionary<int, List<string?[]>>();

        foreach (string?[] row in rows)
        {
            int index = ParseInt(row[0]);

            if (!result.TryGetValue(index, out List<string?[]>? list))
            {
                list = [];
                result[index] = list;
            }

            list.Add(row);
        }

        return result;
    }

    private static ShaderPassType ParsePassType(string? type) => (type ?? string.Empty).ToLowerInvariant() switch
    {
        "common" => ShaderPassType.Common,
        "buffer" => ShaderPassType.Buffer,
        "cubemap" or "cube" => ShaderPassType.Cubemap,
        "sound" => ShaderPassType.Sound,
        "image" => ShaderPassType.Image,
        _ => ShaderPassType.Image,
    };

    private static ShaderInputType ParseInputType(string? type) => (type ?? string.Empty).ToLowerInvariant() switch
    {
        "texture" => ShaderInputType.Texture,
        "buffer" => ShaderInputType.Buffer,
        "cubemap" or "cube" => ShaderInputType.Cubemap,
        "volume" => ShaderInputType.Volume,
        "keyboard" => ShaderInputType.Keyboard,
        "music" => ShaderInputType.Music,
        "musicstream" => ShaderInputType.MusicStream,
        _ => ShaderInputType.None,
    };

    private static SamplerFilter ParseFilter(string? filter) => (filter ?? string.Empty).ToLowerInvariant() switch
    {
        "nearest" => SamplerFilter.Nearest,
        "mipmap" => SamplerFilter.Mipmap,
        _ => SamplerFilter.Linear,
    };

    private static SamplerWrap ParseWrap(string? wrap) =>
        string.Equals(wrap, "repeat", StringComparison.OrdinalIgnoreCase) ? SamplerWrap.Repeat : SamplerWrap.Clamp;

    private static bool IsTrue(string? text) =>
        string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(text, "1", StringComparison.Ordinal);

    private static int ParseInt(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;

    private static DateTime ParseTime(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime value)
            ? value
            : DateTime.MinValue;

    private static IReadOnlyList<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
