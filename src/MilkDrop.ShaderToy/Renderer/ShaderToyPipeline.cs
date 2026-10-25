using System.Numerics;
using System.Text.RegularExpressions;
using MilkDrop.Rendering.Gles3;
using MilkDrop.ShaderToy.Audio;
using MilkDrop.ShaderToy.Channels;
using MilkDrop.ShaderToy.Compile;
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Resources;
using Silk.NET.OpenGLES;

using Framebuffer = MilkDrop.Rendering.Gles3.Framebuffer;
using GlShaderException = MilkDrop.Rendering.Gles3.ShaderException;
using Sampler = MilkDrop.Rendering.Gles3.Sampler;
using Shader = MilkDrop.Rendering.Gles3.Shader;
using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ShaderToy.Renderer;

/// <summary>
/// 一个 pass 的编译失败信息（UI 要把它连同出错行一起显示出来）。
/// </summary>
/// <param name="PassName">pass 名（没有名字时用类型名）。</param>
/// <param name="Message">驱动的原始报错（ANGLE 形如 <c>0:57: 'x' : undeclared identifier</c>）。</param>
/// <param name="Line">从报错里解析出的行号（转译后的 GLSL 行号），解析不出时为 null。</param>
/// <param name="LineText">该行内容。</param>
/// <param name="Source">转译后的完整 GLSL（为 null 表示连转译都没过）。</param>
public sealed record PassCompileError(
    string PassName,
    string? Message,
    int? Line,
    string? LineText,
    string? Source);

/// <summary>管线构建选项。</summary>
public sealed class PipelineOptions
{
    public int Width { get; set; } = 1280;

    public int Height { get; set; } = 720;

    /// <summary>buffer pass 的分辨率策略。</summary>
    public BufferResolution BufferResolution { get; set; } = new();

    /// <summary>渲染目标是否用 RGBA16F（否则 RGBA8）。</summary>
    public bool HalfFloat { get; set; } = true;

    /// <summary>后端是否支持 <c>sampler3D</c>（决定 volume 占位要不要建）。</summary>
    public bool Texture3DSupported { get; set; } = true;

    /// <summary>图片资源的搜索目录（按顺序试）。</summary>
    public List<string> TextureSearchPaths { get; } = [];
}

/// <summary>
/// 一个作品的完整执行链：按 JSON 顺序跑 image / buffer / cubemap pass，并把通道绑好。
/// </summary>
/// <remarks>
/// <para>
/// 与上游对齐的三条硬规则：
/// 1. **执行顺序 = JSON 里的顺序**，不做拓扑排序（<c>viewport.cpp:1180</c> 的 <c>renderpass</c> 循环）；
/// 2. buffer 必须 ping-pong（同一纹理不能又读又写，<c>shaderToyIssue.md</c> S3-1）；
/// 3. 每个 pass 的 <c>iResolution</c> 是它自己的目标尺寸，<c>iChannelResolution[i]</c> 是它实际绑到的纹理尺寸。
/// </para>
/// <para>
/// <c>sound</c> pass 不在这里画：它由 <see cref="Audio.SoundPassRenderer"/> 按"一个片元 = 一个采样点"
/// 批量渲染，音频闭环在 <see cref="AudioEngine"/>；但 program 的编译与生命周期仍由本类统一管，
/// 编译失败只记 <see cref="Notes"/>，不影响画面。cubemap/volume 的**资源**（不是 pass）即使已经
/// 下载到 <c>textures/</c>，绑定侧仍用占位纹理（加载 samplerCube/sampler3D 是 P5 的遗留项）。
/// </para>
/// </remarks>
public sealed class ShaderToyPipeline : IDisposable
{
    private readonly List<Slot> _slots = [];
    private readonly Dictionary<string, Slot> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<(SamplerFilter Filter, SamplerWrap Wrap), Sampler> _samplers = [];
    private readonly List<Texture> _ownedTextures = [];
    private readonly PlaceholderTextures _placeholders;
    private readonly KeyboardChannel _keyboard;
    private readonly MusicTexture _music;
    private readonly FullscreenQuad _quad;
    private readonly PipelineOptions _options;
    private int _width;
    private int _height;
    private bool _disposed;

    /// <summary>通道绑定从 <see cref="Notes"/> 的哪个下标开始写（重绑时从这里截断）。</summary>
    private int _bindingNoteStart = -1;

    public ShaderToyPipeline(ShaderToyProject project, PipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _width = Math.Max(1, options.Width);
        _height = Math.Max(1, options.Height);

        _placeholders = new PlaceholderTextures(options.Texture3DSupported);
        _keyboard = new KeyboardChannel();
        _music = new MusicTexture();
        _quad = new FullscreenQuad();

        string commonCode = project.BuildCommonCode();

        // 1) 编译：顺序就是执行顺序
        foreach (ShaderPass pass in project.Passes)
        {
            if (pass.Type == ShaderPassType.Common)
            {
                continue;
            }

            if (pass.Type == ShaderPassType.Sound)
            {
                ProjectHasSoundPass = true;
                TryCreateSoundRenderer(pass, commonCode);
                continue;
            }

            Shader program;
            ShaderToyTranspileResult result;
            string? source = null;

            try
            {
                result = ShaderToyTranspiler.Transpile(pass, commonCode);
                source = result.Source;

                program = new Shader();
                program.CompileProgram(ShaderToyTranspiler.FullscreenVertexSource, result.Source);
            }
            catch (Exception ex) when (ex is GlShaderException or NotSupportedException or ArgumentException)
            {
                // 把出错的那一行贴出来：只有 "0:57: syntax error" 没法定位
                int? line = TryFindErrorLine(source, ex.Message, out string? lineText);
                CompileErrors.Add(new PassCompileError(Label(pass), ex.Message, line, lineText, source));
                Notes.Add($"{Label(pass)} 编译失败，已跳过：{ex.Message}{FormatOffendingLine(line, lineText)}");
                continue;
            }

            var slot = new Slot { Pass = pass, Program = program };
            _slots.Add(slot);

            if (pass.Type is ShaderPassType.Buffer or ShaderPassType.Cubemap && pass.Id.Length > 0)
            {
                _byId.TryAdd(pass.Id, slot);
            }

            foreach (string note in result.Notes)
            {
                Notes.Add($"{Label(pass)}：{note}");
            }
        }

        // 2) 目标
        foreach (Slot slot in _slots)
        {
            CreateTarget(slot);
        }

        // 3) 哪些 buffer 需要 mip 链
        foreach (Slot slot in _slots)
        {
            foreach (ShaderPassInput input in slot.Pass.Inputs)
            {
                if (input.Type == ShaderInputType.Buffer
                    && input.Sampler.Filter == SamplerFilter.Mipmap
                    && input.Id is { Length: > 0 } bufferId
                    && _byId.TryGetValue(bufferId, out Slot? source)
                    && source.Target is not null)
                {
                    source.NeedsMipmap = true;
                }
            }
        }

        // 4) 通道绑定表（静态部分在这里定，动态部分每帧在 BindChannels 里取）
        _bindingNoteStart = Notes.Count;

        foreach (Slot slot in _slots)
        {
            BuildBindings(slot);
        }
    }

    /// <summary>导入/转译/绑定过程中的提示。</summary>
    public List<string> Notes { get; } = [];

    /// <summary>编译失败但被跳过的 pass（UI 的错误面板用）。</summary>
    public List<PassCompileError> CompileErrors { get; } = [];

    /// <summary>最终上屏的纹理（image pass 的输出）。</summary>
    public Texture? Output => ImageSlot?.Target?.ReadTexture;

    /// <summary>键盘通道（每帧由调用方灌按键状态）。</summary>
    public KeyboardChannel Keyboard => _keyboard;

    /// <summary>音频通道纹理（<c>music</c> / <c>musicstream</c>）：512×2，第 0 行频谱、第 1 行波形。</summary>
    public MusicTexture Music => _music;

    /// <summary>sound pass 的渲染器；作品没有 sound pass 或编译失败时为 null。</summary>
    public SoundPassRenderer? Sound { get; private set; }

    /// <summary>作品是否声明了 sound pass（编译失败时为 true 而 <see cref="Sound"/> 为 null）。</summary>
    public bool ProjectHasSoundPass { get; private set; }

    /// <summary>可渲染 pass 数量（不含被跳过的）。</summary>
    public int PassCount => _slots.Count;

    /// <summary>buffer pass 数量。</summary>
    public int BufferCount => _slots.Count(s => s.Pass.Type == ShaderPassType.Buffer);

    /// <summary>当前 buffer 分辨率描述（UI 用）。</summary>
    public string BufferResolutionDescription => _options.BufferResolution.Describe();

    private Slot? ImageSlot { get; set; }

    /// <summary>按当前视口尺寸重排所有目标（buffer 尺寸按策略重算）。</summary>
    public void SetViewportSize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (width == _width && height == _height)
        {
            return;
        }

        _width = width;
        _height = height;
        ApplySizes();
    }

    /// <summary>换 buffer 分辨率策略（会重建目标，内容清空）。</summary>
    public void SetBufferResolution(BufferResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        _options.BufferResolution = resolution;
        ApplySizes();
    }

    /// <summary>
    /// 按当前的 <see cref="ShaderToyProject"/> 模型重新绑一次通道（改 sampler / vflip / 图片路径后调用）。
    /// </summary>
    /// <remarks>
    /// 只重绑通道、不重编 program：通道的**类型**决定了 <c>sampler2D</c> 还是 <c>samplerCube</c>，
    /// 改类型必须重新构造管线（转译结果不同）。上一轮绑定产生的提示会被清掉，
    /// 否则每次换 sampler 都会在同一处堆一条"找不到图片"。
    /// </remarks>
    public void RefreshBindings()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (Texture texture in _ownedTextures)
        {
            texture.Dispose();
        }

        _ownedTextures.Clear();

        if (_bindingNoteStart >= 0 && Notes.Count > _bindingNoteStart)
        {
            Notes.RemoveRange(_bindingNoteStart, Notes.Count - _bindingNoteStart);
        }

        foreach (Slot slot in _slots)
        {
            slot.Bindings.Clear();
        }

        foreach (Slot slot in _slots)
        {
            BuildBindings(slot);
        }
    }

    /// <summary>渲染一帧。结果在 <see cref="Output"/>。</summary>
    public void Render(GpuInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var gl = GlContext.Gl;

        // ImGui 会留下 scissor/blend/colorMask
        gl.Disable(EnableCap.ScissorTest);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.ColorMask(true, true, true, true);

        _keyboard.BeginFrame();

        foreach (Slot slot in _slots)
        {
            BindChannels(slot, input);
            input.Resolution = new Vector3(slot.Width, slot.Height, 1f);

            slot.Program.Bind();
            input.PushTo(slot.Program);

            if (slot.Cube is { } cube)
            {
                for (var face = 0; face < 6; face++)
                {
                    cube.BeginFace(face);
                    slot.Program.SetUniformInt(ShaderToyTranspiler.CubeFaceIndexUniform, face);
                    _quad.Draw();
                }

                cube.EndFace();
            }
            else if (slot.Target is { } target)
            {
                target.BindWrite();
                gl.Viewport(0, 0, (uint)slot.Width, (uint)slot.Height);
                _quad.Draw();

                // 画完立刻换面：后面的 pass 读到的是本帧结果，自己读自己则是上一帧
                target.Swap();

                if (slot.NeedsMipmap && target.ReadTexture is { } texture)
                {
                    MipmapHelper.Generate(texture);
                }
            }

            Shader.Unbind();
        }

        Framebuffer.Unbind();
        _keyboard.EndFrame();

        input.Resolution = new Vector3(_width, _height, 1f);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (Slot slot in _slots)
        {
            slot.Program.Dispose();
            slot.Target?.Dispose();
            slot.Cube?.Dispose();
        }

        foreach (Sampler sampler in _samplers.Values)
        {
            sampler.Dispose();
        }

        foreach (Texture texture in _ownedTextures)
        {
            texture.Dispose();
        }

        _quad.Dispose();
        _keyboard.Dispose();
        _placeholders.Dispose();
        Sound?.Dispose();
        _music.Dispose();
    }

    /// <summary>
    /// 编译 sound pass 并建它的 float 目标。失败只记提示 —— 没有声音也要能看画面。
    /// </summary>
    private void TryCreateSoundRenderer(ShaderPass pass, string commonCode)
    {
        try
        {
            Sound = new SoundPassRenderer(pass, commonCode, _music, _options.Texture3DSupported);

            foreach (string note in Sound.Notes)
            {
                Notes.Add($"{Label(pass)}：{note}");
            }
        }
        catch (Exception ex) when (ex is GlShaderException or NotSupportedException or ArgumentException)
        {
            int? line = TryFindErrorLine(null, ex.Message, out string? lineText);
            CompileErrors.Add(new PassCompileError(Label(pass), ex.Message, line, lineText, null));
            Notes.Add($"{Label(pass)} 编译失败，音频不可用：{ex.Message}{FormatOffendingLine(line, lineText)}");
        }
    }

    private void CreateTarget(Slot slot)
    {
        (int width, int height) = SizeFor(slot.Pass);
        slot.Width = width;
        slot.Height = height;

        if (slot.Pass.Type == ShaderPassType.Cubemap)
        {
            slot.Cube = new CubeTarget(CubeTarget.FaceSize, (int)GLEnum.Rgba16f, GLEnum.Rgba, GLEnum.HalfFloat);
            slot.Cube.Clear();
            return;
        }

        int internalFormat = _options.HalfFloat ? (int)GLEnum.Rgba16f : (int)GLEnum.Rgba8;
        GLEnum type = _options.HalfFloat ? GLEnum.HalfFloat : GLEnum.UnsignedByte;

        slot.Target = new PassTarget(Label(slot.Pass), width, height, internalFormat, GLEnum.Rgba, type);

        if (!slot.Target.IsComplete())
        {
            Notes.Add($"{Label(slot.Pass)}：{width}x{height} 帧缓冲不完整（HDR 关闭或格式不支持）。");
        }

        if (slot.Pass.Type == ShaderPassType.Image)
        {
            ImageSlot ??= slot;
        }
    }

    private void ApplySizes()
    {
        foreach (Slot slot in _slots)
        {
            (int width, int height) = SizeFor(slot.Pass);
            if (width == slot.Width && height == slot.Height)
            {
                continue;
            }

            slot.Width = width;
            slot.Height = height;

            if (slot.Target is not null)
            {
                slot.Target.SetSize(width, height);
                slot.Target.Clear();
            }
        }
    }

    private (int Width, int Height) SizeFor(ShaderPass pass) => pass.Type switch
    {
        ShaderPassType.Buffer => _options.BufferResolution.Compute(_width, _height),
        ShaderPassType.Cubemap => (CubeTarget.FaceSize, CubeTarget.FaceSize),
        _ => (_width, _height),
    };

    private void BuildBindings(Slot slot)
    {
        foreach (ShaderPassInput input in slot.Pass.Inputs)
        {
            if (input.Channel is < 0 or > 3)
            {
                continue;
            }

            var binding = new Binding
            {
                Unit = input.Channel,
                Input = input,
                Sampler = GetSampler(input.Sampler.Filter, input.Sampler.Wrap),
            };

            switch (input.Type)
            {
                case ShaderInputType.Buffer:
                case ShaderInputType.Cubemap when IsPassReference(input.Id):
                    if (_byId.TryGetValue(input.Id ?? string.Empty, out Slot? source))
                    {
                        binding.Source = source;
                    }
                    else
                    {
                        Notes.Add($"{Label(slot.Pass)} 通道 {input.Channel}：找不到被引用的 pass \"{input.Id}\"，用黑纹理代替。");
                    }

                    break;

                case ShaderInputType.Texture:
                    binding.Static = LoadTexture(slot, input);
                    break;

                case ShaderInputType.Keyboard:
                    binding.Static = _keyboard.Texture;
                    break;

                case ShaderInputType.Music:
                case ShaderInputType.MusicStream:
                    // 自行生成的音频（sound pass 的产出）；没有 sound pass 时这张纹理是全黑
                    binding.Static = _music.Texture;
                    break;

                case ShaderInputType.Cubemap:
                case ShaderInputType.Volume:
                    // 资源文件由 P8 下载到 textures/，但加载 samplerCube/sampler3D 还没接（P5 遗留）
                    Notes.Add($"{Label(slot.Pass)} 通道 {input.Channel}：{input.Type} 资源暂用占位纹理（加载侧未接）。");
                    break;
            }

            slot.Bindings.Add(binding);
        }
    }

    private void BindChannels(Slot slot, GpuInput input)
    {
        // 1) 先按"声明的采样器类型"铺占位纹理：留空的通道采样结果是未定义的
        ShaderInputType[] types = ShaderToyTranspiler.ResolveChannelTypes(slot.Pass);

        for (var unit = 0; unit < 4; unit++)
        {
            Texture? placeholder = types[unit] switch
            {
                ShaderInputType.Cubemap => _placeholders.BlackCube,
                ShaderInputType.Volume => _placeholders.Black3D,
                _ => _placeholders.Black2D,
            };

            placeholder?.Bind(unit, GetSampler(SamplerFilter.Nearest, SamplerWrap.Clamp));

            input.ChannelResolution[unit] = placeholder is null
                ? Vector3.Zero
                : new Vector3(placeholder.Width, placeholder.Height, 1f);
            input.ChannelTime[unit] = input.Time;
        }

        // 2) 再用真实通道覆盖
        foreach (Binding binding in slot.Bindings)
        {
            // 每帧重新取：buffer 的 ping-pong 面每帧都在换
            Texture? texture = binding.Source is { } source
                ? source.Cube?.Texture ?? source.Target?.ReadTexture
                : binding.Static;

            if (texture is null)
            {
                continue;
            }

            texture.Bind(binding.Unit, binding.Sampler);

            input.ChannelResolution[binding.Unit] = new Vector3(texture.Width, texture.Height, 1f);
            input.ChannelTime[binding.Unit] = input.Time;
        }
    }

    private Texture? LoadTexture(Slot slot, ShaderPassInput input)
    {
        string? path = ResolveTexturePath(input.Path);
        if (path is null)
        {
            Notes.Add($"{Label(slot.Pass)} 通道 {input.Channel}：找不到图片 {input.Path}（应在作品目录的 textures/ 下），用黑纹理。");
            return null;
        }

        try
        {
            Texture texture = TextureLoader.Load2D(
                path,
                vflip: input.Sampler.VFlip,
                generateMipmaps: input.Sampler.Filter == SamplerFilter.Mipmap);

            _ownedTextures.Add(texture);
            return texture;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Notes.Add($"{Label(slot.Pass)} 通道 {input.Channel}：加载 {path} 失败（{ex.Message}），用黑纹理。");
            return null;
        }
    }

    /// <summary>
    /// 把 JSON 里的 <c>/media/...</c> / 相对路径解析成本地文件。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 搜索目录通常有：作品目录、<c>&lt;数据根&gt;/media</c>（ShaderToySnap 抓下来的图）、
    /// <c>&lt;数据根&gt;</c> 本身。每个目录里按三种写法试：整条相对路径、只按文件名、<c>textures/</c> 下的文件名。
    /// </para>
    /// <para>
    /// 站点路径（<c>/media/a/xxx.png</c>）先去掉前导分隔符变成相对路径：ShaderToySnap 把
    /// <c>/media/a/x.png</c> 存在 <c>&lt;数据根&gt;/media/media/a/x.png</c>，所以"搜索目录 + 相对路径"
    /// 正好命中；我们下载过的作品写的是 <c>textures/x.png</c>，同样命中第一种。
    /// 手工把图丢进 <c>textures/</c> 时靠第三种兜底（S8-7）。
    /// </para>
    /// </remarks>
    private string? ResolveTexturePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (File.Exists(path))
        {
            return path;
        }

        // 统一成"不带前导分隔符"的相对路径（站点路径 /media/a/x.png → media/a/x.png）
        string relative = path.Replace('\\', '/').TrimStart('/');

        int query = relative.IndexOf('?');
        if (query >= 0)
        {
            relative = relative[..query];
        }

        string fileName = Path.GetFileName(relative);
        if (fileName.Length == 0)
        {
            return null;
        }

        string localRelative = relative.Replace('/', Path.DirectorySeparatorChar);

        foreach (string directory in _options.TextureSearchPaths)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            // 1) 搜索目录 + 相对路径（textures/noise.jpg、media/a/hash.png）
            if (!Path.IsPathRooted(localRelative))
            {
                string candidate = Path.Combine(directory, localRelative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // 2) 只按文件名（老工程把图放在工程 JSON 旁边）
            string byName = Path.Combine(directory, fileName);
            if (File.Exists(byName))
            {
                return byName;
            }

            // 3) <目录>/textures/<文件名>：手工把下载好的图丢进作品目录的 textures/ 时，
            //    JSON 里可能还留着站点路径（/media/a/xxx.png），这里兜底按文件名找（S8-7）
            string inTextures = Path.Combine(directory, "textures", fileName);
            if (File.Exists(inTextures))
            {
                return inTextures;
            }
        }

        return null;
    }

    private Sampler GetSampler(SamplerFilter filter, SamplerWrap wrap)
    {
        var key = (filter, wrap);
        if (_samplers.TryGetValue(key, out Sampler? cached))
        {
            return cached;
        }

        int wrapMode = wrap == SamplerWrap.Repeat ? (int)GLEnum.Repeat : (int)GLEnum.ClampToEdge;

        // mag filter 不能是 mipmap 模式（GL_INVALID_ENUM），所以先建线性，再单独改 min
        var sampler = new Sampler(wrapMode, (int)GLEnum.Linear);

        if (filter == SamplerFilter.Mipmap)
        {
            GlContext.Gl.SamplerParameter(sampler.SamplerId, GLEnum.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        }
        else if (filter == SamplerFilter.Nearest)
        {
            sampler.FilterMode = (int)GLEnum.Nearest;
        }

        _samplers[key] = sampler;
        return sampler;
    }

    /// <summary>从驱动的报错里抠出行号（ANGLE 的格式是 "0:57: ..."），并取出那一行源码。</summary>
    private static int? TryFindErrorLine(string? source, string message, out string? lineText)
    {
        lineText = null;

        Match match = Regex.Match(message, @"0:(\d+)");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int lineNumber))
        {
            return null;
        }

        if (source is not null)
        {
            string[] lines = source.Split('\n');
            if (lineNumber >= 1 && lineNumber <= lines.Length)
            {
                lineText = lines[lineNumber - 1].TrimEnd('\r');
            }
        }

        return lineNumber;
    }

    private static string FormatOffendingLine(int? line, string? lineText) =>
        line is { } number && lineText is { Length: > 0 }
            ? $"{Environment.NewLine}  第 {number} 行: {lineText}"
            : string.Empty;

    private bool IsPassReference(string? id) => !string.IsNullOrEmpty(id) && _byId.ContainsKey(id);

    private static string Label(ShaderPass pass) =>
        string.IsNullOrEmpty(pass.Name) ? pass.Type.ToString() : pass.Name;

    private sealed class Slot
    {
        public required ShaderPass Pass { get; init; }

        public required Shader Program { get; init; }

        public PassTarget? Target { get; set; }

        public CubeTarget? Cube { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public bool NeedsMipmap { get; set; }

        public List<Binding> Bindings { get; } = [];
    }

    private sealed class Binding
    {
        public int Unit { get; init; }

        public required ShaderPassInput Input { get; init; }

        /// <summary>被引用的 pass（buffer / cube）。</summary>
        public Slot? Source { get; set; }

        /// <summary>静态纹理（图片、键盘、音频、占位）。</summary>
        public Texture? Static { get; set; }

        public Sampler? Sampler { get; set; }
    }
}
