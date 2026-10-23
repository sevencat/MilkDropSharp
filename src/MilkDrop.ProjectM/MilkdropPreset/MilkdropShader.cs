using System.Text;
using System.Text.RegularExpressions;
using HlslParser;
using MilkDrop.ProjectM.Renderer;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 一个 warp / composite 预设着色器：预设 HLSL 文本手术 + hlslparser 转译（+ 可选编译）。
/// </summary>
/// <remarks>
/// <para>对应 <c>MilkdropPreset/MilkdropShader.hpp:28-112</c> 与 <c>MilkdropShader.cpp</c>。</para>
/// <para>
/// 文本流程与 C++ 完全一致：<c>GetReferencedSamplers</c>（在**加头前**的代码里找
/// <c>sampler_*</c>/<c>texsize_*</c>）→ <c>PreprocessPresetShader</c>（去掉 <c>sampler_state</c>、
/// 把 <c>shader_body</c> 换成 <c>PS</c> 签名、拼公共头）→ <c>TranspileHLSLShader</c>
/// （预处理器 → 剔除自带的 sampler/texsize 声明 → 注入真实声明 → 解析 → 生成 GLSL）。
/// </para>
/// <para>
/// 与 C++ 的差异：<c>PresetState</c>/<c>TextureManager</c>/<c>BlurTexture</c> 尚未移植（P4/P6/P7），
/// 所以 <c>LoadTexturesAndCompile</c> 不再自己去纹理管理器取描述符，而是由调用方传入三组
/// <see cref="TextureSamplerDescriptor"/>（主纹理 / 模糊纹理 / 其他纹理）；<c>LoadVariables</c>
/// 等 <c>PresetState</c> 到位后补。着色器程序对象也由调用方持有（C++ 的 <c>m_shader</c> 在构造时
/// 就会创建 GL program，本移植不这么早碰 GL，便于无上下文环境下单测文本流程）。
/// </para>
/// </remarks>
public sealed class MilkdropShader
{
    private const string WarpPsSignature =
        "\nvoid PS(float4 _vDiffuse : COLOR,\n" +
        "        float4 _uv : TEXCOORD0,\n" +
        "        float2 _rad_ang : TEXCOORD1,\n" +
        "        out float4 _return_value : COLOR0,\n" +
        "        out float4 _mv_tex_coords : COLOR1)";

    private const string CompPsSignature =
        "\nvoid PS(float4 _vDiffuse : COLOR,\n" +
        "        float2 _uv : TEXCOORD0,\n" +
        "        float2 _rad_ang : TEXCOORD1,\n" +
        "        out float4 _return_value : COLOR)";

    private const string WarpDefines =
        "#define rad _rad_ang.x\n" +
        "#define ang _rad_ang.y\n" +
        "#define uv _uv.xy\n" +
        "#define uv_orig _uv.zw\n";

    private const string CompDefines =
        "#define rad _rad_ang.x\n" +
        "#define ang _rad_ang.y\n" +
        "#define uv _uv.xy\n" +
        "#define uv_orig _uv.xy\n" +
        "#define hue_shader _vDiffuse.xyz\n";

    private const string ShaderBodyKeyword = "shader_body";

    /// <summary>C++ <c>std::regex("sampler(2D|3D|)(\\s+|\\().*")</c>。</summary>
    private static readonly Regex SamplerDeclarationRegex = new("sampler(2D|3D|)(\\s+|\\()[^\\n]*");

    /// <summary>C++ <c>std::regex("float4\\s+texsize_.*")</c>。</summary>
    private static readonly Regex TexSizeDeclarationRegex = new("float4\\s+texsize_[^\\n]*");

    private static readonly char[] SamplerNameStopChars = [' ', ';', ',', '\n', '\r', ')'];
    private static readonly char[] TexSizeNameStopChars = [' ', ';', ',', '.', '\n', '\r', ')'];

    private readonly SortedSet<string> _samplerNames = new(StringComparer.Ordinal);

    public MilkdropShader(ShaderType type)
    {
        Type = type;
    }

    /// <summary>着色器类型。</summary>
    public ShaderType Type { get; }

    /// <summary>原始的预设片段着色器代码（未做任何文本改造）。</summary>
    public string FragmentShaderCode { get; private set; } = string.Empty;

    /// <summary>预处理后的完整 HLSL（公共头 + 宏 + <c>PS</c> 函数），转译器的输入。</summary>
    public string PreprocessedCode { get; private set; } = string.Empty;

    /// <summary>代码里引用到的采样器名（不含 <c>sampler_</c> 前缀，含 <c>main</c>）。</summary>
    public IReadOnlyCollection<string> SamplerNames => _samplerNames;

    /// <summary>本着色器要求的主纹理最大模糊级别。</summary>
    public BlurLevel MaxBlurLevelRequired { get; private set; } = BlurLevel.None;

    /// <summary>载入预设代码并做预处理（对应 <c>MilkdropShader::LoadCode</c>）。</summary>
    /// <param name="presetShaderCode"><c>warp_*</c>/<c>comp_*</c> 拼出来的 HLSL。</param>
    public void LoadCode(string presetShaderCode)
    {
        ArgumentNullException.ThrowIfNull(presetShaderCode);

        FragmentShaderCode = presetShaderCode;
        PreprocessedCode = presetShaderCode;

        GetReferencedSamplers(PreprocessedCode);
        PreprocessedCode = PreprocessPresetShader(PreprocessedCode);
    }

    /// <summary>
    /// 收齐采样器声明后转译成 GLSL（对应 <c>MilkdropShader::TranspileHLSLShader</c> 的文本部分）。
    /// </summary>
    /// <param name="mainTextureDescriptors">主纹理（<c>sampler_main</c> 等）的描述符。</param>
    /// <param name="blurTextureDescriptors">模糊纹理（<c>sampler_blur1..3</c>）的描述符；这些**没有** texsize。</param>
    /// <param name="textureSamplerDescriptors">其余纹理（<c>sampler_noise_lq</c> 等）的描述符。</param>
    /// <returns>完整的 GLSL 片段着色器源（含 <c>#version</c> 行）。</returns>
    /// <exception cref="ShaderException">预处理、解析或生成失败。</exception>
    public string TranspileHLSLShader(
        IEnumerable<TextureSamplerDescriptor> mainTextureDescriptors,
        IEnumerable<TextureSamplerDescriptor> blurTextureDescriptors,
        IEnumerable<TextureSamplerDescriptor> textureSamplerDescriptors)
    {
        ArgumentNullException.ThrowIfNull(mainTextureDescriptors);
        ArgumentNullException.ThrowIfNull(blurTextureDescriptors);
        ArgumentNullException.ThrowIfNull(textureSamplerDescriptors);

        string shaderTypeString = Type == ShaderType.WarpShader ? "warp" : "composite";

        var allocator = new Allocator();
        var tree = new HLSLTree(allocator);
        var diagnostics = new CollectingDiagnosticSink();
        var parser = new HLSLParser(allocator, tree, diagnostics);

        if (!parser.ApplyPreprocessor(string.Empty, PreprocessedCode, out string sourcePreprocessed))
        {
            throw new ShaderException("Error translating HLSL " + shaderTypeString +
                                      " shader: Preprocessing failed.\nSource:\n" + PreprocessedCode);
        }

        // 剔除预设自带的 sampler / texsize 声明，下面按引用到的纹理重新注入。
        sourcePreprocessed = SamplerDeclarationRegex.Replace(sourcePreprocessed, string.Empty);
        sourcePreprocessed = TexSizeDeclarationRegex.Replace(sourcePreprocessed, string.Empty);

        // 收集唯一的声明（std::set 语义：按字节序去重）。
        var samplerDeclarations = new SortedSet<string>(StringComparer.Ordinal);
        var texSizeDeclarations = new SortedSet<string>(StringComparer.Ordinal);

        foreach (TextureSamplerDescriptor descriptor in mainTextureDescriptors)
        {
            AddDeclaration(samplerDeclarations, descriptor.SamplerDeclaration());
            AddDeclaration(texSizeDeclarations, descriptor.TexSizeDeclaration());
        }

        foreach (TextureSamplerDescriptor descriptor in blurTextureDescriptors)
        {
            // 模糊纹理没有 texsize_blur1 之类。
            AddDeclaration(samplerDeclarations, descriptor.SamplerDeclaration());
        }

        foreach (TextureSamplerDescriptor descriptor in textureSamplerDescriptors)
        {
            AddDeclaration(samplerDeclarations, descriptor.SamplerDeclaration());
            AddDeclaration(texSizeDeclarations, descriptor.TexSizeDeclaration());
        }

        // C++ 逐个 insert(0, ...)，所以最终顺序是排序的逆序。
        var source = new StringBuilder(sourcePreprocessed);
        foreach (string declaration in texSizeDeclarations)
        {
            source.Insert(0, declaration);
        }

        foreach (string declaration in samplerDeclarations)
        {
            source.Insert(0, declaration);
        }

        string prepared = source.ToString();

        if (!parser.Parse(string.Empty, prepared))
        {
            throw new ShaderException("Error translating HLSL " + shaderTypeString +
                                      " shader: HLSL parsing failed.\nSource:\n" + prepared + DiagnosticsSuffix(diagnostics));
        }

        var generator = new GLSLGenerator(diagnostics);
        var options = new GLSLGenerator.Options((int)GLSLGenerator.Flags.Flag_AlternateNanPropagation);

        if (!generator.Generate(tree, GLSLGenerator.Target.Target_FragmentShader,
                                MilkdropStaticShaders.Get().GlslGeneratorVersion, "PS", options))
        {
            throw new ShaderException("Error translating HLSL " + shaderTypeString +
                                      " shader: GLSL generating failed.\nSource:\n" + prepared + DiagnosticsSuffix(diagnostics));
        }

        return generator.GetResult();
    }

    /// <summary>
    /// 转译并编译成 GL program（对应 <c>MilkdropShader::LoadTexturesAndCompile</c> 的转译/编译部分）。
    /// </summary>
    /// <param name="shader">要写入的 program（C++ 里由 <c>MilkdropShader</c> 自己持有）。</param>
    /// <param name="mainTextureDescriptors">主纹理描述符。</param>
    /// <param name="blurTextureDescriptors">模糊纹理描述符。</param>
    /// <param name="textureSamplerDescriptors">其余纹理描述符。</param>
    /// <exception cref="ShaderException">转译或编译失败。</exception>
    public void LoadTexturesAndCompile(
        Shader shader,
        IEnumerable<TextureSamplerDescriptor> mainTextureDescriptors,
        IEnumerable<TextureSamplerDescriptor> blurTextureDescriptors,
        IEnumerable<TextureSamplerDescriptor> textureSamplerDescriptors)
    {
        ArgumentNullException.ThrowIfNull(shader);

        string glsl = TranspileHLSLShader(mainTextureDescriptors, blurTextureDescriptors, textureSamplerDescriptors);

        string vertexShader = Type == ShaderType.WarpShader
            ? MilkdropStaticShaders.Get().GetPresetWarpVertexShader()
            : MilkdropStaticShaders.Get().GetPresetCompVertexShader();

        shader.CompileProgram(vertexShader, glsl);
    }

    /// <summary>
    /// 在代码里找 <c>sampler_*</c>/<c>texsize_*</c> 引用（对应 <c>MilkdropShader::GetReferencedSamplers</c>）。
    /// </summary>
    /// <remarks>
    /// 与 C++ 一样在**未加公共头**的代码上调用，否则公共头里的
    /// <c>#define sampler_FC_main sampler_fc_main</c> 等会被误当成引用。注释里的引用不算。
    /// </remarks>
    internal void GetReferencedSamplers(string program)
    {
        ArgumentNullException.ThrowIfNull(program);

        _samplerNames.Clear();

        // "main" 始终存在。
        _samplerNames.Add("main");

        string stripped = Utils.StripComments(program);

        CollectReferencedNames(stripped, "sampler_", SamplerNameStopChars, _samplerNames, skipSamplerState: true);
        CollectReferencedNames(stripped, "texsize_", TexSizeNameStopChars, _samplerNames, skipSamplerState: false);

        RemoveShortRandomNames();

        if (stripped.Contains("GetBlur3", StringComparison.Ordinal))
        {
            UpdateMaxBlurLevel(BlurLevel.Blur3);
        }
        else if (stripped.Contains("GetBlur2", StringComparison.Ordinal))
        {
            UpdateMaxBlurLevel(BlurLevel.Blur2);
        }
        else if (stripped.Contains("GetBlur1", StringComparison.Ordinal))
        {
            UpdateMaxBlurLevel(BlurLevel.Blur1);
        }
        else
        {
            MaxBlurLevelRequired = BlurLevel.None;
        }

        // C++ 里"直接写 sampler_blur1/2/3"的判定在 LoadTexturesAndCompile 遍历采样器名时做；
        // 这里提前做，结果等价（UpdateMaxBlurLevel 会把 blur1..N 补进集合）。
        foreach (string samplerName in _samplerNames.ToArray())
        {
            switch (Utils.ToLower(samplerName))
            {
                case "blur1":
                    UpdateMaxBlurLevel(BlurLevel.Blur1);
                    break;
                case "blur2":
                    UpdateMaxBlurLevel(BlurLevel.Blur2);
                    break;
                case "blur3":
                    UpdateMaxBlurLevel(BlurLevel.Blur3);
                    break;
            }
        }
    }

    /// <summary>
    /// 预设文本手术（对应 <c>MilkdropShader::PreprocessPresetShader</c>）。
    /// </summary>
    /// <param name="program">预设 HLSL 代码（不含公共头）。</param>
    /// <returns>公共头 + 宏 + 改写后的 <c>PS</c> 函数。</returns>
    /// <exception cref="ShaderException">代码为空、缺 <c>shader_body</c> 或括号不配对。</exception>
    internal string PreprocessPresetShader(string program)
    {
        ArgumentNullException.ThrowIfNull(program);

        if (program.Length == 0)
        {
            throw new ShaderException("Preset shader is declared, but empty.");
        }

        // sampler_state 覆盖块 GLSL 不支持，先整块删掉。
        program = RemoveSamplerStateBlocks(program);

        // 用去注释副本定位，避免匹配到注释里的 shader_body。
        string stripped = Utils.StripComments(program);
        int found = stripped.IndexOf(ShaderBodyKeyword, StringComparison.Ordinal);
        if (found < 0)
        {
            throw new ShaderException("Preset shader is missing \"shader_body\" entry point.");
        }

        string signature = Type == ShaderType.WarpShader ? WarpPsSignature : CompPsSignature;
        program = string.Concat(program.AsSpan(0, found), signature,
                                program.AsSpan(found + ShaderBodyKeyword.Length));

        // 把紧跟 shader_body 的 "{" 换成带变量声明的块头。
        found = program.IndexOf('{', found);
        if (found < 0)
        {
            throw new ShaderException("Preset shader has no opening braces.");
        }

        string progMain = "{\nfloat3 ret = 0;\n";
        if (Type == ShaderType.WarpShader)
        {
            progMain += "_mv_tex_coords.xy = _uv.xy;\n";
        }

        program = string.Concat(program.AsSpan(0, found), progMain, program.AsSpan(found + 1));

        // 最后一个 "}" 之前补返回值。
        found = program.LastIndexOf('}');
        if (found < 0)
        {
            throw new ShaderException("Preset shader has no closing brace.");
        }

        program = string.Concat(program.AsSpan(0, found),
                                "_return_value = float4(ret.xyz, 1.0);\n}\n",
                                program.AsSpan(found + 1));

        // 数括号找出函数真正的结尾，砍掉后面的多余文本（跳过注释里的括号）。
        int bracesOpen = 1;
        int pos = found + 1;
        for (; pos < program.Length && bracesOpen > 0; ++pos)
        {
            switch (program[pos])
            {
                case '/':
                    if (pos < program.Length - 1 && program[pos + 1] == '/')
                    {
                        for (; pos < program.Length; ++pos)
                        {
                            if (program[pos] == '\n')
                            {
                                break;
                            }
                        }
                    }
                    else if (pos < program.Length - 1 && program[pos + 1] == '*')
                    {
                        pos += 2;
                        for (; pos < program.Length - 1; ++pos)
                        {
                            if (program[pos] == '*' && program[pos + 1] == '/')
                            {
                                ++pos;
                                break;
                            }
                        }
                    }

                    continue;

                case '{':
                    ++bracesOpen;
                    continue;

                case '}':
                    --bracesOpen;
                    break;
            }
        }

        if (pos < program.Length - 1)
        {
            program = program[..pos];
        }

        var fullSource = new StringBuilder();
        fullSource.Append(MilkdropStaticShaders.Get().GetPresetShaderHeader());
        fullSource.Append(Type == ShaderType.WarpShader ? WarpDefines : CompDefines);
        fullSource.Append(program);
        return fullSource.ToString();
    }

    /// <summary>只升不降地更新模糊级别，并把需要的模糊采样器补进集合。</summary>
    internal void UpdateMaxBlurLevel(BlurLevel requestedLevel)
    {
        if (MaxBlurLevelRequired >= requestedLevel)
        {
            return;
        }

        MaxBlurLevelRequired = requestedLevel;

        switch (requestedLevel)
        {
            case BlurLevel.Blur3:
                _samplerNames.Add("blur1");
                _samplerNames.Add("blur2");
                _samplerNames.Add("blur3");
                break;

            case BlurLevel.Blur2:
                _samplerNames.Add("blur1");
                _samplerNames.Add("blur2");
                break;

            default:
                _samplerNames.Add("blur1");
                break;
        }
    }

    /// <summary>
    /// 采样器名 → 纹理基名：C++ 在 <c>LoadTexturesAndCompile</c> 里对 <c>xx_main</c>
    /// 这类名字剥掉前 3 个字符（<c>name[2] == '_'</c> 时）。供 P7 解析纹理时复用。
    /// </summary>
    internal static string GetSamplerBaseName(string samplerName)
    {
        ArgumentNullException.ThrowIfNull(samplerName);
        return samplerName.Length > 3 && samplerName[2] == '_' ? samplerName[3..] : samplerName;
    }

    private static void AddDeclaration(ISet<string> declarations, string declaration)
    {
        if (declaration.Length > 0)
        {
            declarations.Add(declaration);
        }
    }

    private static string DiagnosticsSuffix(CollectingDiagnosticSink diagnostics)
    {
        if (diagnostics.Diagnostics.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder("\nDiagnostics:\n");
        foreach (Diagnostic diagnostic in diagnostics.Diagnostics)
        {
            text.Append(diagnostic.FileName).Append(':').Append(diagnostic.Line).Append(": ")
                .Append(diagnostic.Message).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// 删掉 <c>sampler xxx = sampler_state { ... };</c> 块（从 <c>=</c> 到 <c>;</c> 之前）。
    /// </summary>
    /// <remarks>
    /// 用去注释副本定位，位置与原文 1:1（<see cref="Utils.StripComments"/> 保持长度）。
    /// 注意 4.1.8 的 C++ 这里改的是 <c>stripped</c> 自己（<c>stripped.replace</c>），
    /// 于是后续 <c>shader_body</c> 的位置相对原文错位、带 <c>sampler_state</c> 的预设会被改坏；
    /// 本移植按同文件测试（<c>tests/libprojectM/MilkdropShaderCommentParsingTest.cpp</c>）的意图
    /// 直接改原文并重新去注释，其余逻辑不变。
    /// </remarks>
    private static string RemoveSamplerStateBlocks(string program)
    {
        const string samplerStateKeyword = "sampler_state";

        string stripped = Utils.StripComments(program);
        int found = stripped.IndexOf(samplerStateKeyword, StringComparison.Ordinal);

        while (found >= 0)
        {
            // 往回找赋值号，再往后找 "};"。
            found = stripped.LastIndexOf('=', found);
            if (found < 0)
            {
                break;
            }

            int startPos = found;
            found = stripped.IndexOf('}', found);
            if (found < 0)
            {
                break;
            }

            found = stripped.IndexOf(';', found);
            if (found < 0)
            {
                // 没有收尾的 "};"。
                break;
            }

            program = program.Remove(startPos, found - startPos);
            stripped = Utils.StripComments(program);
            found = stripped.IndexOf(samplerStateKeyword, StringComparison.Ordinal);
        }

        return program;
    }

    private static void CollectReferencedNames(
        string stripped,
        string prefix,
        char[] stopChars,
        ISet<string> names,
        bool skipSamplerState)
    {
        int found = stripped.IndexOf(prefix, StringComparison.Ordinal);

        while (found >= 0)
        {
            found += prefix.Length;
            int end = stripped.IndexOfAny(stopChars, found);

            if (end >= 0)
            {
                string name = stripped[found..end];

                // "sampler_state" 是保留字，不是采样器。
                if (!skipSamplerState || name != "state")
                {
                    names.Add(name);
                }
            }

            found = stripped.IndexOf(prefix, found, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 去掉 <c>randNN</c> 短名，只留 <c>randNN_xxx</c> 长名（与实际加载的纹理一致）。
    /// </summary>
    /// <remarks>与 C++ 的 <c>erase(it)</c> + 循环 <c>++it</c> 一致：删掉短名后下一项会被跳过。</remarks>
    private void RemoveShortRandomNames()
    {
        var names = new List<string>(_samplerNames);

        for (int i = 0; i < names.Count; i++)
        {
            string lowerName = Utils.ToLower(names[i]);

            if (lowerName.Length != 6 ||
                !lowerName.StartsWith("rand", StringComparison.Ordinal) ||
                !char.IsAsciiDigit(lowerName[4]) ||
                !char.IsAsciiDigit(lowerName[5]))
            {
                continue;
            }

            int next = i + 1;
            if (next >= names.Count)
            {
                continue;
            }

            string nextLowerName = Utils.ToLower(names[next]);
            if (nextLowerName.Length > 7 &&
                nextLowerName.StartsWith(lowerName, StringComparison.Ordinal) &&
                nextLowerName[6] == '_')
            {
                names.RemoveAt(i);
            }
        }

        _samplerNames.Clear();
        foreach (string name in names)
        {
            _samplerNames.Add(name);
        }
    }
}
