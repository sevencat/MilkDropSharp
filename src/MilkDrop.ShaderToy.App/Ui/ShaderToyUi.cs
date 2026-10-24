using System.Numerics;
using ImGuiNET;
using MilkDrop.ShaderToy.Audio;
using MilkDrop.ShaderToy.Import;
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Renderer;

namespace MilkDrop.ShaderToy.App.Ui;

/// <summary>
/// 界面：本地库 + 播放控制 + 只读信息（pass 列表 / 通道 / 报错）。
/// </summary>
/// <remarks>
/// <para>
/// **界面不编辑作品**。事实来源永远是磁盘上的 <c>project.json</c>：在外部编辑器里改完，
/// 回到界面点"重新加载"重新读入（<see cref="ReloadRequested"/> 由 <c>Program</c> 在下一帧开头执行，
/// 避免在 ImGui 帧中途换掉 GL 资源）。
/// </para>
/// <para>
/// 界面状态（选中项、Toast、截图/全屏请求）留在这里；截图必须在画 UI 之前执行，
/// 所以只记"被请求了"，由 <c>Program</c> 在正确的时机做。
/// </para>
/// </remarks>
internal sealed class ShaderToyUi : IDisposable
{
    /// <summary>100% DPI 下左侧库的宽度（实际值乘 <see cref="_scale"/>）。</summary>
    private const float LibraryWidth = 320f;

    private readonly Player _player;
    private readonly LibraryPanel _library;
    private readonly float _scale = 1f;
    private int _layoutWidth = -1;
    private int _layoutHeight = -1;
    private bool _screenshotRequested;
    private bool _fullscreenRequested;
    private string? _toast;
    private double _toastUntil;
    private bool _disposed;

    /// <param name="player">播放器。</param>
    /// <param name="libraryRoot">本地库根目录。</param>
    /// <param name="uiScale">DPI 缩放（面板尺寸跟着放大，否则高 DPI 下字号变大、面板反而放不下按钮）。</param>
    /// <param name="ownerWindow">"打开文件"对话框的父窗口句柄（0 表示不指定）。</param>
    public ShaderToyUi(Player player, string libraryRoot, float uiScale = 1f, nint ownerWindow = default)
    {
        ArgumentNullException.ThrowIfNull(player);

        _player = player;
        _scale = Math.Clamp(uiScale, 0.5f, 3f);
        _library = new LibraryPanel(libraryRoot, ownerWindow);
        _library.AssetsFetched += OnAssetsFetched;
        _library.ReloadRequested += () => ReloadRequested?.Invoke();
    }

    /// <summary>请求把某个 JSON 打开（拖拽/列表双击）；由 Program 负责真正加载。</summary>
    public event Action<string>? OpenRequested;

    /// <summary>请求从磁盘重新读入当前作品；由 Program 在下一帧开头执行。</summary>
    public event Action? ReloadRequested;

    /// <summary>库根目录（命令行 <c>--library</c> 或默认值）。</summary>
    public string LibraryRoot => _library.Root;

    /// <summary>请求截一张图（快捷键 F12 走这里，和按钮同一条路径）。</summary>
    public void RequestScreenshot() => _screenshotRequested = true;

    /// <summary>请求切换全屏（F11）。</summary>
    public void RequestFullscreen() => _fullscreenRequested = true;

    /// <summary>取走一次截图请求（每帧调一次）。</summary>
    public bool ConsumeScreenshotRequest()
    {
        bool requested = _screenshotRequested;
        _screenshotRequested = false;
        return requested;
    }

    /// <summary>取走一次全屏切换请求。</summary>
    public bool ConsumeFullscreenRequest()
    {
        bool requested = _fullscreenRequested;
        _fullscreenRequested = false;
        return requested;
    }

    /// <summary>在界面上显示一条短提示（截图路径、加载失败……）。</summary>
    public void Toast(string message)
    {
        _toast = message;
        _toastUntil = ImGui.GetTime() + 4.0;
    }

    /// <summary>
    /// 播放后更新列表里的"最近播放"（就地改，不重排 —— 顺序在扫描时已固定）。
    /// </summary>
    public void MarkPlayed(string projectPath, DateTimeOffset when) => _library.MarkPlayed(projectPath, when);

    /// <summary>
    /// 处理拖进窗口的文件：<c>.json</c> 直接打开；界面不编辑作品，其他文件只给提示。
    /// </summary>
    public void OnFileDropped(string path)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            OpenRequested?.Invoke(path);
            return;
        }

        Toast("只支持拖入 .json 工程文件；图片等资源请写进 project.json 后用\"重新加载\"。");
    }

    /// <summary>画全部窗口。<paramref name="width"/>/<paramref name="height"/> 是帧缓冲尺寸。</summary>
    public void Draw(int width, int height, double fps, bool imguiCapturesKeyboard)
    {
        // imgui.ini 会记住上次的窗口位置/尺寸；改 DPI 或换分辨率后旧布局会把面板挤扁，
        // 所以尺寸一变就强制重排一次（之后用户拖过的布局仍然会被记住）。
        bool relayout = width != _layoutWidth || height != _layoutHeight;
        _layoutWidth = width;
        _layoutHeight = height;
        ImGuiCond cond = relayout ? ImGuiCond.Always : ImGuiCond.FirstUseEver;

        float libraryWidth = LibraryWidth * _scale;

        // 左列上下分：上面"本地库"，下面"pass 列表"（原右侧面板挪到左下角）
        float libraryHeight = Math.Clamp(height * 0.58f, 240f * _scale, Math.Max(240f * _scale, height - 200f * _scale));

        ShaderLibraryEntry? open = _library.Draw(new Vector2(libraryWidth, libraryHeight), cond);

        if (open is not null)
        {
            OpenRequested?.Invoke(open.ProjectPath);
        }

        // 布局：左=库+pass 列表，右=主控（高度自适应内容，画面在它下面完整可见）
        float centerWidth = Math.Max(520f * _scale, width - libraryWidth);

        ImGui.SetNextWindowPos(new Vector2(libraryWidth, 0), cond);
        ImGui.SetNextWindowSize(new Vector2(centerWidth, 0), cond);
        DrawMainWindow(width, height, fps, imguiCapturesKeyboard);

        DrawPassListWindow(libraryWidth, libraryHeight, height, cond);
        DrawToast();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _library.Dispose();
    }

    /// <summary>补齐资源后：补的若是正在播的作品，请求一次重新加载（纹理才会真的换上）。</summary>
    private void OnAssetsFetched(string directory)
    {
        if (_player.SourceName == Player.BuiltinSource)
        {
            return;
        }

        string? projectDirectory;

        try
        {
            projectDirectory = Path.GetDirectoryName(Path.GetFullPath(_player.SourceName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (projectDirectory is null
            || !string.Equals(projectDirectory, directory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Toast("资源已补齐，正在重新加载…");
        ReloadRequested?.Invoke();
    }

    private void DrawMainWindow(int width, int height, double fps, bool imguiCapturesKeyboard)
    {
        Player player = _player;
        PlaybackClock clock = player.Clock;

        if (!ImGui.Begin("ShaderToy"))
        {
            ImGui.End();
            return;
        }

        ImGui.Text($"{fps:0.0} FPS    viewport {width}x{height}    target {(player.IsHalfFloat ? "RGBA16F" : "RGBA8")}");
        ImGui.Text($"time {clock.Time:0.00}s    frame {clock.Frame}    dt {clock.TimeDelta * 1000.0:0.0} ms    passes {player.PassCount} (buffer {player.BufferCount})");

        // 时间轴：拖动时 Seek，播放中每帧跟着走
        float seek = clock.Time;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat("##seek", ref seek, 0f, MathF.Max(120f, clock.Time + 1f), "%.2f s"))
        {
            player.Seek(seek);
        }

        // 按钮单独一行且"重新加载"排第一：窗口窄的时候也不会被挤出可视区
        if (ImGui.Button("重新加载"))
        {
            ReloadRequested?.Invoke();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("从磁盘重新读入当前作品。\n在外部编辑器里改完 project.json / textures 后点这里；读入失败时保留当前画面。");
        }

        ImGui.SameLine();
        if (ImGui.Button(clock.Paused ? "继续" : "暂停"))
        {
            clock.Paused = !clock.Paused;
        }

        ImGui.SameLine();
        if (ImGui.Button("重置"))
        {
            clock.Reset();
            player.Mouse.Reset();
            player.Seek(0f);
        }

        ImGui.SameLine();
        if (ImGui.Button("截图 F12"))
        {
            _screenshotRequested = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("全屏 F11"))
        {
            _fullscreenRequested = true;
        }

        ImGui.Text($"buffer 分辨率: {player.BufferResolution.Describe()}");
        ImGui.SameLine();
        if (ImGui.SmallButton("viewport##buf"))
        {
            player.SetBufferResolution(BufferResolutionMode.Viewport);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("1024x256##buf"))
        {
            player.SetBufferResolution(BufferResolutionMode.Fixed);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("scale 0.5##buf"))
        {
            player.SetBufferResolution(BufferResolutionMode.Scale);
        }

        DrawAudioLine();

        if (player.Error is { } error)
        {
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), "错误: " + error);
        }

        DrawCompileErrors();

        if (ImGui.CollapsingHeader("通道（只读）"))
        {
            DrawChannelInfo();
        }

        if (ImGui.CollapsingHeader("转译提示"))
        {
            DrawList(player.Notes);
        }

        if (ImGui.CollapsingHeader("导入提示"))
        {
            DrawList(player.ImportWarnings);
        }

        ImGui.Separator();
        ImGui.TextDisabled($"来源: {player.SourceName}");
        if (player.Project is { } project)
        {
            ImGui.TextDisabled(
                $"id {project.Metadata.Id}    author {(string.IsNullOrEmpty(project.Metadata.Author) ? "?" : project.Metadata.Author)}"
                + $"    passes {project.Passes.Count}");
        }

        ImGui.TextDisabled(imguiCapturesKeyboard ? "键盘被界面占用" : "空格 暂停 / R 重置 / F11 全屏 / F12 截图");
        ImGui.TextDisabled("界面不编辑作品：外部改文件后点\"重新加载\"");

        ImGui.End();
    }

    /// <summary>通道绑定（只读展示；改它请改 project.json 后重新加载）。</summary>
    private void DrawChannelInfo()
    {
        if (_player.Project is not { } project)
        {
            ImGui.TextDisabled("（没有加载作品）");
            return;
        }

        var any = false;

        foreach (ShaderPass pass in project.Passes)
        {
            if (pass.Inputs.Count == 0)
            {
                continue;
            }

            any = true;
            ImGui.TextDisabled(Label(pass));

            foreach (ShaderPassInput input in pass.Inputs)
            {
                ImGui.BulletText(DescribeInput(input));
            }
        }

        if (!any)
        {
            ImGui.TextDisabled("（没有输入通道）");
        }
    }

    private static string DescribeInput(ShaderPassInput input)
    {
        string sampler = $"{input.Sampler.Filter}/{input.Sampler.Wrap}" + (input.Sampler.VFlip ? "/vflip" : string.Empty);

        string target = input.Type switch
        {
            ShaderInputType.Buffer => "→ " + (string.IsNullOrEmpty(input.Id) ? "(未指定)" : input.Id),
            ShaderInputType.Cubemap when !string.IsNullOrEmpty(input.Id) => "→ " + input.Id,
            ShaderInputType.Music or ShaderInputType.MusicStream => "自产音频",
            ShaderInputType.Keyboard => "键盘纹理",
            _ => input.Path ?? "(空)",
        };

        return $"i{input.Channel}  {input.Type}  {target}  [{sampler}]";
    }

    private void DrawCompileErrors()
    {
        IReadOnlyList<PassCompileError> errors = _player.CompileErrors;
        if (errors.Count == 0)
        {
            return;
        }

        ImGui.Separator();
        ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"编译失败 {errors.Count} 个 pass（已跳过，其余照常渲染）");

        foreach (PassCompileError error in errors)
        {
            ImGui.PushID(error.PassName);

            if (ImGui.CollapsingHeader($"{error.PassName}    {(error.Line is { } line ? $"第 {line} 行" : "")}", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.PushFont(UiFonts.Mono);
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.55f, 1f), error.Message ?? "(无报错信息)");

                if (error.LineText is { Length: > 0 } text)
                {
                    ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.5f, 1f), $"{error.Line}: {text}");
                }

                if (error.Source is { Length: > 0 } source && error.Line is { } lineNumber)
                {
                    if (ImGui.Button("复制上下文"))
                    {
                        ImGui.SetClipboardText(ExtractContext(source, lineNumber));
                    }

                    ImGui.SameLine();
                    ImGui.TextDisabled("（复制的是转译后的 GLSL，行号与它对应）");
                }

                ImGui.PopFont();
            }

            ImGui.PopID();
        }
    }

    private void DrawAudioLine()
    {
        Player player = _player;

        if (player.HasSound)
        {
            AudioEngine? audio = player.Audio;
            string state = audio is { IsPlaying: true }
                ? $"播放中 {audio.Sink!.SampleRate} Hz    缓冲 {audio.Buffer.AvailableFrames * 1000 / AudioBuffer.SampleRate} ms    欠载 {audio.Buffer.UnderrunFrames} 帧"
                : "静音";
            ImGui.Text($"音频: {state}");
        }
        else if (player.AudioError is { } audioError)
        {
            ImGui.TextDisabled("音频: " + audioError);
        }
    }

    private void DrawPassListWindow(float leftWidth, float topOffset, int height, ImGuiCond cond)
    {
        // 左下角小窗：pass 快照（只读，点不动 —— 界面不编辑作品）
        ImGui.SetNextWindowPos(new Vector2(0f, topOffset), cond);
        ImGui.SetNextWindowSize(new Vector2(leftWidth, Math.Max(120f, height - topOffset)), cond);

        if (!ImGui.Begin("pass 列表", ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        if (_player.Project is not { } project || project.Passes.Count == 0)
        {
            ImGui.TextDisabled("（空）");
            ImGui.End();
            return;
        }

        float footer = ImGui.GetTextLineHeightWithSpacing() * 2f;
        if (ImGui.BeginChild("##pass-list", new Vector2(0, -footer)))
        {
            foreach (ShaderPass pass in project.Passes)
            {
                bool failed = _player.CompileErrors.Any(e =>
                    string.Equals(e.PassName, Label(pass), StringComparison.Ordinal));

                string line = $"{Label(pass)}  [{pass.Type}]  {pass.Inputs.Count} 输入{(failed ? "  ✗ 编译失败" : string.Empty)}";

                if (failed)
                {
                    ImGui.TextColored(new Vector4(1f, 0.55f, 0.45f, 1f), line);
                }
                else
                {
                    ImGui.TextUnformatted(line);
                }

                if (pass.Type == ShaderPassType.Buffer)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(_player.BufferResolution.Describe());
                }

                if (pass.Type == ShaderPassType.Cubemap)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"{CubeTarget.FaceSize}² ×6");
                }
            }
        }

        ImGui.EndChild();

        ImGui.TextDisabled("只读：改 project.json 后点\"重新加载\"");

        ImGui.End();
    }

    private void DrawToast()
    {
        if (_toast is null || ImGui.GetTime() > _toastUntil)
        {
            return;
        }

        ImGui.SetNextWindowPos(new Vector2((LibraryWidth * _scale) + (16f * _scale), 8f * _scale), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.85f);

        if (ImGui.Begin("##toast", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.TextUnformatted(_toast);
        }

        ImGui.End();
    }

    /// <summary>pass 的显示名（没有名字时用类型）。</summary>
    private static string Label(ShaderPass pass) =>
        string.IsNullOrEmpty(pass.Name) ? pass.Type.ToString() : pass.Name;

    private static void DrawList(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            ImGui.TextDisabled("（无）");
            return;
        }

        foreach (string line in lines)
        {
            ImGui.BulletText(line);
        }
    }

    private static string ExtractContext(string source, int line, int radius = 3)
    {
        string[] lines = source.Split('\n');
        int from = Math.Max(0, line - 1 - radius);
        int to = Math.Min(lines.Length - 1, line - 1 + radius);
        var builder = new System.Text.StringBuilder();

        for (int i = from; i <= to; i++)
        {
            builder.Append(i + 1).Append(i + 1 == line ? " > " : "   ").Append(lines[i].TrimEnd('\r')).Append('\n');
        }

        return builder.ToString();
    }
}
