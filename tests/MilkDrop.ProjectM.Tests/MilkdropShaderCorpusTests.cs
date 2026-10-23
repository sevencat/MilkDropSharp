using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using MilkDrop.ProjectM.MilkdropPreset;
using MilkDrop.ProjectM.Renderer;
using MilkDrop.ProjectM.Tests.Rendering;
using Xunit;
using Xunit.Abstractions;
using GLEnum = Silk.NET.OpenGLES.GLEnum;
using Sampler = MilkDrop.ProjectM.Renderer.Sampler;
using Shader = MilkDrop.ProjectM.Renderer.Shader;
using Texture = MilkDrop.ProjectM.Renderer.Texture;

namespace MilkDrop.ProjectM.Tests;

/// <summary>
/// P3 验收：把语料里所有 <c>.milk</c> 的 warp/composite 着色器
/// 「预处理 → 转译 → GLES3 编译」跑一遍，统计成功率。
/// </summary>
/// <remarks>
/// 默认不跑（9800+ 预设要几十秒到几分钟，且需要离屏 GL）。设置环境变量
/// <c>MILKDROP_CORPUS=1</c> 打开；失败清单写到临时目录的
/// <c>milkdrop-corpus-failures.txt</c>。
/// </remarks>
[Collection("GL")]
public sealed class MilkdropShaderCorpusTests(OffscreenGlFixture gl, ITestOutputHelper output)
{
    /// <summary>成功率下限（P3 验收标准）。</summary>
    private const double MinimumSuccessRate = 0.95;

    private static readonly Regex VolumeSamplerRegex = new(@"tex3D\s*\(\s*sampler_([A-Za-z0-9_]+)");

    [Fact]
    public void PresetShaders_TranspileAndCompile()
    {
        if (Environment.GetEnvironmentVariable("MILKDROP_CORPUS") != "1")
        {
            return;
        }

        if (!Directory.Exists(TestPaths.PresetDir))
        {
            output.WriteLine("预设目录不存在，跳过: " + TestPaths.PresetDir);
            return;
        }

        if (!gl.TryMakeCurrent())
        {
            output.WriteLine("没有可用的离屏 GL 上下文，跳过: " + gl.SkipReason);
            return;
        }

        using var sampler = new Sampler((int)GLEnum.ClampToEdge, (int)GLEnum.Linear);

        int total = 0;
        int succeeded = 0;
        var failures = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        foreach (string presetPath in Directory.EnumerateFiles(TestPaths.PresetDir, "*.milk", SearchOption.AllDirectories))
        {
            var fileParser = new PresetFileParser();
            if (!fileParser.ReadFile(presetPath))
            {
                continue;
            }

            foreach (bool warp in new[] { true, false })
            {
                string code = fileParser.GetCode(warp ? "warp_" : "comp_");
                if (code.Length == 0)
                {
                    continue;
                }

                ++total;

                if (TryTranslateAndCompile(code, warp, sampler, out string failure))
                {
                    ++succeeded;
                }
                else
                {
                    failures.Add(Path.GetFileName(presetPath) + (warp ? ".warp: " : ".comp: ") + failure);
                }
            }
        }

        stopwatch.Stop();

        string failureFile = Path.Combine(Path.GetTempPath(), "milkdrop-corpus-failures.txt");
        File.WriteAllLines(failureFile, failures, Encoding.UTF8);

        double successRate = total == 0 ? 1 : (double)succeeded / total;
        output.WriteLine($"预设着色器 {total} 个，成功 {succeeded}，失败 {failures.Count}，成功率 {successRate:P2}，耗时 {stopwatch.Elapsed.TotalSeconds:F1}s");
        output.WriteLine("失败清单: " + failureFile);

        foreach (string failure in failures.Take(20))
        {
            output.WriteLine("  " + failure);
        }

        Assert.True(total > 0, "语料里没有找到任何预设着色器");
        Assert.True(successRate >= MinimumSuccessRate,
            $"成功率 {successRate:P2} 低于 {MinimumSuccessRate:P0}，失败清单见 {failureFile}");
    }

    private static bool TryTranslateAndCompile(string code, bool warp, Sampler sampler, out string failure)
    {
        failure = string.Empty;

        try
        {
            var shader = new MilkdropShader(warp ? ShaderType.WarpShader : ShaderType.CompositeShader);
            shader.LoadCode(code);

            var mainDescriptors = new List<TextureSamplerDescriptor>();
            var otherDescriptors = new List<TextureSamplerDescriptor>();
            var volumeSamplers = FindVolumeSamplers(code);

            foreach (string name in shader.SamplerNames)
            {
                // 模糊纹理由下面按模糊级别单独给（且没有 texsize）。
                string baseName = MilkdropShader.GetSamplerBaseName(name).ToLowerInvariant();
                if (baseName is "blur1" or "blur2" or "blur3")
                {
                    continue;
                }

                if (baseName == "main")
                {
                    mainDescriptors.Add(CreateDescriptor(sampler, name, "main", volume: false));
                }
                else
                {
                    otherDescriptors.Add(CreateDescriptor(sampler, name, name, volumeSamplers.Contains(name)));
                }
            }

            var blurDescriptors = new List<TextureSamplerDescriptor>();
            if (shader.MaxBlurLevelRequired >= BlurLevel.Blur1)
            {
                blurDescriptors.Add(CreateDescriptor(sampler, "blur1", string.Empty, volume: false));
            }

            if (shader.MaxBlurLevelRequired >= BlurLevel.Blur2)
            {
                blurDescriptors.Add(CreateDescriptor(sampler, "blur2", string.Empty, volume: false));
            }

            if (shader.MaxBlurLevelRequired >= BlurLevel.Blur3)
            {
                blurDescriptors.Add(CreateDescriptor(sampler, "blur3", string.Empty, volume: false));
            }

            using var program = new Shader();
            shader.LoadTexturesAndCompile(program, mainDescriptors, blurDescriptors, otherDescriptors);
            return true;
        }
        catch (Exception ex) when (ex is ShaderException or InvalidOperationException or ArgumentException)
        {
            failure = FirstLine(ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 语料里没有 TextureManager，只能从代码推断 3D 纹理（<c>tex3D(sampler_x, ...)</c>）。
    /// 真正的 2D/3D 由纹理自身的 target 决定，见 P7 的 <c>TextureManager</c>。
    /// </summary>
    private static HashSet<string> FindVolumeSamplers(string code)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in VolumeSamplerRegex.Matches(code))
        {
            names.Add(match.Groups[1].Value);
        }

        return names;
    }

    private static TextureSamplerDescriptor CreateDescriptor(Sampler sampler, string samplerName, string sizeName, bool volume)
    {
        // 只用于生成 uniform 声明：接管一个假句柄（id 非 0），不分配 GL 资源。
        var texture = new Texture(samplerName, 1u, volume ? GLEnum.Texture3D : GLEnum.Texture2D, 4, 4, false);
        return new TextureSamplerDescriptor(texture, sampler, samplerName, sizeName);
    }

    private static string FirstLine(string message)
    {
        int index = message.IndexOf('\n');
        return index < 0 ? message : message[..index];
    }
}
