using System.Numerics;
using MilkDrop.Rendering.Gles3;
using MilkDrop.ShaderToy.Audio;
using MilkDrop.ShaderToy.Channels;
using MilkDrop.ShaderToy.Import;
using MilkDrop.ShaderToy.Input;
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Renderer;
using Silk.NET.OpenGLES;

namespace MilkDrop.ShaderToy.App;

/// <summary>
/// 播放器：一个作品（P4 起是完整的 pass 图）+ 时钟 + 鼠标状态 + 屏幕合成。
/// </summary>
/// <remarks>
/// 窗口/ImGui 留在 <see cref="Program"/>，这里只管"把作品画出来"。
/// 渲染全部走 <see cref="ShaderToyPipeline"/>：image/buffer/cubemap 都渲到离屏目标，
/// 最后把 image pass 的输出拷到默认帧缓冲。
/// </remarks>
internal sealed class Player : IDisposable
{
    /// <summary>没有给 JSON 时用的演示作品（顺便验证 iTime / iMouse 都有推送）。</summary>
    public const string DemoCode = """
        // 内置演示：同心波纹，鼠标位置会改变配色相位
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = (fragCoord * 2.0 - iResolution.xy) / iResolution.y;
            float r = length(uv);
            float a = atan(uv.y, uv.x);

            float waves = 0.5 + 0.5 * sin(10.0 * r - 3.0 * iTime + 4.0 * a);
            float phase = waves + 0.1 * (iMouse.x / max(iResolution.x, 1.0));

            vec3 col = 0.5 + 0.5 * cos(6.28318 * (vec3(0.0, 0.33, 0.67) + phase));
            fragColor = vec4(col * smoothstep(1.4, 0.1, r), 1.0);
        }
        """;

    private readonly CopyTexture _copyTexture = new();
    private readonly bool _halfFloat;
    private readonly bool _texture3D;
    private readonly List<string> _textureSearchPaths;
    private readonly BufferResolution _bufferResolution = new();
    private readonly Func<IAudioSink?>? _audioSinkFactory;
    private ShaderToyPipeline? _pipeline;
    private AudioEngine? _audio;
    private int _width;
    private int _height;

    /// <param name="audioSinkFactory">音频后端工厂；返回 null 表示不出声（<c>--no-sound</c> 或没有设备）。</param>
    public Player(
        int width,
        int height,
        bool halfFloat,
        bool texture3D,
        IEnumerable<string>? textureSearchPaths = null,
        Func<IAudioSink?>? audioSinkFactory = null)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _halfFloat = halfFloat;
        _texture3D = texture3D;
        _textureSearchPaths = textureSearchPaths is null ? [] : [.. textureSearchPaths];
        _audioSinkFactory = audioSinkFactory;

        Input = new GpuInput
        {
            Resolution = new Vector3(_width, _height, 1f),
            Date = GpuInput.MakeDate(DateTime.Now),
        };
    }

    /// <summary>播放时钟（暂停/重置/固定步长）。</summary>
    public PlaybackClock Clock { get; } = new();

    /// <summary>鼠标状态机（<c>iMouse</c>）。</summary>
    public MouseState Mouse { get; } = new();

    /// <summary>当前这一帧要推给 shader 的输入。</summary>
    public GpuInput Input { get; }

    /// <summary>已加载的工程（演示作品时为 null）。</summary>
    public ShaderToyProject? Project { get; private set; }

    /// <summary>标题（窗口/UI 用）。</summary>
    public string Title { get; private set; } = "内置演示";

    /// <summary>内置演示用的作品来源标识。</summary>
    public const string BuiltinSource = "builtin";

    /// <summary>当前作品来源（文件路径或 <see cref="BuiltinSource"/>）。</summary>
    public string SourceName { get; private set; } = BuiltinSource;

    /// <summary>最近一次加载/编译的错误（成功时 null）。</summary>
    public string? Error { get; private set; }

    /// <summary>导入与构建过程中的提示。</summary>
    public IReadOnlyList<string> Notes => _pipeline?.Notes ?? [];

    /// <summary>工程导入时的容忍项（未知类型、缺字段…）。</summary>
    public IReadOnlyList<string> ImportWarnings => Project?.Warnings ?? [];

    /// <summary>是否用 RGBA16F 中间缓冲。</summary>
    public bool IsHalfFloat => _halfFloat;

    public int Width => _width;

    public int Height => _height;

    public int PassCount => _pipeline?.PassCount ?? 0;

    public int BufferCount => _pipeline?.BufferCount ?? 0;

    public BufferResolution BufferResolution => _bufferResolution;

    /// <summary>键盘通道（每帧由 Program 灌入按键状态）。</summary>
    public KeyboardChannel? Keyboard => _pipeline?.Keyboard;

    /// <summary>音频闭环；作品没有 sound pass 或音频后端不可用时为 null。</summary>
    public AudioEngine? Audio => _audio;

    /// <summary>作品是否真的带可编译的 sound pass。</summary>
    public bool HasSound => _pipeline?.Sound is not null;

    /// <summary>音频侧的失败原因（没有设备、sound pass 编译失败……）。</summary>
    public string? AudioError { get; private set; }

    /// <summary>当前管线（UI 要读通道绑定/编译错误时用）。</summary>
    public ShaderToyPipeline? Pipeline => _pipeline;

    /// <summary>被跳过的 pass 及其报错（错误面板用）。</summary>
    public IReadOnlyList<PassCompileError> CompileErrors =>
        _pipeline?.CompileErrors ?? (IReadOnlyList<PassCompileError>)[];

    /// <summary>加载一个 Shadertoy 导出 JSON；失败时回落到演示作品。</summary>
    public bool LoadFile(string path)
    {
        try
        {
            ShaderToyProject project = ShaderToyJsonImporter.LoadFile(path);

            if (Compile(project, path))
            {
                return true;
            }

            LoadDemo();
            return false;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException
                                       or ShaderToyImportException or ShaderException
                                       or InvalidOperationException or ArgumentException)
        {
            Error = $"{Path.GetFileName(path)}: {ex.Message}";
            LoadDemo();
            return false;
        }
    }

    /// <summary>
    /// 从磁盘重新读入当前作品（界面上的"重新加载"）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="LoadFile"/> 的区别：失败时**保留当前画面**，只把原因写进 <see cref="Error"/>，
    /// 不回落到演示作品 —— 重新读入不该因为文件正被外部编辑器写坏而丢掉正在看的东西。
    /// </remarks>
    /// <returns>是否重新读入并重建了管线。</returns>
    public bool Reload()
    {
        if (SourceName == BuiltinSource)
        {
            return false;
        }

        try
        {
            ShaderToyProject project = ShaderToyJsonImporter.LoadFile(SourceName);

            // Compile 在构造新管线成功后才换掉旧的：编译失败时画面仍是上一次的内容
            return Compile(project, SourceName);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException
                                       or ShaderToyImportException or ShaderException
                                       or InvalidOperationException or ArgumentException)
        {
            Error = $"{Path.GetFileName(SourceName)}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 用一个工程重建管线（加载 / 重新加载走这里）。
    /// </summary>
    /// <remarks>
    /// 单个 pass 编译失败**不算**整体失败：失败信息进 <see cref="CompileErrors"/>，该 pass 被跳过，
    /// 其余照常渲染 —— 改坏一个 pass 时还能看到别的部分和报错。
    /// 这里不碰 <see cref="Error"/> 以外的状态：标题、时钟都由调用方语义决定。
    /// </remarks>
    public bool Compile(ShaderToyProject project, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(project);

        try
        {
            var pipeline = new ShaderToyPipeline(project, BuildOptions(sourceName));

            _pipeline?.Dispose();
            _pipeline = pipeline;
            AttachAudio(pipeline);

            Project = project;
            SourceName = sourceName;
            Title = string.IsNullOrEmpty(project.Metadata.Name)
                ? (sourceName == BuiltinSource ? "内置演示" : Path.GetFileName(sourceName))
                : project.Metadata.Name;
            Error = null;
            Clock.Reset();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException
                                       or ShaderException or NotSupportedException)
        {
            Error = ex.Message;
            return false;
        }
    }

    /// <summary>跳到指定时刻（时间轴用）：画面与音频一起重新对齐。</summary>
    public void Seek(float seconds)
    {
        seconds = Math.Max(0f, seconds);
        Clock.Seek(seconds);
        _audio?.SeekToSample((long)Math.Round(seconds * (float)AudioBuffer.SampleRate));
    }

    /// <summary>把当前帧缓冲存成 PNG（<b>必须在画完 shader、画 UI 之前调用</b>，否则截图里会有界面）。</summary>
    /// <returns>实际写入的绝对路径；没有可截的画面时返回 null。</returns>
    public string? Screenshot(string? path = null)
    {
        if (_pipeline?.Output is null)
        {
            return null;
        }

        path ??= Path.Combine("screenshots", $"shot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

        // 不再重渲一遍：默认帧缓冲里就是刚才 Render + CopyTexture 的结果，
        // 重渲会推进 buffer 的 ping-pong，截出来的不是屏幕上那一帧。
        return MilkDrop.Rendering.Gles3.Screenshot.Save(GlContext.Gl, _width, _height, path);
    }

    /// <summary>加载内置演示作品（顺便也把它当成可编辑的工程）。</summary>
    public void LoadDemo()
    {
        var project = new ShaderToyProject
        {
            Version = "0.1",
            Metadata = new ShaderToyMetadata { Id = BuiltinSource, Name = "内置演示" },
        };

        var pass = new ShaderPass
        {
            Type = ShaderPassType.Image,
            Name = "Image",
            Code = DemoCode,
            Outputs = { new ShaderPassOutput { Id = "demo", Channel = 0 } },
        };

        project.Passes.Add(pass);

        if (!Compile(project, BuiltinSource))
        {
            Error = "演示作品编译失败: " + Error;
        }
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (width == _width && height == _height)
        {
            return;
        }

        _width = width;
        _height = height;
        _pipeline?.SetViewportSize(width, height);
    }

    /// <summary>换 buffer 分辨率策略（viewport / fixed / scale）。</summary>
    public void SetBufferResolution(BufferResolutionMode mode)
    {
        if (_bufferResolution.Mode == mode)
        {
            return;
        }

        _bufferResolution.Mode = mode;
        _pipeline?.SetBufferResolution(_bufferResolution.Clone());
    }

    /// <summary>
    /// 推进一帧：更新鼠标状态机 → 推进时钟 → 组装 <see cref="Input"/>。
    /// </summary>
    public void Advance(double deltaSeconds, Vector2 mousePixel, bool mouseDown, bool acceptMouse)
    {
        if (acceptMouse)
        {
            if (mouseDown)
            {
                if (Mouse.IsPressed)
                {
                    Mouse.Move(mousePixel);
                }
                else
                {
                    Mouse.Press(mousePixel);
                }
            }
            else if (Mouse.IsPressed)
            {
                Mouse.Release();
            }
        }
        else if (Mouse.IsPressed)
        {
            // 拖到 ImGui 窗口上就认为是松开，避免 iMouse.zw 一直保持正值
            Mouse.Release();
        }

        Clock.Advance(deltaSeconds);

        Input.Resolution = new Vector3(_width, _height, 1f);
        Input.Time = Clock.Time;
        Input.TimeDelta = Clock.TimeDelta;
        Input.FrameRate = Clock.FrameRate;
        Input.Frame = Clock.Frame;
        Input.Mouse = Mouse.Value;
        Input.Date = GpuInput.MakeDate(DateTime.Now);
    }

    /// <summary>跑完整 pass 图，再把 image pass 的输出拷到当前绑定的帧缓冲（默认帧缓冲）。</summary>
    public void Render()
    {
        if (_pipeline is null)
        {
            return;
        }

        // 先补音频：本帧新算出来的采样要先进 music 纹理，image pass 这一帧就能采样到
        _audio?.Pump();

        _pipeline.Render(Input);

        if (_pipeline.Output is not { } output)
        {
            return;
        }

        var gl = GlContext.Gl;
        gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _copyTexture.Draw(output);
    }

    public void Dispose()
    {
        _audio?.Dispose();
        _audio = null;

        _copyTexture.Dispose();
        _pipeline?.Dispose();
        _pipeline = null;
    }

    /// <summary>
    /// 给新管线挂上音频闭环。没有 sound pass / 没有后端时保持静音，但画面照常。
    /// </summary>
    /// <remarks>
    /// 这里必须在 GL 上下文当前的线程上调用（<see cref="MusicTexture"/> 要建纹理）。
    /// </remarks>
    private void AttachAudio(ShaderToyPipeline pipeline)
    {
        _audio?.Dispose();
        _audio = null;
        AudioError = null;

        if (pipeline.Sound is not { } sound)
        {
            if (pipeline.ProjectHasSoundPass)
            {
                AudioError = "sound pass 编译失败，音频不可用（见转译提示）。";
            }

            return;
        }

        var audio = new AudioEngine(sound, pipeline.Music);
        _audio = audio;

        if (_audioSinkFactory is null)
        {
            AudioError = "音频输出已关闭（--no-sound）。";
            return;
        }

        try
        {
            IAudioSink? sink = _audioSinkFactory();
            if (sink is null)
            {
                AudioError = "没有可用的音频设备，静音播放。";
                return;
            }

            audio.Start(sink);
        }
        catch (Exception ex)
        {
            AudioError = "音频设备打开失败：" + ex.Message;
        }
    }

    private PipelineOptions BuildOptions(string? projectPath)
    {
        var options = new PipelineOptions
        {
            Width = _width,
            Height = _height,
            HalfFloat = _halfFloat,
            Texture3DSupported = _texture3D,
            BufferResolution = _bufferResolution.Clone(),
        };

        // 作品自带的图片优先在 JSON 旁边找，其次是显式给的搜索目录
        if (!string.IsNullOrEmpty(projectPath) && projectPath != BuiltinSource)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            if (!string.IsNullOrEmpty(directory))
            {
                options.TextureSearchPaths.Add(directory);
            }
        }

        options.TextureSearchPaths.AddRange(_textureSearchPaths);
        return options;
    }
}
