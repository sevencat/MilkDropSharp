using System.Globalization;

namespace MilkDrop.ProjectM.App;

/// <summary>命令行选项。</summary>
public sealed class AppOptions
{
    public int Width { get; private set; } = 1024;

    public int Height { get; private set; } = 768;

    public bool VSync { get; private set; } = true;

    /// <summary>界面缩放倍数（0 = 按窗口 DPI 自动推断）。</summary>
    public float UiScale { get; private set; }

    /// <summary>无人值守模式：渲染 N 帧后自动退出（0 = 不启用）。</summary>
    public int SmokeFrames { get; private set; }

    public bool ShowUi { get; private set; } = true;

    /// <summary>只做字体检查（建 ImGui 上下文 + 字体图集，不建窗口）。</summary>
    public bool FontCheck { get; private set; }

    public bool ShowHelp { get; private set; }

    // ---- P8：预设 / 播放列表 / 音频 / 参数 ----

    /// <summary>启动时加载的预设文件（为空则不加载，走待机预设或播放列表）。</summary>
    public string? PresetFile { get; private set; }

    /// <summary>要扫描的预设目录（可重复，递归）。</summary>
    public List<string> PresetDirectories { get; } = [];

    /// <summary>播放列表文件（每行一个预设路径，<c>#</c> 开头为注释）。</summary>
    public string? PlaylistFile { get; private set; }

    /// <summary>纹理搜索目录（可重复）。</summary>
    public List<string> TextureDirectories { get; } = [];

    public bool Shuffle { get; private set; }

    /// <summary>单个预设时长（秒，0 = 保持引擎默认）。</summary>
    public double PresetDuration { get; private set; }

    /// <summary>软切时长（秒，0 = 保持引擎默认）。</summary>
    public double BlendDuration { get; private set; }

    public bool HardCut { get; private set; }

    public float HardCutSensitivity { get; private set; }

    public float BeatSensitivity { get; private set; }

    public int MeshX { get; private set; }

    public int MeshY { get; private set; }

    public int TargetFps { get; private set; }

    public bool? AspectCorrection { get; private set; }

    public bool NoAudio { get; private set; }

    /// <summary>无人值守截图路径（配合 <c>--smoke</c>；空 = 不截图）。</summary>
    public string? ScreenshotPath { get; private set; }

    /// <summary>每隔 N 帧截一张图（0 = 关闭）。</summary>
    public int ScreenshotEvery { get; private set; }

    /// <summary>每隔 N 帧自动换下一个预设（0 = 关闭；无人值守验证软切路径用）。</summary>
    public int AutoNextFrames { get; private set; }

    // ---- P9：批量验证 ----

    /// <summary>批量跑模式：扫描所有预设，逐个渲染 N 帧并出报告。</summary>
    public bool Bench { get; private set; }

    public int BenchFrames { get; private set; } = 10;

    /// <summary>只跑前 N 个预设（0 = 全部）。</summary>
    public int BenchLimit { get; private set; }

    /// <summary>跳过前 N 个预设（用于分段跑完全量）。</summary>
    public int BenchSkip { get; private set; }

    /// <summary>批量跑报告（CSV）输出路径。</summary>
    public string? BenchReport { get; private set; }

    /// <summary>批量跑截图输出目录（每个预设一张 PNG）。</summary>
    public string? BenchShots { get; private set; }

    public static AppOptions Parse(string[] args)
    {
        var options = new AppOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--width":
                    options.Width = ReadInt(args, ref i, 1024);
                    break;
                case "--height":
                    options.Height = ReadInt(args, ref i, 768);
                    break;
                case "--ui-scale":
                    options.UiScale = ReadFloat(args, ref i);
                    break;
                case "--novsync":
                    options.VSync = false;
                    break;
                case "--vsync":
                    options.VSync = true;
                    break;
                case "--smoke":
                    options.SmokeFrames = ReadInt(args, ref i, 60);
                    break;
                case "--no-ui":
                    options.ShowUi = false;
                    break;
                case "--font-check":
                    options.FontCheck = true;
                    break;

                case "--preset":
                    options.PresetFile = ReadString(args, ref i);
                    break;
                case "--preset-dir":
                    AddIfPresent(options.PresetDirectories, ReadString(args, ref i));
                    break;
                case "--playlist":
                    options.PlaylistFile = ReadString(args, ref i);
                    break;
                case "--texture-dir":
                    AddIfPresent(options.TextureDirectories, ReadString(args, ref i));
                    break;
                case "--shuffle":
                    options.Shuffle = true;
                    break;
                case "--duration":
                    options.PresetDuration = ReadDouble(args, ref i);
                    break;
                case "--blend":
                    options.BlendDuration = ReadDouble(args, ref i);
                    break;
                case "--hard-cut":
                    options.HardCut = true;
                    break;
                case "--hard-cut-sens":
                    options.HardCutSensitivity = ReadFloat(args, ref i);
                    break;
                case "--beat-sens":
                    options.BeatSensitivity = ReadFloat(args, ref i);
                    break;
                case "--mesh":
                    options.MeshX = ReadInt(args, ref i, 32);
                    options.MeshY = ReadInt(args, ref i, 24);
                    break;
                case "--fps":
                    options.TargetFps = ReadInt(args, ref i, 0);
                    break;
                case "--no-aspect":
                    options.AspectCorrection = false;
                    break;
                case "--aspect":
                    options.AspectCorrection = true;
                    break;
                case "--no-audio":
                    options.NoAudio = true;
                    break;
                case "--shot":
                    options.ScreenshotPath = ReadString(args, ref i);
                    break;
                case "--shot-every":
                    options.ScreenshotEvery = ReadInt(args, ref i, 0);
                    break;
                case "--auto-next":
                    options.AutoNextFrames = ReadInt(args, ref i, 0);
                    break;

                case "--bench":
                    options.Bench = true;
                    break;
                case "--bench-frames":
                    options.BenchFrames = ReadInt(args, ref i, 10);
                    break;
                case "--bench-limit":
                    options.BenchLimit = ReadInt(args, ref i, 0);
                    break;
                case "--bench-skip":
                    options.BenchSkip = ReadInt(args, ref i, 0);
                    break;
                case "--bench-report":
                    options.BenchReport = ReadString(args, ref i);
                    break;
                case "--bench-shots":
                    options.BenchShots = ReadString(args, ref i);
                    break;

                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    break;
                default:
                    Console.WriteLine($"忽略未知参数: {arg}");
                    break;
            }
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            MilkDrop.ProjectM.App — projectM 4.1.8 的 C# 移植（Silk.NET + ANGLE/GLES3 + ImGui）

            用法: MilkDrop.ProjectM.App [选项]

            窗口 / 界面
              --width N             窗口宽度（默认 1024）
              --height N            窗口高度（默认 768）
              --ui-scale F          ImGui 缩放倍数（默认按 DPI 推断）
              --novsync             关闭垂直同步
              --no-ui               不建 ImGui 界面（纯画面）
              --font-check          只检查中文字体字形覆盖（不建窗口），有缺字返回 1

            预设
              --preset FILE         启动时加载该预设
              --preset-dir DIR      扫描目录下的 .milk（可重复，递归）
              --playlist FILE       文本播放列表（每行一个路径，# 为注释）
              --shuffle             随机播放
              --duration SEC        每个预设显示时长（默认 30）
              --blend SEC           软切时长（默认 3；0 = 立即切换）

            画面 / 音频
              --mesh X Y            per-pixel 网格尺寸（默认 32 24）
              --fps N               写进 RenderContext.Fps 的目标帧率
              --no-aspect           关闭宽高比校正
              --no-audio            不采集系统音频（用静音驱动）
              --beat-sens F         节拍灵敏度（0..2）
              --hard-cut            启用节拍硬切
              --hard-cut-sens F     硬切阈值（默认 2.0）

            无人值守 / 验证
              --smoke N             渲染 N 帧后退出
              --shot FILE           退出前把最后一帧存成 PNG
              --shot-every N        每 N 帧存一张 PNG（文件名自动编号）
              --auto-next N         每 N 帧自动切到下一个预设（走软切路径）
              --bench               批量跑：逐个预设渲染 --bench-frames 帧并出报告
              --bench-frames N      每个预设渲染帧数（默认 10）
              --bench-limit N       只跑前 N 个预设（0 = 全部）
              --bench-skip N        跳过前 N 个预设（配合 --bench-limit 分段跑）
              --bench-report FILE   报告（CSV）路径
              --bench-shots DIR     每个预设存一张 PNG

              -h, --help            显示帮助

            快捷键（窗口有焦点时）
              Space 暂停/继续     N / →  下一个      P / ←  上一个
              R 随机              L 锁定当前      S 截图
              F 显示/隐藏界面     H 帮助          Esc / Q 退出
            """);
    }

    private static void AddIfPresent(List<string> list, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            list.Add(value);
        }
    }

    private static string? ReadString(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            return null;
        }

        index++;
        return args[index];
    }

    private static int ReadInt(string[] args, ref int index, int fallback)
    {
        if (index + 1 >= args.Length)
        {
            return fallback;
        }

        if (int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            index++;
            return value;
        }

        return fallback;
    }

    private static float ReadFloat(string[] args, ref int index)
    {
        if (index + 1 < args.Length
            && float.TryParse(args[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            index++;
            return value;
        }

        return 0;
    }

    private static double ReadDouble(string[] args, ref int index)
    {
        if (index + 1 < args.Length
            && double.TryParse(args[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            index++;
            return value;
        }

        return 0;
    }
}
