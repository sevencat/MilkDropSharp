using System.Text.RegularExpressions;
using MilkDrop.Classic.Visualizer;
using MilkDrop.Eval;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// EEL 语料回归：把原版全部预设里的表达式抽出来逐个编译。
/// 基线（cbplan.md 附录 A.3）：692 条 / 0 失败。
/// </summary>
public sealed class PresetCorpusTests(ITestOutputHelper output)
{
    private static readonly string[] MilkPrefixes = ["per_frame_init_", "per_frame_", "per_pixel_"];

    private static readonly Regex ComponentPrefixRegex =
        new(@"^(?<p>wave_\d+_(?:init|per_frame|per_point)|shape_\d+_(?:init|per_frame))\d+$", RegexOptions.IgnoreCase);

    [Fact]
    public void AllPresetExpressionsCompile()
    {
        string root;
        try
        {
            root = ResourceLocator.PresetsDir;
        }
        catch (Exception ex)
        {
            output.WriteLine("跳过：找不到原版资源目录 - " + ex.Message);
            return;
        }

        if (!Directory.Exists(root))
        {
            output.WriteLine("跳过：预设目录不存在 " + root);
            return;
        }

        var cases = CollectCases(root);
        output.WriteLine($"语料单元: {cases.Count}");
        Assert.True(cases.Count > 300, $"语料数量异常（{cases.Count}），可能是资源定位或解析出错");

        var engine = new ProjectMEvalEngine();
        var failures = new List<string>();

        foreach (var (file, key, code) in cases)
        {
            var vm = engine.CreateVm(key);
            if (vm.Compile(code) is null)
            {
                failures.Add($"{Path.GetFileName(file)} [{key}] {vm.LastError} :: {Truncate(code)}");
            }
        }

        foreach (var failure in failures.Take(20))
        {
            output.WriteLine("FAIL " + failure);
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void PresetFilesAreParsable()
    {
        string root;
        try
        {
            root = ResourceLocator.PresetsDir;
        }
        catch (Exception ex)
        {
            output.WriteLine("跳过：找不到原版资源目录 - " + ex.Message);
            return;
        }

        if (!Directory.Exists(root))
        {
            return;
        }

        var files = Directory.EnumerateFiles(root, "*.milk", SearchOption.AllDirectories).ToArray();
        output.WriteLine($".milk 文件数: {files.Length}");
        Assert.True(files.Length > 100);

        foreach (var file in files)
        {
            var preset = PresetKeyValueFile.Load(file);
            Assert.False(string.IsNullOrEmpty(preset.GetString("MILKDROP_PRESET_VERSION")), $"{file} 缺少版本键");
        }
    }

    private static List<(string File, string Key, string Code)> CollectCases(string root)
    {
        var cases = new List<(string, string, string)>();

        foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".milk" or ".wave" or ".shape"))
            {
                continue;
            }

            var preset = PresetKeyValueFile.Load(path);
            IEnumerable<string> prefixes = ext == ".milk"
                ? MilkPrefixes
                : preset.Values.Keys
                    .Select(k => ComponentPrefixRegex.Match(k))
                    .Where(m => m.Success)
                    .Select(m => m.Groups["p"].Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var prefix in prefixes)
            {
                var code = preset.ReadCode(prefix);
                if (code.Trim().Length == 0)
                {
                    continue;
                }

                cases.Add((path, prefix.TrimEnd('_'), code));
            }
        }

        return cases;
    }

    private static string Truncate(string value) => value.Length <= 140 ? value : value[..140] + "...";
}
