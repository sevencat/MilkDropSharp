using MilkDrop.ProjectM.Renderer;
using MilkDrop.ProjectM.Tests.Rendering;
using Silk.NET.OpenGLES;
using Xunit;
using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ProjectM.Tests;

/// <summary>
/// 纹理描述符的失效/重取语义（P7）：纹理被淘汰后由 <c>TryUpdate</c> 按名字补回来。
/// </summary>
[Collection("GL")]
public sealed class TextureSamplerDescriptorTests(OffscreenGlFixture gl) : IDisposable
{
    private readonly string _textureDir = Directory.CreateTempSubdirectory("milkdrop-desc-tex-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_textureDir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void EmptyDescriptorIsResolvedFromTextureManager()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using TextureManager textureManager = new([_textureDir]);

        var descriptor = new TextureSamplerDescriptor(null, null, "noise_lq", "noise_lq");
        Assert.True(descriptor.Empty());

        descriptor.TryUpdate(textureManager);

        Assert.False(descriptor.Empty());
        Assert.False(descriptor.UpdateFailed);
        Assert.NotNull(descriptor.Texture);
        Assert.NotNull(descriptor.Sampler);
        Assert.True(descriptor.Texture!.Width > 0);
        Assert.Equal(GLEnum.NoError, gl.Gl.GetError());
    }

    /// <summary>
    /// 找不到纹理时 <c>TextureManager</c> 会回落到占位纹理，所以描述符会变成"非空"，
    /// 也就不会再反复尝试——与 C++ 行为一致。
    /// </summary>
    [Fact]
    public void UnknownTextureFallsBackToPlaceholder()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using TextureManager textureManager = new([_textureDir]);

        var descriptor = new TextureSamplerDescriptor(null, null, "nonexistent_texture", "nonexistent_texture");
        descriptor.TryUpdate(textureManager);

        Assert.False(descriptor.UpdateFailed);
        Assert.False(descriptor.Empty());
        Assert.True(descriptor.Texture!.Width > 0);
    }

    [Fact]
    public void ValidDescriptorIsNotReplaced()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using TextureManager textureManager = new([_textureDir]);

        TextureSamplerDescriptor source = textureManager.GetTexture("noise_lq");
        Assert.False(source.Empty());

        Texture? before = source.Texture;

        source.TryUpdate(textureManager);

        Assert.Same(before, source.Texture);
        Assert.False(source.UpdateFailed);
    }
}
