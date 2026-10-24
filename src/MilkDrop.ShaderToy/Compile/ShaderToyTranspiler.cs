using System.Text;
using System.Text.RegularExpressions;
using MilkDrop.ShaderToy.Import;
using MilkDrop.ShaderToy.Pass;

namespace MilkDrop.ShaderToy.Compile;

/// <summary>转译选项。</summary>
public sealed class ShaderToyTranspileOptions
{
    /// <summary>
    /// 是否先把字面 <c>\n</c> 反转义。导入器已经做过一次，默认关闭 ——
    /// 对已反转义过的代码再来一次会把用户自己写的 <c>\\n</c> 吃掉一个反斜杠。
    /// </summary>
    public bool UnescapeLiteralNewlines { get; init; }

    /// <summary>是否声明 Shadertoy 内置 uniform（<c>iTime</c> 那一组）。</summary>
    public bool DeclareBuiltinUniforms { get; init; } = true;

    /// <summary>是否声明 <c>iChannel0..3</c> 采样器（类型取自 JSON）。</summary>
    public bool DeclareChannelUniforms { get; init; } = true;
}

/// <summary>转译结果。</summary>
/// <param name="Source">可直接交给 <c>glShaderSource</c> 的 GLSL ES 3.00 源码（含 <c>#version</c>）。</param>
/// <param name="Notes">转译过程中做的处理/降级说明，用于日志与 UI 提示。</param>
public sealed record ShaderToyTranspileResult(string Source, IReadOnlyList<string> Notes);

/// <summary>
/// Shadertoy GLSL → GLSL ES 3.00 的转译（计划 P2）。
/// </summary>
/// <remarks>
/// <para>
/// 与上游的区别：上游需要把代码"Vulkan 化"（<c>CombinedImageSampler2D</c> + push constant + glslang→SPIR-V），
/// 这里目标就是 GLES3，通道直接是真 uniform，所以少一层转换。对应上游 <c>viewport.glsl</c>（152 行）。
/// </para>
/// <para>规则集中在这个类里，每条都能单独断言（见 <c>tests/MilkDrop.ShaderToy.Tests/Compile/TranspileTests.cs</c>）。</para>
/// </remarks>
public static class ShaderToyTranspiler
{
    /// <summary>片元输出变量名。用 <c>ds_</c> 前缀避免和用户变量撞名（S2-5）。</summary>
    public const string FragmentOutputName = "ds_fragColor";

    /// <summary>cubemap pass 的面索引 uniform（0..5，顺序同 Shadertoy）。</summary>
    public const string CubeFaceIndexUniform = "ds_faceIndex";

    /// <summary>全屏三角形的顶点着色器（无顶点缓冲，靠 <c>gl_VertexID</c>）。</summary>
    public const string FullscreenVertexSource = """
        #version 300 es
        precision highp float;

        void main()
        {
            // 3 个顶点覆盖整个裁剪空间：(0,0) (2,0) (0,2)
            vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private static readonly Regex VersionDirective = new(@"^\s*#\s*version\b", RegexOptions.Compiled);
    private static readonly Regex ExtensionDirective = new(@"^\s*#\s*extension\b", RegexOptions.Compiled);
    private static readonly Regex PragOnceDirective = new(@"^\s*#\s*pragma\s+once\b", RegexOptions.Compiled);
    private static readonly Regex MainDefinition = new(@"\bvoid\s+main\s*\(", RegexOptions.Compiled);
    private static readonly Regex MainImageUse = new(@"\bmainImage\s*\(", RegexOptions.Compiled);
    private static readonly Regex MainCubemapUse = new(@"\bmainCubemap\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// 把一个 pass（+ 公共代码）转译成 GLES3 片元着色器源码。
    /// </summary>
    /// <param name="pass">要转译的 pass（image / buffer / cubemap / sound）。</param>
    /// <param name="commonCode">所有 <c>common</c> pass 拼起来的代码，可为 null。</param>
    /// <param name="options">选项，可为 null（用默认值）。</param>
    /// <exception cref="ArgumentException">传入了 <c>common</c> pass（它不单独编译）。</exception>
    public static ShaderToyTranspileResult Transpile(
        ShaderPass pass,
        string? commonCode = null,
        ShaderToyTranspileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pass);
        options ??= new ShaderToyTranspileOptions();

        if (pass.Type == ShaderPassType.Common)
        {
            throw new ArgumentException(
                "common pass 不单独编译：它由 ShaderToyProject.BuildCommonCode() 拼进其他 pass。", nameof(pass));
        }

        if (pass.Type == ShaderPassType.Cubemap && !options.DeclareBuiltinUniforms)
        {
            throw new ArgumentException("cubemap 的包装要用 iResolution 算射线方向，不能关掉内置 uniform。", nameof(options));
        }

        var notes = new List<string>();

        string code = pass.Code;

        // 1) 反转义（默认不做：导入器已经做过）
        if (options.UnescapeLiteralNewlines)
        {
            code = ShaderToyJsonImporter.UnescapeSource(code);
        }

        // 2) common 代码必须在用户代码之前（用户函数会调用它）
        if (!string.IsNullOrEmpty(commonCode))
        {
            code = commonCode.TrimEnd('\n', '\r') + "\n" + code;
        }

        // 3) 统一换行 + 剥掉 #version / #extension / #pragma once
        code = StripUnsupportedDirectives(code, notes);

        // 4) 旧语法兼容：gl_FragColor / gl_FragData[0] / varying / attribute
        code = ApplyLegacyTokenRules(code, notes);

        // 后续所有"代码里有没有 X"的判断都在去掉注释的副本上做 ——
        // 上游的 default-shader.json 注释里就有 "Time varying pixel color"，直接替换会改坏注释，
        // "// void main()" 这种注释更会让入口包裹被误跳过。
        string searchable = StripComments(code);

        // 5) 拼最终的源
        var builder = new StringBuilder(code.Length + 2048);

        builder.Append("#version 300 es\n");
        // S2-2：ES3 片段着色器默认 mediump，高精度必须显式写，否则 raymarch/hash 全是色带
        builder.Append("precision highp float;\n");
        builder.Append("precision highp int;\n");
        builder.Append("precision highp sampler2D;\n");
        builder.Append("precision highp samplerCube;\n");
        builder.Append("precision highp sampler3D;\n\n");

        if (options.DeclareBuiltinUniforms)
        {
            builder.Append(BuildBuiltinUniforms());
        }

        if (options.DeclareChannelUniforms)
        {
            builder.Append(BuildChannelUniforms(pass));
        }

        // Shadertoy 网页端定义的宏：不少作品用 #if HW_PERFORMANCE 分桌面/移动分支
        builder.Append("""
            #ifndef HW_PERFORMANCE
            #define HW_PERFORMANCE 1
            #endif

            """);

        builder.Append(BuildCompatMacros(searchable));

        builder.Append("out vec4 ").Append(FragmentOutputName).Append(";\n\n");

        builder.Append(code);
        if (!code.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        builder.Append('\n');
        builder.Append(BuildEntryPoint(pass, searchable, notes));

        return new ShaderToyTranspileResult(builder.ToString(), notes);
    }

    private static string StripUnsupportedDirectives(string code, List<string> notes)
    {
        string[] lines = code.ReplaceLineEndings("\n").Split('\n');
        var kept = new List<string>(lines.Length);

        bool strippedVersion = false;
        int strippedExtensions = 0;

        foreach (string line in lines)
        {
            if (VersionDirective.IsMatch(line))
            {
                strippedVersion = true;
                continue;
            }

            if (ExtensionDirective.IsMatch(line))
            {
                strippedExtensions++;
                continue;
            }

            if (PragOnceDirective.IsMatch(line))
            {
                continue;
            }

            kept.Add(line);
        }

        if (strippedVersion)
        {
            notes.Add("剥掉用户代码里的 #version（统一用 #version 300 es）。");
        }

        if (strippedExtensions > 0)
        {
            notes.Add($"剥掉 {strippedExtensions} 条 #extension 指令（ES3 上非法或不需要）。");
        }

        return string.Join('\n', kept);
    }

    /// <summary>
    /// 旧 WebGL1 语法 → ES3。按词法扫描（跳过注释），不做整串替换 ——
    /// 注释里出现 <c>varying</c> / <c>gl_FragColor</c> 时不能被改。
    /// </summary>
    private static string ApplyLegacyTokenRules(string code, List<string> notes)
    {
        var builder = new StringBuilder(code.Length);
        bool sawFragColor = false;
        bool sawFragData = false;
        bool sawVarying = false;
        bool sawAttribute = false;

        int i = 0;
        while (i < code.Length)
        {
            char c = code[i];

            // 注释原样搬运
            if (c == '/' && i + 1 < code.Length && (code[i + 1] == '/' || code[i + 1] == '*'))
            {
                int commentEnd = FindCommentEnd(code, i);
                builder.Append(code, i, commentEnd - i);
                i = commentEnd;
                continue;
            }

            if (IsIdentifierStart(c))
            {
                int start = i;
                while (i < code.Length && IsIdentifierPart(code[i]))
                {
                    i++;
                }

                string token = code[start..i];
                switch (token)
                {
                    case "gl_FragColor":
                        builder.Append(FragmentOutputName);
                        sawFragColor = true;
                        break;

                    case "gl_FragData":
                        // 只处理 [0] 这一种；其它写得出来就说明本来也不是 Shadertoy 代码
                        if (TrySkipZeroIndex(code, i, out int afterIndex))
                        {
                            builder.Append(FragmentOutputName);
                            sawFragData = true;
                            i = afterIndex;
                        }
                        else
                        {
                            builder.Append(token);
                        }

                        break;

                    case "varying":
                        builder.Append("in");
                        sawVarying = true;
                        break;

                    case "attribute":
                        builder.Append("in");
                        sawAttribute = true;
                        break;

                    default:
                        builder.Append(token);
                        break;
                }

                continue;
            }

            builder.Append(c);
            i++;
        }

        if (sawFragData)
        {
            notes.Add($"gl_FragData[0] → {FragmentOutputName}");
        }

        if (sawFragColor)
        {
            notes.Add($"gl_FragColor → {FragmentOutputName}");
        }

        if (sawVarying)
        {
            notes.Add("varying → in");
        }

        if (sawAttribute)
        {
            notes.Add("attribute → in");
        }

        return builder.ToString();
    }

    /// <summary>把注释内容换成空格（保留换行），用于"代码里有没有 X"的判断。</summary>
    internal static string StripComments(string code)
    {
        var builder = new StringBuilder(code.Length);
        int i = 0;

        while (i < code.Length)
        {
            char c = code[i];
            if (c == '/' && i + 1 < code.Length && (code[i + 1] == '/' || code[i + 1] == '*'))
            {
                int end = FindCommentEnd(code, i);
                for (int j = i; j < end; j++)
                {
                    builder.Append(code[j] == '\n' ? '\n' : ' ');
                }

                i = end;
                continue;
            }

            builder.Append(c);
            i++;
        }

        return builder.ToString();
    }

    private static int FindCommentEnd(string code, int start)
    {
        if (code[start + 1] == '/')
        {
            int newline = code.IndexOf('\n', start);
            return newline < 0 ? code.Length : newline;
        }

        int end = code.IndexOf("*/", start + 2, StringComparison.Ordinal);
        return end < 0 ? code.Length : end + 2;
    }

    /// <summary>跳过 <c>[ 0 ]</c>（允许任意空白）。</summary>
    private static bool TrySkipZeroIndex(string code, int index, out int afterIndex)
    {
        afterIndex = index;
        int i = index;

        while (i < code.Length && char.IsWhiteSpace(code[i]))
        {
            i++;
        }

        if (i >= code.Length || code[i] != '[')
        {
            return false;
        }

        i++;
        while (i < code.Length && char.IsWhiteSpace(code[i]))
        {
            i++;
        }

        if (i >= code.Length || code[i] != '0')
        {
            return false;
        }

        i++;
        while (i < code.Length && char.IsWhiteSpace(code[i]))
        {
            i++;
        }

        if (i >= code.Length || code[i] != ']')
        {
            return false;
        }

        afterIndex = i + 1;
        return true;
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string BuildCompatMacros(string code)
    {
        var builder = new StringBuilder();

        // 只在代码真的用到时才定义，避免污染用户命名空间（S2-5）
        if (Regex.IsMatch(code, @"\btexture2D\s*\("))
        {
            builder.Append("#define texture2D texture\n");
        }

        if (Regex.IsMatch(code, @"\btextureCube\s*\("))
        {
            builder.Append("#define textureCube texture\n");
        }

        if (Regex.IsMatch(code, @"\btexture2DProj\s*\("))
        {
            builder.Append("#define texture2DProj textureProj\n");
        }

        // 旧名先定义，避免 texture2DLodEXT 被 texture2DLod 的规则吃成半截
        if (Regex.IsMatch(code, @"\btexture2DLodEXT\s*\("))
        {
            builder.Append("#define texture2DLodEXT textureLod\n");
        }

        if (Regex.IsMatch(code, @"\btexture2DLod\s*\("))
        {
            builder.Append("#define texture2DLod textureLod\n");
        }

        if (Regex.IsMatch(code, @"\btextureCubeLodEXT\s*\("))
        {
            builder.Append("#define textureCubeLodEXT textureLod\n");
        }

        if (Regex.IsMatch(code, @"\btextureCubeLod\s*\("))
        {
            builder.Append("#define textureCubeLod textureLod\n");
        }

        if (Regex.IsMatch(code, @"\biGlobalTime\b"))
        {
            builder.Append("#define iGlobalTime iTime\n");
        }

        if (Regex.IsMatch(code, @"\biGlobalFrame\b"))
        {
            builder.Append("#define iGlobalFrame iFrame\n");
        }

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string BuildBuiltinUniforms() => """
        // ---- Shadertoy 内置 uniform（每帧由 P3 的 GpuInput 推送）----
        uniform vec3 iResolution;
        uniform float iTime;
        uniform float iTimeDelta;
        uniform float iFrameRate;
        uniform int iFrame;
        uniform vec4 iMouse;
        uniform vec4 iDate;
        uniform float iSampleRate;
        uniform float iChannelTime[4];
        uniform vec3 iChannelResolution[4];

        """;

    /// <summary>
    /// 按 JSON 的 <c>inputs</c> 算出四个通道的采样器类型（没声明的当 2D）。
    /// </summary>
    /// <remarks>
    /// 渲染侧绑通道时必须用同一套规则：给 <c>samplerCube</c> 绑 2D 纹理是 <c>GL_INVALID_OPERATION</c>。
    /// </remarks>
    public static ShaderInputType[] ResolveChannelTypes(ShaderPass pass)
    {
        ArgumentNullException.ThrowIfNull(pass);

        ShaderInputType[] types =
        [
            ShaderInputType.Texture,
            ShaderInputType.Texture,
            ShaderInputType.Texture,
            ShaderInputType.Texture,
        ];

        foreach (ShaderPassInput input in pass.Inputs)
        {
            if (input.Channel is >= 0 and <= 3)
            {
                types[input.Channel] = input.Type;
            }
        }

        return types;
    }

    /// <summary>把 <see cref="ShaderInputType"/> 换成 GLSL 采样器类型名。</summary>
    public static string SamplerTypeName(ShaderInputType type) => type switch
    {
        ShaderInputType.Cubemap => "samplerCube",
        ShaderInputType.Volume => "sampler3D",
        _ => "sampler2D",
    };

    private static string BuildChannelUniforms(ShaderPass pass)
    {
        // S2-4：代码可能用了 JSON 里没声明的通道，所以四个都声明，默认 sampler2D
        ShaderInputType[] types = ResolveChannelTypes(pass);

        var builder = new StringBuilder(256);
        builder.Append("// ---- 输入通道（类型来自 JSON；未绑定的通道给 1x1 黑纹理）----\n");

        for (int channel = 0; channel < types.Length; channel++)
        {
            builder.Append("uniform ").Append(SamplerTypeName(types[channel]))
                .Append(" iChannel").Append(channel).Append(";\n");
        }

        builder.Append('\n');
        return builder.ToString();
    }

    /// <param name="searchable">去掉注释后的代码（只用来做"有没有 main/mainImage"的判断）。</param>
    private static string BuildEntryPoint(ShaderPass pass, string searchable, List<string> notes)
    {
        // S2-6：少数作品自己写了 main()，那就不能包裹（否则重定义）
        if (MainDefinition.IsMatch(searchable))
        {
            notes.Add("用户代码已定义 main()，跳过入口包裹。");
            return string.Empty;
        }

        return pass.Type switch
        {
            ShaderPassType.Cubemap => BuildCubemapEntryPoints(searchable, notes),
            ShaderPassType.Sound => BuildSoundEntryPoints(searchable, notes),
            _ => BuildImageEntryPoints(searchable, notes),
        };
    }

    /// <summary>
    /// sound pass 的入口：一个片元 = 一个采样点，见 <see cref="MainSoundEntry"/>。
    /// </summary>
    /// <remarks>
    /// 签名有两种（<c>(float time)</c> / <c>(int samp, float time)</c>），必须扫实际参数表再生成调用，
    /// 不能按固定字符串比对（S6-1）。
    /// </remarks>
    private static string BuildSoundEntryPoints(string searchable, List<string> notes)
    {
        if (!MainSoundEntry.TryDetect(searchable, out MainSoundSignature? signature, out string? problem))
        {
            notes.Add($"sound pass 没有可用的 mainSound（{problem}），输出静音。");
            return MainSoundEntry.BuildSilentEntryPoint();
        }

        notes.Add($"mainSound 签名 {signature!.Describe()}，按样本序号批量渲染。");
        return MainSoundEntry.BuildEntryPoint(signature);
    }

    private static string BuildImageEntryPoints(string searchable, List<string> notes)
    {
        if (!MainImageUse.IsMatch(searchable))
        {
            notes.Add("没找到 mainImage，生成的 main() 只清屏为黑色。");
            return $$"""
                void main()
                {
                    {{FragmentOutputName}} = vec4(0.0);
                }
                """;
        }

        return $$"""
            void main()
            {
                vec4 ds_color = vec4(0.0);
                mainImage(ds_color, gl_FragCoord.xy);
                {{FragmentOutputName}} = ds_color;
            }
            """;
    }

    private static string BuildCubemapEntryPoints(string searchable, List<string> notes)
    {
        if (!MainCubemapUse.IsMatch(searchable))
        {
            notes.Add("没找到 mainCubemap，生成的 main() 只清屏为黑色。");
            return $$"""
                void main()
                {
                    {{FragmentOutputName}} = vec4(0.0);
                }
                """;
        }

        // 面序与射线方向照上游 viewport.glsl:144-151；面尺寸由渲染器把 iResolution 设成 1024×1024
        return $$"""
            uniform int {{CubeFaceIndexUniform}};

            void main()
            {
                vec2 ds_fragCoord = gl_FragCoord.xy;
                vec2 ds_uv = ds_fragCoord / iResolution.xy * 2.0 - 1.0;

                vec3 ds_rayDir = vec3(0.0, 0.0, 1.0);
                switch ({{CubeFaceIndexUniform}})
                {
                    case 0: ds_rayDir = vec3(+1.0, -ds_uv.y, -ds_uv.x); break; // +X
                    case 1: ds_rayDir = vec3(-1.0, -ds_uv.y, +ds_uv.x); break; // -X
                    case 2: ds_rayDir = vec3(+ds_uv.x, +1.0, +ds_uv.y); break; // +Y
                    case 3: ds_rayDir = vec3(+ds_uv.x, -1.0, -ds_uv.y); break; // -Y
                    case 4: ds_rayDir = vec3(+ds_uv.x, -ds_uv.y, +1.0); break; // +Z
                    case 5: ds_rayDir = vec3(-ds_uv.x, -ds_uv.y, -1.0); break; // -Z
                }

                vec4 ds_color = vec4(0.0);
                mainCubemap(ds_color, ds_fragCoord, vec3(0.0), normalize(ds_rayDir));
                {{FragmentOutputName}} = ds_color;
            }
            """;
    }
}
