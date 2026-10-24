using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;
using MilkDrop.Rendering.Gles3;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 单个预设的全部状态：预设文件里的初值、表达式代码、Q 变量，以及每帧变化的渲染上下文。
/// </summary>
/// <remarks>
/// <para>对应 <c>MilkdropPreset/PresetState.hpp:35-160</c> 与 <c>PresetState.cpp</c>。</para>
/// <para>
/// 与 C++ 的差异（随 P4 渲染管线逐步补齐）：
/// </para>
/// <list type="bullet">
/// <item><c>mainTexture</c>/<c>blurTexture</c> 已可用：前者是外部设置的引用，后者惰性创建
/// GL 资源；<c>untexturedShader</c>/<c>texturedShader</c>（P4 边框/暗角/视频回声用）同样惰性创建。</item>
/// <item>C++ 每个预设各有一个 gmegabuf（<c>globalMemory</c>）；<c>MilkDrop.Eval</c> 的
/// <c>CompileContext.GlobalMemory</c> 是进程级静态，多个预设共用一份（见 projectMIssue.md）。</item>
/// </list>
/// </remarks>
public sealed class PresetState : IDisposable
{
    private BlurTexture? _blurTexture;
    private Shader? _untexturedShader;
    private Shader? _texturedShader;

    /// <summary>正交投影矩阵（列主序，直接喂 <c>glUniformMatrix4fv</c>）：<c>glm::ortho(-1, 1, 1, -1, -40, 40)</c>。</summary>
    public static readonly float[] OrthogonalProjection =
    [
        1f, 0f, 0f, 0f,
        0f, -1f, 0f, 0f,
        0f, 0f, -0.025f, 0f,
        0f, 0f, 0f, 1f,
    ];

    /// <summary>翻转 Y 的版本（列主序，<c>glm::ortho(-1, 1, -1, 1, -40, 40)</c>）。</summary>
    public static readonly float[] OrthogonalProjectionFlipped =
    [
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, -0.025f, 0f,
        0f, 0f, 0f, 1f,
    ];

    public PresetState()
    {
        // 每个预设固定的 hue 动画偏移（C++ 用 random_device + mt19937，这里用 Random.Shared）。
        HueRandomOffsets[0] = Random.Shared.Next() % 64841 * 0.01f;
        HueRandomOffsets[1] = Random.Shared.Next() % 53751 * 0.01f;
        HueRandomOffsets[2] = Random.Shared.Next() % 42661 * 0.01f;
        HueRandomOffsets[3] = Random.Shared.Next() % 31571 * 0.01f;
    }

    // ---- 通用 ----

    public float GammaAdj { get; set; } = 2.0f;
    public float VideoEchoZoom { get; set; } = 2.0f;
    public float VideoEchoAlpha { get; set; }
    public float VideoEchoAlphaOld { get; set; }
    public int VideoEchoOrientation { get; set; }
    public int VideoEchoOrientationOld { get; set; }

    public float Decay { get; set; } = 0.98f;

    // ---- 波形 ----

    public int WaveMode { get; set; }
    public int OldWaveMode { get; set; } = -1;
    public bool AdditiveWaves { get; set; }
    public float WaveAlpha { get; set; } = 0.8f;
    public float WaveScale { get; set; } = 1.0f;
    public float WaveSmoothing { get; set; } = 0.75f;
    public bool WaveDots { get; set; }
    public bool WaveThick { get; set; }
    public float WaveParam { get; set; }
    public bool ModWaveAlphaByVolume { get; set; }
    public float ModWaveAlphaStart { get; set; } = 0.75f;
    public float ModWaveAlphaEnd { get; set; } = 0.95f;

    // ---- 运动/变形 ----

    public float WarpAnimSpeed { get; set; } = 1.0f;
    public float WarpScale { get; set; } = 1.0f;
    public float ZoomExponent { get; set; } = 1.0f;
    public float Shader { get; set; }
    public bool MaximizeWaveColor { get; set; } = true;
    public bool TexWrap { get; set; } = true;
    public bool DarkenCenter { get; set; }
    public bool RedBlueStereo { get; set; }
    public bool Brighten { get; set; }
    public bool Darken { get; set; }
    public bool Solarize { get; set; }
    public bool Invert { get; set; }

    public float Zoom { get; set; } = 1.0f;
    public float Rot { get; set; }
    public float RotCX { get; set; } = 0.5f;
    public float RotCY { get; set; } = 0.5f;
    public float XPush { get; set; }
    public float YPush { get; set; }
    public float WarpAmount { get; set; } = 1.0f;
    public float StretchX { get; set; } = 1.0f;
    public float StretchY { get; set; } = 1.0f;
    public float WaveR { get; set; } = 1.0f;
    public float WaveG { get; set; } = 1.0f;
    public float WaveB { get; set; } = 1.0f;
    public float WaveX { get; set; } = 0.5f;
    public float WaveY { get; set; } = 0.5f;

    // ---- 边框 ----

    public float OuterBorderSize { get; set; } = 0.01f;
    public float OuterBorderR { get; set; }
    public float OuterBorderG { get; set; }
    public float OuterBorderB { get; set; }
    public float OuterBorderA { get; set; }
    public float InnerBorderSize { get; set; } = 0.01f;
    public float InnerBorderR { get; set; } = 0.25f;
    public float InnerBorderG { get; set; } = 0.25f;
    public float InnerBorderB { get; set; } = 0.25f;
    public float InnerBorderA { get; set; }

    // ---- 运动矢量 ----

    public float MvX { get; set; } = 12.0f;
    public float MvY { get; set; } = 9.0f;
    public float MvDX { get; set; }
    public float MvDY { get; set; }
    public float MvL { get; set; } = 0.9f;
    public float MvR { get; set; } = 1.0f;
    public float MvG { get; set; } = 1.0f;
    public float MvB { get; set; } = 1.0f;
    public float MvA { get; set; }

    // ---- 模糊 ----

    public float Blur1Min { get; set; }
    public float Blur2Min { get; set; }
    public float Blur3Min { get; set; }
    public float Blur1Max { get; set; } = 1.0f;
    public float Blur2Max { get; set; } = 1.0f;
    public float Blur3Max { get; set; } = 1.0f;
    public float Blur1EdgeDarken { get; set; } = 0.25f;

    // ---- 版本 ----

    /// <summary>预设文件里的 <c>MILKDROP_PRESET_VERSION</c>。</summary>
    public int PresetVersion { get; set; } = 100;

    /// <summary><c>PSVERSION</c> / <c>PSVERSION_WARP</c>。</summary>
    public int WarpShaderVersion { get; set; } = 2;

    /// <summary><c>PSVERSION</c> / <c>PSVERSION_COMP</c>。</summary>
    public int CompositeShaderVersion { get; set; } = 2;

    /// <summary>每个预设固定的 hue 动画偏移。</summary>
    public float[] HueRandomOffsets { get; } = new float[4];

    // ---- 表达式运行时 ----

    /// <summary><c>reg00</c>..<c>reg99</c> 的存储，所有表达式上下文共享（对应 C++ <c>globalRegisters</c>）。</summary>
    public double[] GlobalRegisters { get; } = new double[100];

    /// <summary>per-frame 代码执行后的 Q 变量（对应 C++ <c>frameQVariables</c>）。</summary>
    public double[] FrameQVariables { get; } = new double[Constants.QVarCount];

    // ---- 音频 / 渲染上下文 ----

    public FrameAudioData AudioData { get; } = new();

    public RenderContext RenderContext { get; } = new();

    // ---- 预设代码 ----

    public string PerFrameInitCode { get; set; } = string.Empty;
    public string PerFrameCode { get; set; } = string.Empty;
    public string PerPixelCode { get; set; } = string.Empty;

    public string[] CustomWaveInitCode { get; } = NewStrings(Constants.CustomWaveformCount);
    public string[] CustomWavePerFrameCode { get; } = NewStrings(Constants.CustomWaveformCount);
    public string[] CustomWavePerPointCode { get; } = NewStrings(Constants.CustomWaveformCount);

    public string[] CustomShapeInitCode { get; } = NewStrings(Constants.CustomShapeCount);
    public string[] CustomShapePerFrameCode { get; } = NewStrings(Constants.CustomShapeCount);

    public string WarpShader { get; set; } = string.Empty;
    public string CompositeShader { get; set; } = string.Empty;

    /// <summary>
    /// 随机纹理（<c>randNN</c>）的描述符，warp/composite 两个着色器共用同一份，
    /// 保证同一预设里两边拿到同一张纹理。
    /// </summary>
    public SortedDictionary<int, TextureSamplerDescriptor> RandomTextureDescriptors { get; } = new();

    /// <summary>
    /// 主纹理（C++ 的 <c>mainTexture</c> <c>weak_ptr</c>）：warp 阶段的输入，由预设管理器在
    /// 拿到 framebuffer 附件后设置。
    /// </summary>
    public Texture? MainTexture { get; set; }

    /// <summary>
    /// 主纹理的模糊链（对应 C++ 的 <c>blurTexture</c> 值成员）。
    /// 第一次访问时才创建 GL 资源，避免无上下文的纯数据测试被迫初始化 GL。
    /// </summary>
    public BlurTexture BlurTexture => _blurTexture ??= new BlurTexture();

    /// <summary>
    /// 画不带纹理的图元（边框、暗角中心）的着色器（对应 C++ 的 <c>untexturedShader</c>）。
    /// 第一次访问时才编译，避免纯数据测试被迫初始化 GL。
    /// </summary>
    public Shader UntexturedShader => _untexturedShader ??= CreateUntexturedShader();

    /// <summary>
    /// 画带纹理的图元（视频回声、纹理化形状）的着色器（对应 C++ 的 <c>texturedShader</c>）。
    /// </summary>
    public Shader TexturedShader => _texturedShader ??= CreateTexturedShader();

    /// <summary>释放惰性创建的 GL 资源（模糊链与两个通用着色器）。</summary>
    public void Dispose()
    {
        _blurTexture?.Dispose();
        _blurTexture = null;

        if (GlContext.IsInitialized)
        {
            _untexturedShader?.Dispose();
            _texturedShader?.Dispose();
        }

        _untexturedShader = null;
        _texturedShader = null;
    }

    /// <remarks>对应 <c>PresetState.cpp:19-23</c>。</remarks>
    private static Shader CreateUntexturedShader()
    {
        MilkdropStaticShaders staticShaders = MilkdropStaticShaders.Get();

        var shader = new Shader();
        shader.CompileProgram(staticShaders.GetUntexturedDrawVertexShader(),
                              staticShaders.GetUntexturedDrawFragmentShader());
        return shader;
    }

    /// <remarks>对应 <c>PresetState.cpp:22-23</c>。</remarks>
    private static Shader CreateTexturedShader()
    {
        MilkdropStaticShaders staticShaders = MilkdropStaticShaders.Get();

        var shader = new Shader();
        shader.CompileProgram(staticShaders.GetTexturedDrawVertexShader(),
                              staticShaders.GetTexturedDrawFragmentShader());
        return shader;
    }

    /// <summary>从预设文件里载入初值与代码（对应 <c>PresetState::Initialize</c>）。</summary>
    /// <param name="parsedFile">已解析好的预设文件。</param>
    public void Initialize(PresetFileParser parsedFile)
    {
        ArgumentNullException.ThrowIfNull(parsedFile);

        // 通用
        Decay = parsedFile.GetFloat("fDecay", Decay);
        GammaAdj = parsedFile.GetFloat("fGammaAdj", GammaAdj);
        VideoEchoZoom = parsedFile.GetFloat("fVideoEchoZoom", VideoEchoZoom);
        VideoEchoAlpha = parsedFile.GetFloat("fVideoEchoAlpha", VideoEchoAlpha);
        VideoEchoOrientation = parsedFile.GetInt("nVideoEchoOrientation", VideoEchoOrientation);
        RedBlueStereo = parsedFile.GetBool("bRedBlueStereo", RedBlueStereo);
        Brighten = parsedFile.GetBool("bBrighten", Brighten);
        Darken = parsedFile.GetBool("bDarken", Darken);
        Solarize = parsedFile.GetBool("bSolarize", Solarize);
        Invert = parsedFile.GetBool("bInvert", Invert);
        Shader = parsedFile.GetFloat("fShader", Shader);
        Blur1Min = parsedFile.GetFloat("b1n", Blur1Min);
        Blur2Min = parsedFile.GetFloat("b2n", Blur2Min);
        Blur3Min = parsedFile.GetFloat("b3n", Blur3Min);
        Blur1Max = parsedFile.GetFloat("b1x", Blur1Max);
        Blur2Max = parsedFile.GetFloat("b2x", Blur2Max);
        Blur3Max = parsedFile.GetFloat("b3x", Blur3Max);
        Blur1EdgeDarken = parsedFile.GetFloat("b1ed", Blur1EdgeDarken);

        // 波形
        WaveMode = parsedFile.GetInt("nWaveMode", WaveMode);
        AdditiveWaves = parsedFile.GetBool("bAdditiveWaves", AdditiveWaves);
        WaveDots = parsedFile.GetBool("bWaveDots", WaveDots);
        WaveThick = parsedFile.GetBool("bWaveThick", WaveThick);
        ModWaveAlphaByVolume = parsedFile.GetBool("bModWaveAlphaByVolume", ModWaveAlphaByVolume);
        MaximizeWaveColor = parsedFile.GetBool("bMaximizeWaveColor", MaximizeWaveColor);
        WaveAlpha = parsedFile.GetFloat("fWaveAlpha", WaveAlpha);
        WaveScale = parsedFile.GetFloat("fWaveScale", WaveScale);
        WaveSmoothing = parsedFile.GetFloat("fWaveSmoothing", WaveSmoothing);
        WaveParam = parsedFile.GetFloat("fWaveParam", WaveParam);
        ModWaveAlphaStart = parsedFile.GetFloat("fModWaveAlphaStart", ModWaveAlphaStart);
        ModWaveAlphaEnd = parsedFile.GetFloat("fModWaveAlphaEnd", ModWaveAlphaEnd);
        WaveR = parsedFile.GetFloat("wave_r", WaveR);
        WaveG = parsedFile.GetFloat("wave_g", WaveG);
        WaveB = parsedFile.GetFloat("wave_b", WaveB);
        WaveX = parsedFile.GetFloat("wave_x", WaveX);
        WaveY = parsedFile.GetFloat("wave_y", WaveY);

        // 运动矢量
        MvX = parsedFile.GetFloat("nMotionVectorsX", MvX);
        MvY = parsedFile.GetFloat("nMotionVectorsY", MvY);
        MvDX = parsedFile.GetFloat("mv_dx", MvDX);
        MvDY = parsedFile.GetFloat("mv_dy", MvDY);
        MvL = parsedFile.GetFloat("mv_l", MvL);
        MvR = parsedFile.GetFloat("mv_r", MvR);
        MvG = parsedFile.GetFloat("mv_g", MvG);
        MvB = parsedFile.GetFloat("mv_b", MvB);

        // 向后兼容：老预设只有开关，没有 mv_a 的浮点值。
        MvA = parsedFile.GetBool("bMotionVectorsOn", false) ? 1.0f : 0.0f;
        MvA = parsedFile.GetFloat("mv_a", MvA);

        // 运动
        Zoom = parsedFile.GetFloat("zoom", Zoom);
        Rot = parsedFile.GetFloat("rot", Rot);
        RotCX = parsedFile.GetFloat("cx", RotCX);
        RotCY = parsedFile.GetFloat("cy", RotCY);
        XPush = parsedFile.GetFloat("dx", XPush);
        YPush = parsedFile.GetFloat("dy", YPush);
        WarpAmount = parsedFile.GetFloat("warp", WarpAmount);
        StretchX = parsedFile.GetFloat("sx", StretchX);
        StretchY = parsedFile.GetFloat("sy", StretchY);
        TexWrap = parsedFile.GetBool("bTexWrap", TexWrap);
        DarkenCenter = parsedFile.GetBool("bDarkenCenter", DarkenCenter);
        WarpAnimSpeed = parsedFile.GetFloat("fWarpAnimSpeed", WarpAnimSpeed);
        WarpScale = parsedFile.GetFloat("fWarpScale", WarpScale);
        ZoomExponent = parsedFile.GetFloat("fZoomExponent", ZoomExponent);

        // 边框
        OuterBorderSize = parsedFile.GetFloat("ob_size", OuterBorderSize);
        OuterBorderR = parsedFile.GetFloat("ob_r", OuterBorderR);
        OuterBorderG = parsedFile.GetFloat("ob_g", OuterBorderG);
        OuterBorderB = parsedFile.GetFloat("ob_b", OuterBorderB);
        OuterBorderA = parsedFile.GetFloat("ob_a", OuterBorderA);
        InnerBorderSize = parsedFile.GetFloat("ib_size", InnerBorderSize);
        InnerBorderR = parsedFile.GetFloat("ib_r", InnerBorderR);
        InnerBorderG = parsedFile.GetFloat("ib_g", InnerBorderG);
        InnerBorderB = parsedFile.GetFloat("ib_b", InnerBorderB);
        InnerBorderA = parsedFile.GetFloat("ib_a", InnerBorderA);

        // 版本：Milkdrop 1.x 没有着色器；2.0 只有一个 PSVERSION；2.x 起分 warp/comp。
        PresetVersion = parsedFile.GetInt("MILKDROP_PRESET_VERSION", PresetVersion);
        if (PresetVersion < 200)
        {
            WarpShaderVersion = 0;
            CompositeShaderVersion = 0;
        }
        else if (PresetVersion == 200)
        {
            WarpShaderVersion = parsedFile.GetInt("PSVERSION", WarpShaderVersion);
            CompositeShaderVersion = parsedFile.GetInt("PSVERSION", CompositeShaderVersion);
        }
        else
        {
            WarpShaderVersion = parsedFile.GetInt("PSVERSION_WARP", WarpShaderVersion);
            CompositeShaderVersion = parsedFile.GetInt("PSVERSION_COMP", CompositeShaderVersion);
        }

        // 代码
        PerFrameInitCode = parsedFile.GetCode("per_frame_init_");
        PerFrameCode = parsedFile.GetCode("per_frame_");
        PerPixelCode = parsedFile.GetCode("per_pixel_");

        // 自定义波形代码（key 形如 wave_0_init1 / wave_0_per_frame1 / wave_0_per_point1）
        for (int i = 0; i < Constants.CustomWaveformCount; i++)
        {
            string wavePrefix = "wave_" + i + "_";
            CustomWaveInitCode[i] = parsedFile.GetCode(wavePrefix + "init");
            CustomWavePerFrameCode[i] = parsedFile.GetCode(wavePrefix + "per_frame");
            CustomWavePerPointCode[i] = parsedFile.GetCode(wavePrefix + "per_point");
        }

        // 自定义形状代码（key 形如 shape_0_init1 / shape_0_per_frame1）
        for (int i = 0; i < Constants.CustomShapeCount; i++)
        {
            string shapePrefix = "shape_" + i + "_";
            CustomShapeInitCode[i] = parsedFile.GetCode(shapePrefix + "init");
            CustomShapePerFrameCode[i] = parsedFile.GetCode(shapePrefix + "per_frame");
        }

        // 着色器代码
        WarpShader = parsedFile.GetCode("warp_");
        CompositeShader = parsedFile.GetCode("comp_");
    }

    private static string[] NewStrings(int count)
    {
        var values = new string[count];
        Array.Fill(values, string.Empty);
        return values;
    }
}
