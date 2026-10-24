using System.Runtime.InteropServices;

namespace MilkDrop.Rendering.Gles3;

/// <summary>
/// ANGLE 的 EGL 绑定。
/// </summary>
/// <remarks>
/// <para>
/// <c>Silk.NET.OpenGLES</c> 只提供 GL 入口，不提供 EGL 绑定，所以这里手工 P/Invoke。
/// 用到的函数很少（初始化 + 交换缓冲），不需要 egl.xml 那种全量生成。
/// </para>
/// <para>
/// 用的是 Avalonia 的 ANGLE 构建（<c>Avalonia.Angle.Windows.Natives</c>）：EGL 与 GLES 合并成
/// 一个 <c>av_libglesv2.dll</c>，并且 EGL 入口统一加了 <c>EGL_</c> 前缀
/// （<c>eglGetError</c> → <c>EGL_GetError</c>，实测导出表 103 个 <c>egl*</c> 名字全部是这种形式）。
/// 换成 Chrome/官方 ANGLE 的 <c>libEGL.dll</c> 时，只需把 <see cref="LibName"/> 改回去、
/// 并把下面各 <c>EntryPoint</c> 的 <c>EGL_</c> 前缀去掉。
/// </para>
/// </remarks>
public static class Egl
{
    /// <summary>ANGLE 原生库文件名（Avalonia 构建：EGL + GLES 合一）。</summary>
    public const string LibName = "av_libglesv2.dll";

    // ---- 常量（EGL 1.5 + EGL_EXT_platform_base + EGL_ANGLE_platform_angle）----

    public const int Success = 0x3000;

    public const int None = 0x3038;

    /// <summary>EGL_DEFAULT_DISPLAY（0）。</summary>
    public static readonly nint DefaultDisplay = 0;

    public static readonly nint NoContext = 0;
    public static readonly nint NoSurface = 0;
    public static readonly nint NoDisplay = 0;

    public const int AlphaSize = 0x3021;
    public const int BlueSize = 0x3022;
    public const int GreenSize = 0x3023;
    public const int RedSize = 0x3024;
    public const int DepthSize = 0x3025;
    public const int StencilSize = 0x3026;

    public const int SurfaceType = 0x3033;
    public const int WindowBit = 0x0004;
    public const int PbufferBit = 0x0001;

    public const int RenderableType = 0x3040;
    public const int OpenGlEs2Bit = 0x0004;
    public const int OpenGlEs3Bit = 0x0040;

    public const int Vendor = 0x3053;
    public const int Version = 0x3054;
    public const int Extensions = 0x3055;

    public const int Height = 0x3056;
    public const int Width = 0x3057;

    public const int ContextClientVersion = 0x3098;
    public const int OpenGlEsApi = 0x30A0;

    // EGL_ANGLE_platform_angle
    public const int PlatformAngleAngle = 0x3202;
    public const int PlatformAngleTypeAngle = 0x3203;
    public const int PlatformAngleTypeD3D9Angle = 0x3207;
    public const int PlatformAngleTypeD3D11Angle = 0x3208;
    public const int PlatformAngleDeviceTypeAngle = 0x3209;
    public const int PlatformAngleDeviceTypeHardwareAngle = 0x320A;
    public const int PlatformAngleDeviceTypeWarpAngle = 0x320B;
    public const int PlatformAngleTypeVulkanAngle = 0x3450;

    // ---- 入口 ----

    [DllImport(LibName, EntryPoint = "EGL_GetError", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetError();

    [DllImport(LibName, EntryPoint = "EGL_GetProcAddress", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint GetProcAddress(string procName);

    [DllImport(LibName, EntryPoint = "EGL_GetDisplay", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint GetDisplay(nint nativeDisplay);

    [DllImport(LibName, EntryPoint = "EGL_Initialize", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Initialize(nint display, out int major, out int minor);

    [DllImport(LibName, EntryPoint = "EGL_Terminate", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Terminate(nint display);

    [DllImport(LibName, EntryPoint = "EGL_ReleaseThread", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseThread();

    [DllImport(LibName, EntryPoint = "EGL_QueryString", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint QueryString(nint display, int name);

    [DllImport(LibName, EntryPoint = "EGL_BindAPI", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BindApi(int api);

    [DllImport(LibName, EntryPoint = "EGL_ChooseConfig", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChooseConfig(nint display, int[] attribList, [Out] nint[] configs, int configSize, out int numConfig);

    [DllImport(LibName, EntryPoint = "EGL_GetConfigAttrib", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetConfigAttrib(nint display, nint config, int attribute, out int value);

    [DllImport(LibName, EntryPoint = "EGL_CreateWindowSurface", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint CreateWindowSurface(nint display, nint config, nint nativeWindow, int[]? attribList);

    [DllImport(LibName, EntryPoint = "EGL_CreatePbufferSurface", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint CreatePbufferSurface(nint display, nint config, int[] attribList);

    [DllImport(LibName, EntryPoint = "EGL_CreateContext", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint CreateContext(nint display, nint config, nint shareContext, int[] attribList);

    [DllImport(LibName, EntryPoint = "EGL_DestroyContext", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyContext(nint display, nint context);

    [DllImport(LibName, EntryPoint = "EGL_DestroySurface", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroySurface(nint display, nint surface);

    [DllImport(LibName, EntryPoint = "EGL_MakeCurrent", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MakeCurrent(nint display, nint draw, nint read, nint context);

    [DllImport(LibName, EntryPoint = "EGL_SwapBuffers", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SwapBuffers(nint display, nint surface);

    [DllImport(LibName, EntryPoint = "EGL_SwapInterval", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SwapInterval(nint display, int interval);

    /// <summary><c>eglGetPlatformDisplayEXT</c>（EGL_EXT_platform_base），只能通过 <see cref="GetProcAddress"/> 取。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate nint GetPlatformDisplayExtProc(int platform, nint nativeDisplay, int[] attribList);

    /// <summary>把 EGL 错误码翻译成名字，日志里好认。</summary>
    public static string ErrorName(int error) => error switch
    {
        0x3000 => "EGL_SUCCESS",
        0x3001 => "EGL_NOT_INITIALIZED",
        0x3002 => "EGL_BAD_ACCESS",
        0x3003 => "EGL_BAD_ALLOC",
        0x3004 => "EGL_BAD_ATTRIBUTE",
        0x3005 => "EGL_BAD_CONFIG",
        0x3006 => "EGL_BAD_CONTEXT",
        0x3007 => "EGL_BAD_CURRENT_SURFACE",
        0x3008 => "EGL_BAD_DISPLAY",
        0x3009 => "EGL_BAD_MATCH",
        0x300A => "EGL_BAD_NATIVE_PIXMAP",
        0x300B => "EGL_BAD_NATIVE_WINDOW",
        0x300C => "EGL_BAD_PARAMETER",
        0x300D => "EGL_BAD_SURFACE",
        0x300E => "EGL_CONTEXT_LOST",
        _ => $"0x{error:X4}",
    };

    /// <summary>当前线程最后一个 EGL 错误码的名字（用于异常消息）。</summary>
    public static string LastErrorName() => ErrorName(GetError());
}
