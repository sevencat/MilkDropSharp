namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 把 preset 着色器的失败异常整理成一句可以显示在界面上的中文原因。
/// </summary>
/// <remarks>
/// <see cref="ShaderException"/> 的消息里带着完整的转译源码（几千字符、多行），
/// 直接塞进 ImGui 单行文本会把整个窗口撑烂，这里只保留"错误分类 + 第一条诊断 + 第一行细节"。
/// </remarks>
internal static class ShaderFailureReason
{
    /// <summary>单行详情的最大长度（超出截断并加省略号）。</summary>
    private const int MaxDetailLength = 200;

    /// <summary>诊断段落的分隔标记（见 <c>MilkdropShader.DiagnosticsSuffix</c>）。</summary>
    private const string DiagnosticsHeader = "Diagnostics:";

    /// <summary>warp 着色器的失败原因。</summary>
    public static string Warp(ShaderException exception) => Build("warp", exception);

    /// <summary>composite 着色器的失败原因。</summary>
    public static string Composite(ShaderException exception) => Build("composite", exception);

    private static string Build(string shaderKind, ShaderException exception)
    {
        string[] lines = (exception.Message ?? string.Empty).Split('\n');

        string detail = FirstNonEmpty(lines, 0);
        if (detail.Length == 0)
        {
            return $"{shaderKind} 着色器处理失败";
        }

        // "Diagnostics:" 之后的第一个非空行带着行号，比笼统的 failed 有用得多。
        string diagnostic = FirstNonEmptyAfter(lines, DiagnosticsHeader).TrimStart(':');
        if (diagnostic.Length > 0)
        {
            detail = $"{detail}（{diagnostic}）";
        }

        if (detail.Length > MaxDetailLength)
        {
            detail = detail[..MaxDetailLength] + "…";
        }

        return detail;
    }

    /// <summary>从 <paramref name="start"/> 开始找第一个非空行。</summary>
    private static string FirstNonEmpty(string[] lines, int start)
    {
        for (int i = start; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    /// <summary>找 <paramref name="header"/> 那一行之后的第一个非空行。</summary>
    private static string FirstNonEmptyAfter(string[] lines, string header)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == header)
            {
                return FirstNonEmpty(lines, i + 1);
            }
        }

        return string.Empty;
    }
}
