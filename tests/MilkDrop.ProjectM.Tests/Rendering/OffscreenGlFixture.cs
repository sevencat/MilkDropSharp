using MilkDrop.Rendering.Gles3;
using Silk.NET.OpenGLES;
using Xunit;

namespace MilkDrop.ProjectM.Tests.Rendering;

/// <summary>
/// 离屏 GL 上下文（pbuffer），给渲染层单测用。
/// </summary>
/// <remarks>
/// EGL 的"当前上下文"是线程绑定的，xunit 不保证同一线程跑所有测试，所以每个测试开头都要
/// 调 <see cref="EnsureCurrent"/>。创建失败（没有 GPU / 缺 ANGLE 原生库）时测试直接跳过。
/// </remarks>
public sealed class OffscreenGlFixture : IDisposable
{
    public OffscreenGlFixture()
    {
        try
        {
            Context = EglContext.CreateOffscreen(64, 64);
            Capabilities = Context.QueryCapabilities();
        }
        catch (Exception ex)
        {
            SkipReason = "创建离屏 GL 上下文失败: " + ex.Message;
        }
    }

    public EglContext? Context { get; }

    public GpuCapabilities? Capabilities { get; }

    public string? SkipReason { get; }

    public GL Gl => Context!.Gl;

    /// <summary>
    /// 把上下文绑到当前线程。返回 false 表示机器上没有可用的离屏 GL（此时测试直接 return 跳过，
    /// xunit 2.x 没有官方的动态 Skip）。
    /// </summary>
    public bool TryMakeCurrent()
    {
        if (Context is null)
        {
            return false;
        }

        Context.MakeCurrent();
        return true;
    }

    public void Dispose() => Context?.Dispose();
}
