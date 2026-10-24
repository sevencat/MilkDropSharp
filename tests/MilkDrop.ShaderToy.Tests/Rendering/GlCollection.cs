using Xunit;

namespace MilkDrop.ShaderToy.Tests.Rendering;

/// <summary>
/// 所有需要离屏 GL 的测试共用一个 <see cref="OffscreenGlFixture"/>。
/// </summary>
/// <remarks>
/// 必须用 collection fixture：xunit 会并行跑不同 collection，两个 <c>EglContext</c> 同时存在时
/// <c>eglMakeCurrent</c> 会报 <c>EGL_BAD_SURFACE</c>。同一 collection 内的测试类串行执行。
/// </remarks>
[CollectionDefinition("GL")]
public sealed class GlCollection : ICollectionFixture<OffscreenGlFixture>;
