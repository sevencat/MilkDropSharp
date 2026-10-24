namespace MilkDrop.ShaderToy.Pass;

/// <summary>Shadertoy 的 pass 类型。</summary>
public enum ShaderPassType
{
    /// <summary>公共代码（拼到每个 pass 前面）。</summary>
    Common,

    /// <summary>输出到屏幕的主 pass。</summary>
    Image,

    /// <summary>离屏缓冲，可被其他 pass 采样（带 ping-pong）。</summary>
    Buffer,

    /// <summary>立方体贴图 pass。</summary>
    Cubemap,

    /// <summary>音频合成（<c>mainSound</c>）；上游不支持，本项目新增。</summary>
    Sound,
}

/// <summary>输入通道的类型（对应 JSON 里 <c>inputs[].type</c> / <c>ctype</c>）。</summary>
public enum ShaderInputType
{
    None,

    /// <summary>图片纹理。</summary>
    Texture,

    /// <summary>引用另一个 buffer pass 的输出。</summary>
    Buffer,

    /// <summary>立方体贴图。</summary>
    Cubemap,

    /// <summary>3D 纹理（volume）。</summary>
    Volume,

    /// <summary>键盘状态纹理（256×3）。</summary>
    Keyboard,

    /// <summary>音频输入（麦克风 / 系统声音）。</summary>
    Music,

    /// <summary>音频输入（流式）。</summary>
    MusicStream,
}

/// <summary>采样器过滤方式。</summary>
public enum SamplerFilter
{
    Linear,
    Nearest,
    Mipmap,
}

/// <summary>采样器环绕方式。</summary>
public enum SamplerWrap
{
    Clamp,
    Repeat,
}

/// <summary>输入通道的采样器设置（JSON 里 <c>inputs[].sampler</c>）。</summary>
public sealed class ShaderInputSampler
{
    public SamplerFilter Filter { get; set; } = SamplerFilter.Linear;

    public SamplerWrap Wrap { get; set; } = SamplerWrap.Clamp;

    /// <summary>上传时是否垂直翻转（Shadertoy 的 <c>vflip</c>）。</summary>
    public bool VFlip { get; set; }

    public ShaderInputSampler Clone() => new() { Filter = Filter, Wrap = Wrap, VFlip = VFlip };
}

/// <summary>一个 pass 的输入通道绑定。</summary>
public sealed class ShaderPassInput
{
    /// <summary>通道号 0..3（对应 <c>iChannel0..3</c>）。</summary>
    public int Channel { get; set; }

    public ShaderInputType Type { get; set; } = ShaderInputType.None;

    /// <summary>引用目标：buffer / cubemap pass 的 id，或 volume 资源 id。</summary>
    public string? Id { get; set; }

    /// <summary>纹理文件路径（本地相对路径或下载时的原始 URL）。</summary>
    public string? Path { get; set; }

    public ShaderInputSampler Sampler { get; set; } = new();

    public ShaderPassInput Clone() => new()
    {
        Channel = Channel,
        Type = Type,
        Id = Id,
        Path = Path,
        Sampler = Sampler.Clone(),
    };
}

/// <summary>pass 的输出声明（JSON 里 <c>outputs[]</c>，通常是 <c>{ id, channel }</c>）。</summary>
public sealed class ShaderPassOutput
{
    /// <summary>pass 的标识，被其他 pass 的 <see cref="ShaderPassInput.Id"/> 引用。</summary>
    public string Id { get; set; } = string.Empty;

    public int Channel { get; set; }
}

/// <summary>一个 Shadertoy pass。</summary>
public sealed class ShaderPass
{
    public ShaderPassType Type { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>GLSL 源码（已把 JSON 里的字面 <c>\n</c> 还原成换行）。</summary>
    public string Code { get; set; } = string.Empty;

    public List<ShaderPassInput> Inputs { get; } = [];

    public List<ShaderPassOutput> Outputs { get; } = [];

    /// <summary>该 pass 的 id（取 <see cref="Outputs"/> 的第一个，缺失时用索引生成）。</summary>
    public string Id => Outputs.Count > 0 ? Outputs[0].Id : string.Empty;

    public bool IsRenderable => Type is ShaderPassType.Image or ShaderPassType.Buffer
        or ShaderPassType.Cubemap or ShaderPassType.Sound;
}

/// <summary>作品的元信息（JSON 里 <c>info</c> 节）。</summary>
public sealed class ShaderToyMetadata
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>标签（<c>info.tags</c>，可能是数组也可能不存在）。</summary>
    public List<string> Tags { get; } = [];
}

/// <summary>一个完整的 Shadertoy 工程（官方导出格式的内存模型）。</summary>
public sealed class ShaderToyProject
{
    /// <summary>JSON 里的 <c>ver</c>。</summary>
    public string Version { get; set; } = string.Empty;

    public ShaderToyMetadata Metadata { get; set; } = new();

    /// <summary>全部 pass，保持 JSON 中的原始顺序（执行顺序依赖它）。</summary>
    public List<ShaderPass> Passes { get; } = [];

    /// <summary>原始 JSON 文本：保存时必须原样写回，避免丢字段/二次反转义。</summary>
    public string RawJson { get; set; } = string.Empty;

    /// <summary>导入时被容忍/跳过的内容（未知类型、缺字段等），供 UI 提示；解析失败不影响其余部分。</summary>
    public List<string> Warnings { get; } = [];

    public IEnumerable<ShaderPass> CommonPasses => Passes.Where(p => p.Type == ShaderPassType.Common);

    public ShaderPass? ImagePass => Passes.FirstOrDefault(p => p.Type == ShaderPassType.Image);

    public IReadOnlyList<ShaderPass> BufferPasses =>
        Passes.Where(p => p.Type == ShaderPassType.Buffer).ToList();

    public IReadOnlyList<ShaderPass> CubePasses =>
        Passes.Where(p => p.Type == ShaderPassType.Cubemap).ToList();

    public ShaderPass? SoundPass => Passes.FirstOrDefault(p => p.Type == ShaderPassType.Sound);

    /// <summary>把所有 <c>common</c> pass 的代码拼起来（顺序敏感）。</summary>
    public string BuildCommonCode()
    {
        var builder = new System.Text.StringBuilder();

        foreach (ShaderPass pass in CommonPasses)
        {
            builder.Append(pass.Code);
            if (!pass.Code.EndsWith('\n'))
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>按 id 找 buffer / cubemap pass（不看 image/sound）。</summary>
    public ShaderPass? FindPassById(string id) =>
        Passes.FirstOrDefault(p =>
            (p.Type is ShaderPassType.Buffer or ShaderPassType.Cubemap) &&
            string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>该工程用到的所有纹理路径（去重，便于下载/校验）。</summary>
    public IReadOnlyList<string> CollectTexturePaths() =>
        Passes
            .SelectMany(p => p.Inputs)
            .Where(i => i.Type is ShaderInputType.Texture or ShaderInputType.Cubemap or ShaderInputType.Volume)
            .Select(i => i.Path)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
