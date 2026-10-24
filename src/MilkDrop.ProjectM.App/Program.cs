using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using MilkDrop.ProjectM.Rendering;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGLES;
using Silk.NET.Windowing;
using PresetPlaylist = MilkDrop.ProjectM.Playlist.Playlist;

namespace MilkDrop.ProjectM.App;

/// <summary>
/// 入口：窗口 + ANGLE/EGL 上下文 + 引擎 + ImGui 叠加层。
/// </summary>
/// <remarks>
/// 对应 <c>refcode/projectM/frontend-sdl-cpp-2.0.0-pre1</c> 里 SDL 前端的工作（建窗口、主循环、
/// 快捷键、预设切换），只是这里换成 Silk.NET.Windowing + ANGLE(EGL) + ImGui。
/// </remarks>
internal static class Program
{
    private static IWindow? _window;
    private static EglContext? _context;
    private static ImguiGles3Renderer? _imguiRenderer;
    private static bool _imguiContextCreated;
    private static ImguiInput? _imguiInput;
    private static IInputContext? _inputContext;
    private static Hotkeys? _hotkeys;
    private static GpuCapabilities? _capabilities;

    private static ProjectMEngine? _engine;
    private static AudioCapture? _audio;
    private static SyntheticAudio? _syntheticAudio;
    private static PresetLibrary _library = new();
    private static PresetPlaylist? _playlist;

    private static AppOptions _options = new();
    private static float _uiScale = 1f;

    private static double _elapsed;
    private static double _fpsAccum;
    private static int _fpsFrames;
    private static double _fps;
    private static int _framesRendered;
    private static int _swapFailures;
    private static bool _showDemo;
    private static bool _showHelp;
    private static bool _showUi = true;
    private static int _exitCode;
    private static string? _lastError;
    private static string? _lastScreenshot;

    private static string _searchText = string.Empty;

    /// <summary>100% DPI 下的字号（ImGui 默认 13px 偏小）。</summary>
    private const float BaseFontSize = 18f;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(nint dpiAwarenessContext);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [STAThread]
    private static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        if (options.ShowHelp)
        {
            AppOptions.PrintHelp();
            return 0;
        }

        _options = options;
        _showUi = options.ShowUi;

        // 日志里有中文，固定成 UTF-8，避免重定向到文件时变乱码。
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (Exception)
        {
            // 某些宿主不允许改编码，忽略。
        }

        _uiScale = options.UiScale > 0 ? options.UiScale : 1f;

        if (options.FontCheck)
        {
            return RunFontCheck();
        }

        // 必须在建窗口之前声明，否则整块画面会被系统按位图拉伸，字既小又糊。
        TryEnableDpiAwareness();

        var windowOptions = WindowOptions.Default with
        {
            API = GraphicsAPI.None,
            Size = new Vector2D<int>(options.Width, options.Height),
            Title = "MilkDrop.ProjectM (C# port) — Silk.NET + ANGLE/GLES3 + ImGui",
            // 全屏在建窗口时就定好：EGL surface 按客户区定型，事后改尺寸要多绕一圈。
            WindowState = options.Fullscreen ? WindowState.Fullscreen : WindowState.Normal,
            VSync = false,
            PreferredDepthBufferBits = 0,
            PreferredStencilBufferBits = 0,
            UpdatesPerSecond = 0,
            FramesPerSecond = 0,
        };

        // Silk.NET 默认靠反射（Assembly.Load）发现窗口/输入后端，NativeAOT 裁剪后这条路走不通，
        // 会抛 "Couldn't find a suitable window platform"。显式指定 GLFW 平台即可（JIT 下也更省事）。
        Window.PrioritizeGlfw();

        using var window = Window.Create(windowOptions);
        _window = window;

        window.Load += () => OnLoad(window, options);
        window.Render += OnRender;
        window.Resize += size => Console.WriteLine($"窗口尺寸: {size.X}x{size.Y}");
        window.Closing += OnClosing;

        try
        {
            window.Run();
            return _exitCode != 0 ? _exitCode : _lastError is null ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Fatal: " + ex);
            return 1;
        }
    }

    private static void OnLoad(IWindow window, AppOptions options)
    {
        if (window.Native?.Win32 is not { } win32)
        {
            throw new PlatformNotSupportedException("当前窗口句柄不是 Win32（ANGLE/EGL 路径只在 Windows 上验证）。");
        }

        // 高 DPI 下只放大字体与控件尺寸，**不改窗口尺寸**：
        // EGL surface 在 CreateForWindow 时按窗口客户区定型，OnLoad 里改 window.Size 属于异步请求，
        // 会出现"surface 还是旧尺寸、视口按新尺寸"的错位（画面只铺满左上角、截图尺寸也不对）。
        _uiScale = options.UiScale > 0 ? options.UiScale : QueryUiScale(win32.Hwnd);

        var stopwatch = Stopwatch.StartNew();
        _context = EglContext.CreateForWindow(win32.Hwnd, options.VSync);
        stopwatch.Stop();

        Console.WriteLine($"EGL 上下文建立耗时: {stopwatch.ElapsedMilliseconds} ms");
        Console.WriteLine("EGL: " + _context.EglInfo);

        _capabilities = _context.QueryCapabilities();
        Console.WriteLine("---- ANGLE / GLES 能力探测 ----");
        Console.WriteLine(_capabilities.ToLogString());
        Console.WriteLine("------------------------------");

        // 输入上下文同时供 ImGui 与快捷键使用（Silk 要求只创建一次）。
        if (!options.Bench)
        {
            _inputContext = window.CreateInput();

            if (options.ShowUi)
            {
                ImGui.CreateContext();
                _imguiContextCreated = true;
                var io = ImGui.GetIO();
                io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
                ImGui.StyleColorsDark();
                ImGui.GetStyle().ScaleAllSizes(_uiScale);

                if (!LoadUiFont(io, out string? uiFontPath))
                {
                    Console.WriteLine("未找到系统中文字体，ImGui 用默认字体（中文会显示为方块）。");
                }
                else
                {
                    Console.WriteLine("ImGui 中文字体: " + uiFontPath);
                }

                _imguiRenderer = new ImguiGles3Renderer(_context.Gl);
                _imguiInput = new ImguiInput(_inputContext);
            }

            _hotkeys = new Hotkeys(_inputContext);
            _hotkeys.Triggered += OnHotkey;
        }

        CreateEngine(window, options);

        if (_context is not null)
        {
            (int fbWidth, int fbHeight) = GetFramebufferSize(_context.Gl, window);
            Console.WriteLine($"窗口 {window.Size.X}x{window.Size.Y}    绘制缓冲 {fbWidth}x{fbHeight}    DPI 缩放 {_uiScale:0.##}x");
        }
    }

    private static void CreateEngine(IWindow window, AppOptions options)
    {
        if (_context is null)
        {
            return;
        }

        var textureDirectories = new List<string>(options.TextureDirectories);

        if (textureDirectories.Count == 0)
        {
            string defaultTextures = Path.Combine(AppContext.BaseDirectory, "textures");

            if (Directory.Exists(defaultTextures))
            {
                textureDirectories.Add(defaultTextures);
            }
        }

        (int fbWidth, int fbHeight) = GetFramebufferSize(_context.Gl, window);

        _engine = new ProjectMEngine(textureDirectories);
        _engine.SetWindowSize(fbWidth, fbHeight);
        ApplyOptionsToEngine(_engine, options);

        // 引擎请求换预设（时长到 / 节拍硬切）时自动加载下一个。
        _engine.PresetSwitchRequested += hardCut => PlayNext(smooth: !hardCut);

        _engine.PresetSwitchFailed += (file, message) =>
        {
            _lastError = $"{Path.GetFileName(file)}: {message}";
            Console.Error.WriteLine($"预设加载失败 {file}: {message}");
        };

        BuildLibrary(options);

        if (_engine.PresetLocked)
        {
            _engine.PresetLocked = false;
        }

        if (options.NoAudio)
        {
            if (options.SyntheticAudio)
            {
                _syntheticAudio = new SyntheticAudio(_engine.Pcm);
                Console.WriteLine("音频: 使用合成测试音（无人值守验证）");
            }
        }
        else
        {
            _audio = new AudioCapture(_engine.Pcm);
        }

        if (options.Bench)
        {
            IReadOnlyList<string> presets = !string.IsNullOrEmpty(options.BenchList)
                ? ReadPresetList(options.BenchList)
                : _library.All.Count > 0
                    ? _library.All
                    : DefaultPresets();

            if (options.BenchSkip > 0)
            {
                presets = presets.Skip(options.BenchSkip).ToArray();
            }

            if (options.BenchLimit > 0 && presets.Count > options.BenchLimit)
            {
                presets = presets.Take(options.BenchLimit).ToArray();
            }

            _exitCode = Benchmark.Run(_context.Gl, _engine, presets, Math.Max(1, options.BenchFrames),
                options.BenchReport, options.BenchShots, fbWidth, fbHeight,
                _syntheticAudio is null ? null : _syntheticAudio.Pump);

            window.Close();
            return;
        }

        // 启动时优先加载 --preset，否则加载清单里的第一个。
        if (!string.IsNullOrEmpty(options.PresetFile))
        {
            _library.AddFile(options.PresetFile);
            _engine.LoadPresetFile(options.PresetFile, smoothTransition: false);
        }
        else if (_library.All.Count > 0)
        {
            LoadPlaylistIndex(0, smooth: false);
        }
    }

    private static void ApplyOptionsToEngine(ProjectMEngine engine, AppOptions options)
    {
        if (options.FixedTimeStep > 0)
        {
            engine.FixedTimeStep = options.FixedTimeStep;
            engine.TargetFramesPerSecond = Math.Max(1, (int)Math.Round(1.0 / options.FixedTimeStep));
        }

        if (options.PresetDuration > 0)
        {
            engine.PresetDuration = options.PresetDuration;
        }

        if (options.BlendDuration > 0)
        {
            engine.SoftCutDuration = options.BlendDuration;
        }

        if (options.HardCut)
        {
            engine.HardCutEnabled = true;
        }

        if (options.HardCutSensitivity > 0)
        {
            engine.HardCutSensitivity = options.HardCutSensitivity;
        }

        if (options.BeatSensitivity > 0)
        {
            engine.BeatSensitivity = options.BeatSensitivity;
        }

        if (options.MeshX > 0 && options.MeshY > 0)
        {
            engine.SetMeshSize((uint)options.MeshX, (uint)options.MeshY);
        }

        if (options.TargetFps > 0)
        {
            engine.TargetFramesPerSecond = options.TargetFps;
        }

        if (options.AspectCorrection is { } aspect)
        {
            engine.AspectCorrection = aspect;
        }
    }

    private static void BuildLibrary(AppOptions options)
    {
        _library = new PresetLibrary();

        foreach (string directory in options.PresetDirectories)
        {
            int added = _library.AddDirectory(directory);
            Console.WriteLine($"扫描预设目录 {directory}: +{added}");
        }

        if (!string.IsNullOrEmpty(options.PlaylistFile))
        {
            int added = _library.AddPlaylistFile(options.PlaylistFile);
            Console.WriteLine($"读取播放列表 {options.PlaylistFile}: +{added}");
        }

        if (!string.IsNullOrEmpty(options.PresetFile))
        {
            // 只为让 --preset 的预设出现在 UI 列表里，实际加载在 CreateEngine 末尾。
            _library.AddFile(options.PresetFile);
        }

        // 没有显式指定时，尝试 exe 旁边的 presets 目录。
        if (_library.All.Count == 0)
        {
            string defaultPresets = Path.Combine(AppContext.BaseDirectory, "presets");

            if (Directory.Exists(defaultPresets))
            {
                int added = _library.AddDirectory(defaultPresets);
                Console.WriteLine($"扫描默认预设目录 {defaultPresets}: +{added}");
            }
        }

        _playlist = new PresetPlaylist();

        foreach (string preset in _library.All)
        {
            _playlist.AddItem(preset, PresetPlaylist.InsertAtEnd, allowDuplicates: false);
        }

        _playlist.SetShuffle(options.Shuffle);

        Console.WriteLine($"预设清单: {_library.All.Count} 个{(options.Shuffle ? "（随机播放）" : string.Empty)}");
    }

    /// <summary>读取预设清单文件（每行一个路径，<c>#</c> 开头为注释）。</summary>
    private static IReadOnlyList<string> ReadPresetList(string listPath)
    {
        if (!File.Exists(listPath))
        {
            Console.Error.WriteLine("预设清单不存在: " + listPath);
            return [];
        }

        var presets = new List<string>();

        foreach (string rawLine in File.ReadLines(listPath))
        {
            string line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            presets.Add(line);
        }

        Console.WriteLine($"预设清单 {listPath}: {presets.Count} 个");
        return presets;
    }

    private static IReadOnlyList<string> DefaultPresets()
    {
        var library = new PresetLibrary();
        string defaultPresets = Path.Combine(AppContext.BaseDirectory, "presets");

        if (Directory.Exists(defaultPresets))
        {
            library.AddDirectory(defaultPresets);
        }

        return library.All;
    }

    private static void OnRender(double deltaSeconds)
    {
        if (_context is not { } context || _window is not { } window)
        {
            return;
        }

        var gl = context.Gl;
        (int width, int height) = GetFramebufferSize(gl, window);

        try
        {
            context.MakeCurrent();

            _elapsed += deltaSeconds;
            _fpsAccum += deltaSeconds;
            _fpsFrames++;
            if (_fpsAccum >= 0.5)
            {
                _fps = _fpsFrames / _fpsAccum;
                _fpsAccum = 0;
                _fpsFrames = 0;
            }

            _syntheticAudio?.Pump();

            if (_engine is not null)
            {
                _engine.SetWindowSize(width, height);
                _engine.RenderFrame();
            }
            else
            {
                gl.Viewport(0, 0, (uint)width, (uint)height);
                gl.ClearColor(0.05f, 0.07f, 0.12f, 1f);
                gl.Clear((uint)GLEnum.ColorBufferBit);
            }

            if (_imguiRenderer is not null && _imguiInput is not null && _showUi)
            {
                _imguiInput.NewFrame(width, height, deltaSeconds);
                BuildUi();
                ImGui.Render();
                _imguiRenderer.Render(ImGui.GetDrawData(), width, height);
            }

            // smoke 模式前几帧做自检：确认渲染链路真的跑起来、GL 没报错。
            if (_options.SmokeFrames > 0 && _framesRendered < 3)
            {
                var glError = (int)gl.GetError();
                Console.WriteLine(
                    $"帧 {_framesRendered} 自检: 预设={_engine?.ActivePresetFileName ?? "-"}, "
                    + $"glGetError=0x{glError:X4}");
            }

            if (_options.ScreenshotEvery > 0 && _framesRendered % _options.ScreenshotEvery == 0)
            {
                SaveScreenshot($"frame-{_framesRendered:D5}.png");
            }

            // 无人值守的切换压测：每 N 帧换一个预设，覆盖"软切 → 转场结束 → 释放旧预设"这条路径。
            if (_options.AutoNextFrames > 0 && _framesRendered > 0 && _framesRendered % _options.AutoNextFrames == 0)
            {
                PlayNext(smooth: true);
            }

            context.SwapBuffers();
            _framesRendered++;

            if (_options.SmokeFrames > 0 && _framesRendered >= _options.SmokeFrames)
            {
                if (!string.IsNullOrEmpty(_options.ScreenshotPath))
                {
                    SaveScreenshot(_options.ScreenshotPath);
                }

                Console.WriteLine($"smoke: 已渲染 {_framesRendered} 帧，平均 {_fps:0.0} FPS，退出。");
                window.Close();
            }
        }
        catch (Exception ex)
        {
            _swapFailures++;
            _lastError = ex.Message;
            Console.Error.WriteLine("渲染失败: " + ex);
            window.Close();
        }
    }

    /// <summary>
    /// 真正可绘制的像素尺寸。
    /// </summary>
    /// <remarks>
    /// 高 DPI 下 <c>window.Size</c>（逻辑尺寸）与 drawing buffer 尺寸可能不一致；
    /// 视口、ReadPixels、ImGui 都必须用后者，否则画面只铺满左上角、截图尺寸也对不上。
    /// </remarks>
    private static (int Width, int Height) GetFramebufferSize(GL gl, IWindow window)
    {
        Vector2D<int> framebuffer = window.FramebufferSize;

        int width = framebuffer.X > 0 ? framebuffer.X : window.Size.X;
        int height = framebuffer.Y > 0 ? framebuffer.Y : window.Size.Y;

        return (Math.Max(1, width), Math.Max(1, height));
    }

    private static void OnHotkey(HotkeyCommand command)
    {
        // 全屏切换与引擎无关，先处理（--bench 下没有输入上下文，走不到这里）。
        if (command == HotkeyCommand.ToggleFullscreen)
        {
            ToggleFullscreen();
            return;
        }

        if (_engine is null || _window is null)
        {
            return;
        }

        switch (command)
        {
            case HotkeyCommand.TogglePause:
                _engine.PresetLocked = !_engine.PresetLocked;
                break;

            case HotkeyCommand.NextPreset:
                PlayNext(smooth: true);
                break;

            case HotkeyCommand.PreviousPreset:
                PlayPrevious();
                break;

            case HotkeyCommand.RandomPreset:
                if (_playlist is not null)
                {
                    _playlist.SetShuffle(true);
                    PlayNext(smooth: true);
                }

                break;

            case HotkeyCommand.ToggleLock:
                _engine.PresetLocked = !_engine.PresetLocked;
                break;

            case HotkeyCommand.Screenshot:
                SaveScreenshot($"shot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                break;

            case HotkeyCommand.ToggleUi:
                _showUi = !_showUi;
                break;

            case HotkeyCommand.ToggleHelp:
                _showHelp = !_showHelp;
                break;

            case HotkeyCommand.Quit:
                if (_showHelp)
                {
                    _showHelp = false;
                }
                else
                {
                    _window.Close();
                }

                break;
        }
    }

    /// <summary>F11：窗口 ⇄ 全屏。</summary>
    /// <remarks>
    /// 不重建 EGL 上下文：ANGLE 的 window surface 会跟着宿主 HWND 的 WM_SIZE 重新分配 backbuffer，
    /// 每帧的 <see cref="GetFramebufferSize"/> 拿到新尺寸后交给 <c>engine.SetWindowSize</c> 即可。
    /// </remarks>
    private static void ToggleFullscreen()
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            _window.WindowState = _window.WindowState == WindowState.Fullscreen
                ? WindowState.Normal
                : WindowState.Fullscreen;

            Console.WriteLine(
                $"全屏: {(_window.WindowState == WindowState.Fullscreen ? "开" : "关")} "
                + $"({_window.Size.X}x{_window.Size.Y})");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("切换全屏失败: " + ex.Message);
        }
    }

    private static void PlayNext(bool smooth)
    {
        if (_engine is null || _playlist is null || _library.All.Count == 0)
        {
            return;
        }

        LoadPlaylistIndex(_playlist.NextPresetIndex(), smooth);
    }

    private static void PlayPrevious()
    {
        if (_engine is null || _playlist is null || _library.All.Count == 0)
        {
            return;
        }

        LoadPlaylistIndex(_playlist.PreviousPresetIndex(), smooth: true);
    }

    private static void LoadPlaylistIndex(uint index, bool smooth)
    {
        if (_engine is null || index >= _library.All.Count)
        {
            return;
        }

        string path = _library.All[(int)index];
        _engine.LoadPresetFile(path, smooth);
    }

    private static void SaveScreenshot(string fileName)
    {
        if (_context is null || _window is null)
        {
            return;
        }

        try
        {
            // 只给文件名时落到 exe 旁的 artifacts/shots；带目录（或绝对路径）就按宿主给的路径。
            string path = Path.IsPathRooted(fileName)
                          || fileName.Contains(Path.DirectorySeparatorChar)
                          || fileName.Contains('/')
                ? Path.GetFullPath(fileName)
                : Path.Combine(AppContext.BaseDirectory, "artifacts", "shots", fileName);

            (int width, int height) = GetFramebufferSize(_context.Gl, _window);
            _lastScreenshot = Screenshot.Save(_context.Gl, width, height, path);
            Console.WriteLine("截图: " + _lastScreenshot);
        }
        catch (Exception ex)
        {
            _lastError = "截图失败: " + ex.Message;
            Console.Error.WriteLine(_lastError);
        }
    }

    private static void BuildUi()
    {
        if (_engine is null || _inputContext is null)
        {
            return;
        }

        ImGui.SetNextWindowPos(new Vector2(16, 16), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(780, 620), ImGuiCond.FirstUseEver);

        if (ImGui.Begin(UiText.WindowTitle))
        {
            string state = _engine.PresetLocked ? UiText.Paused : UiText.Playing;
            ImGui.Text(string.Format(UiText.StatusFormat, state, _fps, _framesRendered, _swapFailures));
            ImGui.Text(_audio is { IsRunning: true } ? UiText.AudioRunning : UiText.AudioUnavailable);

            string presetName = string.IsNullOrEmpty(_engine.ActivePresetFileName)
                ? UiText.NoPreset
                : Path.GetFileName(_engine.ActivePresetFileName);
            ImGui.Text(string.Format(UiText.CurrentPresetFormat, presetName));
            ImGui.ProgressBar((float)_engine.PresetProgress, new Vector2(-1, 0),
                string.Format(UiText.ProgressFormat, _engine.PresetProgress * 100));

            ImGui.Separator();
            ImGui.Text(UiText.PlaybackSection);

            if (ImGui.Button(UiText.PreviousPreset))
            {
                PlayPrevious();
            }

            ImGui.SameLine();
            if (ImGui.Button(UiText.NextPreset))
            {
                PlayNext(smooth: true);
            }

            ImGui.SameLine();
            if (ImGui.Button(UiText.RandomPreset))
            {
                OnHotkey(HotkeyCommand.RandomPreset);
            }

            ImGui.SameLine();
            bool locked = _engine.PresetLocked;
            if (ImGui.Checkbox(UiText.LockPreset, ref locked))
            {
                _engine.PresetLocked = locked;
            }

            ImGui.SameLine();
            if (ImGui.Button(UiText.TakeScreenshot))
            {
                SaveScreenshot($"shot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            }

            ImGui.Separator();
            ImGui.Text(UiText.ParametersSection);

            float beat = _engine.BeatSensitivity;
            if (ImGui.SliderFloat(UiText.BeatSensitivity, ref beat, 0.0f, 2.0f))
            {
                _engine.BeatSensitivity = beat;
            }

            float duration = (float)_engine.PresetDuration;
            if (ImGui.SliderFloat(UiText.PresetDuration, ref duration, 1.0f, 120.0f))
            {
                _engine.PresetDuration = duration;
            }

            float blend = (float)_engine.SoftCutDuration;
            if (ImGui.SliderFloat(UiText.BlendDuration, ref blend, 0.0f, 10.0f))
            {
                _engine.SoftCutDuration = blend;
            }

            bool hardCut = _engine.HardCutEnabled;
            if (ImGui.Checkbox(UiText.HardCutEnabled, ref hardCut))
            {
                _engine.HardCutEnabled = hardCut;
            }

            float hardCutSensitivity = _engine.HardCutSensitivity;
            if (ImGui.SliderFloat(UiText.HardCutSensitivity, ref hardCutSensitivity, 0.1f, 8.0f))
            {
                _engine.HardCutSensitivity = hardCutSensitivity;
            }

            float easterEgg = _engine.EasterEgg;
            if (ImGui.SliderFloat(UiText.RandomDuration, ref easterEgg, 0.0f, 10.0f))
            {
                _engine.EasterEgg = easterEgg;
            }

            int meshX = (int)_engine.MeshSize.X;
            int meshY = (int)_engine.MeshSize.Y;
            if (ImGui.SliderInt(UiText.MeshSize + " X", ref meshX, 8, 200))
            {
                _engine.SetMeshSize((uint)meshX, (uint)meshY);
            }

            if (ImGui.SliderInt(UiText.MeshSize + " Y", ref meshY, 8, 200))
            {
                _engine.SetMeshSize((uint)meshX, (uint)meshY);
            }

            bool aspect = _engine.AspectCorrection;
            if (ImGui.Checkbox(UiText.AspectCorrection, ref aspect))
            {
                _engine.AspectCorrection = aspect;
            }

            ImGui.Separator();
            ImGui.Text(UiText.PresetList);

            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##search", UiText.SearchHint, ref _searchText, 128);

            IReadOnlyList<string> filtered = _library.Filter(_searchText);
            ImGui.Text(string.Format(UiText.PresetCountFormat, _library.All.Count, filtered.Count));

            if (ImGui.BeginChild("preset-list", new Vector2(0, 220), ImGuiChildFlags.Border))
            {
                // 不做虚拟滚动，直接截断：几千条 Selectable 会拖慢 UI，搜索框足够用。
                const int maxRows = 400;
                int rowCount = Math.Min(filtered.Count, maxRows);

                for (var i = 0; i < rowCount; i++)
                {
                    string path = filtered[i];

                    if (ImGui.Selectable(Path.GetFileName(path)))
                    {
                        _engine.LoadPresetFile(path, smoothTransition: true);
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(path);
                    }
                }

                if (filtered.Count > maxRows)
                {
                    ImGui.Text(string.Format(UiText.PresetListTruncatedFormat, maxRows));
                }

                ImGui.EndChild();
            }

            ImGui.Separator();
            ImGui.Checkbox("ImGui demo", ref _showDemo);

            if (ImGui.Button(UiText.RequeryCapabilities))
            {
                _capabilities = _context?.QueryCapabilities();
                Console.WriteLine(_capabilities?.ToLogString());
            }

            if (_lastScreenshot is not null)
            {
                ImGui.Text(string.Format(UiText.ScreenshotSavedFormat, _lastScreenshot));
            }

            if (_lastError is not null)
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), UiText.ErrorPrefix + _lastError);
            }
        }

        ImGui.End();

        if (_showHelp)
        {
            ImGui.SetNextWindowPos(new Vector2(820, 16), ImGuiCond.FirstUseEver);
            if (ImGui.Begin(UiText.HotkeysTitle, ref _showHelp))
            {
                ImGui.TextUnformatted(UiText.HotkeysBody);
            }

            ImGui.End();
        }

        if (_showDemo)
        {
            ImGui.ShowDemoWindow(ref _showDemo);
        }
    }

    private static void OnClosing()
    {
        Console.WriteLine($"关闭：共渲染 {_framesRendered} 帧，最后 FPS {_fps:0.0}，总时长 {_elapsed:0.0}s");

        _hotkeys?.Dispose();
        _audio?.Dispose();
        _engine?.Dispose();

        _hotkeys = null;
        _audio = null;
        _engine = null;

        _imguiInput?.Dispose();
        _imguiRenderer?.Dispose();
        _inputContext?.Dispose();
        _context?.Dispose();

        _imguiInput = null;
        _imguiRenderer = null;
        _inputContext = null;
        _context = null;

        if (_imguiContextCreated)
        {
            ImGui.DestroyContext();
            _imguiContextCreated = false;
        }
    }

    /// <summary>默认字体没有中文字形，这里挂一个系统中文字体（找不到就退回默认字体）。</summary>
    private static unsafe bool LoadUiFont(ImGuiIOPtr io, out string? fontPath)
    {
        var fontSize = BaseFontSize * _uiScale;
        fontPath = null;

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyhl.ttc"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "simhei.ttf"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "simsun.ttc"),
        ];

        // 只加载界面真正用到的字：ChineseSimplifiedCommon（约 2500 字）会漏"频""帧"这类字，
        // ChineseFull（2 万字）又把图集撑到 4096²。按 UiText 精确取字符两边都避开。
        ushort[] glyphRanges = BuildGlyphRanges();

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                fixed (ushort* ranges = glyphRanges)
                {
                    io.Fonts.AddFontFromFileTTF(path, fontSize, null, (nint)ranges);
                }

                fontPath = path;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("加载中文字体失败: " + ex.Message);
            }
        }

        return false;
    }

    /// <summary>
    /// 生成 ImGui 的 glyph range：ASCII 可见区 + <see cref="UiText"/> 里出现的每个非 ASCII 字符。
    /// </summary>
    /// <remarks>格式是 <c>[begin, end]</c> 成对出现，最后以 0 结尾。</remarks>
    private static ushort[] BuildGlyphRanges()
    {
        var characters = new SortedSet<ushort>();

        for (var code = 0x20; code <= 0x7E; code++)
        {
            characters.Add((ushort)code);
        }

        foreach (string text in GetUiTexts())
        {
            foreach (char c in text)
            {
                if (c > 0x7E)
                {
                    characters.Add(c);
                }
            }
        }

        var ranges = new List<ushort>(characters.Count * 2 + 2);

        foreach (ushort character in characters)
        {
            ranges.Add(character);
            ranges.Add(character);
        }

        ranges.Add(0);
        return ranges.ToArray();
    }

    /// <summary>
    /// 只做字体检查：建 ImGui 上下文 → 挂中文字体 → 建图集 → 检查 <see cref="UiText"/>
    /// 里每个非 ASCII 字符是否有字形。不建窗口、不碰 GL。
    /// </summary>
    /// <returns>0 = 全部有字形；1 = 有缺字或没找到中文字体。</returns>
    private static int RunFontCheck()
    {
        ImGui.CreateContext();

        try
        {
            var io = ImGui.GetIO();
            bool loaded = LoadUiFont(io, out string? fontPath);

            // 触发图集构建（CPU 侧，不需要 GL 上下文）。
            io.Fonts.GetTexDataAsRGBA32(out nint pixels, out int atlasWidth, out int atlasHeight, out int bytesPerPixel);
            _ = pixels;

            string[] texts = GetUiTexts();
            var missing = new SortedSet<char>();

            if (loaded && io.Fonts.Fonts.Size > 0)
            {
                var font = io.Fonts.Fonts[0];

                foreach (string text in texts)
                {
                    foreach (char c in text)
                    {
                        if (c <= 0x7F)
                        {
                            continue; // ASCII 一定在默认字形里
                        }

                        if (FindGlyph(font, c) is null)
                        {
                            missing.Add(c);
                        }
                    }
                }
            }

            Console.WriteLine("---- ImGui 中文字体检查 ----");
            Console.WriteLine("字体文件: " + (fontPath ?? "<未找到，使用默认字体>"));
            Console.WriteLine($"字号: {BaseFontSize * _uiScale:0.#}px    图集: {atlasWidth}x{atlasHeight} ({bytesPerPixel} bpp)");
            Console.WriteLine($"检查文案: {texts.Length} 条，缺字: {missing.Count} 个");

            if (missing.Count > 0)
            {
                Console.WriteLine("缺失字符: " + string.Join(' ', missing));
            }

            bool ok = loaded && missing.Count == 0;
            Console.WriteLine(ok ? "结果: 界面中文全部有字形" : "结果: 有缺字，界面会显示成方块");
            return ok ? 0 : 1;
        }
        finally
        {
            ImGui.DestroyContext();
        }
    }

    /// <summary>在字体图集里找字形；没有则返回 null（<c>ImFontGlyphPtr</c> 的指针为空）。</summary>
    private static object? FindGlyph(ImFontPtr font, char c)
    {
        unsafe
        {
            var glyph = font.FindGlyphNoFallback(c);
            return glyph.NativePtr == null ? null : glyph;
        }
    }

    /// <summary>取 <see cref="UiText"/> 里的全部字符串常量（界面文案的唯一来源）。</summary>
    private static string[] GetUiTexts()
        => typeof(UiText)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                       | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
            .Select(field => (string?)field.GetRawConstantValue())
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .ToArray();

    /// <summary>按窗口所在显示器 DPI 算缩放倍数（96 DPI = 1.0）。</summary>
    private static float QueryUiScale(nint hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
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
            SetProcessDpiAwarenessContext(-4);
        }
        catch (Exception ex)
        {
            Console.WriteLine("设置 DPI 感知失败: " + ex.Message);
        }
    }
}
