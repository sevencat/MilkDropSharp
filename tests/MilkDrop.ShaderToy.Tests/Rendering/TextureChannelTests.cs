using System.Numerics;
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Renderer;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.ShaderToy.Tests.Rendering;

/// <summary>
/// P5 的 texture 通道：图片加载 + <c>vflip</c> + 采样器绑定。
/// </summary>
/// <remarks>
/// 用现写的 2x2 BMP 当素材：文件里第一行是图像最下面一行（BMP bottom-up），
/// 左下蓝、右下白、左上红、右上绿 —— 这样 <c>vflip</c> 有没有生效一眼就能看出来。
/// </remarks>
[Collection("GL")]
public sealed class TextureChannelTests(OffscreenGlFixture fixture, ITestOutputHelper output)
{
    private const int Size = 8;

    [Theory]
    [InlineData(false, 255, 0)]  // 不翻转：纹理左下 = 文件的蓝
    [InlineData(true, 0, 255)]   // 翻转后：纹理左下 = 红（原来的上排）
    public void TextureChannel_LoadsImageAndAppliesVFlip(bool vflip, byte expectedRed, byte expectedBlue)
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), "milkshader-shadertoy-tests");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "vflip-test.bmp");
        WriteTwoByTwoBmp(path);

        ShaderToyProject project = BuildProject(path, vflip);

        using var pipeline = new ShaderToyPipeline(
            project,
            new PipelineOptions { Width = Size, Height = Size, HalfFloat = true, Texture3DSupported = true });

        pipeline.Render(new GpuInput
        {
            Resolution = new Vector3(Size, Size, 1f),
            Date = GpuInput.MakeDate(new DateTime(2026, 10, 24, 12, 0, 0)),
        });

        byte[] pixels = GlReadback.Read(fixture, pipeline.Output, Size, Size);

        byte red = GlReadback.Red(pixels, Size, 3, 3);
        byte blue = GlReadback.Blue(pixels, Size, 3, 3);

        output.WriteLine($"vflip={vflip}: red={red} blue={blue}");
        Assert.InRange(red, Math.Max(0, expectedRed - 3), Math.Min(255, expectedRed + 3));
        Assert.InRange(blue, Math.Max(0, expectedBlue - 3), Math.Min(255, expectedBlue + 3));
    }

    private static ShaderToyProject BuildProject(string texturePath, bool vflip)
    {
        var project = new ShaderToyProject
        {
            Version = "0.1",
            Metadata = new ShaderToyMetadata { Id = "tex", Name = "texture" },
        };

        var input = new ShaderPassInput
        {
            Channel = 0,
            Type = ShaderInputType.Texture,
            Path = texturePath,
            // 用 nearest：避免插值把 2x2 的四个角混在一起
            Sampler = new ShaderInputSampler
            {
                Filter = SamplerFilter.Nearest,
                Wrap = SamplerWrap.Clamp,
                VFlip = vflip,
            },
        };

        var pass = new ShaderPass
        {
            Type = ShaderPassType.Image,
            Name = "Image",
            Code = """
                void mainImage(out vec4 c, in vec2 f)
                {
                    c = texture(iChannel0, vec2(0.25, 0.25));
                }
                """,
        };

        pass.Inputs.Add(input);
        pass.Outputs.Add(new ShaderPassOutput { Id = "img", Channel = 0 });
        project.Passes.Add(pass);

        return project;
    }

    /// <summary>写一张 2x2、24bpp 的 BMP（行按 4 字节对齐）。</summary>
    private static void WriteTwoByTwoBmp(string path)
    {
        const int width = 2;
        const int height = 2;
        const int rowSize = 8; // 2*3=6，补齐到 4 的倍数
        const int imageSize = rowSize * height;
        const int fileSize = 54 + imageSize;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(fileSize);
        writer.Write(0);            // reserved
        writer.Write(54);           // 像素数据偏移

        writer.Write(40);           // BITMAPINFOHEADER 大小
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);     // planes
        writer.Write((short)24);    // bpp
        writer.Write(0);            // 无压缩
        writer.Write(imageSize);
        writer.Write(2835);         // 96 dpi
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        // 文件第一行 = 图像最下面一行：左下蓝(255,0,0 BGR)、右下白
        writer.Write(new byte[] { 255, 0, 0, 255, 255, 255, 0, 0 });
        // 第二行 = 上排：左上红(0,0,255 BGR)、右上绿
        writer.Write(new byte[] { 0, 0, 255, 0, 255, 0, 0, 0 });
    }
}
