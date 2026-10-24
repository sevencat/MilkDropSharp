using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using MilkDrop.Rendering.Gles3;
using MilkDrop.ShaderToy;
using MilkDrop.ShaderToy.App.Ui;
using MilkDrop.ShaderToy.Audio;
using MilkDrop.ShaderToy.Channels;
using MilkDrop.ShaderToy.Import;
using MilkDrop.ShaderToy.Input;
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Renderer;
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
    private static ShaderToyUi? _ui;

    private static string? _shaderPath;
    private static string? _texturePath;
    private static string? _libraryPath;
    private static string? _openPath;
    private static string? _shotPath;
    private static string? _downloadTarget;
    private static string? _importPath;
    private static string? _fetchMissingTarget;
    private static string? _proxyArgument;
    private static bool _listOnly;
    private static bool _downloadAsCopy;
    private static int _shotFrames = 30;
    private static int _renderedFrames;
    private static string? _lastRenderError;
    private static bool _selfCheckReload;
    private static bool _reloadRequested;
    private static float _uiScaleOverride;
    private static float _uiScale = 1f;
    private static readonly Stopwatch _runStopwatch = Stopwatch.StartNew();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(nint dpiAwarenessContext);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
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
    private static double _fpsWindowStart;
    private static int _fpsFrames;

    private static async Task<int> Main(string[] args)
    {
        // 日志里有中文，重定向到文件/管道时默认编码会mojibake
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        ParseArgs(args);

        // 下载/补齐资源都要过代理：命令行 > 环境变量 > 设置文件 > 内置默认（本机 socks5）
        ShaderToyDownloader.SetDefaultProxy(ResolveProxy());

        // 纯命令行模式：不建窗口（库列表/下载都能在 CI 或批处理里跑）
        if (_listOnly)
        {
            return PrintLibrary();
        }

        if (_downloadTarget is not null)
        {
            return await RunDownloadAsync().ConfigureAwait(false);
        }

        if (_importPath is not null)
        {
            return RunImport();
        }

        if (_fetchMissingTarget is not null)
        {
            return await RunFetchMissingAsync().ConfigureAwait(false);
        }

        ResolveShaderArgument();

        // 必须在建窗口之前声明 DPI 感知：否则系统把整块画面按位图拉伸，字既小又糊
        TryEnableDpiAwareness();

        // 感知生效后窗口尺寸按**物理像素**算：175% DPI 下 1280x720 的点数其实是 2240x1260，
        // 不乘这个系数，窗口就只有 731x411 点、界面被挤扁
        float startupScale = _uiScaleOverride > 0 ? _uiScaleOverride : QuerySystemScale();
        Vector2D<int> windowSize = ScaleToScreen(1280, 720, startupScale);

        Console.WriteLine($"启动尺寸: {windowSize.X}x{windowSize.Y} 像素（DPI {startupScale:0.##}x）");

        var windowOptions = WindowOptions.Default with
        {
            // 自己建 EGL/GLES3 上下文（ANGLE），不让 Silk 建 OpenGL 上下文。
            API = GraphicsAPI.None,
            Size = windowSize,
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

        // 高 DPI：只放大字体与控件尺寸，不改窗口尺寸（EGL surface 在 CreateForWindow 时已定型）
        _uiScale = _uiScaleOverride > 0 ? _uiScaleOverride : QueryUiScale(win32.Hwnd);
        ImGui.GetStyle().ScaleAllSizes(_uiScale);

        // 字体图集在渲染器构造时上传，必须在它之前挂字体
        UiFonts.Setup(_uiScale);

        _imguiRenderer = new ImguiGles3Renderer(_context.Gl);
        _imguiInput = new ImguiInput(_input);

        // 拖文件进窗口：JSON 直接播，图片塞给选中通道
        window.FileDrop += OnFileDrop;

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

        _ui = new ShaderToyUi(_player, ResolveLibraryRoot(), _uiScale, win32.Hwnd);
        _ui.OpenRequested += path => _openPath = path;
        _ui.ReloadRequested += RequestReload;

        Console.WriteLine($"本地库: {_ui.LibraryRoot}");
        Console.WriteLine(
            $"DPI 缩放: {_uiScale:0.##}x（界面字体 {18f * _uiScale:0.#}px / 代码 {17f * _uiScale:0.#}px）"
            + $"    窗口 {window.Size.X}x{window.Size.Y}    绘制缓冲 {framebuffer.X}x{framebuffer.Y}");

        if (_shaderPath is null)
        {
            _player.LoadDemo();
            Console.WriteLine("没有指定作品，已加载内置演示。");
        }
        else
        {
            OpenProject(_shaderPath);
        }

        if (_selfCheckReload)
        {
            RunReloadSelfCheck();
        }

        Console.WriteLine($"上游: {ShaderToyInfo.UpstreamVersion}    后端: {ShaderToyInfo.Backend}");

        // 帧率窗口从"加载完"开始算：否则第一帧把编译/上传的 1.6s 算进去，界面上会长时间停在 0.6 FPS
        _fpsWindowStart = _runStopwatch.Elapsed.TotalSeconds;
        _fpsFrames = 0;

        window.Title = $"MilkDrop.ShaderToy — {_player.Title}";
    }

    /// <summary>
    /// <c>--selfcheck-reload</c>：验证"重新加载"确实是从**磁盘**重新读入，而不是把内存里的东西再编译一遍。
    /// </summary>
    /// <remarks>
    /// 做法：先把内存里的 image 代码改掉（不写盘），再调 <see cref="Player.Reload"/>；
    /// 如果代码又变回文件里的内容，说明事实来源确实是磁盘。整个过程不动用户的文件。
    /// </remarks>
    private static void RunReloadSelfCheck()
    {
        Player player = _player!;

        if (player.Project?.ImagePass is not { } image)
        {
            Console.Error.WriteLine("重载自检: 当前作品没有 image pass，跳过。");
            return;
        }

        if (player.SourceName == Player.BuiltinSource)
        {
            Console.Error.WriteLine("重载自检: 当前是内置演示，没有文件可读，跳过。");
            return;
        }

        string original = image.Code;
        image.Code = "// --selfcheck-reload: 内存里的假改动，重新加载后应当消失";

        bool reloaded = player.Reload();
        bool restored = string.Equals(player.Project?.ImagePass?.Code, original, StringComparison.Ordinal);

        Console.WriteLine(
            $"重载自检: Reload {(reloaded ? "成功" : "失败")}，代码已从文件恢复 = {restored}，"
            + $"pass 数 {player.PassCount}，编译错误 {player.CompileErrors.Count}");
    }

    /// <summary>加载一个作品并把关键信息打到控制台（启动和运行中切换共用）。</summary>
    private static void OpenProject(string path)
    {
        Player player = _player!;

        if (player.LoadFile(path))
        {
            Console.WriteLine($"已加载: {player.Title}  ({path})");

            // 本地库里的作品（有 meta.json）记一次播放时间：只就地更新那一项，
            // 不重扫列表 —— 顺序在扫描时就固定，双击播放不该把左栏顺序打乱
            DateTimeOffset playedAt = DateTimeOffset.Now;
            if (LocalLibrary.MarkPlayed(path, playedAt))
            {
                _ui?.MarkPlayed(path, playedAt);
            }
        }
        else
        {
            Console.Error.WriteLine("加载失败: " + player.Error);
        }

        LogLoad(player);
    }

    /// <summary>
    /// 界面上的"重新加载"：下一帧开头从磁盘重新读入当前作品。
    /// </summary>
    /// <remarks>
    /// 不在这里直接调 <see cref="Player.Reload"/>：事件是在 ImGui 帧中途触发的，
    /// 此刻换管线/纹理相当于把正在用的 GL 资源拔掉。
    /// </remarks>
    private static void RequestReload()
    {
        if (_player is null)
        {
            return;
        }

        if (_player.SourceName == Player.BuiltinSource)
        {
            _ui?.Toast("当前是内置演示，没有可重新读入的文件。");
            return;
        }

        _reloadRequested = true;
    }

    /// <summary>从磁盘重新读入当前作品，失败时保留当前画面（只更新错误与提示）。</summary>
    private static void ReloadProject(IWindow window)
    {
        Player player = _player!;
        string source = player.SourceName;

        if (player.Reload())
        {
            Console.WriteLine($"已重新读入: {player.Title}  ({source})");
            LogLoad(player);
            _ui?.Toast("已从文件重新读入");
        }
        else
        {
            Console.Error.WriteLine("重新读入失败: " + player.Error);
            _ui?.Toast("重新读入失败: " + player.Error);
        }

        window.Title = $"MilkDrop.ShaderToy — {player.Title}";
    }

    private static void LogLoad(Player player)
    {
        foreach (string warning in player.ImportWarnings)
        {
            Console.WriteLine("导入提示: " + warning);
        }

        foreach (string note in player.Notes)
        {
            Console.WriteLine("转译提示: " + note);
        }

        foreach (PassCompileError error in player.CompileErrors)
        {
            Console.Error.WriteLine($"编译失败 [{error.PassName}] 第 {error.Line?.ToString() ?? "?"} 行: {error.Message}");
        }

        Console.WriteLine($"目标格式: {(player.IsHalfFloat ? "RGBA16F" : "RGBA8")}");
        Console.WriteLine($"音频: {DescribeAudio(player)}");
        Console.WriteLine($"pass 数: {player.PassCount}（buffer {player.BufferCount}），buffer 分辨率: {player.BufferResolution.Describe()}");
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

            // 界面里双击/拖拽进来的作品：下一帧开头再加载，避免在 ImGui 帧中途换掉 GL 资源
            if (_openPath is { } path)
            {
                _openPath = null;
                OpenProject(path);
                window.Title = $"MilkDrop.ShaderToy — {_player.Title}";
            }

            // "重新加载"同理：界面上点完，等到下一帧开头再重新读盘
            if (_reloadRequested)
            {
                _reloadRequested = false;
                ReloadProject(window);
            }

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

            // 截图必须在画 UI 之前：默认帧缓冲里此刻只有 shader 输出
            if (_ui?.ConsumeScreenshotRequest() == true)
            {
                if (_player.Screenshot() is { } shot)
                {
                    Console.WriteLine("截图: " + shot);
                    _ui.Toast("已保存 " + shot);
                }
                else
                {
                    _ui.Toast("没有可截的画面");
                }
            }

            if (_ui?.ConsumeFullscreenRequest() == true)
            {
                ToggleFullscreen(window);
            }

            _ui?.Draw(width, height, _fps, imguiCapturesKeyboard);
            ImGui.Render();
            _imguiRenderer?.Render(ImGui.GetDrawData(), width, height);

            // --shot：等界面稳定几帧后把"整窗"（含 UI）存图并退出，供回归/肉眼检查
            bool closeAfterSwap = false;

            if (_shotPath is { } shotPath && ++_renderedFrames >= _shotFrames)
            {
                double seconds = _runStopwatch.Elapsed.TotalSeconds;
                Console.WriteLine(
                    $"截图（{_fps:0.0} FPS，全程 {_renderedFrames / Math.Max(0.001, seconds):0.0} FPS / {seconds:0.0}s；"
                    + $"窗口 {_fpsFrames} 帧 / {_fpsAccum:0.00}s；dt {deltaSeconds * 1000:0.0} ms）: "
                    + Screenshot.Save(gl, width, height, shotPath));
                closeAfterSwap = true;
            }

            _context.SwapBuffers();

            // 关窗口必须在 swap 之后：窗口销毁后再 eglSwapBuffers 会 NRE
            if (closeAfterSwap)
            {
                window.Close();
            }

            LogAudioIfNeeded(deltaSeconds);

            // 帧率用自己的表算：Silk 给的 deltaSeconds 在首帧之后就不太可信（会停在启动那一下的值上）
            _fpsFrames++;
            double now = _runStopwatch.Elapsed.TotalSeconds;
            _fpsAccum = now - _fpsWindowStart;

            if (_fpsAccum >= 0.5)
            {
                _fps = _fpsFrames / _fpsAccum;
                _fpsFrames = 0;
                _fpsWindowStart = now;
            }
        }
        catch (Exception ex)
        {
            // 每帧都报同一个错会把控制台刷爆，只在错误内容变化时打完整堆栈
            if (_lastRenderError != ex.Message)
            {
                _lastRenderError = ex.Message;
                Console.Error.WriteLine("渲染失败: " + ex);
            }
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
        // 功能键不受 ImGui 影响：编辑 shader 时也想按 F11/F12
        if (_newlyPressedKeys.Contains(Key.F12))
        {
            // 截图走同一入口：等本帧 shader 画完再读帧缓冲（直接读会拿到上一帧的残影）
            _ui?.RequestScreenshot();
        }

        if (_newlyPressedKeys.Contains(Key.F11))
        {
            _ui?.RequestFullscreen();
        }

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

    /// <summary>系统 DPI（建窗口之前用，此时还没有 HWND）。</summary>
    private static float QuerySystemScale()
    {
        try
        {
            uint dpi = GetDpiForSystem();
            if (dpi > 0)
            {
                return Math.Clamp(dpi / 96f, 1f, 3f);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("查询系统 DPI 失败，按 100% 处理: " + ex.Message);
        }

        return 1f;
    }

    /// <summary>把"100% DPI 下的逻辑尺寸"换算成物理像素，并夹到屏幕可用范围内。</summary>
    private static Vector2D<int> ScaleToScreen(int width, int height, float scale)
    {
        var pixelWidth = (int)Math.Round(width * scale);
        var pixelHeight = (int)Math.Round(height * scale);

        try
        {
            int screenWidth = GetSystemMetrics(0);   // SM_CXSCREEN
            int screenHeight = GetSystemMetrics(1);  // SM_CYSCREEN

            if (screenWidth > 0 && screenHeight > 0)
            {
                pixelWidth = Math.Min(pixelWidth, (int)(screenWidth * 0.92));
                pixelHeight = Math.Min(pixelHeight, (int)(screenHeight * 0.88));
            }
        }
        catch (Exception)
        {
            // 查不到屏幕尺寸就用原值
        }

        return new Vector2D<int>(Math.Max(640, pixelWidth), Math.Max(400, pixelHeight));
    }

    /// <summary>按窗口所在显示器的 DPI 算界面缩放（96 DPI = 1.0）。</summary>
    private static float QueryUiScale(nint hwnd)
    {
        try
        {
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi > 0)
            {
                return Math.Clamp(dpi / 96f, 1f, 3f);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("查询 DPI 失败，按 100% 处理: " + ex.Message);
        }

        return 1f;
    }

    private static void TryEnableDpiAwareness()
    {
        try
        {
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
            bool ok = SetProcessDpiAwarenessContext(-4);
            int error = Marshal.GetLastWin32Error();

            Console.WriteLine(
                ok
                    ? "DPI 感知: 已设为 PerMonitorV2"
                    : $"DPI 感知: 设置失败（GetLastError={error}，manifest 里可能已经声明过）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("设置 DPI 感知失败: " + ex.Message);
        }
    }

    private static void ToggleFullscreen(IWindow window) =>
        window.WindowState = window.WindowState == WindowState.Fullscreen
            ? WindowState.Normal
            : WindowState.Fullscreen;

    /// <summary>拖进来的文件：JSON 交给播放器，图片交给当前选中的通道。</summary>
    private static void OnFileDrop(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                _openPath = path;
            }
            else
            {
                _ui?.OnFileDropped(path);
            }
        }
    }

    /// <summary>
    /// <c>--shader &lt;id&gt;</c>：参数不是磁盘文件时按本地库里的 ID / 目录名解析成 project.json。
    /// </summary>
    private static void ResolveShaderArgument()
    {
        if (_shaderPath is null || File.Exists(_shaderPath))
        {
            return;
        }

        string root = ResolveLibraryRoot();
        ShaderLibraryEntry? entry = ShaderLibrary.Scan(root).Find(_shaderPath);

        if (entry is null)
        {
            Console.Error.WriteLine($"本地库里没有找到 \"{_shaderPath}\"（库: {root}），改播内置演示。");
            _shaderPath = null;
            return;
        }

        Console.WriteLine($"按 ID 解析: {_shaderPath} → {entry.ProjectPath}");
        _shaderPath = entry.ProjectPath;
    }

    /// <summary><c>--list</c>：打印本地库（ID、标题、作者、占用、缺资源），不建窗口。</summary>
    private static int PrintLibrary()
    {
        string root = ResolveLibraryRoot();
        ShaderLibrary library = ShaderLibrary.Scan(root);

        Console.WriteLine($"本地库: {root}    共 {library.Entries.Count} 个作品");

        foreach (ShaderLibraryEntry entry in library.Entries)
        {
            long size = LocalLibrary.ComputeSize(entry);
            string played = entry.LastPlayed is { } last
                ? last.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
                : "从未播放";
            string missing = entry.MissingResources > 0 ? $"    缺 {entry.MissingResources} 资源" : string.Empty;
            string favorite = entry.Favorite ? "    ★" : string.Empty;

            Console.WriteLine(
                $"{entry.Id,-10} {entry.Title}    [{entry.Author}]"
                + $"    {LocalLibrary.DescribeSize(size)}    播放于 {played}{missing}{favorite}");
            Console.WriteLine("    " + entry.ProjectPath);
        }

        foreach (string warning in library.Warnings)
        {
            Console.WriteLine("提示: " + warning);
        }

        return 0;
    }

    /// <summary><c>--download &lt;id|url&gt;</c>：只下载不建窗口（UI 里的下载走同一套代码）。</summary>
    private static async Task<int> RunDownloadAsync()
    {
        string root = ResolveLibraryRoot();
        Console.WriteLine($"本地库: {root}");

        using HttpClient http = ShaderToyDownloader.CreateHttpClient();
        var downloader = new ShaderToyDownloader(root, http);

        DownloadResult result = await downloader
            .DownloadAsync(
                _downloadTarget!,
                new DownloadOptions { Overwrite = !_downloadAsCopy },
                new ConsoleProgress())
            .ConfigureAwait(false);

        Console.WriteLine(result.Describe());

        foreach (string asset in result.Assets)
        {
            Console.WriteLine("  资源: " + asset);
        }

        foreach (MissingResource missing in result.Missing)
        {
            Console.WriteLine($"  缺: {missing.Url}（{missing.Error}）");
        }

        foreach (string warning in result.Warnings)
        {
            Console.WriteLine("  提示: " + warning);
        }

        return result.Success ? 0 : 1;
    }

    /// <summary>命令行进度（直接打印，不经线程池，保证顺序）。</summary>
    private sealed class ConsoleProgress : IProgress<string>
    {
        public void Report(string value) => Console.WriteLine("  " + value);
    }

    /// <summary>代理优先级：命令行 &gt; 环境变量 &gt; 设置文件。</summary>
    private static string ResolveProxy()
    {
        if (_proxyArgument is not null)
        {
            return _proxyArgument;
        }

        string? environment = Environment.GetEnvironmentVariable("MILKDROP_PROXY");
        return string.IsNullOrWhiteSpace(environment) ? HttpSettings.LoadProxy() : environment.Trim();
    }

    /// <summary>
    /// <c>--fetch-missing &lt;id|目录&gt;</c>：把作品里还指向站点的资源抓下来存进 <c>textures/</c>。
    /// </summary>
    private static async Task<int> RunFetchMissingAsync()
    {
        string root = ResolveLibraryRoot();
        string target = _fetchMissingTarget!;

        // 先按库里的 ID/目录名解析；解析不到就把参数当路径用
        ShaderLibraryEntry? entry = ShaderLibrary.Scan(root).Find(target);
        string directory = entry?.Directory ?? target;

        Console.WriteLine($"本地库: {root}");
        Console.WriteLine($"代理: {ShaderToyDownloader.DefaultProxyUrl ?? "直连"}");
        Console.WriteLine($"目标: {directory}");

        var fetcher = new MissingResourceFetcher();
        FetchResult result = await fetcher
            .FetchAsync(directory, new DownloadOptions(), new ConsoleProgress())
            .ConfigureAwait(false);

        Console.WriteLine(result.Describe());

        foreach (MissingResource missing in result.StillMissing)
        {
            Console.WriteLine($"  仍缺: {missing.Url}（{missing.Error}）");
        }

        // 有资源没补上也返回非 0，方便批处理判断
        return result.Success && result.Failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// <c>--import &lt;file.json&gt;</c>：把本地 json 拷进库（文件名即 ID），不建窗口。
    /// </summary>
    private static int RunImport()
    {
        string root = ResolveLibraryRoot();
        Console.WriteLine($"本地库: {root}");

        var importer = new LocalJsonImporter(root);
        ImportResult result = importer.Import(_importPath!, overwrite: !_downloadAsCopy);

        Console.WriteLine(result.Describe());

        foreach (string asset in result.Assets)
        {
            Console.WriteLine("  资源: " + asset);
        }

        foreach (string warning in result.Warnings)
        {
            Console.WriteLine("  提示: " + warning);
        }

        return result.Success ? 0 : 1;
    }

    /// <summary>本地库根目录：先看 <c>--library</c>，再找 exe 旁的 <c>shaders/</c>，最后退回仓库里的示例目录。</summary>
    private static string ResolveLibraryRoot()
    {
        if (_libraryPath is not null)
        {
            return Path.GetFullPath(_libraryPath);
        }

        foreach (string candidate in (string[])
                 [
                     Path.Combine(AppContext.BaseDirectory, "shaders"),
                     Path.Combine(Environment.CurrentDirectory, "shaders"),
                     Path.Combine(Environment.CurrentDirectory, "res", "shadertoy", "stpreset"),
                 ])
        {
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "shaders"));
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
        _ui?.Dispose();
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

                case "--library":
                    if (i + 1 < args.Length)
                    {
                        _libraryPath = args[++i];
                    }

                    break;

                case "--list":
                    _listOnly = true;
                    break;

                case "--download":
                    if (i + 1 < args.Length)
                    {
                        _downloadTarget = args[++i];
                    }
                    else
                    {
                        Console.Error.WriteLine("--download 需要一个作品 ID 或链接。");
                    }

                    break;

                case "--as-copy":
                    _downloadAsCopy = true;
                    break;

                case "--import":
                    if (i + 1 < args.Length)
                    {
                        _importPath = args[++i];
                    }
                    else
                    {
                        Console.Error.WriteLine("--import 需要一个本地 json 路径。");
                    }

                    break;

                case "--fetch-missing":
                    if (i + 1 < args.Length)
                    {
                        _fetchMissingTarget = args[++i];
                    }
                    else
                    {
                        Console.Error.WriteLine("--fetch-missing 需要作品 ID 或作品目录。");
                    }

                    break;

                case "--proxy":
                    if (i + 1 < args.Length)
                    {
                        _proxyArgument = args[++i];
                    }
                    else
                    {
                        Console.Error.WriteLine("--proxy 需要一个代理地址（如 socks5://127.0.0.1:1080，none 表示直连）。");
                    }

                    break;

                case "--shot":
                    if (i + 1 < args.Length)
                    {
                        _shotPath = args[++i];
                    }

                    break;

                case "--ui-scale":
                    if (i + 1 < args.Length
                        && float.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out float uiScale)
                        && uiScale > 0)
                    {
                        _uiScaleOverride = Math.Clamp(uiScale, 0.5f, 3f);
                    }

                    break;

                case "--selfcheck-reload":
                    _selfCheckReload = true;
                    break;

                case "--shot-frames":
                    if (i + 1 < args.Length
                        && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int shotFrames)
                        && shotFrames > 0)
                    {
                        _shotFrames = shotFrames;
                    }

                    break;

                case "--help":
                case "-h":
                    Console.WriteLine("用法: MilkDrop.ShaderToy.App [project.json|作品ID] [--library DIR] [--fixed-dt 0.0166] [--pause] [--no-sound] [--textures DIR]");
                    Console.WriteLine("  project.json  Shadertoy 导出 JSON，或本地库里的作品 ID（如 flGyWc）；省略则播放内置演示");
                    Console.WriteLine("  --library     本地库根目录（默认 exe 旁的 shaders/，其次 res/shadertoy/stpreset）");
                    Console.WriteLine("  --list        打印本地库内容后退出（不建窗口）");
                    Console.WriteLine("  --download X  下载作品 X（ID 或链接）到本地库后退出；--as-copy 让同名作品另存为 <id>-2");
                    Console.WriteLine("  --import FILE 把本地 json 导入本地库后退出（文件名即作品 ID；旁边的图片一起带上）");
                    Console.WriteLine("  --fetch-missing X  把作品 X（ID 或目录）里还指向 shadertoy 的资源抓到 textures/");
                    Console.WriteLine("  --proxy URL   下载/补齐资源用的代理，默认 socks5://127.0.0.1:1080；none 表示直连");
                    Console.WriteLine("                （也可用环境变量 MILKDROP_PROXY，或界面左侧\"代理\"一栏持久化）");
                    Console.WriteLine("  --fixed-dt    固定时间步长（截图/回归用，同帧号 = 同状态）");
                    Console.WriteLine("  --pause       启动即暂停");
                    Console.WriteLine("  --no-sound    不出声（sound pass 仍编译，music 通道不更新）");
                    Console.WriteLine("  --audio-log   每 2 秒打印音频水位（已渲染/已播/缓冲/欠载）");
                    Console.WriteLine("  --textures    额外的图片搜索目录（作品自带的图片优先在 JSON 旁边找）");
                    Console.WriteLine("  --ui-scale F  界面缩放倍数（默认按窗口 DPI 推断，1 = 96 DPI）");
                    Console.WriteLine("  --shot PATH   N 帧后把整窗（含界面）存成 PNG 并退出");
                    Console.WriteLine("  --shot-frames N  配合 --shot，默认 30");
                    Console.WriteLine("快捷键: 空格 暂停 / R 重置 / F11 全屏 / F12 截图；文件可直接拖进窗口");
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
