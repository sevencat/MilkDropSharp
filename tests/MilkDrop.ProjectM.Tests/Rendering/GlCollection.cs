using Xunit;

namespace MilkDrop.ProjectM.Tests.Rendering;

/// <summary>
/// 所有需要离屏 GL 的测试共用一个 <see cref="OffscreenGlFixture"/>。
/// </summary>
/// <remarks>
/// 必须用 collection fixture 而不是每个类各自的 <c>IClassFixture</c>：xunit 会并行跑不同 collection，
/// 于是两个 <c>EglContext</c> 会同时存在/销毁，<c>eglMakeCurrent</c> 报 <c>EGL_BAD_SURFACE</c>
/// （一个线程把 display 上的 surface 销毁了，另一个线程还在用）。同一 collection 内的测试类串行执行。
/// </remarks>
[CollectionDefinition("GL")]
public sealed class GlCollection : ICollectionFixture<OffscreenGlFixture>;
