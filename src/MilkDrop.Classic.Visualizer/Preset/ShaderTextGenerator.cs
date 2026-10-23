using System.Globalization;

namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary>
/// <c>CPlugin::GenWarpPShaderText</c>（plugin.cpp:8782-8802）与
/// <c>CPlugin::GenCompPShaderText</c>（plugin.cpp:8804-8847）的移植。
/// </summary>
/// <remarks>
/// 这两个函数只在"预设没写自己的 shader 正文"时被调用：把默认模板
/// （<c>data/warp_ps.fx</c> / <c>data/comp_ps.fx</c>）里<b>最后一个 <c>{</c></b> 之后的内容
/// 换成按预设参数生成的老式正文 —— 也就是 MilkDrop 1 时代的固定效果（decay / video echo /
/// gamma / hue / brighten / darken / solarize / invert）在 ps_2_0 里的等价实现。
/// 换行一律用 <see cref="EelPreprocessor.LinefeedControlChar"/>，与 C 版一致。
/// </remarks>
public static class ShaderTextGenerator
{
    private static readonly char Lf = EelPreprocessor.LinefeedControlChar;

    /// <summary>
    /// 生成"默认 warp 像素着色器"正文：采样上一帧 + 按 decay 变暗。
    /// </summary>
    /// <param name="template">默认模板（含 <c>shader_body { ... }</c>）。</param>
    /// <param name="decay"><c>m_fDecay.eval(-1)</c>。</param>
    /// <param name="wrap"><c>bTexWrap</c>：true 用 <c>sampler_main</c>，false 用 <c>sampler_fc_main</c>。</param>
    public static string GenWarpPShaderText(string template, float decay, bool wrap)
    {
        var body = string.Concat(
            "    // sample previous frame", Lf,
            wrap
                ? "    ret = tex2D( sampler_main, uv ).xyz;"
                : "    ret = tex2D( sampler_fc_main, uv ).xyz;",
            Lf,
            Lf,
            "    // darken (decay) over time", Lf,
            "    ret *= ", Format2(decay), "; //or try: ret -= 0.004;", Lf);

        return ReplaceBody(template, body);
    }

    /// <summary>
    /// 生成"默认 comp 像素着色器"正文：video echo + gamma + 老式 hue + 四种颜色后处理。
    /// </summary>
    /// <param name="template">默认模板（含 <c>shader_body { ... }</c>）。</param>
    /// <param name="brightness"><c>m_fGammaAdj.eval(-1)</c>。</param>
    /// <param name="veAlpha"><c>m_fVideoEchoAlpha.eval(-1)</c>，&gt;0.001 才产生 echo 分支。</param>
    /// <param name="veZoom"><c>m_fVideoEchoZoom.eval(-1)</c>。</param>
    /// <param name="veOrient"><c>m_nVideoEchoOrientation</c>（0..3）。</param>
    /// <param name="hueShader"><c>m_fShader.eval(-1)</c>（&gt;=1 用旧式"直接乘"，否则按比例混）。</param>
    public static string GenCompPShaderText(
        string template,
        float brightness,
        float veAlpha,
        float veZoom,
        int veOrient,
        float hueShader,
        bool brighten,
        bool darken,
        bool solarize,
        bool invert)
    {
        var body = new System.Text.StringBuilder();

        if (veAlpha > 0.001f)
        {
            // 朝向：0/2 不翻转 X，1/3 翻 X；2/3 翻 Y
            var orientX = (veOrient % 2) != 0 ? -1 : 1;
            var orientY = veOrient >= 2 ? -1 : 1;

            body.Append("    float2 uv_echo = (uv - 0.5)*")
                .Append(Format3(1.0f / veZoom))
                .Append("*float2(").Append(orientX).Append(',').Append(orientY).Append(") + 0.5;").Append(Lf);
            body.Append("    ret = lerp( tex2D(sampler_main, uv).xyz, ").Append(Lf);
            body.Append("                tex2D(sampler_main, uv_echo).xyz, ").Append(Lf);
            body.Append("                ").Append(Format2(veAlpha)).Append(' ').Append(Lf);
            body.Append("              ); //video echo").Append(Lf);
            body.Append("    ret *= ").Append(Format2(brightness)).Append("; //gamma").Append(Lf);
        }
        else
        {
            body.Append("    ret = tex2D(sampler_main, uv).xyz;").Append(Lf);
            body.Append("    ret *= ").Append(Format2(brightness)).Append("; //gamma").Append(Lf);
        }

        if (hueShader >= 1.0f)
        {
            // 旧式 hue shader：直接乘 shader 常量
            body.Append("    ret *= hue_shader; //old hue shader effect").Append(Lf);
        }
        else if (hueShader > 0.001f)
        {
            body.Append("    ret *= ")
                .Append(Format2(1.0f - hueShader))
                .Append(" + ")
                .Append(Format2(hueShader))
                .Append("*hue_shader; //old hue shader effect")
                .Append(Lf);
        }

        if (brighten)
        {
            body.Append("    ret = sqrt(ret); //brighten").Append(Lf);
        }

        if (darken)
        {
            body.Append("    ret *= ret; //darken").Append(Lf);
        }

        if (solarize)
        {
            body.Append("    ret = ret*(1-ret)*4; //solarize").Append(Lf);
        }

        if (invert)
        {
            body.Append("    ret = 1 - ret; //invert").Append(Lf);
        }

        return ReplaceBody(template, body.ToString());
    }

    /// <summary>
    /// 把模板里<b>最后一个</b> <c>{</c> 之后的内容整段替换掉（对应 <c>strrchr(szShaderText, '{')</c>）。
    /// </summary>
    private static string ReplaceBody(string template, string body)
    {
        var brace = template.LastIndexOf('{');
        if (brace < 0)
        {
            // 模板异常（没有函数体）：原版直接原样返回
            return template;
        }

        return template[..(brace + 1)] + Lf + body + "}" + Lf;
    }

    /// <summary><c>"%.2f"</c>（printf 用小数点，受区域设置影响，这里固定不变区域）。</summary>
    private static string Format2(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary><c>"%.3f"</c>。</summary>
    private static string Format3(float value) => value.ToString("0.000", CultureInfo.InvariantCulture);
}
