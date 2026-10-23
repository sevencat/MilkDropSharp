using System.Globalization;

namespace MilkDrop.ProjectM.App;

/// <summary>命令行选项。</summary>
/// <remarks>
/// 目前只有 P0（ANGLE/EGL 打通）需要的几项；后续阶段按 <c>MilkDrop.Classic.App/AppOptions.cs</c>
/// 的样式继续补 <c>--preset</c>/<c>--audio</c>/<c>--shot</c> 等。
/// </remarks>
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

              --width N         窗口宽度（默认 1024）
              --height N        窗口高度（默认 768）
              --ui-scale F      ImGui 缩放倍数（默认按 DPI 推断）
              --novsync         关闭垂直同步
              --smoke N         无人值守：渲染 N 帧后退出并返回 0
              --no-ui           不建 ImGui 界面（只清屏 + 探测）
              --font-check      只检查中文字体字形覆盖（不建窗口），有缺字返回 1
              -h, --help        显示帮助
            """);
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
}
