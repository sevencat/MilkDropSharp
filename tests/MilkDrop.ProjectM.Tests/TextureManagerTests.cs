using MilkDrop.ProjectM.Renderer;
using MilkDrop.Rendering.Gles3;
using MilkDrop.ProjectM.Tests.Rendering;
using Xunit;
using GLEnum = Silk.NET.OpenGLES.GLEnum;

namespace MilkDrop.ProjectM.Tests;

/// <summary>纹理名前缀解析（不需要 GL）。</summary>
public sealed class TextureSettingsTests
{
    [Fact]
    public void ExtractTextureSettings_NoPrefixUsesBilinearRepeat()
    {
        TextureManager.ExtractTextureSettings("noise_lq", out int wrap, out int filter, out string name);

        Assert.Equal((int)GLEnum.Repeat, wrap);
        Assert.Equal((int)GLEnum.Linear, filter);
        Assert.Equal("noise_lq", name);
    }

    [Theory]
    [InlineData("fc_tex", 0, 0, "tex")] // clamp + linear
    [InlineData("cf_tex", 0, 0, "tex")]
    [InlineData("fw_tex", 1, 0, "tex")] // repeat + linear
    [InlineData("pc_tex", 0, 1, "tex")] // clamp + nearest
    [InlineData("pw_tex", 1, 1, "tex")] // repeat + nearest
    [InlineData("xy_tex", 1, 0, "tex")] // 不认识的前缀也去掉
    public void ExtractTextureSettings_ParsesPrefix(string qualifiedName, int expectedWrap, int expectedFilter, string expectedName)
    {
        TextureManager.ExtractTextureSettings(qualifiedName, out int wrap, out int filter, out string name);

        int wrapExpected = expectedWrap == 0 ? (int)GLEnum.ClampToEdge : (int)GLEnum.Repeat;
        int filterExpected = expectedFilter == 0 ? (int)GLEnum.Linear : (int)GLEnum.Nearest;

        Assert.Equal(wrapExpected, wrap);
        Assert.Equal(filterExpected, filter);
        Assert.Equal(expectedName, name);
    }

    [Fact]
    public void ExtractTextureSettings_ShortNameIsKeptAsIs()
    {
        TextureManager.ExtractTextureSettings("ab", out _, out _, out string name);

        Assert.Equal("ab", name);
    }
}

/// <summary><see cref="TextureManager"/> 的预载、纹理加载与随机纹理（需要离屏 GL）。</summary>
[Collection("GL")]
public sealed class TextureManagerTests(OffscreenGlFixture gl) : IDisposable
{
    private readonly string _textureDir = Directory.CreateTempSubdirectory("milkdrop-textures-").FullName;

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
    public void Constructor_PreloadsNoiseAndIdleTextures()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using var manager = new TextureManager([_textureDir]);

        Assert.Equal("noise_lq", manager.GetTexture("noise_lq").Texture!.Name);
        Assert.Equal(256, manager.GetTexture("noise_hq").Texture!.Width);
        Assert.Equal(GLEnum.Texture3D, manager.GetTexture("noisevol_hq").Texture!.Target);

        TextureSamplerDescriptor idle = manager.GetTexture("idlem");
        Assert.Equal("idlem", idle.Texture!.Name);
        Assert.Equal(256, idle.Texture.Width);
        Assert.Equal(256, idle.Texture.Height);

        Assert.Equal("idleheadphones", manager.GetTexture("idleheadphones").Texture!.Name);
        Assert.Equal(0, (int)GlContext.Gl.GetError());
    }

    [Fact]
    public void GetTexture_UnknownNameReturnsPlaceholder()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using var manager = new TextureManager([_textureDir]);

        TextureSamplerDescriptor descriptor = manager.GetTexture("doesnotexist");

        Assert.Equal("placeholder", descriptor.Texture!.Name);
        Assert.Equal(1, descriptor.Texture.Width);
        // 描述符里的名字仍是预设里的原名。
        Assert.Equal("doesnotexist", descriptor.SamplerName);
    }

    [Fact]
    public void GetTexture_LoadsFileAndAppliesPrefixSampler()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        WriteTga(Path.Combine(_textureDir, "mytex.tga"), 2, 255, 0, 0);

        using var manager = new TextureManager([_textureDir]);

        TextureSamplerDescriptor descriptor = manager.GetTexture("pc_mytex");

        Assert.Equal("mytex", descriptor.Texture!.Name);
        Assert.Equal(2, descriptor.Texture.Width);
        Assert.True(descriptor.Texture.IsUserTexture);
        Assert.Equal((int)GLEnum.Nearest, descriptor.Sampler!.FilterMode);
        Assert.Equal((int)GLEnum.ClampToEdge, descriptor.Sampler.WrapMode);
    }

    [Fact]
    public void GetRandomTexture_PicksTextureMatchingPrefix()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        WriteTga(Path.Combine(_textureDir, "foo_a.tga"), 1, 255, 0, 0);
        WriteTga(Path.Combine(_textureDir, "foo_b.tga"), 1, 0, 255, 0);
        WriteTga(Path.Combine(_textureDir, "bar.tga"), 1, 0, 0, 255);

        using var manager = new TextureManager([_textureDir]);

        for (int i = 0; i < 8; i++)
        {
            TextureSamplerDescriptor descriptor = manager.GetRandomTexture("rand00_foo");
            Assert.NotNull(descriptor.Texture);
            Assert.StartsWith("foo_", descriptor.Texture!.Name, StringComparison.Ordinal);
            // 描述符保留 rand 原名，供着色器 uniform 用。
            Assert.Equal("rand00_foo", descriptor.SamplerName);
        }

        // 没有匹配前缀 → 空描述符。
        Assert.Null(manager.GetRandomTexture("rand01_nomatch").Texture);
    }

    [Fact]
    public void PurgeTextures_EvictsOldUserTextures()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        WriteTga(Path.Combine(_textureDir, "mytex.tga"), 2, 255, 0, 0);
        WriteTga(Path.Combine(_textureDir, "other.tga"), 2, 0, 255, 0);

        using var manager = new TextureManager([_textureDir]);

        Texture first = manager.GetTexture("mytex").Texture!;
        Assert.True(first.IsUserTexture);

        // 淘汰要求"年龄 > 1"，且参与比较的纹理年龄不能全相同
        // （相同会让 C++ 的 (age - newest) / (oldest - newest) 变成 0/0，见 projectMIssue.md I-29）。
        manager.PurgeTextures(); // mytex: 1
        manager.PurgeTextures(); // mytex: 2

        Texture other = manager.GetTexture("other").Texture!; // other: 0
        manager.PurgeTextures(); // mytex: 3, other: 1
        manager.PurgeTextures(); // mytex: 4, other: 2 → 淘汰更老更大的 mytex

        // 淘汰后会重新从磁盘加载，拿到的是新的 GL 纹理。
        Texture second = manager.GetTexture("mytex").Texture!;
        Assert.NotSame(first, second);
        Assert.True(second.IsUserTexture);
        Assert.Same(other, manager.GetTexture("other").Texture);
    }

    /// <summary>写一张未压缩 32 位 TGA（stb_image 能直接解码）。</summary>
    private static void WriteTga(string path, int size, byte r, byte g, byte b)
    {
        var header = new byte[18];
        header[2] = 2; // uncompressed true-color
        header[12] = (byte)size;
        header[13] = (byte)(size >> 8);
        header[14] = (byte)size;
        header[15] = (byte)(size >> 8);
        header[16] = 32; // bpp
        header[17] = 8;  // 8 alpha bits

        using FileStream stream = File.Create(path);
        stream.Write(header);

        var pixel = new byte[] { b, g, r, 255 };
        for (int i = 0; i < size * size; i++)
        {
            stream.Write(pixel);
        }
    }
}
