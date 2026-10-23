using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using MilkDrop.ProjectM.Rendering;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGLES;
using Silk.NET.Windowing;

namespace MilkDrop.ProjectM.App;

/// <summary>
/// 入口。对应 <c>refcode/projectM/frontend-sdl-cpp-2.0.0-pre1</c> 里 SDL 前端的工作：
/// 建窗口 → 建 GL 上下文 → 主循环，只是这里换成 Silk.NET.Windowing + ANGLE(EGL) + ImGui。
/// </summary>
/// <remarks>
/// P0 阶段只做"环境打通"：窗口、EGL/GLES3 上下文、能力探测、ImGui 叠加层、SwapBuffers。
/// 预设渲染在 P2/P4 接入 <c>MilkDrop.ProjectM</c>。
/// </remarks>
internal static class Program
{
    private static IWindow? _window;
    private static EglContext? _context;
    private static ImguiGles3Renderer? _imguiRenderer;
    private static ImguiInput? _input;
    private static GpuCapabilities? _capabilities;

    private static AppOptions _options = new();
    private static float _uiScale = 1f;

    private static double _elapsed;
    private static double _fpsAccum;
    private static int _fpsFrames;
    private static double _fps;
    private static int _framesRendered;
    private static int _swapFailures;
    private static bool _showDemo;
    private static bool _windowOpen = true;
    private static string _status = "starting";
    private static string? _lastError;

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

        // 日志里有中文，固定成 UTF-8，避免重定向到文件时变乱码。
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
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
            VSync = false,
            PreferredDepthBufferBits = 0,
            PreferredStencilBufferBits = 0,
            UpdatesPerSecond = 0,
            FramesPerSecond = 0,
        };

        using var window = Window.Create(windowOptions);
        _window = window;

        window.Load += () => OnLoad(window, options);
        window.Render += OnRender;
        window.Resize += size => Console.WriteLine($"窗口尺寸: {size.X}x{size.Y}");
        window.Closing += OnClosing;

        try
        {
            window.Run();
            return _lastError is null ? 0 : 1;
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

        _uiScale = options.UiScale > 0 ? options.UiScale : QueryUiScale(win32.Hwnd);

        // 高 DPI 屏上 18px 字体仍然偏小，按 DPI 等比放大窗口（EGL 表面尺寸跟随窗口，之后再建）。
        if (_uiScale > 1.001f)
        {
            var scaled = new Vector2D<int>(
                (int)(window.Size.X * _uiScale),
                (int)(window.Size.Y * _uiScale));
            Console.WriteLine($"DPI 缩放 {_uiScale:0.##}x，窗口放大到 {scaled.X}x{scaled.Y}");
            window.Size = scaled;
        }

        var stopwatch = Stopwatch.StartNew();
        _context = EglContext.CreateForWindow(win32.Hwnd, options.VSync);
        stopwatch.Stop();

        Console.WriteLine($"EGL 上下文建立耗时: {stopwatch.ElapsedMilliseconds} ms");
        Console.WriteLine("EGL: " + _context.EglInfo);

        _capabilities = _context.QueryCapabilities();
        Console.WriteLine("---- ANGLE / GLES 能力探测 ----");
        Console.WriteLine(_capabilities.ToLogString());
        Console.WriteLine("------------------------------");

        if (options.ShowUi)
        {
            ImGui.CreateContext();
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
            _input = new ImguiInput(window.CreateInput());
        }

        _status = "ready";
    }

    private static void OnRender(double deltaSeconds)
    {
        if (_context is not { } context || _window is not { } window)
        {
            return;
        }

        var gl = context.Gl;
        var width = Math.Max(1, window.Size.X);
        var height = Math.Max(1, window.Size.Y);

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

            // 底色随时间轻微呼吸：肉眼确认 swapchain / vsync 是否正常工作。
            var wave = (float)((Math.Sin(_elapsed * 0.7) + 1) * 0.5);
            gl.Viewport(0, 0, (uint)width, (uint)height);
            gl.ClearColor(0.05f + 0.08f * wave, 0.07f, 0.12f + 0.10f * (1 - wave), 1f);
            gl.Clear((uint)GLEnum.ColorBufferBit);

            if (_imguiRenderer is not null && _input is not null)
            {
                _input.NewFrame(width, height, deltaSeconds);
                BuildUi();
                ImGui.Render();
                _imguiRenderer.Render(ImGui.GetDrawData(), width, height);
            }

            // smoke 模式前几帧做自检：确认 ImGui 真的产出了绘制指令、GL 没报错。
            if (_options.SmokeFrames > 0 && _framesRendered < 3 && _imguiRenderer is not null)
            {
                var drawData = ImGui.GetDrawData();
                var glError = (int)gl.GetError();
                Console.WriteLine(
                    $"帧 {_framesRendered} 自检: ImGui 指令列表={drawData.CmdListsCount}, "
                    + $"顶点={drawData.TotalVtxCount}, 索引={drawData.TotalIdxCount}, glGetError=0x{glError:X4}");
            }

            context.SwapBuffers();
            _framesRendered++;

            if (_options.SmokeFrames > 0 && _framesRendered >= _options.SmokeFrames)
            {
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

    private static void BuildUi()
    {
        ImGui.SetNextWindowPos(new Vector2(16, 16), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(760, 520), ImGuiCond.FirstUseEver);

        if (ImGui.Begin(UiText.WindowTitle, ref _windowOpen))
        {
            ImGui.Text(string.Format(UiText.StatusFormat, _status, _fps, _framesRendered, _swapFailures));
            ImGui.Separator();

            if (ImGui.Button(UiText.RequeryCapabilities))
            {
                _capabilities = _context?.QueryCapabilities();
                Console.WriteLine(_capabilities?.ToLogString());
            }

            ImGui.SameLine();
            ImGui.Checkbox("ImGui demo", ref _showDemo);

            ImGui.Separator();
            if (_capabilities is not null)
            {
                var log = _capabilities.ToLogString();
                if (ImGui.BeginChild("caps", new Vector2(0, 0), ImGuiChildFlags.Border))
                {
                    ImGui.TextUnformatted(log);
                }

                ImGui.EndChild();
            }

            if (_lastError is not null)
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), UiText.ErrorPrefix + _lastError);
            }
        }

        ImGui.End();

        if (_showDemo)
        {
            ImGui.ShowDemoWindow(ref _showDemo);
        }
    }

    private static void OnClosing()
    {
        Console.WriteLine($"关闭：共渲染 {_framesRendered} 帧，最后 FPS {_fps:0.0}，总时长 {_elapsed:0.0}s");

        _input?.Dispose();
        _imguiRenderer?.Dispose();
        _context?.Dispose();
        _input = null;
        _imguiRenderer = null;
        _context = null;

        if (_options.ShowUi)
        {
            ImGui.DestroyContext();
        }
    }

    /// <summary>默认字体没有中文字形，这里挂一个系统中文字体（找不到就退回默认字体）。</summary>
    private static bool LoadUiFont(ImGuiIOPtr io, out string? fontPath)
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

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                io.Fonts.AddFontFromFileTTF(path, fontSize, null, io.Fonts.GetGlyphRangesChineseSimplifiedCommon());
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
