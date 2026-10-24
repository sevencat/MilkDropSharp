using MilkDrop.ShaderToy.Import;
using MilkDrop.ShaderToy.Pass;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.ShaderToy.Tests;

/// <summary>
/// <see cref="ShaderToyJsonImporter"/> 的解析语义（对齐上游 <c>viewport.cpp load_shadertoy_json()</c>）。
/// </summary>
public sealed class JsonImportTests(ITestOutputHelper output)
{
    /// <summary>
    /// 覆盖：common/buffer/cubemap/sound/image 五种 pass、六类 input、数字 id、<c>ctype</c>、
    /// 缺 sampler、未知 pass 类型、未知 input 类型。
    /// </summary>
    private const string MultiPassJson = """
        {
            "ver": "0.1",
            "info": {
                "id": "tsLGRl",
                "name": "Fixture",
                "username": "someone",
                "description": "multi pass fixture",
                "tags": ["fluid", "noise"]
            },
            "renderpass": [
                {
                    "type": "common",
                    "name": "Common",
                    "description": "",
                    "code": "#define PI 3.14159\nvec2 rot(vec2 p) { return p; }",
                    "inputs": [],
                    "outputs": []
                },
                {
                    "type": "buffer",
                    "name": "Buffer A",
                    "description": "first buffer",
                    "code": "void mainImage(out vec4 c, in vec2 f) { c = texture(iChannel0, f/iResolution.xy); }",
                    "inputs": [
                        { "channel": 0, "type": "buffer", "id": "4dfGRr", "sampler": { "filter": "nearest", "wrap": "repeat" } },
                        { "channel": 1, "type": "texture", "src": "https://example.com/noise.jpg", "sampler": { "filter": "mipmap", "wrap": "clamp", "vflip": "true" } }
                    ],
                    "outputs": [ { "id": "4dfGR1", "channel": 0 } ]
                },
                {
                    "type": "buffer",
                    "name": "Buffer B",
                    "code": "",
                    "inputs": [ { "channel": 0, "ctype": "buffer", "id": 12345 } ],
                    "outputs": [ { "id": 12345, "channel": 0 } ]
                },
                {
                    "type": "cubemap",
                    "name": "Cube A",
                    "code": "",
                    "inputs": [
                        { "channel": 0, "type": "keyboard" },
                        { "channel": 1, "type": "volume", "id": "volumeAsset" }
                    ],
                    "outputs": [ { "id": "cubeId", "channel": 0 } ]
                },
                {
                    "type": "sound",
                    "name": "Sound",
                    "code": "vec2 mainSound(int s, float t) { return vec2(sin(t * 440.0)); }",
                    "inputs": [ { "channel": 0, "type": "music" } ],
                    "outputs": []
                },
                {
                    "type": "image",
                    "name": "Image",
                    "code": "void mainImage(out vec4 c, in vec2 f) { c = vec4(1.0); }",
                    "inputs": [
                        { "channel": 0, "type": "cubemap", "id": "cubeId" },
                        { "channel": 1, "type": "musicstream" }
                    ],
                    "outputs": [ { "id": "imgId", "channel": 0 } ]
                },
                {
                    "type": "weird",
                    "name": "NotAPass",
                    "code": "",
                    "inputs": [],
                    "outputs": []
                },
                {
                    "type": "image",
                    "name": "Image 2",
                    "code": "",
                    "inputs": [ { "channel": 0, "type": "shadow" } ],
                    "outputs": [ { "id": "img2", "channel": 0 } ]
                }
            ]
        }
        """;

    [Fact]
    public void LoadFile_ParsesUpstreamDefaultShader()
    {
        ShaderToyProject project = ShaderToyJsonImporter.LoadFile(TestPaths.DefaultShaderJson);

        Assert.Equal("0.1", project.Version);
        Assert.Equal("mtcyRj", project.Metadata.Id);
        Assert.Equal("desktop-shadertoy", project.Metadata.Name);
        Assert.Equal(["test"], project.Metadata.Tags);
        Assert.Empty(project.Warnings);

        ShaderPass image = Assert.Single(project.Passes);
        Assert.Equal(ShaderPassType.Image, image.Type);
        Assert.Equal("Image", image.Name);
        Assert.Equal("4dfGRr", image.Id);
        Assert.Empty(image.Inputs);

        // 字面 \n 必须还原成真换行，且不能残留两字符序列
        Assert.Contains("\n{\n", image.Code);
        Assert.DoesNotContain("\\n", image.Code);
        Assert.StartsWith("void mainImage(out vec4 fragColor, in vec2 fragCoord)", image.Code);

        // 保存要原样写回：RawJson 与文件内容逐字节一致（忽略 BOM 差异）
        string raw = File.ReadAllText(TestPaths.DefaultShaderJson);
        Assert.Equal(raw.TrimStart('\uFEFF'), project.RawJson.TrimStart('\uFEFF'));

        output.WriteLine($"passes: {string.Join(", ", project.Passes.Select(p => $"{p.Type}:{p.Name}"))}");
    }

    [Fact]
    public void LoadFile_RepairsMangledRealExport()
    {
        // 真实导出：根节点是数组、键名带前导空格、字符串里有裸 CR/LF、还有被打断的 "\ /"
        ShaderToyProject project = ShaderToyJsonImporter.LoadFile(TestPaths.FlGyWcJson);

        output.WriteLine(string.Join(Environment.NewLine, project.Warnings));

        Assert.Equal("flGyWc", project.Metadata.Id);
        Assert.Equal("lunchbreak", project.Metadata.Name);
        Assert.Equal("nabr", project.Metadata.Author);
        Assert.Equal(["sound", "minkowski", "fdn", "combfiltering"], project.Metadata.Tags);

        // [ { ... } ] 这种"API 返回数组"的根节点也要认
        Assert.Equal(2, project.Passes.Count);

        ShaderPass image = Assert.IsType<ShaderPass>(project.ImagePass);
        Assert.Equal("Image", image.Name); // 原文件是 " Image"
        Assert.Equal("4dfGRr", image.Id);
        Assert.Contains("void mainImage(out vec4 fragColor, in vec2 fragCoord)", image.Code);
        Assert.Contains("\n{\n", image.Code);   // 字面 \n 已还原成真换行
        Assert.DoesNotContain('\r', image.Code); // CRLF 折成了单个 \n

        ShaderPassInput texture = Assert.Single(image.Inputs);
        Assert.Equal(3, texture.Channel);
        Assert.Equal(ShaderInputType.Texture, texture.Type);
        Assert.StartsWith("/media/", texture.Path, StringComparison.Ordinal);
        Assert.Equal(SamplerFilter.Mipmap, texture.Sampler.Filter);
        Assert.Equal(SamplerWrap.Repeat, texture.Sampler.Wrap);
        Assert.True(texture.Sampler.VFlip);

        ShaderPass sound = Assert.IsType<ShaderPass>(project.SoundPass); // 键名是 " type"
        Assert.Contains("mainSound(int s, float t)", sound.Code);
        Assert.Empty(sound.Outputs);

        // 两类修复都要在 warnings 里说清楚，别让用户以为文件本来就是这样
        Assert.Contains(project.Warnings, w => w.Contains("裸控制字符", StringComparison.Ordinal));
        Assert.Contains(project.Warnings, w => w.Contains("转义序列", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_BuildsPassGraph()
    {
        ShaderToyProject project = ShaderToyJsonImporter.Parse(MultiPassJson);

        output.WriteLine(string.Join(Environment.NewLine, project.Warnings));

        // 未知 pass 类型被跳过，其余 6 个（含第二个 image）保留原顺序
        Assert.Equal(7, project.Passes.Count);
        Assert.Equal(
            [
                ShaderPassType.Common,
                ShaderPassType.Buffer,
                ShaderPassType.Buffer,
                ShaderPassType.Cubemap,
                ShaderPassType.Sound,
                ShaderPassType.Image,
                ShaderPassType.Image,
            ],
            project.Passes.Select(p => p.Type));
        Assert.Contains(project.Warnings, w => w.Contains("weird", StringComparison.Ordinal));

        // image 只认第一个
        Assert.Equal("imgId", project.ImagePass?.Id);

        // common 代码按顺序拼接，并补换行
        Assert.Equal("#define PI 3.14159\nvec2 rot(vec2 p) { return p; }\n", project.BuildCommonCode());

        IReadOnlyList<ShaderPass> buffers = project.BufferPasses;
        Assert.Equal(2, buffers.Count);
        Assert.Equal("4dfGR1", buffers[0].Id);
        Assert.Equal("12345", buffers[1].Id); // 数字 id 转成字符串

        Assert.Same(buffers[1], project.FindPassById("12345"));
        Assert.Null(project.FindPassById("imgId")); // image 不参与缓冲查找

        IReadOnlyList<ShaderPass> cubes = project.CubePasses;
        Assert.Equal("cubeId", Assert.Single(cubes).Id);
        Assert.Equal(ShaderPassType.Sound, project.SoundPass?.Type);
        Assert.True(project.SoundPass!.IsRenderable);
    }

    [Fact]
    public void Parse_ReadsInputTypesAndSamplers()
    {
        ShaderToyProject project = ShaderToyJsonImporter.Parse(MultiPassJson);
        ShaderPass bufferA = project.BufferPasses[0];

        Assert.Equal(2, bufferA.Inputs.Count);

        ShaderPassInput selfRef = bufferA.Inputs[0];
        Assert.Equal(0, selfRef.Channel);
        Assert.Equal(ShaderInputType.Buffer, selfRef.Type);
        Assert.Equal("4dfGRr", selfRef.Id);
        Assert.Null(selfRef.Path); // buffer 引用不该被当成纹理路径
        Assert.Equal(SamplerFilter.Nearest, selfRef.Sampler.Filter);
        Assert.Equal(SamplerWrap.Repeat, selfRef.Sampler.Wrap);
        Assert.False(selfRef.Sampler.VFlip);

        ShaderPassInput texture = bufferA.Inputs[1];
        Assert.Equal(ShaderInputType.Texture, texture.Type);
        Assert.Equal("https://example.com/noise.jpg", texture.Path); // 只有 src 时用它
        Assert.Equal(SamplerFilter.Mipmap, texture.Sampler.Filter);
        Assert.Equal(SamplerWrap.Clamp, texture.Sampler.Wrap);
        Assert.True(texture.Sampler.VFlip); // 字符串 "true" 也要认

        ShaderPass cube = project.CubePasses[0];
        Assert.Equal(ShaderInputType.Keyboard, cube.Inputs[0].Type);
        Assert.Equal(ShaderInputType.Volume, cube.Inputs[1].Type);
        Assert.Equal("volumeAsset", cube.Inputs[1].Path); // volume 用 id 当资源键

        ShaderPassInput imageCubeInput = project.ImagePass!.Inputs[0];
        Assert.Equal(ShaderInputType.Cubemap, imageCubeInput.Type);
        Assert.Equal("cubeId", imageCubeInput.Id);
        Assert.Equal(ShaderInputType.MusicStream, project.ImagePass.Inputs[1].Type);

        // 未知 input 类型被跳过并记进 warnings
        Assert.Empty(project.Passes[^1].Inputs);
        Assert.Contains(project.Warnings, w => w.Contains("shadow", StringComparison.Ordinal));

        // 纹理路径去重，含 volume 资源
        Assert.Equal(
            ["https://example.com/noise.jpg", "volumeAsset"],
            project.CollectTexturePaths());
    }

    [Fact]
    public void Parse_KeepsEscapedBackslashIntact()
    {
        const string json = """
            {
                "ver": "0.1",
                "info": { "id": "x" },
                "renderpass": [
                    { "type": "image", "name": "Image", "code": "a\\\\nb", "inputs": [], "outputs": [ { "id": "y" } ] }
                ]
            }
            """;

        ShaderToyProject project = ShaderToyJsonImporter.Parse(json);

        // JSON 解出来是 a\\nb（两反斜杠 + n），不能被反转义成 "反斜杠 + 换行"
        Assert.Equal(@"a\\nb", project.ImagePass!.Code);
    }

    [Fact]
    public void Parse_AcceptsPassesKeyAndMissingOptionalFields()
    {
        const string json = """
            {
                "info": {},
                "passes": [
                    { "type": "image", "code": "void mainImage(out vec4 c, in vec2 f) { c = vec4(0.0); }" }
                ]
            }
            """;

        ShaderToyProject project = ShaderToyJsonImporter.Parse(json);

        Assert.Equal(string.Empty, project.Version);
        ShaderPass image = Assert.Single(project.Passes);
        Assert.Equal(ShaderPassType.Image, image.Type);
        Assert.Equal(string.Empty, image.Name);
        Assert.Empty(image.Id);
        Assert.Contains(project.Warnings, w => w.Contains("没有 outputs", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_MissingRenderpassProducesWarning()
    {
        ShaderToyProject project = ShaderToyJsonImporter.Parse("""{ "ver": "0.1", "info": { "name": "empty" } }""");

        Assert.Equal("0.1", project.Version);
        Assert.Empty(project.Passes);
        Assert.Contains(project.Warnings, w => w.Contains("renderpass", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_WarnsWhenNoImagePass()
    {
        const string json = """
            {
                "ver": "0.1",
                "info": { "name": "buffers only" },
                "renderpass": [
                    { "type": "buffer", "name": "Buffer A", "code": "", "inputs": [], "outputs": [ { "id": "a" } ] }
                ]
            }
            """;

        ShaderToyProject project = ShaderToyJsonImporter.Parse(json);

        Assert.Null(project.ImagePass);
        Assert.Contains(project.Warnings, w => w.Contains("image pass", StringComparison.Ordinal));
    }

    [Fact]
    public void TryParse_ReportsErrorsWithoutThrowing()
    {
        Assert.False(ShaderToyJsonImporter.TryParse("{ not json", out ShaderToyProject? broken, out string? error));
        Assert.Null(broken);
        Assert.False(string.IsNullOrEmpty(error));

        Assert.False(ShaderToyJsonImporter.TryParse("[]", out _, out string? notObject));
        Assert.Contains("空数组", notObject!);

        Assert.False(ShaderToyJsonImporter.TryParse("123", out _, out string? notObjectEither));
        Assert.Contains("对象", notObjectEither!);

        Assert.True(ShaderToyJsonImporter.TryParse(MultiPassJson, out ShaderToyProject? ok, out string? noError));
        Assert.NotNull(ok);
        Assert.Null(noError);
    }

    [Theory]
    [InlineData("void mainImage(out vec4 c, in vec2 f)\n{\n  c = vec4(1.0);\n}", "void mainImage(out vec4 c, in vec2 f)\n{\n  c = vec4(1.0);\n}")]
    [InlineData("a\\nb", "a\nb")]
    [InlineData("a\\\\nb", @"a\\nb")]
    [InlineData("no escapes", "no escapes")]
    public void UnescapeSource_HandlesLiteralNewlines(string input, string expected)
    {
        Assert.Equal(expected, ShaderToyJsonImporter.UnescapeSource(input));
    }
}
