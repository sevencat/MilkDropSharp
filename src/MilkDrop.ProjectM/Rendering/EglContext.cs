using System.Runtime.InteropServices;
using Silk.NET.OpenGLES;

namespace MilkDrop.ProjectM.Rendering;

/// <summary>
/// ANGLE（EGL + OpenGL ES 3）上下文。窗口版走 <c>eglCreateWindowSurface(HWND)</c>，
/// 离屏版走 pbuffer（单测 / 像素回读用）。
/// </summary>
/// <remarks>
/// <para>
/// 宿主负责建窗口（Silk.NET.Windowing，<c>GraphicsAPI.None</c>），把 HWND 交给这里；
/// 交换缓冲由 <see cref="SwapBuffers"/> 走 <c>eglSwapBuffers</c>，与
/// <c>MilkDrop.Classic.App</c> 里"自己创建设备自己 Present"的分工一致。
/// </para>
/// <para>
/// GL 入口先用 <c>libGLESv2.dll</c> 的导出表解析（GLES 3.0 核心函数都是直接导出的），
/// 失败再退回 <c>eglGetProcAddress</c>（扩展函数）。两者都拿到同一个模块实例。
/// </para>
/// </remarks>
public sealed unsafe class EglContext : IDisposable
{
    private static readonly object ModuleLock = new();
    private static nint _glesModule;

    private readonly nint _display;
    private readonly nint _surface;
    private readonly nint _context;
    private readonly bool _ownsDisplay;
    private bool _disposed;

    private EglContext(nint display, nint surface, nint context, bool windowSurface, bool ownsDisplay, string eglInfo)
    {
        _display = display;
        _surface = surface;
        _context = context;
        _ownsDisplay = ownsDisplay;
        EglInfo = eglInfo;
        IsWindowSurface = windowSurface;
        Gl = GL.GetApi(ResolveGlProc);

        // Renderer 里的类都通过 GlContext.Gl 访问入口（对应 C++ 的全局 gl* 函数）。
        GlContext.Initialize(Gl);
    }

    /// <summary>Silk.NET 的 GL 入口（全部绘制都经过它）。</summary>
    public GL Gl { get; }

    /// <summary>EGL 版本/厂商/扩展串，启动日志用。</summary>
    public string EglInfo { get; }

    /// <summary>是否是窗口表面（否则是 pbuffer 离屏表面）。</summary>
    public bool IsWindowSurface { get; }

    /// <summary>为已有窗口（HWND）建 EGL 窗口表面 + ES3 上下文。</summary>
    /// <param name="hwnd">窗口句柄（Silk.NET.Windowing 的 <c>window.Native.Win32.Hwnd</c>）。</param>
    /// <param name="vsync">是否开启垂直同步（<c>eglSwapInterval</c>）。</param>
    public static EglContext CreateForWindow(nint hwnd, bool vsync)
    {
        if (hwnd == 0)
        {
            throw new ArgumentException("窗口句柄为空。", nameof(hwnd));
        }

        var (display, ownsDisplay, info) = CreateDisplay();
        try
        {
            var config = ChooseConfig(display, Egl.WindowBit);
            var surface = Egl.CreateWindowSurface(display, config, hwnd, null);
            if (surface == Egl.NoSurface)
            {
                throw new InvalidOperationException("eglCreateWindowSurface 失败: " + Egl.LastErrorName());
            }

            var context = CreateEs3Context(display, config);
            Egl.MakeCurrent(display, surface, surface, context);
            Egl.SwapInterval(display, vsync ? 1 : 0);

            return new EglContext(display, surface, context, windowSurface: true, ownsDisplay, info);
        }
        catch
        {
            if (ownsDisplay)
            {
                Egl.Terminate(display);
            }

            throw;
        }
    }

    /// <summary>建立一个不依赖窗口的 pbuffer 上下文（离屏渲染 / 单元测试）。</summary>
    public static EglContext CreateOffscreen(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var (display, ownsDisplay, info) = CreateDisplay();
        try
        {
            var config = ChooseConfig(display, Egl.PbufferBit);
            var surface = Egl.CreatePbufferSurface(display, config,
            [
                Egl.Width, width,
                Egl.Height, height,
                Egl.None,
            ]);
            if (surface == Egl.NoSurface)
            {
                throw new InvalidOperationException("eglCreatePbufferSurface 失败: " + Egl.LastErrorName());
            }

            var context = CreateEs3Context(display, config);
            Egl.MakeCurrent(display, surface, surface, context);

            return new EglContext(display, surface, context, windowSurface: false, ownsDisplay, info);
        }
        catch
        {
            if (ownsDisplay)
            {
                Egl.Terminate(display);
            }

            throw;
        }
    }

    /// <summary>把上下文绑到当前线程（窗口重建 / 多线程渲染时需要）。</summary>
    public void MakeCurrent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Egl.MakeCurrent(_display, _surface, _surface, _context))
        {
            throw new InvalidOperationException("eglMakeCurrent 失败: " + Egl.LastErrorName());
        }
    }

    /// <summary>呈现（窗口表面专用）。离屏表面下是空操作。</summary>
    public void SwapBuffers()
    {
        if (_disposed || !IsWindowSurface)
        {
            return;
        }

        if (!Egl.SwapBuffers(_display, _surface))
        {
            throw new InvalidOperationException("eglSwapBuffers 失败: " + Egl.LastErrorName());
        }
    }

    /// <summary>查询本机 GLES 能力（P0 验收 / 后续能力降级判定用）。</summary>
    public GpuCapabilities QueryCapabilities() => GpuCapabilities.Query(Gl);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GlContext.Reset();
        Gl.Dispose();

        Egl.MakeCurrent(_display, Egl.NoSurface, Egl.NoSurface, Egl.NoContext);
        if (_context != Egl.NoContext)
        {
            Egl.DestroyContext(_display, _context);
        }

        if (_surface != Egl.NoSurface)
        {
            Egl.DestroySurface(_display, _surface);
        }

        if (_ownsDisplay)
        {
            Egl.Terminate(_display);
        }

        Egl.ReleaseThread();
    }

    /// <summary>
    /// 优先用 EGL_ANGLE_platform_angle 指定 D3D11 后端（ANGLE 在 Windows 上的默认后端，
    /// 但显式指定能避免落到 D3D9/WARP）；扩展不可用时退回 <c>eglGetDisplay</c>。
    /// </summary>
    private static (nint Display, bool OwnsDisplay, string Info) CreateDisplay()
    {
        var display = CreateAngleDisplay();
        var info = "eglGetPlatformDisplayEXT(EGL_ANGLE_platform_angle, D3D11)";

        if (display == Egl.NoDisplay)
        {
            display = Egl.GetDisplay(Egl.DefaultDisplay);
            info = "eglGetDisplay(EGL_DEFAULT_DISPLAY)";
        }

        if (display == Egl.NoDisplay)
        {
            throw new InvalidOperationException("取不到 EGLDisplay: " + Egl.LastErrorName()
                + $"（确认 {Egl.LibName} 在程序目录下，且是 x64 版本）");
        }

        if (!Egl.Initialize(display, out var major, out var minor))
        {
            throw new InvalidOperationException("eglInitialize 失败: " + Egl.LastErrorName());
        }

        var vendor = Marshal.PtrToStringAnsi(Egl.QueryString(display, Egl.Vendor)) ?? "?";
        var version = Marshal.PtrToStringAnsi(Egl.QueryString(display, Egl.Version)) ?? "?";
        var extensions = Marshal.PtrToStringAnsi(Egl.QueryString(display, Egl.Extensions)) ?? "";

        // ANGLE 不把 EGL_ANGLE_platform_angle 列进 EGL_EXTENSIONS（只在 eglGetProcAddress 里暴露），
        // 所以这里只记录一下，判定走"平台显示是否真的建起来了"。
        var advertisesPlatformAngle = extensions.Contains("EGL_ANGLE_platform_angle", StringComparison.Ordinal);

        var fullInfo = $"{info}, EGL {major}.{minor}, vendor={vendor}, version={version}, "
            + $"EXT 串里声明 EGL_ANGLE_platform_angle={(advertisesPlatformAngle ? "yes" : "no(正常)")}";

        return (display, true, fullInfo);
    }

    private static nint CreateAngleDisplay()
    {
        // Avalonia 构建改了导出名（EGL_ 前缀），eglGetProcAddress 内部表里的名字不一定是哪种写法，两种都试。
        var proc = Egl.GetProcAddress("eglGetPlatformDisplayEXT");
        if (proc == 0)
        {
            proc = Egl.GetProcAddress("EGL_GetPlatformDisplayEXT");
        }

        if (proc == 0)
        {
            return Egl.NoDisplay;
        }

        var getPlatformDisplay = Marshal.GetDelegateForFunctionPointer<Egl.GetPlatformDisplayExtProc>(proc);
        int[] attribs =
        [
            Egl.PlatformAngleTypeAngle, Egl.PlatformAngleTypeD3D11Angle,
            Egl.PlatformAngleDeviceTypeAngle, Egl.PlatformAngleDeviceTypeHardwareAngle,
            Egl.None,
        ];

        return getPlatformDisplay(Egl.PlatformAngleAngle, Egl.DefaultDisplay, attribs);
    }

    private static nint ChooseConfig(nint display, int surfaceBit)
    {
        int[] attribs =
        [
            Egl.SurfaceType, surfaceBit,
            Egl.RenderableType, Egl.OpenGlEs3Bit,
            Egl.RedSize, 8,
            Egl.GreenSize, 8,
            Egl.BlueSize, 8,
            Egl.AlphaSize, 8,
            Egl.DepthSize, 0,
            Egl.StencilSize, 0,
            Egl.None,
        ];

        var configs = new nint[1];
        if (!Egl.ChooseConfig(display, attribs, configs, 1, out var count) || count == 0)
        {
            throw new InvalidOperationException(
                $"eglChooseConfig 失败（surface=0x{surfaceBit:X}）: " + Egl.LastErrorName());
        }

        return configs[0];
    }

    private static nint CreateEs3Context(nint display, nint config)
    {
        Egl.BindApi(Egl.OpenGlEsApi);
        int[] attribs = [Egl.ContextClientVersion, 3, Egl.None];
        var context = Egl.CreateContext(display, config, Egl.NoContext, attribs);
        if (context == Egl.NoContext)
        {
            throw new InvalidOperationException("eglCreateContext(ES3) 失败: " + Egl.LastErrorName());
        }

        return context;
    }

    /// <summary>GL 入口解析：模块导出优先，扩展退回 <c>eglGetProcAddress</c>。</summary>
    private static nint ResolveGlProc(string name)
    {
        var module = EnsureGlesModule();
        if (module != 0 && NativeLibrary.TryGetExport(module, name, out var address) && address != 0)
        {
            return address;
        }

        return Egl.GetProcAddress(name);
    }

    private static nint EnsureGlesModule()
    {
        if (_glesModule != 0)
        {
            return _glesModule;
        }

        lock (ModuleLock)
        {
            if (_glesModule != 0)
            {
                return _glesModule;
            }

            var local = Path.Combine(AppContext.BaseDirectory, Egl.LibName);
            if (!NativeLibrary.TryLoad(local, out _glesModule))
            {
                NativeLibrary.TryLoad(Egl.LibName, out _glesModule);
            }

            return _glesModule;
        }
    }
}
