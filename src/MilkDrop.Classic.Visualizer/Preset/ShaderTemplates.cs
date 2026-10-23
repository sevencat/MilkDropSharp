using System.Text;

namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary>
/// 读取 <c>data/*.fx</c> 模板（对应 <c>CPlugin::ReadFileToString</c>，plugin.cpp:739-783）。
/// </summary>
/// <remarks>
/// 关键点：模板文件里的 <c>\r\n</c>、<c>\r</c>、<c>\n</c> 全部换成
/// <see cref="EelPreprocessor.LinefeedControlChar"/>（CRLF 只留一个），末尾再补一个空格 ——
/// 这是 C 版为了"把多行 shader 文本塞进一条 <c>.milk</c> 的 <c>warp_1=</c> 行"而定的编码方式。
/// 生成 shader 正文时也用同一个控制符，否则 D3DCompile 收到的是一整行。
/// </remarks>
public static class ShaderTemplates
{
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    /// <summary>默认 warp 像素着色器模板（<c>data/warp_ps.fx</c>）。</summary>
    public static string? DefaultWarpPixelShader => TryGet("warp_ps.fx");

    /// <summary>默认 comp 像素着色器模板（<c>data/comp_ps.fx</c>）。</summary>
    public static string? DefaultCompPixelShader => TryGet("comp_ps.fx");

    /// <summary>默认 warp 顶点着色器（<c>data/warp_vs.fx</c>）。</summary>
    public static string? DefaultWarpVertexShader => TryGet("warp_vs.fx");

    /// <summary>默认 comp 顶点着色器（<c>data/comp_vs.fx</c>）。</summary>
    public static string? DefaultCompVertexShader => TryGet("comp_vs.fx");

    /// <summary>着色器公共 include（<c>data/include.fx</c>）。</summary>
    public static string? Include => TryGet("include.fx");

    /// <summary>
    /// 读取并规范化一个 shader 文件；找不到时返回 <c>null</c>（资源目录缺失时不算致命错误，
    /// 调用方会退化成"没有 shader"）。
    /// </summary>
    public static string? TryGet(string fileName)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(fileName, out var cached))
            {
                return cached;
            }

            string? text = null;
            var path = Path.Combine(ResourceLocator.DataDir, fileName);
            if (File.Exists(path))
            {
                text = ReadNormalized(path);
            }

            Cache[fileName] = text;
            return text;
        }
    }

    /// <summary>
    /// 按 <c>ReadFileToString(..., bConvertLFsToSpecialChar: true)</c> 的规则读入文本。
    /// </summary>
    public static string ReadNormalized(string path)
    {
        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        var builder = new StringBuilder(raw.Length + 2);

        var previous = '\0';
        foreach (var ch in raw)
        {
            var original = ch;
            var current = ch;
            var skip = false;

            if (current == '\n')
            {
                if (previous == '\r')
                {
                    skip = true;
                }
                else
                {
                    current = EelPreprocessor.LinefeedControlChar;
                }
            }
            else if (current == '\r')
            {
                current = EelPreprocessor.LinefeedControlChar;
            }

            if (!skip)
            {
                builder.Append(current);
            }

            previous = original;
        }

        // 原版末尾补一个空格，保证拼接进来的正文不会和模板最后一行粘连
        builder.Append(' ');
        return builder.ToString();
    }
}
