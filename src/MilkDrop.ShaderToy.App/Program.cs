using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MilkDrop.Rendering.Gles3;
using MilkDrop.ShaderToy;
using MilkDrop.ShaderToy.Audio;
using MilkDrop.ShaderToy.Channels;
using MilkDrop.ShaderToy.Input;
using MilkDrop.ShaderToy.Pass;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGLES;
using Silk.NET.Windowing;

namespace MilkDrop.ShaderToy.App;

/// <summary>
/// ShaderToy 播放器的入口。
/// </summary>
/// <remarks>
/// P0 打通了 GLFW → ANGLE(EGL/GLES3) → ImGui 这条链路；P3 起真正渲染 image pass：
/// 转译 → 编译 → 画到 RGBA16F 离屏目标 → 拷到默认帧缓冲 → 叠 ImGui。
/// 用法：<c>MilkDrop.ShaderToy.App [project.json] [--fixed-dt 0.0166] [--pause]</c>
/// </remarks>
internal static class Program
{
    private static EglContext? _context;
    private static IInputContext? _input;
    private static ImguiGles3Renderer? _imguiRenderer;
    private static ImguiInput? _imguiInput;
    private static Player? _player;

    private static string? _shaderPath;
    private static string? _texturePath;
    private static double _fixedDt;
    private static bool _startPaused;
    private static bool _soundEnabled = true;
    private static bool _audioLog;
    private static double _audioLogAccum;
    private static HashSet<int> _pressedKeyCodes = [];
    private static readonly HashSet<Key> _pressedKeys = [];
    private static readonly HashSet<Key> _newlyPressedKeys = [];

    private static double _fps;
    private static double _fpsAccum;
    private static int _fpsFrames;

    private static int Main(string[] args)
    {
        // 日志里有中文，重定向到文件/管道时默认编码会mojibake
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        ParseArgs(args);

        var windowOptions = WindowOptions.Default with
        {
            // 自己建 EGL/GLES3 上下文（ANGLE），不让 Silk 建 OpenGL 上下文。
            API = GraphicsAPI.None,
            Size = new Vector2D<int>(1280, 720),
            Title = "MilkDrop.ShaderToy (C# port) — Silk.NET + ANGLE/GLES3 + ImGui",
            VSync = false,
            PreferredDepthBufferBits = 0,
            PreferredStencilBufferBits = 0,
            UpdatesPerSecond = 0,
            FramesPerSecond = 0,
        };

        // Silk.NET 默认靠反射发现窗口/输入后端，NativeAOT 裁剪后这条路走不通。
        Window.PrioritizeGlfw();

        using var window = Window.Create(windowOptions);
        window.Load += () => OnLoad(window);
        window.Render += delta => OnRender(window, delta);
        window.Closing += OnClosing;

        try
        {
            window.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Fatal: " + ex);
            return 1;
        }
    }

    private static void OnLoad(IWindow window)
    {
        if (window.Native?.Win32 is not { } win32)
        {
            throw new PlatformNotSupportedException("当前窗口句柄不是 Win32（ANGLE/EGL 路径只在 Windows 上验证）。");
        }

        var stopwatch = Stopwatch.StartNew();
        _context = EglContext.CreateForWindow(win32.Hwnd, vsync: false);
        stopwatch.Stop();

        Console.WriteLine($"EGL 上下文建立耗时: {stopwatch.ElapsedMilliseconds} ms");
        Console.WriteLine("EGL: " + _context.EglInfo);

        GpuCapabilities capabilities = _context.QueryCapabilities();
        Console.WriteLine("---- ANGLE / GLES 能力探测 ----");
        Console.WriteLine(capabilities.ToLogString());
        Console.WriteLine("------------------------------");

        _input = window.CreateInput();

        // 键盘用事件维护状态集合（IKeyboard 没有 PressedKeys，IsKeyPressed 只是状态查询）
        if (_input.Keyboards.Count > 0)
        {
            _input.Keyboards[0].KeyDown += (_, key, _) => OnKeyDown(key);
            _input.Keyboards[0].KeyUp += (_, key, _) => OnKeyUp(key);
        }

        ImGui.CreateContext();
        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        ImGui.StyleColorsDark();

        _imguiRenderer = new ImguiGles3Renderer(_context.Gl);
        _imguiInput = new ImguiInput(_input);

        Vector2D<int> framebuffer = window.FramebufferSize;

        // 图片资源：作品自带的在 JSON 旁边找；Shadertoy 下载的资源在 res/shadertoy 下找
        var texturePaths = new List<string>();
        if (_texturePath is not null)
        {
            texturePaths.Add(_texturePath);
        }

        texturePaths.Add(Path.Combine(Environment.CurrentDirectory, "res", "shadertoy"));

        _player = new Player(
            Math.Max(1, framebuffer.X),
            Math.Max(1, framebuffer.Y),
            capabilities.Rgba16fRenderable,
            capabilities.Texture3D,
            texturePaths,
            _soundEnabled ? CreateAudioSink : null);

        _player.Clock.FixedTimeStep = _fixedDt;
        _player.Clock.Paused = _startPaused;

        if (_shaderPath is null)
        {
            _player.LoadDemo();
            Console.WriteLine("没有指定作品，已加载内置演示。");
        }
        else if (_player.LoadFile(_shaderPath))
        {
            Console.WriteLine($"已加载: {_player.Title}  ({_shaderPath})");
        }
        else
        {
            Console.Error.WriteLine("加载失败: " + _player.Error);
        }

        foreach (string warning in _player.ImportWarnings)
        {
            Console.WriteLine("导入提示: " + warning);
        }

        foreach (string note in _player.Notes)
        {
            Console.WriteLine("转译提示: " + note);
        }

        Console.WriteLine($"目标格式: {(_player.IsHalfFloat ? "RGBA16F" : "RGBA8")}");
        Console.WriteLine($"音频: {DescribeAudio(_player)}");
        Console.WriteLine($"pass 数: {_player.PassCount}（buffer {_player.BufferCount}），buffer 分辨率: {_player.BufferResolution.Describe()}");
        Console.WriteLine($"上游: {ShaderToyInfo.UpstreamVersion}    后端: {ShaderToyInfo.Backend}");

        window.Title = $"MilkDrop.ShaderToy — {_player.Title}";
    }

    private static void OnRender(IWindow window, double deltaSeconds)
    {
        if (_context is null || _player is null)
        {
            return;
        }

        try
        {
            _context.MakeCurrent();

            GL gl = _context.Gl;
            Vector2D<int> framebuffer = window.FramebufferSize;
            int width = Math.Max(1, framebuffer.X);
            int height = Math.Max(1, framebuffer.Y);

            gl.Viewport(0, 0, (uint)width, (uint)height);
            gl.ClearColor(0f, 0f, 0f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);

            _player.Resize(width, height);

            // ImGui 要先 NewFrame 才能问 WantCaptureMouse；UI 内容在 shader 之后再加。
            _imguiInput?.NewFrame(width, height, deltaSeconds);
            bool imguiCapturesMouse = ImGui.GetIO().WantCaptureMouse;

            UpdatePlayerInput(window, width, height, deltaSeconds, imguiCapturesMouse, out bool imguiCapturesKeyboard);

            _player.Render();

            DrawUi(width, height, imguiCapturesKeyboard);
            ImGui.Render();
            _imguiRenderer?.Render(ImGui.GetDrawData(), width, height);

            _context.SwapBuffers();

            LogAudioIfNeeded(deltaSeconds);

            _fpsAccum += deltaSeconds;
            _fpsFrames++;
            if (_fpsAccum >= 0.5)
            {
                _fps = _fpsFrames / _fpsAccum;
                _fpsAccum = 0;
                _fpsFrames = 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("渲染失败: " + ex.Message);
        }
    }

    private static void UpdatePlayerInput(
        IWindow window,
        int width,
        int height,
        double deltaSeconds,
        bool imguiCapturesMouse,
        out bool imguiCapturesKeyboard)
    {
        Player player = _player!;

        IMouse? mouse = _input?.Mice.Count > 0 ? _input.Mice[0] : null;
        var windowSize = new Vector2(window.Size.X, window.Size.Y);
        var framebufferSize = new Vector2(width, height);

        Vector2 mousePixel = mouse is null
            ? Vector2.Zero
            : MouseState.FromWindowPoint(mouse.Position, windowSize, framebufferSize);

        bool leftDown = mouse is not null && mouse.IsButtonPressed(MouseButton.Left);

        player.Advance(deltaSeconds, mousePixel, leftDown, acceptMouse: !imguiCapturesMouse);

        imguiCapturesKeyboard = ImGui.GetIO().WantCaptureKeyboard;
        UpdateKeyboard(player, imguiCapturesKeyboard);
    }

    /// <summary>
    /// 键盘：热键（空格/R）用"本帧刚按下"判定，同时把按键状态灌进 <c>keyboard</c> 通道。
    /// </summary>
    /// <remarks>
    /// <see cref="IKeyboard.IsKeyPressed"/> 是状态查询不是事件，按住会每帧都返回 true ——
    /// 直接拿它切暂停会疯狂抖动，所以这里自己做按下沿检测。
    /// </remarks>
    private static void UpdateKeyboard(Player player, bool imguiCapturesKeyboard)
    {
        if (!imguiCapturesKeyboard)
        {
            if (_newlyPressedKeys.Contains(Key.Space))
            {
                player.Clock.Paused = !player.Clock.Paused;
            }

            if (_newlyPressedKeys.Contains(Key.R))
            {
                player.Clock.Reset();
                player.Mouse.Reset();
            }
        }

        KeyboardChannel? channel = player.Keyboard;
        if (channel is not null)
        {
            var currentCodes = new HashSet<int>();
            foreach (Key key in _pressedKeys)
            {
                int code = KeyboardKeyMap.ToKeyCode(key);
                if (code != 0)
                {
                    currentCodes.Add(code);
                }
            }

            foreach (int code in currentCodes)
            {
                if (!_pressedKeyCodes.Contains(code))
                {
                    channel.SetKey(code, true);
                }
            }

            foreach (int code in _pressedKeyCodes)
            {
                if (!currentCodes.Contains(code))
                {
                    channel.SetKey(code, false);
                }
            }

            _pressedKeyCodes = currentCodes;
        }

        _newlyPressedKeys.Clear();
    }

    private static void OnKeyDown(Key key)
    {
        if (_pressedKeys.Add(key))
        {
            _newlyPressedKeys.Add(key);
        }
    }

    private static void OnKeyUp(Key key) => _pressedKeys.Remove(key);

    private static void DrawUi(int width, int height, bool imguiCapturesKeyboard)
    {
        Player player = _player!;
        PlaybackClock clock = player.Clock;

        if (!ImGui.Begin("ShaderToy — P4/P5 pass graph"))
        {
            ImGui.End();
            return;
        }

        ImGui.Text($"{_fps:0.0} FPS    viewport {width}x{height}");
        ImGui.Text($"time {clock.Time:0.00}s    frame {clock.Frame}    dt {clock.TimeDelta * 1000.0:0.0} ms");
        ImGui.Text($"target {(player.IsHalfFloat ? "RGBA16F" : "RGBA8")}    passes {player.PassCount} (buffer {player.BufferCount})");

        Vector4 mouse = player.Mouse.Value;
        ImGui.Text($"iMouse {mouse.X:0},{mouse.Y:0}  click {mouse.Z:0},{mouse.W:0}  {(player.Mouse.IsPressed ? "down" : "up")}");

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

        ImGui.Separator();
        if (ImGui.Button(clock.Paused ? "Resume (Space)" : "Pause (Space)"))
        {
            clock.Paused = !clock.Paused;
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset (R)"))
        {
            clock.Reset();
            player.Mouse.Reset();
        }

        ImGui.SameLine();
        if (ImGui.Button("Quit"))
        {
            Environment.Exit(0);
        }

        ImGui.TextWrapped($"source: {player.SourceName}");
        if (player.Project is { } project)
        {
            ImGui.TextWrapped(
                $"id {project.Metadata.Id}    author {(string.IsNullOrEmpty(project.Metadata.Author) ? "?" : project.Metadata.Author)}"
                + $"    passes {project.Passes.Count}");
        }

        if (player.Error is { } error)
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), "错误: " + error);
        }

        if (player.ImportWarnings.Count > 0)
        {
            ImGui.Separator();
            if (ImGui.CollapsingHeader($"导入提示 ({player.ImportWarnings.Count})"))
            {
                foreach (string warning in player.ImportWarnings)
                {
                    ImGui.BulletText(warning);
                }
            }
        }

        if (player.Notes.Count > 0)
        {
            if (ImGui.CollapsingHeader($"转译提示 ({player.Notes.Count})"))
            {
                foreach (string note in player.Notes)
                {
                    ImGui.BulletText(note);
                }
            }
        }

        ImGui.Separator();
        ImGui.TextDisabled(imguiCapturesKeyboard ? "ImGui 正在接收键盘输入" : "快捷键已启用");
        ImGui.TextWrapped($"Upstream: {ShaderToyInfo.UpstreamVersion}    Backend: {ShaderToyInfo.Backend}");

        ImGui.End();
    }

    /// <summary>
    /// <c>--audio-log</c>：每 2 秒打印一次音频水位。
    /// </summary>
    /// <remarks>
    /// 耳朵听不到的时候，这是判断"声卡线程到底有没有在消费"的唯一证据：
    /// <c>已播</c> 一直不长就是设备没跑起来，<c>欠载</c> 一直涨就是 GPU 渲染跟不上。
    /// </remarks>
    private static void LogAudioIfNeeded(double deltaSeconds)
    {
        if (!_audioLog || _player?.Audio is not { IsPlaying: true } audio)
        {
            return;
        }

        _audioLogAccum += deltaSeconds;
        if (_audioLogAccum < 2.0)
        {
            return;
        }

        _audioLogAccum = 0;

        AudioBuffer buffer = audio.Buffer;
        Console.WriteLine(
            $"音频: 已渲染 {audio.SamplesRendered} 采样，已播 {buffer.TotalConsumedFrames} 帧，"
            + $"缓冲 {buffer.AvailableFrames * 1000 / AudioBuffer.SampleRate} ms，欠载 {buffer.UnderrunFrames} 帧");
    }

    /// <summary>打开音频设备；失败返回 null，由 Player 静音并在 UI 里说明。</summary>
    private static IAudioSink? CreateAudioSink()
    {
        try
        {
            return new WaveOutAudioSink();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("音频设备不可用: " + ex.Message);
            return null;
        }
    }

    private static string DescribeAudio(Player player)
    {
        if (!player.HasSound)
        {
            return player.AudioError ?? "作品没有 sound pass";
        }

        AudioEngine? audio = player.Audio;
        return audio is { IsPlaying: true }
            ? $"sound pass 已启用（{audio.Sink!.Description}）"
            : "静音：" + (player.AudioError ?? "未启动");
    }

    private static void OnClosing()
    {
        _imguiInput?.Dispose();
        _imguiRenderer?.Dispose();
        _player?.Dispose();

        if (_context is not null)
        {
            ImGui.DestroyContext();
            _context.Dispose();
            _context = null;
        }
    }

    private static void ParseArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "--fixed-dt":
                    if (i + 1 < args.Length
                        && double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double dt)
                        && dt > 0)
                    {
                        _fixedDt = dt;
                    }
                    else
                    {
                        Console.Error.WriteLine("--fixed-dt 需要一个正数（秒）。");
                    }

                    break;

                case "--pause":
                    _startPaused = true;
                    break;

                case "--no-sound":
                case "--mute":
                    _soundEnabled = false;
                    break;

                case "--audio-log":
                    _audioLog = true;
                    break;

                case "--textures":
                    if (i + 1 < args.Length)
                    {
                        _texturePath = args[++i];
                    }

                    break;

                case "--help":
                case "-h":
                    Console.WriteLine("用法: MilkDrop.ShaderToy.App [project.json] [--fixed-dt 0.0166] [--pause] [--no-sound] [--textures DIR]");
                    Console.WriteLine("  project.json  Shadertoy 导出 JSON；省略则播放内置演示");
                    Console.WriteLine("  --fixed-dt    固定时间步长（截图/回归用，同帧号 = 同状态）");
                    Console.WriteLine("  --pause       启动即暂停");
                    Console.WriteLine("  --no-sound    不出声（sound pass 仍编译，music 通道不更新）");
                    Console.WriteLine("  --audio-log   每 2 秒打印音频水位（已渲染/已播/缓冲/欠载）");
                    Console.WriteLine("  --textures    额外的图片搜索目录（作品自带的图片优先在 JSON 旁边找）");
                    break;

                default:
                    if (arg.StartsWith("--shader=", StringComparison.Ordinal))
                    {
                        _shaderPath = arg["--shader=".Length..];
                    }
                    else if (arg == "--shader" && i + 1 < args.Length)
                    {
                        _shaderPath = args[++i];
                    }
                    else if (!arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        _shaderPath = arg;
                    }
                    else
                    {
                        Console.Error.WriteLine("未知参数: " + arg);
                    }

                    break;
            }
        }
    }
}
