using Silk.NET.OpenGLES;

namespace MilkDrop.ProjectM.Rendering;

/// <summary>
/// 全局 GL 入口持有者。
/// </summary>
/// <remarks>
/// <para>
/// C++ 侧的 Renderer 直接调用全局 <c>gl*</c> 函数；Silk.NET 的入口是 <see cref="GL"/> 实例，
/// 所以这里做一层静态持有，让移植过来的类可以逐行对应（<c>Gl.TexImage2D(...)</c> ↔ <c>glTexImage2D(...)</c>）。
/// </para>
/// <para>
/// 由 <see cref="EglContext"/> 在构造/释放时设置和清空。一个进程同时只有一个当前上下文，
/// 与 projectM 的用法一致（它也不支持多上下文并存）。
/// </para>
/// </remarks>
public static class GlContext
{
    private static GL? _gl;

    /// <summary>当前 GL 入口。未初始化时抛 <see cref="InvalidOperationException"/>。</summary>
    public static GL Gl => _gl
        ?? throw new InvalidOperationException("GL 上下文尚未初始化（先创建 EglContext）。");

    /// <summary>是否已绑定上下文。</summary>
    public static bool IsInitialized => _gl is not null;

    /// <summary>绑定新的 GL 入口（由 <see cref="EglContext"/> 调用）。</summary>
    public static void Initialize(GL gl) => _gl = gl ?? throw new ArgumentNullException(nameof(gl));

    /// <summary>解绑（上下文销毁时调用）。</summary>
    public static void Reset() => _gl = null;

    /// <summary>便捷访问：每次调用都取当前入口（避免长生命周期缓存导致上下文切换后失效）。</summary>
    internal static GL Api => Gl;
}
