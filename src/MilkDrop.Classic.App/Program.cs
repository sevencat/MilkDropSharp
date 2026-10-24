using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using MilkDrop.Classic.Render.D3D9;
using MilkDrop.Classic.Visualizer;
using MilkDrop.Classic.Visualizer.Audio;
using MilkDrop.Classic.Visualizer.Sprites;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;

namespace MilkDrop.Classic.App;

/// <summary>
/// 入口。对应原版 <c>Milkdrop2PcmVisualizer.cpp</c> 的 <c>WinMain</c>（行 680）：
/// 建窗口 → 建 D3D9 设备 → 主循环 RenderFrame + Present。
/// </summary>
internal static class Program
{
    private static D3D9Context? _context;
    private static D3D9RenderBackend? _backend;
    private static ImguiDx9Renderer? _imguiRenderer;
    private static ImguiInput? _input;
    private static VisualizerEngine? _visualizer;

    private static IWindow? _window;
    private static AppOptions _options = new();

    /// <summary>ImGui 布局文件名。三个 App 各自一份，避免互相覆盖。</summary>
    private const string ImGuiIniFileName = "imgui-classic.ini";

    /// <summary>
    /// 设置 ImGui 布局文件的路径（null = 不读写，用于临时上下文）。
    /// ImGui.NET 1.90 的 <c>ImGuiIOPtr.IniFilename</c> 只有 getter，只能直接写 native 结构体字段；
    /// 字符串用 CoTaskMem 分配后故意不释放 —— ImGui 只保存指针，整个进程生命周期都要有效。
    /// </summary>
    private static unsafe void SetImGuiIniFileName(ImGuiIOPtr io, string? fileName)
        => io.NativePtr->IniFilename = fileName is null
            ? null
            : (byte*)Marshal.StringToCoTaskMemUTF8(fileName);

    private static double _elapsed;
    private static double _fpsAccum;
    private static int _fpsFrames;
    private static double _fps = 60;

    // 退出时打印的平均帧时间（区分"引擎 CPU 提交耗时"与"整帧墙体时间"）。
    private static int _perfFrames;
    private static long _perfCpuTicks;
    private static long _perfFrameTicks;
    private static bool _showDemo;
    private static bool _captured;
    private static bool _pendingClose;
    private static Vector2D<int>? _pendingResize;
    private static string _status = "starting";
    private static string? _lastError;

    /// <summary>资源目录里的全部预设（排序后，N/P 按这个顺序走）。</summary>
    private static List<string> _presetFiles = [];

    /// <summary><c>sprites/</c> 里的图片（测试 sprite 用；原版靠 milk2_img.ini 定义）。</summary>
    private static List<string> _spriteFiles = [];
    private static int _spriteIndex;

    private static int _presetIndex = -1;
    private static string? _presetName;
    private static readonly Random PresetRandom = new();

    // ---- 预设调度 / 混合（对应 plugin.cpp:1022-1030 的 m_fTimeBetweenPresets 等默认值）----

    /// <summary>两次自动换预设之间的间隔（秒，<c>m_fTimeBetweenPresets</c>）。</summary>
    private static float _timeBetweenPresets = 16f;

    /// <summary>间隔的随机抖动上限（秒，<c>m_fTimeBetweenPresetsRand</c>）。</summary>
    private static float _timeBetweenPresetsRand = 10f;

    /// <summary>自动换预设时的交叉时长（秒，<c>m_fBlendTimeAuto</c>）。</summary>
    private static float _blendTimeAuto = 2.7f;

    /// <summary>手动换预设时的交叉时长（秒，<c>m_fBlendTimeUser</c>）。</summary>
    private static float _blendTimeUser = 1.7f;

    /// <summary>下一次自动换预设的时间戳；负数 = 还没安排。</summary>
    private static float _nextPresetTime = -1f;

    /// <summary>锁定当前预设（<c>m_bPresetLockedByUser</c>）：只阻止自动切换，手动切换照常。</summary>
    private static bool _presetLocked;

    /// <summary>UI 缩放倍数（1 = 96 DPI）。字体、控件尺寸、面板间距都按它等比放大。</summary>
    private static float _uiScale = 1f;

    /// <summary>100% DPI 下的字号（ImGui 默认 13px 偏小）。</summary>
    private const float BaseFontSize = 18f;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(nint dpiAwarenessContext);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [STAThread]
    private static int Main(string[] args)
    {
        // 必须在建窗口之前声明：否则系统会把整块画面按位图拉伸，字既小又糊。
        TryEnableDpiAwareness();

        var options = AppOptions.Parse(args);

        // 显式注册 GLFW 后端。Silk.NET 默认靠扫描已加载程序集发现平台，
        // Native AOT 下这条发现路径被裁掉，不写会抛 "Couldn't find a suitable window platform"。
        GlfwWindowing.Use();

        // 输入后端同理：AOT 下 Silk.NET 扫不到 IInputPlatform（GlfwInputPlatform 是 internal，
        // 没法直接 Add），只能按程序集名让它自己注册；对应程序集在 csproj 里用 TrimmerRootAssembly 保住。
        if (!InputWindowExtensions.TryAdd("Silk.NET.Input.Glfw"))
        {
            Console.WriteLine("警告：GLFW 输入后端注册失败，键鼠输入不可用。");
        }

        var windowOptions = WindowOptions.Default with
        {
            API = GraphicsAPI.None,
            Size = new Vector2D<int>(options.Width, options.Height),
            Title = "MilkDrop.Classic (C# port) — D3D9 + Silk.NET + ImGui",
            VSync = false,
            PreferredDepthBufferBits = 0,
            PreferredStencilBufferBits = 0,
            UpdatesPerSecond = 0,
            FramesPerSecond = 0,
        };

        using var window = Window.Create(windowOptions);
        _window = window;
        _options = options;

        window.Load += () => OnLoad(window, options);
        window.Resize += OnResize;
        window.Update += OnUpdate;
        window.Render += OnRender;
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

    private static void OnLoad(IWindow window, AppOptions options)
    {
        if (window.Native?.Win32 is not { } win32)
        {
            throw new PlatformNotSupportedException("当前窗口句柄不是 Win32（D3D9 后端只能在 Windows 上运行）。");
        }

        // 高 DPI 屏上 16px 字体太小：按窗口 DPI 等比放大字体 + 控件尺寸，
        // 并且窗口本身也放大（没显式指定 --width/--height 时）。
        _uiScale = options.UiScale > 0 ? options.UiScale : QueryUiScale(win32.Hwnd);

        if (!options.HasExplicitSize && _uiScale > 1.001f)
        {
            window.Size = new Vector2D<int>(
                (int)(options.Width * _uiScale),
                (int)(options.Height * _uiScale));

            // 尺寸已经直接用于下面的设备创建，不需要再走一遍 Reset 流程
            _pendingResize = null;
        }

        _context = D3D9Context.Create(win32.Hwnd, window.Size.X, window.Size.Y, options.VSync);

        _backend = new D3D9RenderBackend(_context);

        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;

        // 三个 App 默认都写 imgui.ini，装在同一目录时会互相覆盖布局，各自用独立文件名。
        SetImGuiIniFileName(io, ImGuiIniFileName);

        ImGui.StyleColorsDark();

        // 控件间距/边框/滚动条等按同一倍数放大，否则字大了会顶出控件
        if (_uiScale > 1.001f)
        {
            ImGui.GetStyle().ScaleAllSizes(_uiScale);
        }

        if (!LoadUiFont(io))
        {
            // 没找到中文字体时退回 ImGui 默认字体，只能靠全局缩放让它别太小
            io.FontGlobalScale = _uiScale;
        }

        Console.WriteLine($"UI 缩放: {_uiScale:0.##}x（字体 {BaseFontSize * _uiScale:0}px）");

        _imguiRenderer = new ImguiDx9Renderer(_context.Device);
        _imguiRenderer.CreateResources();

        _input = new ImguiInput(window.CreateInput());
        _showDemo = options.ShowDemoWindow;

        // 预设调度参数（默认值见字段声明处的注释）
        if (options.PresetDuration is { } presetDuration)
        {
            _timeBetweenPresets = Math.Max(1f, presetDuration);
        }

        if (options.BlendTime is { } blendTime)
        {
            _blendTimeAuto = _blendTimeUser = Math.Max(0f, blendTime);
        }

        _presetLocked = options.LockPreset;

        _visualizer = new VisualizerEngine(_backend, CreateAudioSource(options))
        {
            Mode = options.Pattern == "fan" ? VisualizerMode.FanPattern : VisualizerMode.FeedbackSpike,
            ShaderCompat = options.ShaderCompat,
        };

        InitializePresetLibrary();

        if (options.RandomPreset)
        {
            LoadRandomPreset();
        }
        else if (options.Preset is { } requested)
        {
            LoadPresetByNameOrPath(requested);
        }

        if (options.Sprite is { } sprite)
        {
            LaunchSpriteByNameOrPath(sprite);
        }

        _status = DescribeEnvironment();
        Console.WriteLine(_status);
    }

    // ---------------- 预设切换 ----------------

    private static void InitializePresetLibrary()
    {
        try
        {
            _presetFiles = ResourceLocator.EnumeratePresets()
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _presetFiles = [];
            Console.WriteLine("枚举预设失败: " + ex.Message);
        }

        Console.WriteLine($"可用预设: {_presetFiles.Count} 个");

        try
        {
            _spriteFiles = Directory.Exists(ResourceLocator.SpritesDir)
                ? Directory.EnumerateFiles(ResourceLocator.SpritesDir)
                    .Where(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                   || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                   || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                                   || path.EndsWith(".jfif", StringComparison.OrdinalIgnoreCase)
                                   || path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
                                   || path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
        }
        catch (Exception ex)
        {
            _spriteFiles = [];
            Console.WriteLine("枚举 sprite 失败: " + ex.Message);
        }
    }

    /// <summary>按 <c>--sprite</c> 的参数挂 sprite：先当文件路径，再当 sprites 目录里的文件名片段。</summary>
    private static void LaunchSpriteByNameOrPath(string nameOrPath)
    {
        if (_visualizer is null)
        {
            return;
        }

        string? path = null;
        if (File.Exists(nameOrPath))
        {
            path = nameOrPath;
        }
        else
        {
            path = _spriteFiles.FirstOrDefault(
                candidate => Path.GetFileName(candidate).Contains(nameOrPath, StringComparison.OrdinalIgnoreCase));
        }

        if (path is null)
        {
            Console.WriteLine($"找不到 sprite: {nameOrPath}");
            _lastError = $"找不到 sprite: {nameOrPath}";
            return;
        }

        LaunchSpriteFile(path);
    }

    private static void LaunchSpriteFile(string path)
    {
        if (_visualizer is null)
        {
            return;
        }

        if (_visualizer.LaunchSpriteFile(path, colorKey: 0, initCode: _options.SpriteInitCode))
        {
            Console.WriteLine($"启动 sprite: {Path.GetFileName(path)}");
        }
        else
        {
            _lastError = _visualizer.LastError ?? "sprite 启动失败";
            Console.WriteLine("sprite 启动失败: " + _lastError);
        }
    }

    /// <summary>按 <c>--preset</c> 的参数加载：先当文件路径，再当文件名片段匹配。</summary>
    private static void LoadPresetByNameOrPath(string nameOrPath)
    {
        if (File.Exists(nameOrPath))
        {
            LoadPresetFile(nameOrPath, blendTime: 0f);   // 启动时直接出画面，不做混合
            return;
        }

        var index = _presetFiles.FindIndex(
            path => Path.GetFileName(path).Contains(nameOrPath, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            Console.WriteLine($"找不到预设: {nameOrPath}");
            _lastError = $"找不到预设: {nameOrPath}";
            return;
        }

        LoadPresetAt(index, blendTime: 0f);
    }

    private static void LoadPresetAt(int index, float blendTime)
    {
        if (_visualizer is null || index < 0 || index >= _presetFiles.Count)
        {
            return;
        }

        _presetIndex = index;
        LoadPresetFile(_presetFiles[index], blendTime);
    }

    private static void LoadPresetFile(string path, float blendTime)
    {
        if (_visualizer is null)
        {
            return;
        }

        if (_visualizer.LoadPreset(path, blendTime))
        {
            _presetName = Path.GetFileNameWithoutExtension(path);
            _lastError = null;
            Console.WriteLine($"加载预设: {_presetName}（{Path.GetFileName(path)}）");

            if (_visualizer.PresetNotes is { } presetNotes)
            {
                Console.WriteLine("  [提示] " + presetNotes);
            }

            ScheduleNextPreset();
        }
        else
        {
            _lastError = _visualizer.LastError ?? "预设加载失败";
            Console.WriteLine($"预设加载失败: {path} — {_lastError}");
        }
    }

    /// <summary>
    /// 安排下一次自动换预设，并同步给可视化器（它要用这个时间算 <c>progress</c>）。
    /// </summary>
    /// <remarks>
    /// 对应 <c>UpdateTime</c> 里的 <c>m_fNextPresetTime = GetTime() + m_fBlendTimeAuto + m_fTimeBetweenPresets + dt</c>
    /// （milkdropfs.cpp:771-775），其中 <c>dt</c> 是 <c>m_fTimeBetweenPresetsRand</c> 的随机部分。
    /// 原版在每次 LoadPreset 后把 <c>m_fNextPresetTime</c> 置 -1，等下一次 UpdateTime 重算，效果一样。
    /// </remarks>
    private static void ScheduleNextPreset()
    {
        if (_visualizer is null)
        {
            return;
        }

        _nextPresetTime = _visualizer.Time
            + _blendTimeAuto
            + _timeBetweenPresets
            + (_timeBetweenPresetsRand * (float)PresetRandom.NextDouble());

        _visualizer.NextPresetTime = _nextPresetTime;
    }

    /// <summary>到点就换一个随机预设（锁定/未安排时不动）。</summary>
    private static void UpdatePresetScheduling()
    {
        if (_visualizer is null || _presetLocked || _nextPresetTime < 0f)
        {
            return;
        }

        if (_visualizer.Mode != VisualizerMode.Preset || _presetFiles.Count == 0)
        {
            return;
        }

        if (_visualizer.Time >= _nextPresetTime)
        {
            LoadRandomPreset(_blendTimeAuto);
        }
    }

    private static void LoadPresetStep(int step) => LoadPresetStep(step, _blendTimeUser);

    private static void LoadPresetStep(int step, float blendTime)
    {
        if (_presetFiles.Count == 0)
        {
            return;
        }

        var next = _presetIndex < 0 ? 0 : ((_presetIndex + step) % _presetFiles.Count + _presetFiles.Count) % _presetFiles.Count;
        LoadPresetAt(next, blendTime);
    }

    private static void LoadRandomPreset() => LoadRandomPreset(_blendTimeUser);

    private static void LoadRandomPreset(float blendTime)
    {
        if (_presetFiles.Count == 0)
        {
            return;
        }

        var next = PresetRandom.Next(_presetFiles.Count);
        if (_presetFiles.Count > 1 && next == _presetIndex)
        {
            next = (next + 1) % _presetFiles.Count;
        }

        LoadPresetAt(next, blendTime);
    }

    private static IAudioSource CreateAudioSource(AppOptions options)
    {
        if (options.Audio == "none")
        {
            return new SilenceAudioSource();
        }

        // NAudio 的 CoreAudioApi 靠 [ComImport] 的 coclass（MMDeviceEnumeratorComObject）建对象，
        // Native AOT 不支持这种 COM 激活（ILC 直接把构造函数编译成 throw），所以 AOT 构建里
        // 连试都别试，直接静音：否则每帧都是空的，日志里还多一条 "invalid program"。
        if (!RuntimeFeature.IsDynamicCodeCompiled)
        {
            Console.WriteLine("Native AOT 构建：NAudio 的 WASAPI 回环不可用（COM 激活不支持），音频退回静音。");
            return new SilenceAudioSource();
        }

        try
        {
            return new WasapiLoopbackAudioSource();
        }
        catch (Exception ex)
        {
            // 没有音频设备 / 非 Windows 会话时不要让可视化器起不来，退回静音。
            Console.WriteLine("WASAPI loopback 不可用，退回静音: " + ex.Message);
            return new SilenceAudioSource();
        }
    }

    /// <summary>默认字体没有中文字形，这里挂一个系统中文字体（找不到就退回默认字体）。</summary>
    /// <returns>是否成功挂上自定义字体。</returns>
    private static bool LoadUiFont(ImGuiIOPtr io)
    {
        var fontSize = BaseFontSize * _uiScale;

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyhl.ttc"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "simhei.ttf"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "simsun.ttc"),
        ];

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                io.Fonts.AddFontFromFileTTF(path, fontSize, null, io.Fonts.GetGlyphRangesChineseSimplifiedCommon());
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("加载中文字体失败: " + ex.Message);
            }
        }

        return false;
    }

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
            // Win10 1607 之前没有这个 API；退回系统默认行为即可
            Console.WriteLine("启用 DPI 感知失败: " + ex.Message);
        }
    }

    private static string DescribeEnvironment()
    {
        try
        {
            var presets = ResourceLocator.EnumeratePresets().Count();
            return $"资源根目录: {ResourceLocator.ResourceRoot}（{presets} 个 .milk 预设）";
        }
        catch (Exception ex)
        {
            return "未找到原版资源目录（可设置 " + ResourceLocator.EnvironmentVariable + "）: " + ex.Message;
        }
    }

    private static void OnResize(Vector2D<int> size)
    {
        // Silk.NET 的 Resize 可能在渲染回调里触发，而 D3D9 的 Reset 不允许在 BeginScene/EndScene 之间调用，
        // 所以这里只记下来，等下一帧的 Update 再真正重建后台缓冲。
        if (size.X <= 0 || size.Y <= 0)
        {
            return;
        }

        _pendingResize = size;
    }

    private static void OnUpdate(double delta)
    {
        if (_pendingClose)
        {
            _window?.Close();
            return;
        }

        if (_input is null || _visualizer is null || _context is null)
        {
            return;
        }

        if (_pendingResize is { } size)
        {
            _pendingResize = null;

            // Reset 前必须释放所有 Pool.Default 资源（离屏 RT、ImGui 的 VB/IB）。
            _imguiRenderer?.InvalidateResources();
            _visualizer.OnDeviceLost();
            _context.Resize(size.X, size.Y);
            _imguiRenderer?.CreateResources();
            _visualizer.OnDeviceReset();
        }

        _input.NewFrame(_context.Width, _context.Height, delta);
        BuildUi();
    }

    private static void BuildUi()
    {
        if (_showDemo)
        {
            ImGui.ShowDemoWindow();
        }

        ImGui.SetNextWindowPos(new System.Numerics.Vector2(12 * _uiScale, 12 * _uiScale), ImGuiCond.FirstUseEver);

        // 面板默认 alpha=0.94，底下的高亮画面按 sRGB 一放大就明显泛红、字看不清；这里改成不透明
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new System.Numerics.Vector4(0.055f, 0.055f, 0.075f, 1.0f));
        ImGui.Begin("MilkDrop.Classic (C# port)", ImGuiWindowFlags.AlwaysAutoResize);

        ImGui.Text($"FPS: {_fps:F1}   Frame: {_visualizer!.Frame}   Stage: {_visualizer.CurrentStage}");
        ImGui.Text($"Mode: {_visualizer.Mode}   Backbuffer: {_context!.Width}x{_context.Height}   VSync: {_context.VSync}");

        // 预设变量看到的相对电平（mysound.imm_rel / avg_rel）
        var mySound = _visualizer.Analyzer.MySound;
        ImGui.Text(
            $"bass {mySound.ImmRel[0]:F2}  mid {mySound.ImmRel[1]:F2}  treb {mySound.ImmRel[2]:F2}" +
            $"   |   bass_att {mySound.AvgRel[0]:F2}  mid_att {mySound.AvgRel[1]:F2}  treb_att {mySound.AvgRel[2]:F2}");
        if (_lastError is not null)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.45f, 0.4f, 1f), _lastError);
        }

        ImGui.Separator();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + (480 * _uiScale));
        ImGui.TextUnformatted(_status);
        ImGui.PopTextWrapPos();
        ImGui.Separator();

        if (_visualizer.Mode == VisualizerMode.Preset && _visualizer.Preset is { } preset)
        {
            ImGui.Text($"Preset: {_presetName}");
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + (480 * _uiScale));
            ImGui.TextUnformatted(preset.Description);
            ImGui.PopTextWrapPos();
            ImGui.Text(
                $"PS: warp {preset.WarpPsVersion} / comp {preset.CompPsVersion}   " +
                $"Shaders: vs={(_visualizer.HasPresetVertexShader ? "on" : "off")} " +
                $"warp={(_visualizer.HasWarpShader ? "on" : "off")} comp={(_visualizer.HasCompShader ? "on" : "off")}   " +
                $"Grid: {_visualizer.GridX}x{_visualizer.GridY}");
            if (_visualizer.ShaderError is { } shaderError)
            {
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.75f, 0.35f, 1f), "shader: " + shaderError);
            }

            if (_visualizer.ShaderNotes is { } shaderNotes)
            {
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + (480 * _uiScale));
                ImGui.TextColored(new System.Numerics.Vector4(0.55f, 0.85f, 1f, 1f), shaderNotes);
                ImGui.PopTextWrapPos();
            }

            // 预设本身能加载、但原版画不出来的部分（如越界的内置波形模式）
            if (_visualizer.PresetNotes is { } presetNotes)
            {
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + (480 * _uiScale));
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.8f, 0.4f, 1f), "提示: " + presetNotes);
                ImGui.PopTextWrapPos();
            }

            if (_visualizer.BlurError is { } blurError)
            {
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.75f, 0.35f, 1f), "blur: " + blurError);
            }
        }
        else
        {
            ImGui.Text($"Preset: （未加载，{_presetFiles.Count} 个可用）");
        }

        if (ImGui.Button("上一个 (P)"))
        {
            LoadPresetStep(-1);
        }

        ImGui.SameLine();
        if (ImGui.Button("随机 (空格)"))
        {
            LoadRandomPreset();
        }

        ImGui.SameLine();
        if (ImGui.Button("下一个 (N)"))
        {
            LoadPresetStep(1);
        }

        ImGui.SameLine();
        ImGui.Checkbox("锁定 (L)", ref _presetLocked);

        if (_visualizer is not null && !_presetLocked && _nextPresetTime > 0f)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"| 下次切换 {Math.Max(0f, _nextPresetTime - _visualizer.Time):0.0}s");
        }

        if (ImGui.IsKeyPressed(ImGuiKey.N))
        {
            LoadPresetStep(1);
        }

        if (ImGui.IsKeyPressed(ImGuiKey.P))
        {
            LoadPresetStep(-1);
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Space))
        {
            LoadRandomPreset();
        }

        if (ImGui.IsKeyPressed(ImGuiKey.L))
        {
            _presetLocked = !_presetLocked;
        }

        // ---- sprite（对应原版"数字键挂 sprite"）----
        ImGui.Separator();
        if (_spriteFiles.Count > 0)
        {
            _spriteIndex = Math.Clamp(_spriteIndex, 0, _spriteFiles.Count - 1);

            if (ImGui.BeginCombo("sprite", Path.GetFileName(_spriteFiles[_spriteIndex])))
            {
                for (var i = 0; i < _spriteFiles.Count; i++)
                {
                    var selected = i == _spriteIndex;
                    if (ImGui.Selectable(Path.GetFileName(_spriteFiles[i]), selected))
                    {
                        _spriteIndex = i;
                    }

                    if (selected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (ImGui.Button("挂上"))
            {
                LaunchSpriteFile(_spriteFiles[_spriteIndex]);
            }

            ImGui.SameLine();
            if (ImGui.Button("清空 sprites"))
            {
                _visualizer!.KillAllSprites();
            }

            ImGui.SameLine();
            ImGui.Text($"激活 {_visualizer!.Sprites.ActiveCount} / {SpriteManager.NumSlots}");
        }
        else
        {
            ImGui.Text($"sprites 目录为空: {ResourceLocator.SpritesDir}");
        }

        ImGui.Separator();
        var shaderCompat = _visualizer!.ShaderCompat;
        if (ImGui.Checkbox("shader 兼容层", ref shaderCompat))
        {
            _visualizer.ShaderCompat = shaderCompat;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "把原版编译失败的预设 shader 改成能编译的形式（默认关）。\n" +
                "· 宏名遮蔽：float aspect = aspect.x/aspect.y; —— aspect 是宏（_c0），\n" +
                "  预处理后变量名也变成 _c0，.y 非法（X3018）。\n" +
                "· double3 等双精度类型降级成 float（X3684）。\n" +
                "· 1.#INF / 除以零字面量换成有限值（X4579）。\n" +
                "· 补 MD3 PRO 专有的 get_fft / get_fft_hz / mouse 内建（X3004）。\n" +
                "· ps_2_0 指令槽超限时升到 ps_2_a / ps_2_b（X5608）。\n" +
                "原版对这些预设一律编译失败并回退默认 shader，\n" +
                "所以打开后画面会与原版不同。");
        }

        ImGui.Separator();
        ImGui.Checkbox("ImGui demo", ref _showDemo);

        if (ImGui.Button("Quit"))
        {
            _pendingClose = true;
        }

        ImGui.End();
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// 把光标位置换算成 -1..1 交给 <see cref="VisualizerEngine.MousePosition"/>（兼容层的 <c>mouse</c> 内建）。
    /// </summary>
    private static void UpdateMousePosition()
    {
        var io = ImGui.GetIO();
        var size = io.DisplaySize;
        if (size.X <= 0 || size.Y <= 0)
        {
            return;
        }

        // 首帧 MousePos 还是默认值（-FLT_MAX），必须 clamp 掉
        _visualizer!.MousePosition = new System.Numerics.Vector2(
            Math.Clamp((io.MousePos.X / size.X * 2f) - 1f, -1f, 1f),
            Math.Clamp(1f - (io.MousePos.Y / size.Y * 2f), -1f, 1f));
    }

    private static void OnRender(double delta)
    {
        if (_visualizer is null || _backend is null || _imguiRenderer is null)
        {
            return;
        }

        var stopwatch = Stopwatch.GetTimestamp();
        var cpuStopwatch = stopwatch;

        UpdatePresetScheduling();
        UpdateMousePosition();
        _visualizer.RenderFrame();
        _perfCpuTicks += Stopwatch.GetTimestamp() - cpuStopwatch;

        if (_visualizer.LastError is { } error && error != _lastError)
        {
            _lastError = error;
            Console.WriteLine("[visualizer] " + error);
        }

        ImGui.Render();
        _backend.BeginFrame();
        _imguiRenderer.Render();
        _backend.EndFrame();

        if (!_captured && !string.IsNullOrEmpty(_options.ScreenshotPath) && _elapsed >= _options.ScreenshotDelay)
        {
            _captured = true;
            var saved = ScreenCapture.TrySaveBackBuffer(_context!.Device, _options.ScreenshotPath!, out var captureError);
            Console.WriteLine(saved
                ? $"截屏已保存: {Path.GetFullPath(_options.ScreenshotPath!)}"
                : "截屏失败: " + captureError);
            _pendingClose = true;
        }

        _backend.Present();

        _elapsed += delta;

        if (_options.SmokeSeconds > 0 && _elapsed >= _options.SmokeSeconds)
        {
            _pendingClose = true;
        }

        _fpsAccum += delta;
        _fpsFrames++;
        if (_fpsAccum >= 0.5)
        {
            _fps = _fpsFrames / _fpsAccum;
            _fpsAccum = 0;
            _fpsFrames = 0;
        }

        _perfFrameTicks += Stopwatch.GetTimestamp() - stopwatch;
        _perfFrames++;
    }

    private static void OnClosing()
    {
        if (_perfFrames > 0)
        {
            var cpuMs = Stopwatch.GetElapsedTime(0, _perfCpuTicks).TotalMilliseconds / _perfFrames;
            var frameMs = Stopwatch.GetElapsedTime(0, _perfFrameTicks).TotalMilliseconds / _perfFrames;
            Console.WriteLine(
                $"性能: {_perfFrames} 帧，平均 {frameMs:0.00} ms/帧（{1000 / frameMs:0.0} fps），" +
                $"其中引擎 CPU {cpuMs:0.00} ms/帧");
        }

        _input?.Dispose();
        _visualizer?.Dispose();
        _imguiRenderer?.Dispose();
        ImGui.DestroyContext();
        _context?.Dispose();
    }
}
