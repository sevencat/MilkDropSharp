namespace MilkDrop.Classic.App;

/// <summary>命令行参数。</summary>
public sealed class AppOptions
{
    public int Width { get; private set; } = 1280;

    public int Height { get; private set; } = 720;

    /// <summary>用户显式指定过宽/高（此时不再按 DPI 放大窗口）。</summary>
    public bool HasExplicitSize { get; private set; }

    /// <summary>UI 缩放倍数；0 = 按窗口 DPI 自动判断。</summary>
    public float UiScale { get; private set; }

    public bool VSync { get; private set; } = true;

    public bool Hardware { get; private set; } = true;

    /// <summary>&gt;0 时跑够秒数后自动退出（用于 CI / 冒烟验证）。</summary>
    public double SmokeSeconds { get; private set; }

    public bool ShowDemoWindow { get; private set; }

    /// <summary>非空时，跑到 <see cref="ScreenshotDelay"/> 秒后截屏并退出。</summary>
    public string? ScreenshotPath { get; private set; }

    public double ScreenshotDelay { get; private set; } = 1.5;

    /// <summary><c>feedback</c>（Spike S1，默认）或 <c>fan</c>（M0 测试图案）。</summary>
    public string Pattern { get; private set; } = "feedback";

    /// <summary><c>loopback</c>（WASAPI 回环，默认）或 <c>none</c>（静音，确定性测试用）。</summary>
    public string Audio { get; private set; } = "loopback";

    /// <summary><c>--preset</c> 指定的预设（<c>.milk</c> 路径或文件名片段）；null = 不预设。</summary>
    public string? Preset { get; private set; }

    /// <summary><c>--preset random</c>：从资源目录里随机挑一个。</summary>
    public bool RandomPreset { get; private set; }

    /// <summary><c>--sprite</c>：启动即挂一个 sprite（图片路径或 sprites 目录里的文件名片段）。</summary>
    public string? Sprite { get; private set; }

    /// <summary><c>--sprite-code</c>：给 <c>--sprite</c> 的 sprite 一段 init 代码（调试用）。</summary>
    public string SpriteInitCode { get; private set; } = string.Empty;

    /// <summary>
    /// <c>--shader-compat</c>：打开 shader 兼容层（源码级改写 + profile 升级）。
    /// 默认关：与原版一致 —— 这些 shader 编译失败并回退默认 shader。
    /// 别名 <c>--fix-macro-shadowing</c>（旧参数名）仍然有效。
    /// </summary>
    public bool ShaderCompat { get; private set; }

    /// <summary>
    /// <c>--preset-duration &lt;秒&gt;</c>：自动换预设的平均间隔（默认 16，另有 0..10 秒随机抖动）。
    /// </summary>
    public float? PresetDuration { get; private set; }

    /// <summary>
    /// <c>--blend &lt;秒&gt;</c>：换预设时的交叉时长（默认自动 2.7 / 手动 1.7）；<c>0</c> = 硬切。
    /// </summary>
    public float? BlendTime { get; private set; }

    /// <summary><c>--lock-preset</c>：锁定当前预设，不自动切换（UI 里也能勾）。</summary>
    public bool LockPreset { get; private set; }

    public static AppOptions Parse(string[] args)
    {
        var options = new AppOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--width" when i + 1 < args.Length:
                    options.Width = int.Parse(args[++i]);
                    options.HasExplicitSize = true;
                    break;
                case "--height" when i + 1 < args.Length:
                    options.Height = int.Parse(args[++i]);
                    options.HasExplicitSize = true;
                    break;
                case "--ui-scale" when i + 1 < args.Length:
                    options.UiScale = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--novsync":
                    options.VSync = false;
                    break;
                case "--reference":
                    options.Hardware = false;
                    break;
                case "--demo":
                    options.ShowDemoWindow = true;
                    break;
                case "--smoke" when i + 1 < args.Length:
                    options.SmokeSeconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--smoke":
                    options.SmokeSeconds = 3.0;
                    break;
                case "--pattern" when i + 1 < args.Length:
                    options.Pattern = args[++i].ToLowerInvariant();
                    break;
                case "--audio" when i + 1 < args.Length:
                    options.Audio = args[++i].ToLowerInvariant();
                    break;
                case "--preset" when i + 1 < args.Length:
                    var preset = args[++i];
                    if (preset.Equals("random", StringComparison.OrdinalIgnoreCase))
                    {
                        options.RandomPreset = true;
                    }
                    else
                    {
                        options.Preset = preset;
                    }

                    break;
                case "--sprite" when i + 1 < args.Length:
                    options.Sprite = args[++i];
                    break;
                case "--shader-compat":
                case "--fix-macro-shadowing":   // 旧参数名，保留兼容
                    options.ShaderCompat = true;
                    break;
                case "--sprite-code" when i + 1 < args.Length:
                    options.SpriteInitCode = args[++i];
                    break;
                case "--preset-duration" when i + 1 < args.Length:
                    options.PresetDuration = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--blend" when i + 1 < args.Length:
                    options.BlendTime = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--lock-preset":
                    options.LockPreset = true;
                    break;
                case "--shot" when i + 1 < args.Length:
                    options.ScreenshotPath = args[++i];
                    break;
                case "--shot-delay" when i + 1 < args.Length:
                    options.ScreenshotDelay = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }

        return options;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            MilkDrop.Classic (C# port) — 命令行参数
              --width <px>     窗口宽（默认 1280）
              --height <px>    窗口高（默认 720）
              --novsync        关闭垂直同步
              --reference      使用 D3D9 reference 设备（软件光栅，调试用）
              --demo           打开 ImGui demo 窗口
              --smoke [秒]     跑指定秒数后自动退出（默认 3）
              --audio <mode>   loopback（默认，WASAPI 回环）或 none（静音）
              --preset <名字>  启动即加载预设（.milk 路径、文件名片段，或 random）
                              预设模式下按 N / P / 空格 切下一个 / 上一个 / 随机
              --preset-duration <秒>  自动换预设的平均间隔（默认 16，另有 0..10 秒随机抖动）
              --blend <秒>     换预设时的交叉时长（默认自动 2.7 / 手动 1.7）；0 = 硬切
              --lock-preset    锁定当前预设，不自动切换（UI 里也有开关，快捷键 L）
              --sprite <名字>  启动即挂一个 sprite（图片路径，或 sprites 目录里的文件名片段）
              --sprite-code <EEL>  给 --sprite 的 sprite 一段 init 代码（调试用，例如
                               "sx=0.25;sy=0.25;blendmode=2;"）
              --shader-compat  打开 shader 兼容层（宏名遮蔽 / double / 非有限字面量 /
                               get_fft / mouse / ps_2_0 指令槽升级；默认关）
              --fix-macro-shadowing  --shader-compat 的旧名字
              --ui-scale <倍率> UI 缩放（默认按窗口 DPI 自动，100% = 1.0）
              -h, --help       显示帮助
            """);
    }
}
