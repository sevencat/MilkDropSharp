using System.Numerics;
using MilkDrop.Eval;

namespace MilkDrop.Classic.Visualizer.Preset;

/// <summary>
/// <c>CState</c>（state.h:240-385）的移植：一个预设的全部可混合参数。
/// </summary>
/// <remarks>
/// 目前只覆盖 <c>Default</c> / <c>RandomizePresetVars</c> 需要的字段与逻辑；
/// <c>Import</c>/<c>Export</c>、<c>StartBlendFrom</c> 的混合状态机和 EEL 句柄随后补。
/// 字段名对应原版的 <c>m_*</c>（去掉前缀）。
/// </remarks>
public sealed class PresetState
{
    /// <summary><c>MAX_CUSTOM_WAVES</c>（md_defines.h:56）。</summary>
    public const int MaxCustomWaves = 4;

    /// <summary>
    /// 原版能画出来的内置波形模式个数：合法 <c>nWaveMode</c> 是 0..8，所以这里是 9。
    /// </summary>
    /// <remarks>
    /// <c>NUM_WAVES</c> 在 md_defines.h:51 是 8（只用于菜单增减时的取模），但
    /// <c>milkdropfs.cpp:2801</c> 的 <c>switch(wave)</c> 里 <c>case 8</c> 与 6/7 合并处理，所以 8 也确实会画。
    /// 越界（9 及以上）没有 <c>default</c> 分支 → 不产生任何顶点。
    /// 更高编号是 MD3 PRO 扩展的内置波形（语料里 58 个预设在用），见 <c>Visualizer.PresetNotes</c>。
    /// </remarks>
    public const int NumWaveModes = 9;

    /// <summary>
    /// 预设用了原版画不出来的内置波形模式时，返回一句给人看的说明；否则返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 单纯的黑屏预设通常就是这个原因，例如
    /// <c>A story of lasers - MilkDrop2077 - Over Water.milk</c>：它的 warp 只做 <c>ret *= 0.94</c>、
    /// comp 只做拷贝、<c>per_frame</c> 里还把 <c>wave_a</c> 归零，唯一的内容来源就是 <c>nWaveMode=15</c>。
    /// </remarks>
    public string? DescribeUnsupportedWaveMode()
    {
        if (WaveMode >= 0 && WaveMode < NumWaveModes)
        {
            return null;
        }

        return $"nWaveMode={WaveMode} 超出原版支持范围 0..{NumWaveModes - 1}"
             + "（更高编号是 MD3 PRO 的扩展内置波形，本项目没有参考实现）"
             + " → 内置波形不会渲染；如果画面是空的，通常就是这个原因。";
    }

    /// <summary><c>MAX_CUSTOM_SHAPES</c>（md_defines.h:57）。</summary>
    public const int MaxCustomShapes = 4;

    /// <summary><c>INVALID_PRESET_DESC</c>（state.h:227）。</summary>
    public const string InvalidPresetDesc = "<no description>";

    public const int StateGeneral = 1;
    public const int StateMotion = 2;
    public const int StateWave = 4;
    public const int StateWarp = 8;
    public const int StateComp = 16;
    public const int StateAll = StateGeneral | StateMotion | StateWave | StateWarp | StateComp;

    /// <summary><c>RECOMPILE_PRESET_CODE</c>（state.h:45）。</summary>
    public const int RecompilePresetCode = 1;

    /// <summary><c>RECOMPILE_WAVE_CODE</c>（state.h:46）。</summary>
    public const int RecompileWaveCode = 2;

    /// <summary><c>RECOMPILE_SHAPE_CODE</c>（state.h:47）。</summary>
    public const int RecompileShapeCode = 4;

    private readonly Random _rng;
    private readonly List<string> _errors = [];

    public PresetState(Random? random = null, IExpressionEngine? engine = null)
    {
        _rng = random ?? Random.Shared;
        var evalEngine = engine ?? new ProjectMEvalEngine();

        PerFrame = new PerFrameVariables(evalEngine);
        PerVertex = new PerVertexVariables(evalEngine);

        for (var i = 0; i < MaxCustomWaves; i++)
        {
            WaveExprs[i] = new WaveExpressions(evalEngine, i);
        }

        for (var i = 0; i < MaxCustomShapes; i++)
        {
            ShapeExprs[i] = new ShapeExpressions(evalEngine, i);
        }
    }

    /// <summary>per-frame VM 与变量表（<c>m_pf_eel</c> + <c>var_pf_*</c>）。</summary>
    public PerFrameVariables PerFrame { get; }

    /// <summary>per-vertex VM 与变量表（<c>m_pv_eel</c> + <c>var_pv_*</c>）。</summary>
    public PerVertexVariables PerVertex { get; }

    /// <summary>4 个波形的 per-frame / per-point VM（<c>m_wave[i].m_pf_eel</c> / <c>m_pp_eel</c>）。</summary>
    public WaveExpressions[] WaveExprs { get; } = new WaveExpressions[MaxCustomWaves];

    /// <summary>4 个图形的 per-frame VM（<c>m_shape[i].m_pf_eel</c>）。</summary>
    public ShapeExpressions[] ShapeExprs { get; } = new ShapeExpressions[MaxCustomShapes];

    /// <summary>已编译的 per-frame 代码（<c>m_pf_codehandle</c>）；为 null 表示没有或编译失败。</summary>
    public ICompiledExpression? PerFrameCode { get; private set; }

    /// <summary>已编译的 per-vertex 代码（<c>m_pp_codehandle</c>）；为 null 表示没有或编译失败。</summary>
    public ICompiledExpression? PerVertexCode => PerVertex.Code;

    /// <summary>最近一次编译/初始化失败的描述（对应原版 AddError 弹窗的内容）。</summary>
    public string? LastError { get; private set; }

    /// <summary>最近一次 <see cref="RecompileExpressions"/> 期间收集到的全部错误。</summary>
    public IReadOnlyList<string> Errors => _errors;

    // ---- 标识 / 版本 ----

    /// <summary>预设文件名（不含路径与扩展名）。</summary>
    public string Description = InvalidPresetDesc;

    /// <summary>warp / comp 两个版本号的最小值。</summary>
    public int MinPsVersion;

    /// <summary>warp / comp 两个版本号的最大值。</summary>
    public int MaxPsVersion;

    /// <summary>0 = MilkDrop 1 时代（无 shader），2 = ps_2_0，3 = ps_3_0。</summary>
    public int WarpPsVersion;

    public int CompPsVersion;

    /// <summary>0..5。</summary>
    public float Rating;

    /// <summary><c>m_fPresetStartTime</c>：&lt;0 表示尚未开始（进度未定）。</summary>
    public float PresetStartTime;

    // ---- 后期 / 通用 ----

    /// <summary>1.0 = 无衰减，0.95 = 强衰减。</summary>
    public readonly BlendableFloat Decay = new();

    public readonly BlendableFloat GammaAdj = new();
    public readonly BlendableFloat VideoEchoZoom = new();
    public readonly BlendableFloat VideoEchoAlpha = new();
    public float VideoEchoAlphaOld;
    public int VideoEchoOrientation;
    public int VideoEchoOrientationOld;
    public readonly BlendableFloat Shader = new();

    public bool RedBlueStereo;
    public bool Brighten;
    public bool Darken;
    public bool TexWrap;
    public bool DarkenCenter;
    public bool Solarize;
    public bool Invert;

    public readonly BlendableFloat Blur1Min = new();
    public readonly BlendableFloat Blur2Min = new();
    public readonly BlendableFloat Blur3Min = new();
    public readonly BlendableFloat Blur1Max = new();
    public readonly BlendableFloat Blur2Max = new();
    public readonly BlendableFloat Blur3Max = new();
    public readonly BlendableFloat Blur1EdgeDarken = new();

    // ---- 波形 ----

    public int WaveMode;
    public int OldWaveMode;
    public bool AdditiveWaves;
    public readonly BlendableFloat WaveAlpha = new();
    public readonly BlendableFloat WaveScale = new();
    public readonly BlendableFloat WaveSmoothing = new();
    public bool WaveDots;
    public bool WaveThick;
    public readonly BlendableFloat WaveParam = new();
    public bool ModWaveAlphaByVolume;
    public readonly BlendableFloat ModWaveAlphaStart = new();
    public readonly BlendableFloat ModWaveAlphaEnd = new();
    public bool MaximizeWaveColor;
    public readonly BlendableFloat WaveR = new();
    public readonly BlendableFloat WaveG = new();
    public readonly BlendableFloat WaveB = new();
    public readonly BlendableFloat WaveX = new();
    public readonly BlendableFloat WaveY = new();

    public readonly PresetWave[] Waves = [new(), new(), new(), new()];
    public readonly PresetShape[] Shapes = [new(), new(), new(), new()];

    // ---- 运动（warp）----

    public float WarpAnimSpeed;
    public readonly BlendableFloat WarpScale = new();
    public readonly BlendableFloat ZoomExponent = new();
    public readonly BlendableFloat Zoom = new();
    public readonly BlendableFloat Rot = new();
    public readonly BlendableFloat RotCX = new();
    public readonly BlendableFloat RotCY = new();
    public readonly BlendableFloat XPush = new();
    public readonly BlendableFloat YPush = new();
    public readonly BlendableFloat WarpAmount = new();
    public readonly BlendableFloat StretchX = new();
    public readonly BlendableFloat StretchY = new();

    public readonly BlendableFloat OuterBorderSize = new();
    public readonly BlendableFloat OuterBorderR = new();
    public readonly BlendableFloat OuterBorderG = new();
    public readonly BlendableFloat OuterBorderB = new();
    public readonly BlendableFloat OuterBorderA = new();
    public readonly BlendableFloat InnerBorderSize = new();
    public readonly BlendableFloat InnerBorderR = new();
    public readonly BlendableFloat InnerBorderG = new();
    public readonly BlendableFloat InnerBorderB = new();
    public readonly BlendableFloat InnerBorderA = new();

    // ---- 运动矢量 ----

    public readonly BlendableFloat MvX = new();
    public readonly BlendableFloat MvY = new();
    public readonly BlendableFloat MvDX = new();
    public readonly BlendableFloat MvDY = new();
    public readonly BlendableFloat MvL = new();
    public readonly BlendableFloat MvR = new();
    public readonly BlendableFloat MvG = new();
    public readonly BlendableFloat MvB = new();
    public readonly BlendableFloat MvA = new();

    // ---- 代码文本 ----

    public string PerFrameInit = "";
    public string PerFrameExpr = "";
    public string PerPixelExpr = "";
    public string WarpShadersText = "";
    public string CompShadersText = "";

    // ---- 混合状态 ----

    public bool IsBlending;
    public float BlendStartTime;
    public float BlendDuration;
    public float BlendProgress;

    // ---- 给 shader 用的随机量 ----

    /// <summary>4 个 0..1 的随机数，预设加载时生成，喂给像素着色器。</summary>
    public Vector4 RandPreset;

    public readonly Vector3[] Xlate = new Vector3[20];
    public readonly Vector3[] RotBase = new Vector3[20];
    public readonly Vector3[] RotSpeed = new Vector3[20];

    /// <summary>对应 <c>CState::Default</c>（state.cpp:515-702）。</summary>
    public void Default(int flags = StateAll)
    {
        if ((flags & StateGeneral) != 0 && (flags & StateMotion) != 0 && (flags & StateWave) != 0)
        {
            // 三个区一起重置时才动名字：warp/comp 锁定切换只更新参数，mash-up 不动名字
            Description = InvalidPresetDesc;
            PresetStartTime = -1f;
        }

        MinPsVersion = 0;
        MaxPsVersion = 0;
        IsBlending = false;

        if ((flags & StateGeneral) != 0)
        {
            Rating = 3.0f;
            Decay.Value = 0.98f;
            GammaAdj.Value = 2.0f;
            VideoEchoZoom.Value = 2.0f;
            VideoEchoAlpha.Value = 0.0f;
            VideoEchoOrientation = 0;
            RedBlueStereo = false;
            Brighten = false;
            Darken = false;
            TexWrap = true;
            DarkenCenter = false;
            Solarize = false;
            Invert = false;
            Shader.Value = 0.0f;
            Blur1Min.Value = 0.0f;
            Blur2Min.Value = 0.0f;
            Blur3Min.Value = 0.0f;
            Blur1Max.Value = 1.0f;
            Blur2Max.Value = 1.0f;
            Blur3Max.Value = 1.0f;
            Blur1EdgeDarken.Value = 0.25f;
        }

        if ((flags & StateWave) != 0)
        {
            WaveMode = 0;
            OldWaveMode = -1;
            AdditiveWaves = false;
            WaveDots = false;
            WaveThick = false;
            WaveAlpha.Value = 0.8f;
            WaveScale.Value = 1.0f;
            WaveSmoothing.Value = 0.75f;
            WaveParam.Value = 0.0f;
            ModWaveAlphaByVolume = false;
            ModWaveAlphaStart.Value = 0.75f;
            ModWaveAlphaEnd.Value = 0.95f;
            WaveR.Value = 1.0f;
            WaveG.Value = 1.0f;
            WaveB.Value = 1.0f;
            WaveX.Value = 0.5f;
            WaveY.Value = 0.5f;
            MaximizeWaveColor = true;
            MvX.Value = 12.0f;
            MvY.Value = 9.0f;
            MvDX.Value = 0.0f;
            MvDY.Value = 0.0f;
            MvL.Value = 0.9f;
            MvR.Value = 1.0f;
            MvG.Value = 1.0f;
            MvB.Value = 1.0f;
            MvA.Value = 1.0f;

            foreach (var wave in Waves)
            {
                wave.SetDefaults();
            }

            foreach (var shape in Shapes)
            {
                shape.SetDefaults();
            }
        }

        if ((flags & StateMotion) != 0)
        {
            WarpAnimSpeed = 1.0f;
            WarpScale.Value = 1.0f;
            ZoomExponent.Value = 1.0f;
            Zoom.Value = 1.0f;
            Rot.Value = 0.0f;
            RotCX.Value = 0.5f;
            RotCY.Value = 0.5f;
            XPush.Value = 0.0f;
            YPush.Value = 0.0f;
            WarpAmount.Value = 1.0f;
            StretchX.Value = 1.0f;
            StretchY.Value = 1.0f;
            OuterBorderSize.Value = 0.01f;
            OuterBorderR.Value = 0.0f;
            OuterBorderG.Value = 0.0f;
            OuterBorderB.Value = 0.0f;
            OuterBorderA.Value = 0.0f;
            InnerBorderSize.Value = 0.01f;
            InnerBorderR.Value = 0.25f;
            InnerBorderG.Value = 0.25f;
            InnerBorderB.Value = 0.25f;
            InnerBorderA.Value = 0.0f;

            PerFrameInit = "";
            PerFrameExpr = "";
            PerPixelExpr = "";
        }

        if ((flags & StateWarp) != 0)
        {
            WarpShadersText = "";
            WarpPsVersion = 0;
        }

        if ((flags & StateComp) != 0)
        {
            CompShadersText = "";
            CompPsVersion = 0;
        }

        RandomizePresetVars();
    }

    /// <summary>
    /// 对应 <c>CState::RandomizePresetVars</c>（state.cpp:1875-1899）。
    /// </summary>
    /// <remarks>
    /// 原版用 <c>FRAND = (rand() % 7381)/7380.0f</c>（值域 0..1，闭区间），这里照抄这个分布，
    /// 只是换成可注入的 <see cref="Random"/> 以便测试可复现。
    /// </remarks>
    public void RandomizePresetVars()
    {
        RandPreset = new Vector4(Frand(), Frand(), Frand(), Frand());

        var k = 0;
        do
        {
            for (var i = 0; i < 4; i++)
            {
                const float xlateMult = 1f;   // 原版这里留了 (j==0)?1:0 的占位
                var rotMult = 0.9f * MathF.Pow(k / 8.0f, 3.2f);

                Xlate[k] = new Vector3(
                    ((Frand() * 2) - 1) * xlateMult,
                    ((Frand() * 2) - 1) * xlateMult,
                    ((Frand() * 2) - 1) * xlateMult);

                RotBase[k] = new Vector3(Frand() * 6.28f, Frand() * 6.28f, Frand() * 6.28f);

                RotSpeed[k] = new Vector3(
                    ((Frand() * 2) - 1) * rotMult,
                    ((Frand() * 2) - 1) * rotMult,
                    ((Frand() * 2) - 1) * rotMult);

                k++;
            }
        }
        while (k < Xlate.Length);
    }

    /// <summary>
    /// 对应 <c>CState::Import</c>（state.cpp:1263-1457）。
    /// </summary>
    /// <param name="filePath"><c>.milk</c> 路径。</param>
    /// <param name="time">当前动画时间，写进 <see cref="PresetStartTime"/>。</param>
    /// <param name="oldState">mash-up 的来源状态；<c>null</c> 表示整份导入。</param>
    /// <param name="applyFlags">要覆盖的区段；缺的区段从 <paramref name="oldState"/> 整块拷贝。</param>
    /// <returns>文件打不开时返回 <c>false</c>（对应原版 <c>_wfopen</c> 失败）。</returns>
    public bool Import(string filePath, float time, PresetState? oldState = null, int applyFlags = StateAll)
    {
        if (oldState is null)
        {
            applyFlags = StateAll;
        }

        // mash-up：先把旧状态整块拷过来，再只覆盖要改的区段
        if (applyFlags != StateAll && !ReferenceEquals(this, oldState))
        {
            CopyFrom(oldState!);
        }

        // 先铺默认值，再用文件里的键覆盖
        Default(applyFlags);

        if ((applyFlags & StateGeneral) != 0 && (applyFlags & StateMotion) != 0 && (applyFlags & StateWave) != 0)
        {
            PresetStartTime = time;

            // 描述取文件名（去路径、去扩展名）
            Description = Path.GetFileNameWithoutExtension(filePath);
        }

        PresetKeyValueFile file;
        try
        {
            file = PresetKeyValueFile.Load(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }

        // ---- 版本 ----
        var presetVersion = file.GetIntExact("MILKDROP_PRESET_VERSION", 100);

        int warpPsVersionInFile;
        int compPsVersionInFile;
        if (presetVersion < 200)
        {
            // MilkDrop 1 时代：没有 shader
            warpPsVersionInFile = 0;
            compPsVersionInFile = 0;
        }
        else if (presetVersion == 200)
        {
            warpPsVersionInFile = file.GetIntExact("PSVERSION", 2);
            compPsVersionInFile = warpPsVersionInFile;
        }
        else
        {
            warpPsVersionInFile = file.GetIntExact("PSVERSION_WARP", 2);
            compPsVersionInFile = file.GetIntExact("PSVERSION_COMP", 2);
        }

        // ---- general ----
        if ((applyFlags & StateGeneral) != 0)
        {
            Rating = file.GetFloatExact("fRating", Rating);
            Decay.Value = file.GetFloatExact("fDecay", Decay.Eval(-1f));
            GammaAdj.Value = file.GetFloatExact("fGammaAdj", GammaAdj.Eval(-1f));
            VideoEchoZoom.Value = file.GetFloatExact("fVideoEchoZoom", VideoEchoZoom.Eval(-1f));
            VideoEchoAlpha.Value = file.GetFloatExact("fVideoEchoAlpha", VideoEchoAlpha.Eval(-1f));
            VideoEchoOrientation = file.GetIntExact("nVideoEchoOrientation", VideoEchoOrientation);
            RedBlueStereo = file.GetIntExact("bRedBlueStereo", RedBlueStereo ? 1 : 0) != 0;
            Brighten = file.GetIntExact("bBrighten", Brighten ? 1 : 0) != 0;
            Darken = file.GetIntExact("bDarken", Darken ? 1 : 0) != 0;
            Solarize = file.GetIntExact("bSolarize", Solarize ? 1 : 0) != 0;
            Invert = file.GetIntExact("bInvert", Invert ? 1 : 0) != 0;
            Shader.Value = file.GetFloatExact("fShader", Shader.Eval(-1f));
            Blur1Min.Value = file.GetFloatExact("b1n", Blur1Min.Eval(-1f));
            Blur2Min.Value = file.GetFloatExact("b2n", Blur2Min.Eval(-1f));
            Blur3Min.Value = file.GetFloatExact("b3n", Blur3Min.Eval(-1f));
            Blur1Max.Value = file.GetFloatExact("b1x", Blur1Max.Eval(-1f));
            Blur2Max.Value = file.GetFloatExact("b2x", Blur2Max.Eval(-1f));
            Blur3Max.Value = file.GetFloatExact("b3x", Blur3Max.Eval(-1f));
            Blur1EdgeDarken.Value = file.GetFloatExact("b1ed", Blur1EdgeDarken.Eval(-1f));
        }

        // ---- wave ----
        if ((applyFlags & StateWave) != 0)
        {
            WaveMode = file.GetIntExact("nWaveMode", WaveMode);
            AdditiveWaves = file.GetIntExact("bAdditiveWaves", AdditiveWaves ? 1 : 0) != 0;
            WaveDots = file.GetIntExact("bWaveDots", WaveDots ? 1 : 0) != 0;
            WaveThick = file.GetIntExact("bWaveThick", WaveThick ? 1 : 0) != 0;
            ModWaveAlphaByVolume = file.GetIntExact("bModWaveAlphaByVolume", ModWaveAlphaByVolume ? 1 : 0) != 0;
            MaximizeWaveColor = file.GetIntExact("bMaximizeWaveColor", MaximizeWaveColor ? 1 : 0) != 0;
            WaveAlpha.Value = file.GetFloatExact("fWaveAlpha", WaveAlpha.Eval(-1f));
            WaveScale.Value = file.GetFloatExact("fWaveScale", WaveScale.Eval(-1f));
            WaveSmoothing.Value = file.GetFloatExact("fWaveSmoothing", WaveSmoothing.Eval(-1f));
            WaveParam.Value = file.GetFloatExact("fWaveParam", WaveParam.Eval(-1f));
            ModWaveAlphaStart.Value = file.GetFloatExact("fModWaveAlphaStart", ModWaveAlphaStart.Eval(-1f));
            ModWaveAlphaEnd.Value = file.GetFloatExact("fModWaveAlphaEnd", ModWaveAlphaEnd.Eval(-1f));

            // 原版这里五个 wave_* 的缺省值都写成了 m_fRot.eval(-1)（复制粘贴错误），照抄。
            // 只有预设缺少这些键时才会暴露，但缺键时颜色/位置会变成 rot 的值。
            WaveR.Value = file.GetFloatExact("wave_r", Rot.Eval(-1f));
            WaveG.Value = file.GetFloatExact("wave_g", Rot.Eval(-1f));
            WaveB.Value = file.GetFloatExact("wave_b", Rot.Eval(-1f));
            WaveX.Value = file.GetFloatExact("wave_x", Rot.Eval(-1f));
            WaveY.Value = file.GetFloatExact("wave_y", Rot.Eval(-1f));

            MvX.Value = file.GetFloatExact("nMotionVectorsX", MvX.Eval(-1f));
            MvY.Value = file.GetFloatExact("nMotionVectorsY", MvY.Eval(-1f));
            MvDX.Value = file.GetFloatExact("mv_dx", MvDX.Eval(-1f));
            MvDY.Value = file.GetFloatExact("mv_dy", MvDY.Eval(-1f));
            MvL.Value = file.GetFloatExact("mv_l", MvL.Eval(-1f));
            MvR.Value = file.GetFloatExact("mv_r", MvR.Eval(-1f));
            MvG.Value = file.GetFloatExact("mv_g", MvG.Eval(-1f));
            MvB.Value = file.GetFloatExact("mv_b", MvB.Eval(-1f));

            // 老预设只有 bMotionVectorsOn；之后那行一定会再覆盖一次。
            MvA.Value = file.GetIntExact("bMotionVectorsOn", 0) == 0 ? 0.0f : 1.0f;
            MvA.Value = file.GetFloatExact("mv_a", MvA.Eval(-1f));

            for (var i = 0; i < MaxCustomWaves; i++)
            {
                ImportWave(file, i);
            }

            for (var i = 0; i < MaxCustomShapes; i++)
            {
                ImportShape(file, i);
            }
        }

        // ---- motion ----
        if ((applyFlags & StateMotion) != 0)
        {
            Zoom.Value = file.GetFloatExact("zoom", Zoom.Eval(-1f));
            Rot.Value = file.GetFloatExact("rot", Rot.Eval(-1f));
            RotCX.Value = file.GetFloatExact("cx", RotCX.Eval(-1f));
            RotCY.Value = file.GetFloatExact("cy", RotCY.Eval(-1f));
            XPush.Value = file.GetFloatExact("dx", XPush.Eval(-1f));
            YPush.Value = file.GetFloatExact("dy", YPush.Eval(-1f));
            WarpAmount.Value = file.GetFloatExact("warp", WarpAmount.Eval(-1f));
            StretchX.Value = file.GetFloatExact("sx", StretchX.Eval(-1f));
            StretchY.Value = file.GetFloatExact("sy", StretchY.Eval(-1f));
            TexWrap = file.GetIntExact("bTexWrap", TexWrap ? 1 : 0) != 0;
            DarkenCenter = file.GetIntExact("bDarkenCenter", DarkenCenter ? 1 : 0) != 0;
            WarpAnimSpeed = file.GetFloatExact("fWarpAnimSpeed", WarpAnimSpeed);
            WarpScale.Value = file.GetFloatExact("fWarpScale", WarpScale.Eval(-1f));
            ZoomExponent.Value = file.GetFloatExact("fZoomExponent", ZoomExponent.Eval(-1f));
            OuterBorderSize.Value = file.GetFloatExact("ob_size", OuterBorderSize.Eval(-1f));
            OuterBorderR.Value = file.GetFloatExact("ob_r", OuterBorderR.Eval(-1f));
            OuterBorderG.Value = file.GetFloatExact("ob_g", OuterBorderG.Eval(-1f));
            OuterBorderB.Value = file.GetFloatExact("ob_b", OuterBorderB.Eval(-1f));
            OuterBorderA.Value = file.GetFloatExact("ob_a", OuterBorderA.Eval(-1f));
            InnerBorderSize.Value = file.GetFloatExact("ib_size", InnerBorderSize.Eval(-1f));
            InnerBorderR.Value = file.GetFloatExact("ib_r", InnerBorderR.Eval(-1f));
            InnerBorderG.Value = file.GetFloatExact("ib_g", InnerBorderG.Eval(-1f));
            InnerBorderB.Value = file.GetFloatExact("ib_b", InnerBorderB.Eval(-1f));
            InnerBorderA.Value = file.GetFloatExact("ib_a", InnerBorderA.Eval(-1f));

            // 这里存的是带 LINEFEED_CONTROL_CHAR 的原文，剥离发生在编译前
            PerFrameInit = file.ReadCodeRaw("per_frame_init_");
            PerFrameExpr = file.ReadCodeRaw("per_frame_");
            PerPixelExpr = file.ReadCodeRaw("per_pixel_");
        }

        // ---- warp shader ----
        if ((applyFlags & StateWarp) != 0)
        {
            // 版本号先落，生成函数会按它判断"本应有 shader"
            WarpPsVersion = warpPsVersionInFile;
            WarpShadersText = file.ReadCodeRaw("warp_");

            // 预设没写正文时用模板 + 老式效果生成（state.cpp:1432-1434，**不看版本号**）
            if (WarpShadersText.Length == 0)
            {
                GenerateWarpShaderText();
            }
        }

        // ---- comp shader ----
        if ((applyFlags & StateComp) != 0)
        {
            CompPsVersion = compPsVersionInFile;
            CompShadersText = file.ReadCodeRaw("comp_");

            // state.cpp:1442-1444
            if (CompShadersText.Length == 0)
            {
                GenerateCompShaderText();
            }
        }

        MaxPsVersion = Math.Max(WarpPsVersion, CompPsVersion);
        MinPsVersion = Math.Min(WarpPsVersion, CompPsVersion);

        // RecompileExpressions() 随 per-frame VM 接入一起做（M3 后半）。
        return true;
    }

    /// <summary>
    /// 用当前 decay / wrap 重新生成默认 warp shader（对应 <c>CState::GenDefaultWarpShader</c>，state.cpp:1459-1463）。
    /// </summary>
    public void GenDefaultWarpShader()
    {
        if (WarpPsVersion <= 0)
        {
            return;
        }

        GenerateWarpShaderText();
    }

    /// <summary>不看版本号地生成 warp 正文（Import 走这条，与 state.cpp:1433-1434 一致）。</summary>
    private void GenerateWarpShaderText()
    {
        var template = ShaderTemplates.DefaultWarpPixelShader;
        if (template is null)
        {
            return;
        }

        WarpShadersText = ShaderTextGenerator.GenWarpPShaderText(template, Decay.Eval(-1f), TexWrap);
    }

    /// <summary>
    /// 用当前的 gamma / video echo / hue / 颜色后处理重新生成默认 comp shader
    /// （对应 <c>CState::GenDefaultCompShader</c>，state.cpp:1464-1468）。
    /// </summary>
    public void GenDefaultCompShader()
    {
        if (CompPsVersion <= 0)
        {
            return;
        }

        GenerateCompShaderText();
    }

    /// <summary>不看版本号地生成 comp 正文（Import 走这条，与 state.cpp:1443-1444 一致）。</summary>
    private void GenerateCompShaderText()
    {
        var template = ShaderTemplates.DefaultCompPixelShader;
        if (template is null)
        {
            return;
        }

        CompShadersText = ShaderTextGenerator.GenCompPShaderText(
            template,
            GammaAdj.Eval(-1f),
            VideoEchoAlpha.Eval(-1f),
            VideoEchoZoom.Eval(-1f),
            VideoEchoOrientation,
            Shader.Eval(-1f),
            Brighten,
            Darken,
            Solarize,
            Invert);
    }

    /// <summary>对应 <c>CWave::Import</c>（state.cpp:1179-1214）。</summary>
    private void ImportWave(PresetKeyValueFile file, int index)
    {
        var wave = Waves[index];
        var prefix = $"wavecode_{index}_";

        wave.Enabled = file.GetIntExact(prefix + "enabled", wave.Enabled);
        wave.Samples = file.GetIntExact(prefix + "samples", wave.Samples);
        wave.Sep = file.GetIntExact(prefix + "sep", wave.Sep);
        wave.Spectrum = file.GetIntExact(prefix + "bSpectrum", wave.Spectrum);
        wave.UseDots = file.GetIntExact(prefix + "bUseDots", wave.UseDots);
        wave.DrawThick = file.GetIntExact(prefix + "bDrawThick", wave.DrawThick);
        wave.Additive = file.GetIntExact(prefix + "bAdditive", wave.Additive);
        wave.Scaling = file.GetFloatExact(prefix + "scaling", wave.Scaling);
        wave.Smoothing = file.GetFloatExact(prefix + "smoothing", wave.Smoothing);
        wave.R = file.GetFloatExact(prefix + "r", wave.R);
        wave.G = file.GetFloatExact(prefix + "g", wave.G);
        wave.B = file.GetFloatExact(prefix + "b", wave.B);
        wave.A = file.GetFloatExact(prefix + "a", wave.A);

        wave.InitCode = file.ReadCodeRaw($"wave_{index}_init");
        wave.PerFrameCode = file.ReadCodeRaw($"wave_{index}_per_frame");
        wave.PerPointCode = file.ReadCodeRaw($"wave_{index}_per_point");
    }

    /// <summary>对应 <c>CShape::Import</c>（state.cpp:1216-1261）。</summary>
    private void ImportShape(PresetKeyValueFile file, int index)
    {
        var shape = Shapes[index];
        var prefix = $"shapecode_{index}_";

        shape.Enabled = file.GetIntExact(prefix + "enabled", shape.Enabled);
        shape.Sides = file.GetIntExact(prefix + "sides", shape.Sides);
        shape.Additive = file.GetIntExact(prefix + "additive", shape.Additive);
        shape.ThickOutline = file.GetIntExact(prefix + "thickOutline", shape.ThickOutline);
        shape.Textured = file.GetIntExact(prefix + "textured", shape.Textured);
        shape.Instances = file.GetIntExact(prefix + "num_inst", shape.Instances);
        shape.X = file.GetFloatExact(prefix + "x", shape.X);
        shape.Y = file.GetFloatExact(prefix + "y", shape.Y);
        shape.Rad = file.GetFloatExact(prefix + "rad", shape.Rad);
        shape.Ang = file.GetFloatExact(prefix + "ang", shape.Ang);
        shape.TexAng = file.GetFloatExact(prefix + "tex_ang", shape.TexAng);
        shape.TexZoom = file.GetFloatExact(prefix + "tex_zoom", shape.TexZoom);
        shape.R = file.GetFloatExact(prefix + "r", shape.R);
        shape.G = file.GetFloatExact(prefix + "g", shape.G);
        shape.B = file.GetFloatExact(prefix + "b", shape.B);
        shape.A = file.GetFloatExact(prefix + "a", shape.A);
        shape.R2 = file.GetFloatExact(prefix + "r2", shape.R2);
        shape.G2 = file.GetFloatExact(prefix + "g2", shape.G2);
        shape.B2 = file.GetFloatExact(prefix + "b2", shape.B2);
        shape.A2 = file.GetFloatExact(prefix + "a2", shape.A2);
        shape.BorderR = file.GetFloatExact(prefix + "border_r", shape.BorderR);
        shape.BorderG = file.GetFloatExact(prefix + "border_g", shape.BorderG);
        shape.BorderB = file.GetFloatExact(prefix + "border_b", shape.BorderB);
        shape.BorderA = file.GetFloatExact(prefix + "border_a", shape.BorderA);

        shape.InitCode = file.ReadCodeRaw($"shape_{index}_init");
        shape.PerFrameCode = file.ReadCodeRaw($"shape_{index}_per_frame");
    }

    /// <summary>
    /// 整块拷贝，对应原版 <c>memcpy(this, pOldState, sizeof(CState))</c>。
    /// </summary>
    public void CopyFrom(PresetState other)
    {
        Description = other.Description;
        MinPsVersion = other.MinPsVersion;
        MaxPsVersion = other.MaxPsVersion;
        WarpPsVersion = other.WarpPsVersion;
        CompPsVersion = other.CompPsVersion;
        Rating = other.Rating;
        PresetStartTime = other.PresetStartTime;

        Decay.CopyFrom(other.Decay);
        GammaAdj.CopyFrom(other.GammaAdj);
        VideoEchoZoom.CopyFrom(other.VideoEchoZoom);
        VideoEchoAlpha.CopyFrom(other.VideoEchoAlpha);
        VideoEchoAlphaOld = other.VideoEchoAlphaOld;
        VideoEchoOrientation = other.VideoEchoOrientation;
        VideoEchoOrientationOld = other.VideoEchoOrientationOld;
        Shader.CopyFrom(other.Shader);

        RedBlueStereo = other.RedBlueStereo;
        Brighten = other.Brighten;
        Darken = other.Darken;
        TexWrap = other.TexWrap;
        DarkenCenter = other.DarkenCenter;
        Solarize = other.Solarize;
        Invert = other.Invert;

        Blur1Min.CopyFrom(other.Blur1Min);
        Blur2Min.CopyFrom(other.Blur2Min);
        Blur3Min.CopyFrom(other.Blur3Min);
        Blur1Max.CopyFrom(other.Blur1Max);
        Blur2Max.CopyFrom(other.Blur2Max);
        Blur3Max.CopyFrom(other.Blur3Max);
        Blur1EdgeDarken.CopyFrom(other.Blur1EdgeDarken);

        WaveMode = other.WaveMode;
        OldWaveMode = other.OldWaveMode;
        AdditiveWaves = other.AdditiveWaves;
        WaveAlpha.CopyFrom(other.WaveAlpha);
        WaveScale.CopyFrom(other.WaveScale);
        WaveSmoothing.CopyFrom(other.WaveSmoothing);
        WaveDots = other.WaveDots;
        WaveThick = other.WaveThick;
        WaveParam.CopyFrom(other.WaveParam);
        ModWaveAlphaByVolume = other.ModWaveAlphaByVolume;
        ModWaveAlphaStart.CopyFrom(other.ModWaveAlphaStart);
        ModWaveAlphaEnd.CopyFrom(other.ModWaveAlphaEnd);
        MaximizeWaveColor = other.MaximizeWaveColor;
        WaveR.CopyFrom(other.WaveR);
        WaveG.CopyFrom(other.WaveG);
        WaveB.CopyFrom(other.WaveB);
        WaveX.CopyFrom(other.WaveX);
        WaveY.CopyFrom(other.WaveY);

        for (var i = 0; i < MaxCustomWaves; i++)
        {
            Waves[i].CopyFrom(other.Waves[i]);
        }

        for (var i = 0; i < MaxCustomShapes; i++)
        {
            Shapes[i].CopyFrom(other.Shapes[i]);
        }

        WarpAnimSpeed = other.WarpAnimSpeed;
        WarpScale.CopyFrom(other.WarpScale);
        ZoomExponent.CopyFrom(other.ZoomExponent);
        Zoom.CopyFrom(other.Zoom);
        Rot.CopyFrom(other.Rot);
        RotCX.CopyFrom(other.RotCX);
        RotCY.CopyFrom(other.RotCY);
        XPush.CopyFrom(other.XPush);
        YPush.CopyFrom(other.YPush);
        WarpAmount.CopyFrom(other.WarpAmount);
        StretchX.CopyFrom(other.StretchX);
        StretchY.CopyFrom(other.StretchY);

        OuterBorderSize.CopyFrom(other.OuterBorderSize);
        OuterBorderR.CopyFrom(other.OuterBorderR);
        OuterBorderG.CopyFrom(other.OuterBorderG);
        OuterBorderB.CopyFrom(other.OuterBorderB);
        OuterBorderA.CopyFrom(other.OuterBorderA);
        InnerBorderSize.CopyFrom(other.InnerBorderSize);
        InnerBorderR.CopyFrom(other.InnerBorderR);
        InnerBorderG.CopyFrom(other.InnerBorderG);
        InnerBorderB.CopyFrom(other.InnerBorderB);
        InnerBorderA.CopyFrom(other.InnerBorderA);

        MvX.CopyFrom(other.MvX);
        MvY.CopyFrom(other.MvY);
        MvDX.CopyFrom(other.MvDX);
        MvDY.CopyFrom(other.MvDY);
        MvL.CopyFrom(other.MvL);
        MvR.CopyFrom(other.MvR);
        MvG.CopyFrom(other.MvG);
        MvB.CopyFrom(other.MvB);
        MvA.CopyFrom(other.MvA);

        PerFrameInit = other.PerFrameInit;
        PerFrameExpr = other.PerFrameExpr;
        PerPixelExpr = other.PerPixelExpr;
        WarpShadersText = other.WarpShadersText;
        CompShadersText = other.CompShadersText;

        IsBlending = other.IsBlending;
        BlendStartTime = other.BlendStartTime;
        BlendDuration = other.BlendDuration;
        BlendProgress = other.BlendProgress;

        RandPreset = other.RandPreset;
        Array.Copy(other.Xlate, Xlate, Xlate.Length);
        Array.Copy(other.RotBase, RotBase, RotBase.Length);
        Array.Copy(other.RotSpeed, RotSpeed, RotSpeed.Length);
    }

    /// <summary>
    /// 对应 <c>CPlugin::LoadPerFrameEvallibVars</c>（milkdropfs.cpp:460-554）：
    /// 把状态量灌进 per-frame VM，供 init / per-frame 代码读取。
    /// </summary>
    /// <remarks>
    /// 影响像素运动的量用 <c>eval(-1)</c>（不混合），混合在执行完 per-vertex 之后再补；
    /// 不影响运动的量用 <c>eval(now)</c>，这样混合期间两个状态看到同一个插值结果。
    /// </remarks>
    public void SyncPerFrameVars(in PerFrameContext ctx)
    {
        var vars = PerFrame;

        // 只读量（时间/帧率/声音/帧号/进度）
        vars.SyncReadOnly(ctx.Time, ctx.Fps, ctx.MySound, ctx.Frame, ctx.Progress);

        // ---- 1. 影响像素运动：eval(-1) ----
        vars["zoom"].Value = Zoom.Eval(-1f);
        vars["zoomexp"].Value = ZoomExponent.Eval(-1f);
        vars["rot"].Value = Rot.Eval(-1f);
        vars["warp"].Value = WarpAmount.Eval(-1f);
        vars["cx"].Value = RotCX.Eval(-1f);
        vars["cy"].Value = RotCY.Eval(-1f);
        vars["dx"].Value = XPush.Eval(-1f);
        vars["dy"].Value = YPush.Eval(-1f);
        vars["sx"].Value = StretchX.Eval(-1f);
        vars["sy"].Value = StretchY.Eval(-1f);

        // ---- 2. 不影响像素运动：eval(now) ----
        var t = ctx.Time;
        vars["decay"].Value = Decay.Eval(t);
        vars["wave_a"].Value = WaveAlpha.Eval(t);
        vars["wave_r"].Value = WaveR.Eval(t);
        vars["wave_g"].Value = WaveG.Eval(t);
        vars["wave_b"].Value = WaveB.Eval(t);
        vars["wave_x"].Value = WaveX.Eval(t);
        vars["wave_y"].Value = WaveY.Eval(t);
        vars["wave_mystery"].Value = WaveParam.Eval(t);
        vars["wave_mode"].Value = WaveMode;

        vars["ob_size"].Value = OuterBorderSize.Eval(t);
        vars["ob_r"].Value = OuterBorderR.Eval(t);
        vars["ob_g"].Value = OuterBorderG.Eval(t);
        vars["ob_b"].Value = OuterBorderB.Eval(t);
        vars["ob_a"].Value = OuterBorderA.Eval(t);
        vars["ib_size"].Value = InnerBorderSize.Eval(t);
        vars["ib_r"].Value = InnerBorderR.Eval(t);
        vars["ib_g"].Value = InnerBorderG.Eval(t);
        vars["ib_b"].Value = InnerBorderB.Eval(t);
        vars["ib_a"].Value = InnerBorderA.Eval(t);

        vars["mv_x"].Value = MvX.Eval(t);
        vars["mv_y"].Value = MvY.Eval(t);
        vars["mv_dx"].Value = MvDX.Eval(t);
        vars["mv_dy"].Value = MvDY.Eval(t);
        vars["mv_l"].Value = MvL.Eval(t);
        vars["mv_r"].Value = MvR.Eval(t);
        vars["mv_g"].Value = MvG.Eval(t);
        vars["mv_b"].Value = MvB.Eval(t);
        vars["mv_a"].Value = MvA.Eval(t);

        vars["echo_zoom"].Value = VideoEchoZoom.Eval(t);
        vars["echo_alpha"].Value = VideoEchoAlpha.Eval(t);
        vars["echo_orient"].Value = VideoEchoOrientation;

        // v1.04 新增
        vars["wave_usedots"].Value = WaveDots ? 1 : 0;
        vars["wave_thick"].Value = WaveThick ? 1 : 0;
        vars["wave_additive"].Value = AdditiveWaves ? 1 : 0;
        vars["wave_brighten"].Value = MaximizeWaveColor ? 1 : 0;
        vars["darken_center"].Value = DarkenCenter ? 1 : 0;
        vars["gamma"].Value = GammaAdj.Eval(t);
        vars["wrap"].Value = TexWrap ? 1 : 0;
        vars["invert"].Value = Invert ? 1 : 0;
        vars["brighten"].Value = Brighten ? 1 : 0;
        vars["darken"].Value = Darken ? 1 : 0;
        vars["solarize"].Value = Solarize ? 1 : 0;

        vars["meshx"].Value = ctx.GridX;
        vars["meshy"].Value = ctx.GridY;
        vars["pixelsx"].Value = ctx.Width;
        vars["pixelsy"].Value = ctx.Height;
        vars["aspectx"].Value = ctx.InvAspectX;
        vars["aspecty"].Value = ctx.InvAspectY;

        // v2.0 新增
        vars["blur1_min"].Value = Blur1Min.Eval(t);
        vars["blur2_min"].Value = Blur2Min.Eval(t);
        vars["blur3_min"].Value = Blur3Min.Eval(t);
        vars["blur1_max"].Value = Blur1Max.Eval(t);
        vars["blur2_max"].Value = Blur2Max.Eval(t);
        vars["blur3_max"].Value = Blur3Max.Eval(t);
        vars["blur1_edge_darken"].Value = Blur1EdgeDarken.Eval(t);
    }

    /// <summary>
    /// 对应 <c>CState::RecompileExpressions</c>（state.cpp:1552-1790）。
    /// </summary>
    /// <param name="flags">要重编译的部分。</param>
    /// <param name="reInit">是否重跑 <c>per_frame_init</c>（会顺带重编译波形/图形代码）。</param>
    /// <param name="initContext">init 执行前的变量同步上下文；<c>null</c> 表示沿用当前变量值。</param>
    /// <remarks>
    /// 顺序与原版一致（state.cpp:1662-1870）：per_frame_init → per-frame → per-vertex →
    /// 4 个波形的 init/per-frame/per-point → 4 个图形的 init/per-frame。
    /// 各组代码相互独立，某一段编译失败只记错误，不影响后面继续编译。
    /// 波形/图形的 init 代码需要 <paramref name="initContext"/> 才能执行（原版用插件的
    /// <c>LoadCustomWavePerFrameEvallibVars</c> 灌变量）；传 <c>null</c> 时跳过执行。
    /// </remarks>
    public void RecompileExpressions(int flags = RecompilePresetCode, bool reInit = true, PerFrameContext? initContext = null)
    {
        LastError = null;
        _errors.Clear();

        // 重跑 init 会改 q1..q32，所以波形/图形代码也必须跟着重编译
        if ((flags & RecompilePresetCode) != 0 && reInit)
        {
            flags |= RecompileWaveCode | RecompileShapeCode;
        }

        if ((flags & RecompilePresetCode) != 0)
        {
            PerFrameCode = null;
            PerVertex.Code = null;
        }

        // QUICK FIX：只有空白和换行控制符的代码串直接清空（原版注释说这会让 compileCode 报错）
        if (IsBlankCode(PerFrameExpr)) PerFrameExpr = "";
        if (IsBlankCode(PerPixelExpr)) PerPixelExpr = "";
        if (IsBlankCode(PerFrameInit)) PerFrameInit = "";

        foreach (var wave in Waves)
        {
            if (IsBlankCode(wave.InitCode)) wave.InitCode = "";
            if (IsBlankCode(wave.PerFrameCode)) wave.PerFrameCode = "";
            if (IsBlankCode(wave.PerPointCode)) wave.PerPointCode = "";
        }

        foreach (var shape in Shapes)
        {
            if (IsBlankCode(shape.InitCode)) shape.InitCode = "";
            if (IsBlankCode(shape.PerFrameCode)) shape.PerFrameCode = "";
        }

        if ((flags & RecompilePresetCode) != 0)
        {
            // 1. 编译并执行 per_frame_init
            var initCode = EelPreprocessor.StripLinefeedsAndComments(PerFrameInit);
            if (initCode.Length > 0 && reInit)
            {
                // 原版在 bReInit 时重新注册内置变量（值归零），避免旧预设残留
                PerFrame.ResetValues();

                var context = initContext;
                Action? sync = context is { } c ? () => SyncPerFrameVars(c) : null;

                if (!PerFrame.RunInitCode(initCode, sync, out var initError))
                {
                    // 失败时 q/monitor 归零由 RunInitCode 保证
                    AddError(initError?.ToString() ?? "per_frame_init 编译失败");
                }
            }

            // 2. 编译 per-frame 代码
            var body = EelPreprocessor.StripLinefeedsAndComments(PerFrameExpr);
            if (body.Length > 0)
            {
                PerFrameCode = PerFrame.Compile(body);
                if (PerFrameCode is null)
                {
                    AddError(PerFrame.Vm.LastError?.ToString() ?? "per_frame 编译失败");
                }
            }

            // 3. 编译 per-vertex 代码（原版字段名是 per_pixel，但编进 m_pv_eel，state.cpp:1709-1719）
            if (reInit)
            {
                // 对应 bReInit 时重建 m_pv_eel 的变量（q1..q32 也一起归零），
                // 与有没有代码无关 —— 否则上一个预设的 q 会漏进新预设。
                PerVertex.ResetValues();
            }

            var perVertexBody = EelPreprocessor.StripLinefeedsAndComments(PerPixelExpr);
            if (perVertexBody.Length > 0)
            {
                PerVertex.Code = PerVertex.Compile(perVertexBody);
                if (PerVertex.Code is null)
                {
                    AddError("per_vertex 编译失败: " + (PerVertex.Vm.LastError?.ToString() ?? "unknown"));
                }
            }
        }

        if ((flags & RecompileWaveCode) != 0)
        {
            for (var i = 0; i < MaxCustomWaves; i++)
            {
                CompileWave(i, reInit, initContext);
            }
        }

        if ((flags & RecompileShapeCode) != 0)
        {
            for (var i = 0; i < MaxCustomShapes; i++)
            {
                CompileShape(i, reInit, initContext);
            }
        }
    }

    /// <summary>
    /// 单个波形的三段代码（state.cpp:1726-1792）。
    /// </summary>
    private void CompileWave(int index, bool reInit, PerFrameContext? initContext)
    {
        var wave = Waves[index];
        var exprs = WaveExprs[index];
        exprs.ResetPrograms();

        // 1. init：编译 + 执行 + 回读 t1..t8
        var initCode = EelPreprocessor.StripLinefeedsAndComments(wave.InitCode);
        if (initCode.Length > 0 && reInit && initContext is { } ctx)
        {
            if (!exprs.CompileInit(initCode, ctx, PerFrame, wave, out var error))
            {
                AddError($"wave{index + 1} init 编译失败: {error}");
            }
        }

        // 2. per-frame
        var perFrameCode = EelPreprocessor.StripLinefeedsAndComments(wave.PerFrameCode);
        if (perFrameCode.Length > 0 && !exprs.CompilePerFrame(perFrameCode, out var pfError))
        {
            AddError($"wave{index + 1} per_frame 编译失败: {pfError}");
        }

        // 3. per-point
        var perPointCode = EelPreprocessor.StripLinefeedsAndComments(wave.PerPointCode);
        if (perPointCode.Length > 0 && !exprs.CompilePerPoint(perPointCode, out var ppError))
        {
            AddError($"wave{index + 1} per_point 编译失败: {ppError}");
        }
    }

    /// <summary>
    /// 单个图形的两段代码（state.cpp:1795-1869）。
    /// </summary>
    private void CompileShape(int index, bool reInit, PerFrameContext? initContext)
    {
        var shape = Shapes[index];
        var exprs = ShapeExprs[index];
        exprs.ResetProgram();

        var initCode = EelPreprocessor.StripLinefeedsAndComments(shape.InitCode);
        if (initCode.Length > 0 && reInit && initContext is { } ctx)
        {
            if (!exprs.CompileInit(initCode, ctx, PerFrame, shape, out var error))
            {
                AddError($"shape{index + 1} init 编译失败: {error}");
            }
        }

        var perFrameCode = EelPreprocessor.StripLinefeedsAndComments(shape.PerFrameCode);
        if (perFrameCode.Length > 0 && !exprs.CompilePerFrame(perFrameCode, out var pfError))
        {
            AddError($"shape{index + 1} per_frame 编译失败: {pfError}");
        }
    }

    private void AddError(string message)
    {
        _errors.Add(message);
        LastError ??= message;
    }

    /// <summary>
    /// 执行一帧的 per-frame 代码（对应 <c>RunPerFrameEquations</c> 里 rep==0/1 的同步 + 执行部分，
    /// milkdropfs.cpp:624-631、1700 附近）。
    /// </summary>
    /// <param name="ctx">本帧的时间/声音等只读量。</param>
    /// <param name="oldState">混合中的"上一个预设"状态；只有 <see cref="IsBlending"/> 时才会执行它。</param>
    /// <remarks>
    /// 重点：结果**留在 VM 变量里**，不回写 <c>m_fZoom</c> 等字段 —— 原版渲染直接用
    /// <c>*pState->var_pf_zoom</c>（milkdropfs.cpp:1728），所以每帧都从状态值重新灌入，
    /// 天然不会跨帧累积。需要跨帧保存的量必须写进 <c>q1..q32</c>。
    /// </remarks>
    public void RunPerFrame(in PerFrameContext ctx, PresetState? oldState = null)
    {
        SyncPerFrameVars(ctx);
        PerFrameCode?.Execute();

        // per-vertex VM 的只读量每帧只灌一次（milkdropfs.cpp:638-655）
        PerVertex.SyncOncePerFrame(ctx);

        if (oldState is not null && IsBlending)
        {
            oldState.SyncPerFrameVars(ctx);
            oldState.PerFrameCode?.Execute();
            oldState.PerVertex.SyncOncePerFrame(ctx);
        }
    }

    /// <summary>
    /// 推进混合进度（对应 <c>UpdateTime</c> 里的 <c>m_fBlendProgress</c> 更新，milkdropfs.cpp:815-822）。
    /// </summary>
    /// <param name="now">当前时间（秒）。</param>
    /// <remarks>
    /// 进度可以短暂超过 1（原版也不夹），超过后就把 <see cref="IsBlending"/> 关掉。
    /// 时长为 0 时视为"硬切"：直接结束混合。
    /// </remarks>
    public void UpdateBlend(float now)
    {
        if (!IsBlending)
        {
            return;
        }

        if (BlendDuration <= 0.0001f)
        {
            BlendProgress = 1f;
            IsBlending = false;
            return;
        }

        BlendProgress = (now - BlendStartTime) / BlendDuration;
        if (BlendProgress > 1f)
        {
            IsBlending = false;
        }
    }

    /// <summary>
    /// 对应 <c>CState::StartBlendFrom</c>（state.cpp:704-849）：记录"从哪个状态混合过来"。
    /// </summary>
    /// <remarks>
    /// bool/int/字符串不算混合量，只把三个 <c>*Old</c> 字段记下来；
    /// 运动矢量的 X/Y 数量是整数，界面上的做法是"从透明态淡入时别做位移插值"，
    /// 因此下面两个特例分支会**同时改写 from 与 this**（原版就是这么写的）。
    /// </remarks>
    public void StartBlendFrom(PresetState from, float animTime, float timespan)
    {
        // 瞬时改变的字段
        VideoEchoAlphaOld = from.VideoEchoAlpha.Eval(-1f);
        VideoEchoOrientationOld = from.VideoEchoOrientation;
        OldWaveMode = from.WaveMode;

        IsBlending = true;
        BlendStartTime = animTime;
        BlendDuration = timespan;

        // 需要随时间插值的量
        GammaAdj.StartBlendFrom(from.GammaAdj, animTime, timespan);
        VideoEchoZoom.StartBlendFrom(from.VideoEchoZoom, animTime, timespan);
        VideoEchoAlpha.StartBlendFrom(from.VideoEchoAlpha, animTime, timespan);
        Decay.StartBlendFrom(from.Decay, animTime, timespan);
        WaveAlpha.StartBlendFrom(from.WaveAlpha, animTime, timespan);
        WaveScale.StartBlendFrom(from.WaveScale, animTime, timespan);
        WaveSmoothing.StartBlendFrom(from.WaveSmoothing, animTime, timespan);
        WaveParam.StartBlendFrom(from.WaveParam, animTime, timespan);
        WarpScale.StartBlendFrom(from.WarpScale, animTime, timespan);
        ZoomExponent.StartBlendFrom(from.ZoomExponent, animTime, timespan);
        Shader.StartBlendFrom(from.Shader, animTime, timespan);
        ModWaveAlphaStart.StartBlendFrom(from.ModWaveAlphaStart, animTime, timespan);
        ModWaveAlphaEnd.StartBlendFrom(from.ModWaveAlphaEnd, animTime, timespan);

        Zoom.StartBlendFrom(from.Zoom, animTime, timespan);
        Rot.StartBlendFrom(from.Rot, animTime, timespan);
        RotCX.StartBlendFrom(from.RotCX, animTime, timespan);
        RotCY.StartBlendFrom(from.RotCY, animTime, timespan);
        XPush.StartBlendFrom(from.XPush, animTime, timespan);
        YPush.StartBlendFrom(from.YPush, animTime, timespan);
        WarpAmount.StartBlendFrom(from.WarpAmount, animTime, timespan);
        StretchX.StartBlendFrom(from.StretchX, animTime, timespan);
        StretchY.StartBlendFrom(from.StretchY, animTime, timespan);
        WaveR.StartBlendFrom(from.WaveR, animTime, timespan);
        WaveG.StartBlendFrom(from.WaveG, animTime, timespan);
        WaveB.StartBlendFrom(from.WaveB, animTime, timespan);
        WaveX.StartBlendFrom(from.WaveX, animTime, timespan);
        WaveY.StartBlendFrom(from.WaveY, animTime, timespan);

        OuterBorderSize.StartBlendFrom(from.OuterBorderSize, animTime, timespan);
        OuterBorderR.StartBlendFrom(from.OuterBorderR, animTime, timespan);
        OuterBorderG.StartBlendFrom(from.OuterBorderG, animTime, timespan);
        OuterBorderB.StartBlendFrom(from.OuterBorderB, animTime, timespan);
        OuterBorderA.StartBlendFrom(from.OuterBorderA, animTime, timespan);
        InnerBorderSize.StartBlendFrom(from.InnerBorderSize, animTime, timespan);
        InnerBorderR.StartBlendFrom(from.InnerBorderR, animTime, timespan);
        InnerBorderG.StartBlendFrom(from.InnerBorderG, animTime, timespan);
        InnerBorderB.StartBlendFrom(from.InnerBorderB, animTime, timespan);
        InnerBorderA.StartBlendFrom(from.InnerBorderA, animTime, timespan);

        MvX.StartBlendFrom(from.MvX, animTime, timespan);
        MvY.StartBlendFrom(from.MvY, animTime, timespan);
        MvDX.StartBlendFrom(from.MvDX, animTime, timespan);
        MvDY.StartBlendFrom(from.MvDY, animTime, timespan);
        MvL.StartBlendFrom(from.MvL, animTime, timespan);
        MvR.StartBlendFrom(from.MvR, animTime, timespan);
        MvG.StartBlendFrom(from.MvG, animTime, timespan);
        MvB.StartBlendFrom(from.MvB, animTime, timespan);
        MvA.StartBlendFrom(from.MvA, animTime, timespan);

        Blur1Min.StartBlendFrom(from.Blur1Min, animTime, timespan);
        Blur2Min.StartBlendFrom(from.Blur2Min, animTime, timespan);
        Blur3Min.StartBlendFrom(from.Blur3Min, animTime, timespan);
        Blur1Max.StartBlendFrom(from.Blur1Max, animTime, timespan);
        Blur2Max.StartBlendFrom(from.Blur2Max, animTime, timespan);
        Blur3Max.StartBlendFrom(from.Blur3Max, animTime, timespan);
        Blur1EdgeDarken.StartBlendFrom(from.Blur1EdgeDarken, animTime, timespan);

        // 运动矢量从透明态淡入/淡出：不做数量插值，直接把起点对齐到当前值
        var oldStateTransparent = from.MvA.Eval(-1f) < 0.001f;
        var newStateTransparent = MvA.Eval(-1f) < 0.001f;

        if (!oldStateTransparent && newStateTransparent)
        {
            from.MvX.Value = MvX.Eval(animTime);
            from.MvY.Value = MvY.Eval(animTime);
            from.MvDX.Value = MvDX.Eval(animTime);
            from.MvDY.Value = MvDY.Eval(animTime);
            from.MvL.Value = MvL.Eval(animTime);
            from.MvR.Value = MvR.Eval(animTime);
            from.MvG.Value = MvG.Eval(animTime);
            from.MvB.Value = MvB.Eval(animTime);
        }

        if (newStateTransparent && !oldStateTransparent)
        {
            MvX.Value = from.MvX.Eval(animTime);
            MvY.Value = from.MvY.Eval(animTime);
            MvDX.Value = from.MvDX.Eval(animTime);
            MvDY.Value = from.MvDY.Eval(animTime);
            MvL.Value = from.MvL.Eval(animTime);
            MvR.Value = from.MvR.Eval(animTime);
            MvG.Value = from.MvG.Eval(animTime);
            MvB.Value = from.MvB.Eval(animTime);
        }
    }

    /// <summary>代码串是否只有空白与换行控制符。</summary>
    private static bool IsBlankCode(string code)
        => code.Length > 0 && code.AsSpan().IndexOfAnyExcept(' ', EelPreprocessor.LinefeedControlChar) < 0;

    /// <summary><c>FRAND</c>（state.cpp:44）。</summary>
    private float Frand() => _rng.Next(7381) / 7380.0f;
}
