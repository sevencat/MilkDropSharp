using System.Diagnostics;
using System.Globalization;
using System.Text;
using Silk.NET.OpenGLES;

namespace MilkDrop.ProjectM.App;

/// <summary>单个预设的批量跑结果。</summary>
/// <param name="MeanLuminance">最后一帧的平均亮度（0..1），用来发现"跑通但全黑"的预设。</param>
/// <param name="NonBlackRatio">最后一帧里亮度 &gt; 2% 的像素占比。</param>
internal sealed record BenchmarkEntry(
    string Path,
    bool Loaded,
    int FramesRendered,
    int GlError,
    double TotalMilliseconds,
    double MeanLuminance,
    double NonBlackRatio,
    string? Error,
    string? Screenshot);

/// <summary>
/// P9 批量验证：把一整个预设目录逐个加载、渲染 N 帧，统计崩溃/GL error/加载失败并出 CSV 报告。
/// </summary>
internal static class Benchmark
{
    /// <summary>跑完整批，返回进程退出码（0 = 全部通过）。</summary>
    public static int Run(
        GL gl,
        ProjectMEngine engine,
        IReadOnlyList<string> presets,
        int frames,
        string? reportPath,
        string? screenshotDirectory,
        int width,
        int height,
        Action? pumpAudioBeforeFrame = null)
    {
        if (presets.Count == 0)
        {
            Console.Error.WriteLine("bench: 没有可用的预设（用 --preset-dir 或 --playlist 指定）");
            return 2;
        }

        engine.SetWindowSize(width, height);

        var entries = new List<BenchmarkEntry>(presets.Count);
        // 批量跑固定时间步长：结果可复现，也方便和 AOT/JIT 版本逐像素对比。
        double previousFixedTimeStep = engine.FixedTimeStep;
        int previousTargetFps = engine.TargetFramesPerSecond;
        engine.FixedTimeStep = 1.0 / 60.0;
        engine.TargetFramesPerSecond = 60;

        var total = Stopwatch.StartNew();

        Console.WriteLine($"bench: {presets.Count} 个预设 x {frames} 帧 @ {width}x{height}");

        for (var index = 0; index < presets.Count; index++)
        {
            string presetPath = presets[index];

            string? loadError = null;
            void OnFailed(string file, string message) => loadError = message;

            engine.PresetSwitchFailed += OnFailed;

            var stopwatch = Stopwatch.StartNew();
            engine.LoadPresetFile(presetPath, smoothTransition: false);
            stopwatch.Stop();

            int glError = 0;
            var rendered = 0;

            if (loadError is null)
            {
                for (var frame = 0; frame < frames; frame++)
                {
                    pumpAudioBeforeFrame?.Invoke();
                    engine.RenderFrame();
                    rendered++;

                    var error = gl.GetError();
                    if (error != GLEnum.NoError)
                    {
                        glError = (int)error;
                        break;
                    }
                }
            }

            engine.PresetSwitchFailed -= OnFailed;

            string? screenshot = null;
            double meanLuminance = 0;
            double nonBlackRatio = 0;

            if (loadError is null && glError == 0)
            {
                (meanLuminance, nonBlackRatio) = MeasureFrame(gl, width, height);

                if (screenshotDirectory is not null)
                {
                    string fileName = $"{index:D5}_{Path.GetFileNameWithoutExtension(presetPath)}.png";
                    screenshot = Screenshot.Save(gl, width, height, Path.Combine(screenshotDirectory, fileName));
                }
            }

            var entry = new BenchmarkEntry(
                presetPath,
                loadError is null,
                rendered,
                glError,
                stopwatch.Elapsed.TotalMilliseconds,
                meanLuminance,
                nonBlackRatio,
                loadError,
                screenshot);

            entries.Add(entry);

            if (loadError is not null)
            {
                Console.WriteLine($"[{index + 1}/{presets.Count}] 加载失败 {Path.GetFileName(presetPath)}: {loadError}");
            }
            else if (glError != 0)
            {
                Console.WriteLine($"[{index + 1}/{presets.Count}] GL error 0x{glError:X4}: {presetPath}");
            }
            else if ((index + 1) % 50 == 0)
            {
                Console.WriteLine($"[{index + 1}/{presets.Count}] ...");
            }

            // 增量落盘：全量跑要一两个小时，中途被打断也能拿到已完成的部分。
            if (!string.IsNullOrEmpty(reportPath) && (index + 1) % 200 == 0)
            {
                WriteReport(reportPath, entries);
            }
        }

        total.Stop();

        engine.FixedTimeStep = previousFixedTimeStep;
        engine.TargetFramesPerSecond = previousTargetFps;

        int failed = entries.Count(entry => !entry.Loaded);
        int withGlError = entries.Count(entry => entry.GlError != 0);
        int ok = entries.Count(entry => entry.Loaded && entry.GlError == 0);
        int blank = entries.Count(entry => entry.Loaded && entry.GlError == 0 && entry.NonBlackRatio < 0.01);

        Console.WriteLine();
        Console.WriteLine("---- bench 汇总 ----");
        Console.WriteLine($"预设数: {entries.Count}    通过: {ok}    加载失败: {failed}    GL error: {withGlError}    全黑: {blank}");
        Console.WriteLine($"总耗时: {total.Elapsed.TotalSeconds:0.0}s    单预设平均: {entries.Average(e => e.TotalMilliseconds):0.0}ms");

        foreach (BenchmarkEntry slow in entries.OrderByDescending(e => e.TotalMilliseconds).Take(3))
        {
            Console.WriteLine($"最慢: {slow.TotalMilliseconds:0.0}ms  {Path.GetFileName(slow.Path)}");
        }

        if (failed + withGlError > 0)
        {
            Console.WriteLine("前 20 条失败:");
            foreach (BenchmarkEntry bad in entries.Where(e => !e.Loaded || e.GlError != 0).Take(20))
            {
                string reason = !bad.Loaded ? bad.Error ?? "unknown" : $"GL 0x{bad.GlError:X4}";
                Console.WriteLine($"  {Path.GetFileName(bad.Path)}: {reason}");
            }
        }

        if (!string.IsNullOrEmpty(reportPath))
        {
            WriteReport(reportPath, entries);
            Console.WriteLine("报告: " + Path.GetFullPath(reportPath));
        }

        return failed + withGlError == 0 ? 0 : 1;
    }

    /// <summary>
    /// 读回默认帧缓冲，统计平均亮度与非黑像素比例（用于发现"能跑但全黑"的预设）。
    /// </summary>
    /// <remarks>按每 16 个像素采样一次，几百 KB 的读取代价对批量跑可以忽略。</remarks>
    private static unsafe (double MeanLuminance, double NonBlackRatio) MeasureFrame(GL gl, int width, int height)
    {
        var pixels = new byte[width * height * 4];

        fixed (byte* pointer = pixels)
        {
            gl.PixelStore(GLEnum.PackAlignment, 1);
            gl.ReadPixels(0, 0, (uint)width, (uint)height, GLEnum.Rgba, GLEnum.UnsignedByte, pointer);
        }

        long luminanceSum = 0;
        var sampled = 0;
        var nonBlack = 0;

        for (var i = 0; i + 3 < pixels.Length; i += 4 * 16)
        {
            int luminance = (pixels[i] * 299 + pixels[i + 1] * 587 + pixels[i + 2] * 114) / 1000;
            luminanceSum += luminance;
            sampled++;

            if (luminance > 5)
            {
                nonBlack++;
            }
        }

        if (sampled == 0)
        {
            return (0, 0);
        }

        return (luminanceSum / (sampled * 255.0), nonBlack / (double)sampled);
    }

    private static void WriteReport(string reportPath, List<BenchmarkEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("index,file,loaded,frames,gl_error,load_ms,mean_luminance,non_black_ratio,error,screenshot");

        for (var i = 0; i < entries.Count; i++)
        {
            BenchmarkEntry entry = entries[i];
            builder.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(Escape(entry.Path)).Append(',');
            builder.Append(entry.Loaded ? "1" : "0").Append(',');
            builder.Append(entry.FramesRendered.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(entry.GlError.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(entry.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(entry.MeanLuminance.ToString("0.#####", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(entry.NonBlackRatio.ToString("0.#####", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(Escape(entry.Error ?? string.Empty)).Append(',');
            builder.Append(Escape(entry.Screenshot ?? string.Empty));
            builder.AppendLine();
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(reportPath, builder.ToString(), new UTF8Encoding(false));

        static string Escape(string value)
            => value.Contains(',') || value.Contains('"')
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
    }
}
