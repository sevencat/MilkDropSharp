using ImGuiNET;

namespace MilkDrop.ShaderToy.App.Ui;

/// <summary>
/// ImGui 字体：默认是 <c>ProggyClean</c>（只有 ASCII），界面里的中文会变成方块，
/// 所以这里挂一个系统中文字体 + 一个等宽字体。
/// </summary>
/// <remarks>
/// 全部从 <c>C:\Windows\Fonts</c> 读，不随包分发（省体积，也避免字体授权问题）。
/// 找不到就退回默认字体 —— 文字难看但不影响功能。
/// 注意：ImGui 用 <c>Fonts[0]</c>（或 <c>io.FontDefault</c>）当默认字体，
/// 所以中文字体加载成功时它就是默认字体，不必再额外指定。
/// </remarks>
internal static unsafe class UiFonts
{
    private static readonly string[] ChineseFonts =
    [
        @"C:\Windows\Fonts\msyh.ttc",   // 微软雅黑（TTC 索引 0）
        @"C:\Windows\Fonts\msyh.ttf",
        @"C:\Windows\Fonts\simhei.ttf", // 黑体
        @"C:\Windows\Fonts\simsun.ttc",
    ];

    private static readonly string[] MonoFonts =
    [
        @"C:\Windows\Fonts\consola.ttf",
        @"C:\Windows\Fonts\cour.ttf",
    ];

    /// <summary>100% DPI 下的界面字号（ImGui 默认 13px 太小）。</summary>
    private const float UiFontSize = 18f;

    /// <summary>100% DPI 下的代码字号。</summary>
    private const float MonoFontSize = 17f;

    private static ImFontPtr _mono;

    /// <summary>中文字体是否加载成功。</summary>
    public static bool ChineseLoaded { get; private set; }

    /// <summary>等宽字体（shader 代码/报错日志用）；没加载成功时退回当前默认字体。</summary>
    public static ImFontPtr Mono =>
        _mono.NativePtr != null ? _mono : ImGui.GetIO().FontDefault;

    /// <summary>
    /// 在 <c>ImGui.CreateContext()</c> 之后、创建渲染器（建字体图集）之前调用。
    /// </summary>
    /// <param name="scale">DPI 缩放（1.0 = 96 DPI）。字号要跟着放大，否则高 DPI 下字只有一半大。</param>
    public static void Setup(float scale = 1f)
    {
        scale = Math.Clamp(scale, 0.5f, 3f);

        ImGuiIOPtr io = ImGui.GetIO();
        ImFontAtlasPtr fonts = io.Fonts;
        float uiSize = UiFontSize * scale;
        float monoSize = MonoFontSize * scale;

        nint chineseRanges = fonts.GetGlyphRangesChineseSimplifiedCommon();
        ImFontPtr chinese = default;
        string? chineseFontPath = null;

        foreach (string path in ChineseFonts)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                ImFontPtr font;
                ImFontConfig* config = CreateConfig(oversample: 1);

                try
                {
                    font = fonts.AddFontFromFileTTF(path, uiSize, config, chineseRanges);
                }
                finally
                {
                    ImGuiNative.ImFontConfig_destroy(config);
                }

                if (font.NativePtr != null)
                {
                    chinese = font;
                    chineseFontPath = path;
                    Console.WriteLine($"ImGui 中文字体: {path}  {uiSize:0.#}px");
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"加载中文字体失败（{path}）: {ex.Message}");
            }
        }

        if (chinese.NativePtr != null)
        {
            ChineseLoaded = true;
            io.NativePtr->FontDefault = chinese.NativePtr;
        }
        else
        {
            // 没中文也得有字：默认字体兜底
            fonts.AddFontDefault();
            Console.Error.WriteLine("没找到可用的中文字体，界面里的中文会显示成方块。");
        }

        foreach (string path in MonoFonts)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                ImFontPtr font;
                ImFontConfig* monoConfig = CreateConfig(oversample: 1);

                try
                {
                    font = fonts.AddFontFromFileTTF(path, monoSize, monoConfig);
                }
                finally
                {
                    ImGuiNative.ImFontConfig_destroy(monoConfig);
                }

                if (font.NativePtr == null)
                {
                    continue;
                }

                // 等宽字体只有 ASCII，把中文字形合并进来（MergeMode）：
                // shader 注释里写中文时不至于全是方块，代价是图集大一圈。
                if (chinese.NativePtr != null && chineseFontPath is not null)
                {
                    ImFontConfig* mergeConfig = CreateConfig(oversample: 1);
                    mergeConfig->MergeMode = 1;

                    try
                    {
                        fonts.AddFontFromFileTTF(chineseFontPath, monoSize, mergeConfig, chineseRanges);
                    }
                    finally
                    {
                        ImGuiNative.ImFontConfig_destroy(mergeConfig);
                    }
                }

                _mono = font;
                Console.WriteLine($"ImGui 等宽字体: {path}  {monoSize:0.#}px");
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"加载等宽字体失败（{path}）: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 统一的字体配置：关闭水平/垂直过采样。
    /// </summary>
    /// <remarks>
    /// ImGui 1.90 默认 `OversampleH/V = 2`，即每个字形按 2 倍分辨率栅格化 ——
    /// 2500 个汉字的图集在 175% DPI（31.5px）下会直接顶到 2048×4096（32 MB）。
    /// 字号已经足够大，过采样收益很小，关掉后图集面积降到 1/4。
    /// </remarks>
    private static ImFontConfig* CreateConfig(int oversample)
    {
        ImFontConfig* config = ImGuiNative.ImFontConfig_ImFontConfig();
        config->OversampleH = oversample;
        config->OversampleV = oversample;
        config->PixelSnapH = 1;
        return config;
    }
}
