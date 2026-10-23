using System.Numerics;
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
/// 与 C++ 的差异：C++ 的 <c>m_shader</c> 在构造时就创建 GL program，本移植改成第一次用到
/// （<see cref="ShaderProgram"/>）才创建，这样没有 GL 上下文时也能单测文本流程；
/// 另外保留了一组"描述符由调用方传入"的 <c>LoadTexturesAndCompile</c> 重载，方便工具/测试
/// 在没有完整 <see cref="PresetState"/> 时使用。
/// </para>
/// </remarks>
public sealed class MilkdropShader : IDisposable
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

    private readonly List<TextureSamplerDescriptor> _mainTextureDescriptors = [];
    private readonly List<TextureSamplerDescriptor> _textureSamplerDescriptors = [];

    private readonly float[] _randValues = new float[4];
    private readonly Vector3[] _randTranslation = new Vector3[20];
    private readonly Vector3[] _randRotationCenters = new Vector3[20];
    private readonly Vector3[] _randRotationSpeeds = new Vector3[20];

    /// <summary>GL program，第一次用到时才创建（无 GL 上下文时也能单测文本流程）。</summary>
    private Shader? _shader;

    public MilkdropShader(ShaderType type)
    {
        Type = type;

        // 对应 C++ 构造函数里的 m_randValues / m_randTranslation / m_randRotationCenters / m_randRotationSpeeds。
        for (int i = 0; i < _randValues.Length; i++)
        {
            _randValues[i] = FloatRand();
        }

        int index = 0;
        do
        {
            for (int i = 0; i < 4; i++)
            {
                const float translationMult = 1f;
                float rotMult = 0.9f * MathF.Pow(index / 8.0f, 3.2f);

                _randTranslation[index] = new Vector3(
                    (FloatRand() * 2 - 1) * translationMult,
                    (FloatRand() * 2 - 1) * translationMult,
                    (FloatRand() * 2 - 1) * translationMult);

                _randRotationCenters[index] = new Vector3(FloatRand() * 6.28f, FloatRand() * 6.28f, FloatRand() * 6.28f);
                _randRotationSpeeds[index] = new Vector3(
                    (FloatRand() * 2 - 1) * rotMult,
                    (FloatRand() * 2 - 1) * rotMult,
                    (FloatRand() * 2 - 1) * rotMult);

                index++;
            }
        }
        while (index < _randTranslation.Length);
    }

    /// <summary>本着色器的 GL program（对应 C++ 的 <c>Shader()</c>）。</summary>
    public Shader ShaderProgram => _shader ??= new Shader();

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
    /// 从 <see cref="PresetState"/> 解析纹理、转译并编译（对应 <c>MilkdropShader::LoadTexturesAndCompile</c>）。
    /// </summary>
    /// <remarks>
    /// 需要 <see cref="PresetState"/> 已经设好 <c>RenderContext.TextureManager</c> 与主纹理；
    /// 模糊级别会在编译成功后写回 <see cref="PresetState.BlurTexture"/>。
    /// </remarks>
    /// <exception cref="InvalidOperationException">没有纹理管理器。</exception>
    /// <exception cref="ShaderException">转译或编译失败。</exception>
    public void LoadTexturesAndCompile(PresetState presetState)
    {
        ArgumentNullException.ThrowIfNull(presetState);

        TextureManager textureManager = presetState.RenderContext.TextureManager
            ?? throw new InvalidOperationException(
                "PresetState.RenderContext.TextureManager 未设置，无法解析预设引用的纹理。");

        _mainTextureDescriptors.Clear();
        _textureSamplerDescriptors.Clear();

        foreach (string name in _samplerNames)
        {
            string lowerCaseName = Utils.ToLower(GetSamplerBaseName(name));

            // main 与 blur1..3 都是预设私有的，不归 TextureManager 管。
            if (lowerCaseName == "main")
            {
                _mainTextureDescriptors.Add(
                    new TextureSamplerDescriptor(presetState.MainTexture, textureManager.GetSampler(name), name, "main"));
                continue;
            }

            // 少数预设直接写（未文档化的）采样器名。
            if (lowerCaseName is "blur1" or "blur2" or "blur3")
            {
                UpdateMaxBlurLevel(lowerCaseName switch
                {
                    "blur1" => BlurLevel.Blur1,
                    "blur2" => BlurLevel.Blur2,
                    _ => BlurLevel.Blur3,
                });
                continue;
            }

            // 随机纹理：warp/composite 两个着色器要拿到同一张，所以按 slot 存在 PresetState 里。
            if (IsRandomTextureName(lowerCaseName))
            {
                int randomSlot = int.Parse(lowerCaseName.AsSpan(4, 2));
                if (randomSlot is >= 0 and <= 15)
                {
                    if (presetState.RandomTextureDescriptors.TryGetValue(randomSlot, out TextureSamplerDescriptor? existing))
                    {
                        _textureSamplerDescriptors.Add(existing);
                        continue;
                    }

                    TextureSamplerDescriptor descriptor = textureManager.GetRandomTexture(name);
                    presetState.RandomTextureDescriptors[randomSlot] = descriptor;
                    _textureSamplerDescriptors.Add(descriptor);
                    continue;
                }
            }

            _textureSamplerDescriptors.Add(textureManager.GetTexture(name));
        }

        // 现在有纹理了，转译并编译。
        List<TextureSamplerDescriptor> blurDescriptors =
            presetState.BlurTexture.GetDescriptorsForBlurLevel(MaxBlurLevelRequired);
        string glsl = TranspileHLSLShader(_mainTextureDescriptors, blurDescriptors, _textureSamplerDescriptors);

        string vertexShader = Type == ShaderType.WarpShader
            ? MilkdropStaticShaders.Get().GetPresetWarpVertexShader()
            : MilkdropStaticShaders.Get().GetPresetCompVertexShader();

        ShaderProgram.CompileProgram(vertexShader, glsl);

        // 编译成功后才更新模糊级别。
        presetState.BlurTexture.SetRequiredBlurLevel(MaxBlurLevelRequired);
    }

    /// <summary>
    /// 把所有 uniform（时间/音频/颜色常量/随机矩阵/Q 变量）与纹理写进已编译的 program
    /// （对应 <c>MilkdropShader::LoadVariables</c>）。
    /// </summary>
    public void LoadVariables(PresetState presetState, PerFrameContext perFrameContext)
    {
        ArgumentNullException.ThrowIfNull(presetState);
        ArgumentNullException.ThrowIfNull(perFrameContext);

        Shader shader = ShaderProgram;
        RenderContext renderContext = presetState.RenderContext;

        float floatTime = renderContext.Time;
        float timeSincePresetStartWrapped = floatTime - (int)(floatTime / 10000.0) * 10000;
        float mipX = MathF.Log(renderContext.ViewportSizeX) / MathF.Log(2.0f);
        float mipY = MathF.Log(renderContext.ViewportSizeY) / MathF.Log(2.0f);
        float mipAvg = 0.5f * (mipX + mipY);

        Span<float> blurMin = stackalloc float[3];
        Span<float> blurMax = stackalloc float[3];
        BlurTexture.GetSafeBlurMinMaxValues(perFrameContext, blurMin, blurMax);

        shader.Bind();

        shader.SetUniformMat4x4("vertex_transformation", PresetState.OrthogonalProjection);

        shader.SetUniformFloat4("rand_frame", new Vector4(FloatRand(), FloatRand(), FloatRand(), FloatRand()));
        shader.SetUniformFloat4("rand_preset", new Vector4(_randValues[0], _randValues[1], _randValues[2], _randValues[3]));

        shader.SetUniformFloat4("_c0", new Vector4(renderContext.AspectX, renderContext.AspectY,
                                                  1.0f / renderContext.AspectX, 1.0f / renderContext.AspectY));
        shader.SetUniformFloat4("_c1", Vector4.Zero);
        shader.SetUniformFloat4("_c2", new Vector4(timeSincePresetStartWrapped, renderContext.Fps,
                                                   renderContext.Frame, renderContext.Progress));
        shader.SetUniformFloat4("_c3", new Vector4(presetState.AudioData.Bass, presetState.AudioData.Mid,
                                                   presetState.AudioData.Treb, presetState.AudioData.Vol));
        shader.SetUniformFloat4("_c4", new Vector4(presetState.AudioData.BassAtt, presetState.AudioData.MidAtt,
                                                   presetState.AudioData.TrebAtt, presetState.AudioData.VolAtt));
        shader.SetUniformFloat4("_c5", new Vector4(blurMax[0] - blurMin[0], blurMin[0],
                                                   blurMax[1] - blurMin[1], blurMin[1]));
        shader.SetUniformFloat4("_c6", new Vector4(blurMax[2] - blurMin[2], blurMin[2], blurMin[0], blurMax[0]));
        shader.SetUniformFloat4("_c7", new Vector4(renderContext.ViewportSizeX, renderContext.ViewportSizeY,
                                                  1.0f / renderContext.ViewportSizeX, 1.0f / renderContext.ViewportSizeY));

        shader.SetUniformFloat4("_c8", new Vector4(
            0.5f + 0.5f * MathF.Cos(floatTime * 0.329f + 1.2f),
            0.5f + 0.5f * MathF.Cos(floatTime * 1.293f + 3.9f),
            0.5f + 0.5f * MathF.Cos(floatTime * 5.070f + 2.5f),
            0.5f + 0.5f * MathF.Cos(floatTime * 20.051f + 5.4f)));

        shader.SetUniformFloat4("_c9", new Vector4(
            0.5f + 0.5f * MathF.Sin(floatTime * 0.329f + 1.2f),
            0.5f + 0.5f * MathF.Sin(floatTime * 1.293f + 3.9f),
            0.5f + 0.5f * MathF.Sin(floatTime * 5.070f + 2.5f),
            0.5f + 0.5f * MathF.Sin(floatTime * 20.051f + 5.4f)));

        shader.SetUniformFloat4("_c10", new Vector4(
            0.5f + 0.5f * MathF.Cos(floatTime * 0.0050f + 2.7f),
            0.5f + 0.5f * MathF.Cos(floatTime * 0.0085f + 5.3f),
            0.5f + 0.5f * MathF.Cos(floatTime * 0.0133f + 4.5f),
            0.5f + 0.5f * MathF.Cos(floatTime * 0.0217f + 3.8f)));

        shader.SetUniformFloat4("_c11", new Vector4(
            0.5f + 0.5f * MathF.Sin(floatTime * 0.0050f + 2.7f),
            0.5f + 0.5f * MathF.Sin(floatTime * 0.0085f + 5.3f),
            0.5f + 0.5f * MathF.Sin(floatTime * 0.0133f + 4.5f),
            0.5f + 0.5f * MathF.Sin(floatTime * 0.0217f + 3.8f)));

        shader.SetUniformFloat4("_c12", new Vector4(mipX, mipY, mipAvg, 0));
        shader.SetUniformFloat4("_c13", new Vector4(blurMin[1], blurMax[1], blurMin[2], blurMax[2]));

        // 24 个随机变换矩阵：前 20 个每个预设固定（随时间旋转），后 4 个每帧全随机。
        var tempMatrices = new float[24][];

        for (int i = 0; i < 20; i++)
        {
            float rx = _randRotationCenters[i].X + _randRotationSpeeds[i].X * floatTime;
            float ry = _randRotationCenters[i].Y + _randRotationSpeeds[i].Y * floatTime;
            float rz = _randRotationCenters[i].Z + _randRotationSpeeds[i].Z * floatTime;

            tempMatrices[i] = ComposeRandomMatrix(_randTranslation[i].X, _randTranslation[i].Y, _randTranslation[i].Z, rx, ry, rz);
        }

        for (int i = 20; i < 24; i++)
        {
            tempMatrices[i] = ComposeRandomMatrix(FloatRand(), FloatRand(), FloatRand(),
                                                  FloatRand() * 6.28f, FloatRand() * 6.28f, FloatRand() * 6.28f);
        }

        ReadOnlySpan<string> matrixNames =
        [
            "rot_s1", "rot_s2", "rot_s3", "rot_s4",
            "rot_d1", "rot_d2", "rot_d3", "rot_d4",
            "rot_f1", "rot_f2", "rot_f3", "rot_f4",
            "rot_vf1", "rot_vf2", "rot_vf3", "rot_vf4",
            "rot_uf1", "rot_uf2", "rot_uf3", "rot_uf4",
            "rot_rand1", "rot_rand2", "rot_rand3", "rot_rand4",
        ];

        for (int i = 0; i < matrixNames.Length; i++)
        {
            // GLSL mat3x4 = 前 3 列的 12 个 float。
            shader.SetUniformMat3x4(matrixNames[i], tempMatrices[i].AsSpan(0, 12));
        }

        // _qa.x.._qa.w / _qb... 对应 q1..q32。
        for (int i = 0; i < Constants.QVarCount; i += 4)
        {
            string variableName = "_q" + (char)('a' + i / 4);
            shader.SetUniformFloat4(variableName, new Vector4(
                (float)presetState.FrameQVariables[i],
                (float)presetState.FrameQVariables[i + 1],
                (float)presetState.FrameQVariables[i + 2],
                (float)presetState.FrameQVariables[i + 3]));
        }

        // 绑定纹理与采样器（含主纹理与模糊纹理）。
        int textureUnit = 0;

        foreach (TextureSamplerDescriptor descriptor in _mainTextureDescriptors)
        {
            // 主纹理每帧互换，必须刷新引用。
            descriptor.Texture = presetState.MainTexture;
            descriptor.Bind(textureUnit, shader);
            textureUnit++;
        }

        presetState.BlurTexture.Bind(ref textureUnit, shader);

        foreach (TextureSamplerDescriptor descriptor in _textureSamplerDescriptors)
        {
            // 纹理被淘汰/管理器重建后按名字重新取一次（对应 C++ 的同名调用）。
            if (descriptor.Empty() && !descriptor.UpdateFailed && renderContext.TextureManager is not null)
            {
                descriptor.TryUpdate(renderContext.TextureManager);
            }

            descriptor.Bind(textureUnit, shader);
            textureUnit++;
        }
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

    /// <summary>释放惰性创建的 GL program（C++ 里由 <c>Shader</c> 的析构函数负责）。</summary>
    public void Dispose()
    {
        if (_shader is not null && Rendering.GlContext.IsInitialized)
        {
            _shader.Dispose();
        }

        _shader = null;
    }

    /// <summary>C++ 的 <c>floatRand</c>：<c>rand() % 7381 / 7380</c>。</summary>
    private static float FloatRand() => Random.Shared.Next() % 7381 / 7380.0f;

    /// <summary>名字形如 <c>randNN</c>（NN 是两位数字）。</summary>
    private static bool IsRandomTextureName(string lowerCaseName)
        => lowerCaseName.Length >= 6 &&
           lowerCaseName.StartsWith("rand", StringComparison.Ordinal) &&
           char.IsAsciiDigit(lowerCaseName[4]) &&
           char.IsAsciiDigit(lowerCaseName[5]);

    /// <summary>
    /// 拼出 <c>rotationY * rotationZ * translation * rotationX</c>（列主序），
    /// 与 C++ 里 <c>tempMatrices[i] = randomTranslation * rotationX; tempMatrices[i] = rotationZ * ...</c> 的顺序一致。
    /// </summary>
    private static float[] ComposeRandomMatrix(float translateX, float translateY, float translateZ,
                                               float rotX, float rotY, float rotZ)
    {
        float[] matrix = MultiplyColumnMajor(Translation(translateX, translateY, translateZ), RotationX(rotX));
        matrix = MultiplyColumnMajor(RotationZ(rotZ), matrix);
        return MultiplyColumnMajor(RotationY(rotY), matrix);
    }

    /// <summary>列主序 4x4 相乘：<c>result = a * b</c>。</summary>
    private static float[] MultiplyColumnMajor(float[] a, float[] b)
    {
        var result = new float[16];

        for (int column = 0; column < 4; column++)
        {
            for (int row = 0; row < 4; row++)
            {
                float sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    sum += a[k * 4 + row] * b[column * 4 + k];
                }

                result[column * 4 + row] = sum;
            }
        }

        return result;
    }

    private static float[] Translation(float x, float y, float z) =>
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        x, y, z, 1,
    ];

    private static float[] RotationX(float angle)
    {
        float c = MathF.Cos(angle);
        float s = MathF.Sin(angle);

        return
        [
            1, 0, 0, 0,
            0, c, s, 0,
            0, -s, c, 0,
            0, 0, 0, 1,
        ];
    }

    private static float[] RotationY(float angle)
    {
        float c = MathF.Cos(angle);
        float s = MathF.Sin(angle);

        return
        [
            c, 0, -s, 0,
            0, 1, 0, 0,
            s, 0, c, 0,
            0, 0, 0, 1,
        ];
    }

    private static float[] RotationZ(float angle)
    {
        float c = MathF.Cos(angle);
        float s = MathF.Sin(angle);

        return
        [
            c, s, 0, 0,
            -s, c, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        ];
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
