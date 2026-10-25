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
/// 用法：<c>MilkDrop.ShaderToy.App [作品ID|some.json] [--data DIR] [--fixed-dt 0.0166] [--pause]</c>
/// 数据来源：作品数据（工程 JSON / pass 代码 / 通道绑定）全部从数据根的 <c>shadertoy.db</c> 读，
/// 图片资源从数据根的 <c>media/</c> 读。
/// </remarks>
// 整个 App 本来就只跑 Windows x64（EGL/ANGLE + win32 句柄 + winmm 音频），
// 标注出来是为了让 CA1416 在调用 WaveOutAudioSink 时不再报警。
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
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
    private static string? _dataPath;
    private static string? _gpuSelector;
    private static GpuAdapter? _gpuAdapter;
    private static bool _gpuListOnly;
    private static string? _openPath;
    private static ShaderLibraryEntry? _openEntry;
    private static string? _shotPath;
    private static bool _listOnly;
    private static int _shotFrames = 30;
    private static int _renderedFrames;
    private static string? _lastRenderError;
    private static bool _selfCheckReload;
    private static bool _vsync = true;
    private static bool _reloadRequested;
    private static float _uiScaleOverride;
    private static float _uiScale = 1f;

    /// <summary>ImGui 布局文件名。三个 App 各自一份，避免互相覆盖。</summary>
    private const string ImGuiIniFileName = "imgui-shadertoy.ini";

    /// <summary>
    /// 设置 ImGui 布局文件的路径（null = 不读写，用于临时上下文）。
    /// ImGui.NET 1.90 的 <c>ImGuiIOPtr.IniFilename</c> 只有 getter，只能直接写 native 结构体字段；
    /// 字符串用 CoTaskMem 分配后故意不释放 —— ImGui 只保存指针，整个进程生命周期都要有效。
    /// </summary>
    private static unsafe void SetImGuiIniFileName(ImGuiIOPtr io, string? fileName)
        => io.NativePtr->IniFilename = fileName is null
            ? null
            : (byte*)Marshal.StringToCoTaskMemUTF8(fileName);
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

    private static int Main(string[] args)
    {
        // 日志里有中文，重定向到文件/管道时默认编码会mojibake
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        ParseArgs(args);

        // 纯命令行模式：不建窗口（库列表/显卡列表能在 CI 或批处理里跑）
        if (_listOnly)
        {
            return PrintLibrary();
        }

        if (_gpuListOnly)
        {
            return PrintGpus();
        }

        ResolveGpuAdapter();

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

        // 默认开垂直同步：跟显示器刷新率对齐，避免撕裂与满载空转（--no-vsync 可关掉，截图/跑分时用）
        _context = EglContext.CreateForWindow(win32.Hwnd, _vsync, _gpuAdapter);
        stopwatch.Stop();

        Console.WriteLine($"EGL 上下文建立耗时: {stopwatch.ElapsedMilliseconds} ms    VSync: {(_vsync ? "开（跟随显示器刷新率）" : "关")}");
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

        // 三个 App 默认都写 imgui.ini，装在同一目录时会互相覆盖布局，各自用独立文件名。
        SetImGuiIniFileName(io, ImGuiIniFileName);

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

        // 图片资源：作品自带的在 JSON 旁边找；站点资源在 <数据根>/media 树里找
        var texturePaths = new List<string>(ResolveTextureSearchPaths());

        _player = new Player(
            Math.Max(1, framebuffer.X),
            Math.Max(1, framebuffer.Y),
            capabilities.Rgba16fRenderable,
            capabilities.Texture3D,
            texturePaths,
            _soundEnabled ? CreateAudioSink : null);

        // 截图也落在数据目录里，不散到进程工作目录
        _player.ScreenshotDirectory = Path.Combine(ResolveDataRoot(), "screenshots");
        _player.Clock.FixedTimeStep = _fixedDt;
        _player.Clock.Paused = _startPaused;

        _ui = new ShaderToyUi(_player, ResolveDataRoot(), _uiScale, win32.Hwnd);
        _ui.OpenEntryRequested += entry => _openEntry = entry;
        _ui.OpenFileRequested += path => _openPath = path;
        _ui.ReloadRequested += RequestReload;

        Console.WriteLine($"数据目录: {ResolveDataRoot()}");
        Console.WriteLine($"数据库: {_ui.DatabasePath}    作品 {_ui.EntryCount} 个");
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
            LoadArgument(_shaderPath);
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
    /// <c>--selfcheck-reload</c>：验证"重新加载"确实是从**磁盘/数据库**重新读入，而不是把内存里的东西再编译一遍。
    /// </summary>
    /// <remarks>
    /// 做法：先把内存里的 image 代码改掉（不写盘），再调 <see cref="Player.Reload"/>；
    /// 如果代码又变回来源里的内容，说明事实来源确实是磁盘或数据库。整个过程不写任何文件。
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
            $"重载自检: Reload {(reloaded ? "成功" : "失败")}，代码已从来源（库/文件）恢复 = {restored}，"
            + $"pass 数 {player.PassCount}，编译错误 {player.CompileErrors.Count}");
    }

    /// <summary>
    /// 命令行参数：磁盘上的 JSON 直接读文件，否则当成作品 ID 去数据库里找。
    /// </summary>
    private static void LoadArgument(string argument)
    {
        if (File.Exists(argument))
        {
            OpenFile(argument);
            return;
        }

        ShaderLibrary library = ShaderLibrary.Scan(ResolveDataRoot());
        ShaderLibraryEntry? entry = library.Find(argument);

        if (entry is null)
        {
            Console.Error.WriteLine(
                $"数据库里没有 \"{argument}\"（库: {library.DatabasePath}），改播内置演示。");

            foreach (string warning in library.Warnings)
            {
                Console.Error.WriteLine("提示: " + warning);
            }

            _player!.LoadDemo();
            return;
        }

        OpenEntry(entry);
    }

    /// <summary>从数据库加载一个作品（列表双击/命令行 ID 共用）。</summary>
    private static void OpenEntry(ShaderLibraryEntry entry)
    {
        Player player = _player!;

        if (player.LoadDatabase(entry.DatabasePath, entry.Id))
        {
            Console.WriteLine($"已加载: {player.Title}  ({entry.Id} ← {entry.DatabasePath})");
        }
        else
        {
            Console.Error.WriteLine("加载失败: " + player.Error);
        }

        LogLoad(player);
    }

    /// <summary>加载一个磁盘上的 JSON 文件（拖进窗口/命令行路径）。</summary>
    private static void OpenFile(string path)
    {
        Player player = _player!;

        if (player.LoadFile(path))
        {
            Console.WriteLine($"已加载: {player.Title}  ({path})");
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
            _ui?.Toast("已重新读入");
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

            // 界面里双击（数据库里的作品）或拖入 JSON：下一帧开头再加载，避免在 ImGui 帧中途换掉 GL 资源
            if (_openEntry is { } entry)
            {
                _openEntry = null;
                OpenEntry(entry);
                window.Title = $"MilkDrop.ShaderToy — {_player.Title}";
            }
            else if (_openPath is { } path)
            {
                _openPath = null;
                OpenFile(path);
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

    /// <summary><c>--list</c>：打印数据库里的作品（ID、标题、作者、pass 数、缺图），不建窗口。</summary>
    private static int PrintLibrary()
    {
        string dataRoot = ResolveDataRoot();
        ShaderLibrary library = ShaderLibrary.Scan(dataRoot);

        Console.WriteLine($"数据目录: {dataRoot}");
        Console.WriteLine($"数据库: {library.DatabasePath}    共 {library.Entries.Count} 个作品");

        foreach (ShaderLibraryEntry entry in library.Entries)
        {
            string captured = entry.CapturedAt == DateTime.MinValue
                ? "时间未知"
                : entry.CapturedAt.ToString("yyyy-MM-dd HH:mm");

            // 细节（pass 数 / 缺图 / 标签）是按需查的：列表本身只读名字那几列
            ShaderDatabaseDetails? details = library.TryReadDetails(entry);
            string passes = details is null ? "?" : details.PassCount.ToString(CultureInfo.InvariantCulture);
            string missing = details is { MissingMedia: > 0 } ? $"    缺 {details.MissingMedia} 个图" : string.Empty;

            Console.WriteLine(
                $"{entry.Id,-10} {entry.Title}    [{entry.Author}]"
                + $"    {passes} pass    抓取于 {captured}{missing}");
        }

        foreach (string warning in library.Warnings)
        {
            Console.WriteLine("提示: " + warning);
        }

        return 0;
    }

    /// <summary>数据根目录：<c>--data</c> → 设置文件的 <c>dataDir</c> → exe 旁的 <c>stdata</c>。</summary>
    private static string ResolveDataRoot() => DataPaths.ResolveRoot(_dataPath);

    /// <summary><c>--gpu list</c>：打印本机所有 D3D 适配器（编号 / 名称 / 显存 / LUID），不建窗口。</summary>
    private static int PrintGpus()
    {
        IReadOnlyList<GpuAdapter> adapters = GpuAdapters.Enumerate();

        Console.WriteLine("D3D 适配器（用 --gpu <编号|名字片段> 指定，如 --gpu 1 / --gpu nvidia）:");
        Console.WriteLine(GpuAdapters.Describe(adapters));
        Console.WriteLine("不给 --gpu 时用系统默认卡，可在 Windows 设置 → 系统 → 显示 → 图形 里给本 exe 选“高性能/省电”。");

        return 0;
    }

    /// <summary>
    /// 解析要用哪块显卡：<c>--gpu</c>（命令行）优先，其次设置文件 <c>shadertoy.json</c> 的 <c>gpu</c>。
    /// </summary>
    /// <remarks>
    /// 没指定（或指定失败）时保持 <see cref="_gpuAdapter"/> 为 null —— 交给 ANGLE/D3D11 的默认适配器，
    /// 也就是系统"图形设置"里的偏好。多卡机器上顺手把候选列表打出来，省得用户去猜。
    /// </remarks>
    private static void ResolveGpuAdapter()
    {
        string? selector = _gpuSelector ?? AppSettings.LoadGpu();

        if (string.IsNullOrWhiteSpace(selector))
        {
            IReadOnlyList<GpuAdapter> available = GpuAdapters.Enumerate();

            if (available.Count > 1)
            {
                Console.WriteLine("检测到多块显卡（默认用系统首选，可用 --gpu 指定）:");
                Console.WriteLine(GpuAdapters.Describe(available));
            }

            return;
        }

        IReadOnlyList<GpuAdapter> adapters = GpuAdapters.Enumerate();
        GpuAdapter? adapter = GpuAdapters.Match(adapters, selector, out string? error);

        if (adapter is null)
        {
            Console.Error.WriteLine($"GPU 选择失败（{selector}）：{error}");
            Console.Error.WriteLine("可用适配器:");
            Console.Error.WriteLine(GpuAdapters.Describe(adapters));
            Console.Error.WriteLine("改为系统默认显卡。");
            return;
        }

        _gpuAdapter = adapter;
        Console.WriteLine($"GPU: 指定 {adapter}");
    }

    /// <summary>
    /// 图片搜索目录：作品目录由 <see cref="Player"/> 自己加在最前，这里给数据目录那一层。
    /// </summary>
    private static IReadOnlyList<string> ResolveTextureSearchPaths()
    {
        var paths = new List<string>();

        if (_texturePath is not null)
        {
            paths.Add(Path.GetFullPath(_texturePath));
        }

        paths.AddRange(DataPaths.TextureSearchPaths(ResolveDataRoot()));

        // 仓库里的示例资源（开发期用）
        paths.Add(Path.Combine(Environment.CurrentDirectory, "res", "shadertoy"));
        return paths;
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

                case "--data":
                    if (i + 1 < args.Length)
                    {
                        _dataPath = args[++i];
                    }
                    else
                    {
                        Console.Error.WriteLine("--data 需要一个数据目录路径。");
                    }

                    break;

                case "--list":
                    _listOnly = true;
                    break;

                case "--vsync":
                    _vsync = true;
                    break;

                case "--no-vsync":
                    _vsync = false;
                    break;

                case "--gpu":
                    if (i + 1 < args.Length)
                    {
                        string value = args[++i];

                        if (string.Equals(value, "list", StringComparison.OrdinalIgnoreCase))
                        {
                            _gpuListOnly = true;
                        }
                        else
                        {
                            _gpuSelector = value;
                        }
                    }
                    else
                    {
                        Console.Error.WriteLine("--gpu 需要一个适配器编号/名字片段（如 1、nvidia），或 list 列出所有显卡。");
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
                    Console.WriteLine("用法: MilkDrop.ShaderToy.App [作品ID|some.json] [--data DIR] [--fixed-dt 0.0166] [--pause] [--no-sound] [--textures DIR]");
                    Console.WriteLine("  作品ID        shadertoy.db 里的作品（如 flGyWc）；磁盘上的 json 路径则直接读文件；省略则播放内置演示");
                    Console.WriteLine("  --data DIR    数据目录（默认设置文件里的 dataDir，其次 exe 旁的 stdata/）");
                    Console.WriteLine("                作品数据来自其中的 shadertoy.db，图片来自其中的 media/");
                    Console.WriteLine("  --list        打印数据库里的作品后退出（不建窗口）");
                    Console.WriteLine("  --gpu X       用哪块显卡：编号（0/1，见 --gpu list）、名字片段（nvidia/radeon/intel）");
                    Console.WriteLine("                或 luid:1a2b3c4d；不给则用系统默认（跟随 Windows 图形设置里的高性能/省电）");
                    Console.WriteLine("  --gpu list    列出本机所有 D3D 适配器后退出（不建窗口）");
                    Console.WriteLine("  --no-vsync    关掉垂直同步（默认开，跟随显示器刷新率；截图/跑分时用这个）");
                    Console.WriteLine("  --fixed-dt    固定时间步长（截图/回归用，同帧号 = 同状态）");
                    Console.WriteLine("  --pause       启动即暂停");
                    Console.WriteLine("  --no-sound    不出声（sound pass 仍编译，music 通道不更新）");
                    Console.WriteLine("  --audio-log   每 2 秒打印音频水位（已渲染/已播/缓冲/欠载）");
                    Console.WriteLine("  --textures    额外的图片搜索目录（默认先找数据根的 media/）");
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
