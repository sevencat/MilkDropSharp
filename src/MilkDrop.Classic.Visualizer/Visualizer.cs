using System.Numerics;
using System.Runtime.InteropServices;
using MilkDrop.Classic.Render;
using MilkDrop.Classic.Render.D3D9;
using MilkDrop.Classic.Visualizer.Audio;
using MilkDrop.Classic.Visualizer.Noise;
using MilkDrop.Classic.Visualizer.Preset;
using MilkDrop.Classic.Visualizer.Sound;
using MilkDrop.Classic.Visualizer.Sprites;
using MilkDrop.Eval;

namespace MilkDrop.Classic.Visualizer;

public enum VisualizerMode
{
    /// <summary>M0 测试图案（纯固定功能）。</summary>
    FanPattern,

    /// <summary>Spike S1：离屏反馈链 + 自定义像素着色器 + 常量绑定。</summary>
    FeedbackSpike,

    /// <summary>M4：跑真实预设（per-frame 代码 → warp 网格 → 反馈）。</summary>
    Preset,
}

/// <summary>
/// 可视化器主循环骨架。当前有两种模式：
/// <list type="bullet">
/// <item><see cref="VisualizerMode.FanPattern"/>：M0 验证固定功能管线；</item>
/// <item><see cref="VisualizerMode.FeedbackSpike"/>：Spike S1，验证"离屏 RT 乒乓 + warped 采样 + <c>: register(cN)</c> 常量绑定"。</item>
/// </list>
/// 后续增量（按 cbplan.md §7）：M1 音频分析 → M3 预设运行时 → M4 完整 warp/blur/合成 → M5 shader → M6 wave/shape/sprite。
/// </summary>
public sealed class VisualizerEngine : IDisposable
{
    private const string PassthroughVertexShader = """
        struct VS_OUT
        {
            float4 pos : POSITION;
            float2 uv  : TEXCOORD0;
        };

        VS_OUT VS(float4 pos : POSITION, float2 uv : TEXCOORD0)
        {
            VS_OUT o;
            o.pos = pos;
            o.uv  = uv;
            return o;
        }
        """;

    /// <summary>
    /// 反馈 warp 着色器（ps_2_0）。
    /// 常量用显式 <c>: register(cN)</c> 注解 —— 这正是替代 D3DXCONSTANTTABLE 的方案（cbplan §2.2 / §4.2）。
    /// 如果常量绑定失效（<c>_tint</c> 读到 0），画面会直接变全黑，因此这个 spike 是"可见即通过"。
    /// </summary>
    private const string FeedbackPixelShader = """
        float4 _tint : register(c0);   // rgb = per-frame decay
        float4 _warp : register(c1);   // xy = drift, z = swirl, w = zoom
        sampler2D tex0 : register(s0);

        float4 PS(float2 uv : TEXCOORD0) : COLOR
        {
            float2 d   = uv - 0.5;
            float  ang = _warp.z * length(d);
            float  s   = sin(ang);
            float  c   = cos(ang);
            float2 r   = float2(d.x * c - d.y * s, d.x * s + d.y * c);
            float2 uv2 = 0.5 + r * _warp.w + _warp.xy;
            return tex2D(tex0, uv2) * _tint;
        }
        """;

    private readonly IRenderBackend _backend;
    private readonly IAudioSource _audioSource;
    private readonly TimeKeeper _timeKeeper = new();
    private readonly SoundAnalyzer _analyzer = new();
    private readonly AudioRingBuffer _audioBuffer = new();
    private readonly float[] _audioScratch = new float[AudioRingBuffer.SampleCount * 2];
    private readonly sbyte[] _waveL = new sbyte[SoundAnalysis.WaveformSlots];
    private readonly sbyte[] _waveR = new sbyte[SoundAnalysis.WaveformSlots];
    private readonly MilkVertex[] _fan = new MilkVertex[3 * 24];
    private readonly MilkVertex[] _blob = new MilkVertex[3 * 20];

    private ITexture? _feedbackA;
    private ITexture? _feedbackB;
    private int _renderTargetWidth;
    private int _renderTargetHeight;
    private IShaderProgram? _warpShader;
    private IShaderProgram? _passthroughVertexShader;
    private bool _spikeAvailable;

    private PresetState? _preset;
    private PresetState? _blendSource;
    private MilkVertex[] _warpMesh = [];
    private readonly MilkVertex[] _compMesh = new MilkVertex[CompMesh.VertexCount];

    // 自定义波形顶点缓冲（对应 milkdropfs.cpp 里 DrawCustomWaves 的栈上 v[1024]/v2[2048]）。
    private readonly MilkVertex[] _waveVertices = new MilkVertex[WaveGeometry.MaxSamples];
    private readonly MilkVertex[] _waveSmoothed = new MilkVertex[WaveGeometry.MaxVertices];
    private readonly MilkVertex[] _waveThick = new MilkVertex[WaveGeometry.MaxVertices];

    // 内置波形（DrawWave）：最多 576 点，mode 7 会拼成两段。
    private readonly MilkVertex[] _builtInWave = new MilkVertex[1200];
    private readonly MilkVertex[] _builtInWaveSmoothed = new MilkVertex[2400];

    // 自定义图形（DrawCustomShapes）：最多 100 边 + 中心 + 收尾。
    private readonly MilkVertex[] _shapeVerts = new MilkVertex[512];
    private readonly MilkVertex[] _shapeBorder = new MilkVertex[512];

    // ---- 预设 shader ----
    private readonly Random _shaderRandom = new();
    private readonly Vector4 _randStart;
    private PresetState? _shaderPreset;
    private bool _shaderCompileAttempted;
    private IShaderProgram? _presetVertexShaderV2;
    private IShaderProgram? _presetVertexShaderV3;
    private IShaderProgram? _presetWarpShader;
    private IShaderProgram? _presetCompShader;
    private ITexture? _noiseLq;
    private ITexture? _noiseLqLite;
    private ITexture? _noiseMq;
    private ITexture? _noiseHq;
    private ITexture? _noiseVolLq;
    private ITexture? _noiseVolHq;
    private bool _noiseAttempted;
    private Vector4 _randFrame;

    // ---- 预设自己声明的纹理 sampler（textures/ 里的图片）----
    private readonly PresetTextureCache _presetTextures;

    // ---- 兼容层：频谱纹理（sampler_fft，给 get_fft/get_fft_hz 用）----
    private const int FftTextureWidth = 512;
    private readonly byte[] _fftPixels = new byte[FftTextureWidth * 4];
    private ITexture? _fftTexture;
    private float _fftPeak = 1f;
    private bool _fftUnavailable;

    // ---- blur 链（原版 m_lpBlur[6]：blur1..3 各占一对水平/垂直 RT）----
    private IShaderProgram? _blurVertexShader;
    private IShaderProgram? _blurHorizontalShader;
    private IShaderProgram? _blurVerticalShader;
    private readonly ITexture?[] _blurTargets = new ITexture?[6];
    private bool _blurCompileAttempted;
    private int _blurTargetWidth;
    private int _blurTargetHeight;
    private int _blurLevelsUsed;

    // ---- sprite（texmgr + DrawSprites/DrawUserSprites）----
    private readonly SpriteManager _sprites;
    private readonly MilkVertex[] _spriteVertices = new MilkVertex[SpriteGeometry.VertexCount];
    private readonly MilkVertex[] _spriteBurnVertices = new MilkVertex[SpriteGeometry.VertexCount];
    private readonly MilkVertex[] _centerVertices = new MilkVertex[6];
    private readonly MilkVertex[] _borderVertices = new MilkVertex[4];
    private readonly Vector2[] _borderClip = new Vector2[4];

    // ---- 固定管线合成（ShowToUser_NoShaders）----
    private readonly MilkVertex[] _compQuad = new MilkVertex[4];
    private readonly float[] _gammaWeights = new float[CompFallback.MaxGammaRedraws];

    // motion vector 场（每行最多 64 段 = 128 个顶点）
    private readonly MilkVertex[] _motionVectorVertices = new MilkVertex[MotionVectors.MaxColumns * 2];
    private float _compX0;
    private float _compY0;
    private float _compX1;
    private float _compY1;

    public string? BlurError { get; private set; }

    private float _aspectX = 1f;
    private float _aspectY = 1f;
    private float _invAspectX = 1f;
    private float _invAspectY = 1f;

    private float _time;

    public VisualizerEngine(IRenderBackend backend, IAudioSource audioSource)
    {
        _backend = backend;
        _audioSource = audioSource;
        _audioSource.Start();

        // 对应 CPlugin::Randomize 里的 m_fRandStart（plugin.cpp:6619-6626）。
        _randStart = new Vector4(
            _shaderRandom.Next(64841) * 0.01f,
            _shaderRandom.Next(53751) * 0.01f,
            _shaderRandom.Next(42661) * 0.01f,
            _shaderRandom.Next(31571) * 0.01f);

        // sprite 有自己的一套 EEL VM（16 个槽各一个，texmgr.cpp:60-67），生命周期跟引擎一致。
        _sprites = new SpriteManager(new ProjectMEvalEngine(), LoadSpriteTexture);

        // 预设纹理 sampler（textures/ 里的图片）跨预设共享，设备 Reset 时统一释放。
        _presetTextures = new PresetTextureCache(backend);
    }

    public long Frame { get; private set; }

    public PipelineStage CurrentStage { get; private set; } = PipelineStage.SetupFrame;

    public SoundAnalysis Sound => _analyzer.Sound;

    public SoundAnalyzer Analyzer => _analyzer;

    /// <summary>帧率估计（原版 <c>m_fps</c>，供声音分析的阻尼系数使用）。</summary>
    public float Fps => _timeKeeper.Fps;

    /// <summary>累计动画时间（原版 <c>m_time</c>，单位秒）。</summary>
    public float Time => _timeKeeper.Time;

    public VisualizerMode Mode { get; set; } = VisualizerMode.FeedbackSpike;

    /// <summary>当前预设（<see cref="VisualizerMode.Preset"/> 模式下非 null）。</summary>
    public PresetState? Preset => _preset;

    /// <summary>warp 网格列数（原版 <c>m_nGridX</c>，plugin.cpp:954 是 64）。</summary>
    public int GridX { get; set; } = 64;

    /// <summary>warp 网格行数（原版 <c>m_nGridY</c>）。</summary>
    public int GridY { get; set; } = 64;

    private bool _shaderCompat;

    /// <summary>
    /// shader 兼容层总开关（默认关）：打开后对原版会编译失败的预设做源码级改写 /
    /// profile 升级（见 <see cref="ShaderCompatFix"/>）。
    /// </summary>
    /// <remarks>
    /// 原版遇到这些 shader 一律编译失败并回退默认 shader，所以打开兼容层后**画面会与原版不同**，
    /// 因此默认关闭。切换会立刻触发重新装配。
    /// </remarks>
    public bool ShaderCompat
    {
        get => _shaderCompat;
        set
        {
            if (_shaderCompat == value)
            {
                return;
            }

            _shaderCompat = value;
            _shaderPreset = null;
            _shaderCompileAttempted = false;
            ShaderNotes = null;
        }
    }

    /// <summary>
    /// 光标位置，归一化到 -1..1（供兼容层的 <c>mouse</c> 内建使用）；默认 (0,0) = 屏幕中心。
    /// </summary>
    public Vector2 MousePosition { get; set; }

    /// <summary>
    /// 下一次自动换预设的时间戳（秒，与 <see cref="Time"/> 同一时钟）；负数 = 未安排。
    /// 只用来算 <c>progress</c>，真正的"到点换预设"由宿主（<c>App/Program.cs</c>）驱动。
    /// </summary>
    /// <remarks>
    /// 对应 <c>CPlugin::m_fNextPresetTime</c>：<c>progress = (time - presetStartTime) / (nextPresetTime - presetStartTime)</c>
    /// （milkdropfs.cpp:494）。原版不夹 0..1，这里也不夹。
    /// </remarks>
    public float NextPresetTime { get; set; } = -1f;

    /// <summary>当前预设的 <c>progress</c>（0..1，可能因换预设延迟而略大于 1）。</summary>
    private double ComputeProgress()
        => _preset is null ? 0.0 : ComputeProgressAt(Time, _preset.PresetStartTime, NextPresetTime);

    /// <summary><c>progress</c> 的计算（milkdropfs.cpp:494）：不夹 0..1，与原版一致。</summary>
    /// <param name="now">当前时间（秒）。</param>
    /// <param name="presetStartTime">本预设开始的时间。</param>
    /// <param name="nextPresetTime">下一次自动换预设的时间；&lt;= 0 表示未安排。</param>
    public static double ComputeProgressAt(float now, float presetStartTime, float nextPresetTime)
    {
        if (nextPresetTime <= 0f)
        {
            return 0.0;
        }

        var span = nextPresetTime - presetStartTime;
        return span <= 0.0001f ? 0.0 : (now - presetStartTime) / span;
    }

    /// <summary>装配阶段的兼容处理记录（如 <c>aspect -&gt; aspect__md</c>）；null = 无。</summary>
    public string? ShaderNotes { get; private set; }

    /// <summary>最近一次初始化失败的原因（null = 正常）。</summary>
    public string? LastError { get; private set; }

    /// <summary>最近一次预设 shader 编译失败的原因（null = 正常或还没编译）。</summary>
    public string? ShaderError { get; private set; }

    /// <summary>预设 shader pass 的顶点着色器是否已成功编译。</summary>
    public bool HasPresetVertexShader => _presetVertexShaderV2 is not null || _presetVertexShaderV3 is not null;

    /// <summary>当前预设的 warp 像素着色器是否已成功编译。</summary>
    public bool HasWarpShader => _presetWarpShader is not null;

    /// <summary>当前预设的 comp 像素着色器是否已成功编译。</summary>
    public bool HasCompShader => _presetCompShader is not null;

    public void RenderFrame()
    {
        // 原版顺序：DoTime → AnalyzeNewSound → AlignWaves → 绘制（pluginshell.cpp:1322-1326）；
        // m_frame 在帧末才自增（pluginshell.cpp:1330），所以首帧的 Frame 是 0。
        _timeKeeper.Update(Frame);
        _time = _timeKeeper.Time;
        UpdateSound();

        _backend.BeginFrame();

        if (Mode == VisualizerMode.Preset && _preset is not null && EnsureFeedbackTargets())
        {
            RenderPreset();
        }
        else if (Mode == VisualizerMode.FeedbackSpike && EnsureFeedbackSpike())
        {
            RenderFeedbackSpike();
        }
        else
        {
            _backend.Clear(0xFF101018);
            DrawFanPattern();
        }

        // 注意：这里不 Present —— UI 叠加层由 App 在本帧最后一起呈现。
        CurrentStage = PipelineStage.Present;
        _backend.EndFrame();

        Frame++;
    }

    // ---------------- 声音输入 ----------------

    /// <summary>
    /// 对应 <c>Milkdrop2PcmVisualizer.cpp:418-419</c> 与 <c>pluginshell.cpp:1323-1324</c>：
    /// 每帧取音频 → 写入 576 帧环形缓冲 → 按"最老 → 最新"取出一窗 → 分析 + 波形对齐。
    /// </summary>
    private void UpdateSound()
    {
        var read = _audioSource.Read(_audioScratch);
        if (read > 0)
        {
            _audioBuffer.Write(_audioScratch.AsSpan(0, read));
        }

        _audioBuffer.ReadAudioBuf(_waveL, _waveR);
        _analyzer.ProcessFrame(_waveL, _waveR, Fps, Frame);
    }

    /// <summary>设备 Reset 前调用：释放 <c>Pool.Default</c> 资源。</summary>
    public void OnDeviceLost()
    {
        _warpShader?.Dispose();
        _warpShader = null;
        _passthroughVertexShader?.Dispose();
        _passthroughVertexShader = null;
        (_feedbackA as IDisposable)?.Dispose();
        _feedbackA = null;
        (_feedbackB as IDisposable)?.Dispose();
        _feedbackB = null;
        _renderTargetWidth = 0;
        _renderTargetHeight = 0;
        _spikeAvailable = false;

        // 预设 shader 与噪点纹理也是设备资源，Reset 前必须释放；下一帧会重新编译/生成。
        DisposePresetShaders();
        DisposeNoiseTextures();
        _presetTextures.Dispose();
        DisposeBlurTargets();
        DisposeBlurShaders();
        DisposeFftTexture();
        ShaderError = null;

        // sprite 纹理同样是 Pool.Default（Resize 会 Reset 设备）；表达式 VM 保留。
        _sprites.ResetTextures();
    }

    /// <summary>设备 Reset 后调用：下一帧重建资源。</summary>
    public void OnDeviceReset()
    {
        _spikeAvailable = false;
    }

    // ---------------- 预设 ----------------

    /// <summary>
    /// 加载一个 <c>.milk</c> 预设并切到 <see cref="VisualizerMode.Preset"/>（硬切，不混合）。
    /// </summary>
    public bool LoadPreset(string path) => LoadPreset(path, 0f);

    /// <summary>
    /// 加载预设，并可以指定"从当前预设混合过来"的时长。
    /// </summary>
    /// <param name="blendTime">
    /// 混合时长（秒）；<c>0</c> = 硬切。对应 <c>m_fBlendTimeUser</c>/<c>m_fBlendTimeAuto</c>。
    /// 混合期间会继续跑上一个预设的 per-frame 代码（<see cref="PresetState.RunPerFrame"/> 的 <c>oldState</c>），
    /// 这样它的波形/图形不会突然冻住。
    /// </param>
    public bool LoadPreset(string path, float blendTime)
    {
        var state = new PresetState();

        if (!state.Import(path, Time))
        {
            LastError = "导入预设失败: " + path;
            return false;
        }

        EnsureFeedbackTargets();

        // 对应 state.cpp:1552 的 RecompileExpressions：编译 init（并执行一次）+ per-frame
        state.RecompileExpressions(PresetState.RecompilePresetCode, true, BuildPerFrameContext());

        // 对应 state.cpp:704 的 StartBlendFrom：要在 Import 之后（此时新状态已就绪）再记来源。
        var previous = _preset;
        if (blendTime > 0f && previous is not null && Mode == VisualizerMode.Preset)
        {
            state.StartBlendFrom(previous, Time, blendTime);
            _blendSource = previous;
        }
        else
        {
            state.IsBlending = false;
            _blendSource = null;
        }

        DisposePresetShaders();
        _preset = state;
        Mode = VisualizerMode.Preset;
        LastError = state.LastError;
        PresetNotes = state.DescribeUnsupportedWaveMode();
        return true;
    }

    /// <summary>
    /// 预设"能加载但原版画不出来"的部分（目前只有越界的内置波形模式）；null = 没有。
    /// </summary>
    /// <remarks>
    /// 和 <see cref="ShaderError"/> 不同：这不是失败，只是内容缺失。
    /// 单纯的黑屏预设（如 <c>A story of lasers - MilkDrop2077 - Over Water</c>）通常就是它导致的：
    /// 预设自己的 warp 只做 <c>ret *= 0.94</c>、comp 只做拷贝、<c>wave_a=0</c>，
    /// 唯一的内容来源就是那个越界的 <c>nWaveMode</c>。
    /// </remarks>
    public string? PresetNotes { get; private set; }

    // ---------------- sprite（texmgr.cpp / DrawSprites / DrawUserSprites）----------------

    /// <summary>16 个 sprite 槽（原版 <c>m_texmgr</c>）。</summary>
    public SpriteManager Sprites => _sprites;

    /// <summary>
    /// sprite 定义文件（原版 <c>m_szImgIniFile</c>）。null = <c>&lt;资源根&gt;/milk2_img.ini</c>。
    /// </summary>
    public string? SpriteIniPath { get; set; }

    /// <summary>
    /// 按编号启动一个 sprite（<c>CPlugin::LaunchSprite</c>，<c>plugin.cpp:8567-8713</c>）。
    /// </summary>
    /// <param name="spriteNumber">段名 <c>img00</c>..<c>img15</c> 的编号。</param>
    /// <param name="slot">-1 = 自动挑空槽 / 回收最老的槽。</param>
    public bool LaunchSprite(int spriteNumber, int slot = -1)
    {
        string iniPath;
        string resourceRoot;
        try
        {
            resourceRoot = ResourceLocator.ResourceRoot;
            iniPath = SpriteIniPath ?? FindDefaultSpriteIni(resourceRoot);
        }
        catch (Exception ex)
        {
            LastError = "找不到 sprite 资源目录: " + ex.Message;
            return false;
        }

        var ok = _sprites.LaunchFromIni(iniPath, spriteNumber, slot, resourceRoot, Time, Frame, out var error);
        if (error is not null)
        {
            // 代码编译失败时 ok 仍为 true（原版只是警告），但要把原因留在 UI 上。
            LastError = error;
        }

        return ok;
    }

    /// <summary>
    /// 直接按文件启动一个 sprite（不需要 <c>milk2_img.ini</c>，用于命令行/调试）。
    /// </summary>
    public bool LaunchSpriteFile(
        string path,
        int slot = -1,
        uint colorKey = 0,
        string initCode = "",
        string frameCode = "")
    {
        if (!File.Exists(path))
        {
            LastError = "sprite 图片不存在: " + path;
            return false;
        }

        if (slot < 0)
        {
            slot = _sprites.FindSlotForNewSprite();
        }

        var result = _sprites.LoadTexture(
            Path.GetFullPath(path),
            slot,
            initCode,
            frameCode,
            Time,
            Frame,
            colorKey & 0xFFFFFFu);

        if (!result.Ok)
        {
            LastError = result.Error == SpriteLoadError.OutOfMemory
                ? "sprite: 显存不足"
                : "sprite: 图片无法解码（" + path + "）";
            return false;
        }

        if (_sprites[slot].CodeError is { } codeError)
        {
            LastError = codeError;
        }

        return true;
    }

    /// <summary>清掉一个槽（<c>CPlugin::KillSprite</c>，<c>plugin.cpp:8715-8718</c>）。</summary>
    public void KillSprite(int slot) => _sprites.KillTexture(slot);

    /// <summary>按编号清掉 sprite（原版快捷键模式下按 <c>nUserData</c> 匹配）。</summary>
    public int KillSpriteByNumber(int spriteNumber) => _sprites.KillByUserData(spriteNumber);

    /// <summary>清掉全部 sprite。</summary>
    public void KillAllSprites() => _sprites.ResetTextures();

    /// <summary>
    /// 找默认的 <c>milk2_img.ini</c>：先看资源根目录，再看仓库根目录（原版放在配置目录）。
    /// 都找不到时返回资源根下的路径，让报错信息里能看到期望的位置。
    /// </summary>
    private static string FindDefaultSpriteIni(string resourceRoot)
    {
        var inResources = Path.Combine(resourceRoot, "milk2_img.ini");
        if (File.Exists(inResources))
        {
            return inResources;
        }

        var inProject = Path.Combine(ResourceLocator.ProjectRoot, "milk2_img.ini");
        return File.Exists(inProject) ? inProject : inResources;
    }

    /// <summary>图片 → 纹理（<c>D3DXCreateTextureFromFileExW</c> 的替代）。失败返回 null。</summary>
    private ITexture? LoadSpriteTexture(string fileName, uint colorKey)
    {
        try
        {
            return SpriteImage.Load(_backend, fileName, colorKey);
        }
        catch
        {
            return null;
        }
    }

    private PerFrameContext BuildPerFrameContext()
        => new(
            Time,
            Fps,
            _analyzer.MySound,
            Frame,
            ComputeProgress(),
            GridX,
            GridY,
            _backend.Width,
            _backend.Height,
            _invAspectX,
            _invAspectY,
            new Vector4(MousePosition.X, MousePosition.Y, 0f, 0f));

    /// <summary>
    /// M4 的预设渲染：per-frame 代码 → warp 网格（UV 由 zoom/rot/warp/decay 算出）→ 反馈。
    /// </summary>
    private void RenderPreset()
    {
        var preset = _preset!;
        var a = _feedbackA!;
        var b = _feedbackB!;

        // 混合进度要在 per-frame 之前推进（原版 UpdateTime 的顺序），
        // 混合期间把上一个预设也跑一遍，波形/图形才不会冻住。
        preset.UpdateBlend(Time);
        if (!preset.IsBlending)
        {
            _blendSource = null;
        }

        var context = BuildPerFrameContext();
        preset.RunPerFrame(context, _blendSource);

        _randFrame = new Vector4(Frand(), Frand(), Frand(), Frand());
        EnsurePresetShaders(preset);
        EnsureNoiseTextures();
        UpdateFftTexture();

        if (_blurLevelsUsed > 0)
        {
            EnsureBlurShaders();
            EnsureBlurTargets();
        }

        _backend.Clear(0xFF000000);

        // ---- pass 1: warp A → B ----
        CurrentStage = PipelineStage.Warp;

        var vertexCount = WarpMesh.VertexCount(GridX, GridY);
        if (_warpMesh.Length != vertexCount)
        {
            _warpMesh = new MilkVertex[vertexCount];
        }

        // decay 通过顶点色乘到采样结果上（固定功能的 MODULATE(TEXTURE, DIFFUSE)）
        var decay = preset.Decay.Eval(Time);
        var decayByte = (byte)Math.Clamp((int)(decay * 255f), 0, 255);
        var decayColor = 0xFF000000u | ((uint)decayByte << 16) | ((uint)decayByte << 8) | decayByte;

        WarpMesh.Build(
            _warpMesh,
            GridX,
            GridY,
            _backend.Width,
            _backend.Height,
            _aspectX,
            _aspectY,
            _invAspectX,
            _invAspectY,
            WarpMesh.FromState(preset, Time, _backend.Width, _backend.Height),
            decayColor,
            preset.PerVertex,
            preset.PerFrame);

        var sizes = new ShaderTextureSizes(
            _backend.Width,
            _backend.Height,
            NoiseLq: 256,
            NoiseLqLite: 32,
            NoiseMq: 256,
            NoiseHq: 256,
            NoiseVolLq: 32,
            NoiseVolHq: 32);

        _backend.PushRenderTarget(b);

        if (_presetWarpShader is not null)
        {
            // 原版 WarpedBlit_Shaders：固定功能顶点 + 预设 warp 像素着色器。
            _backend.SetShaders(VertexShaderFor(preset.WarpPsVersion), _presetWarpShader);
            BindShaderTextures(_presetWarpShader, a);
            ShaderParams.Apply(_presetWarpShader, preset, context, sizes, _randFrame, _shaderRandom);
            _backend.DrawWarpGrid(_warpMesh, GridX, GridY, a, BlendMode.Replace, bindTexture: false);
        }
        else
        {
            _backend.SetShaders(null, null);
            _backend.DrawWarpGrid(_warpMesh, GridX, GridY, a, BlendMode.Replace);
        }

        // ---- pass 1.2: motion vector 场（milkdropfs.cpp:964-965）----
        CurrentStage = PipelineStage.CustomShapes;
        DrawMotionVectors(preset);

        // ---- pass 1.5: blur 链（milkdropfs.cpp:1050-1051 / 1501-1679）----
        // 原版顺序：warp → blur（读 VS1 写 blur RT）→ shapes/waves 再画回 VS1。
        BlurPasses(preset);

        // ---- pass 1.6: 自定义图形 / 自定义波形 / 内置波形（milkdropfs.cpp:1053-1056）----
        CurrentStage = PipelineStage.CustomShapes;
        DrawPresetShapes(preset, context, a);

        CurrentStage = PipelineStage.CustomShapes;
        DrawPresetWaves(preset, context);
        DrawBuiltInWave(preset, context);

        // milkdropfs.cpp:1057：darken_center + 内外边框（sprite 本体的两遍画法见 DrawUserSprites）
        DrawSprites(preset);

        _backend.PopRenderTarget();

        // ---- pass 2: 合成到后备缓冲 ----
        CurrentStage = PipelineStage.Composite;

        if (_presetCompShader is not null)
        {
            // 原版 ShowToUser_Shaders：comp 着色器 + 32×24 全屏网格。
            CompMesh.Build(_compMesh, _backend.Width, _backend.Height, _aspectX, _aspectY, Time, _randStart);

            _backend.SetShaders(VertexShaderFor(preset.CompPsVersion), _presetCompShader);
            BindShaderTextures(_presetCompShader, b);
            ShaderParams.Apply(_presetCompShader, preset, context, sizes, _randFrame, _shaderRandom);
            _backend.DrawWarpGrid(_compMesh, CompMesh.GridX, CompMesh.GridY, b, BlendMode.Replace, bindTexture: false);
            _backend.SetShaders(null, null);
        }
        else
        {
            // 原版 ShowToUser_NoShaders（milkdropfs.cpp:3967-4287）：没有 comp shader 时
            // 由固定管线做 hue 四角着色 + video echo + gamma 重绘 + 颜色后处理。
            DrawCompNoShaders(preset, b);
        }

        // milkdropfs.cpp:1131：sprite 本体画到后备缓冲（burn-in 那遍同时烧进 VS1）
        DrawUserSprites(preset);

        // 乒乓交换（milkdropfs.cpp:1134-1136）
        CurrentStage = PipelineStage.SwapFeedback;
        (_feedbackA, _feedbackB) = (_feedbackB, _feedbackA);
    }

    /// <summary>
    /// 为当前预设装配并编译 warp/comp 像素着色器（对应 <c>CPlugin::LoadShaders</c> 的简化版）。
    /// 失败时对应 pass 回退固定功能。
    /// </summary>
    private void EnsurePresetShaders(PresetState preset)
    {
        if (ReferenceEquals(_shaderPreset, preset) && _shaderCompileAttempted)
        {
            return;
        }

        DisposePresetShaders();
        _shaderPreset = preset;
        _shaderCompileAttempted = true;
        ShaderError = null;
        ShaderNotes = null;

        var include = ShaderTemplates.Include;
        if (include is null)
        {
            ShaderError = "找不到 data/include.fx，预设 shader 回退固定功能";
            return;
        }

        // PS 版本必须和 VS 版本匹配：本机驱动在 vs_2_0+ps_3_0 和 vs_3_0+ps_2_0 下都会静默不画。
        // 每个 pass 按自己的像素着色器版本挑 VS，两个版本都可能用到。
        var needVs2 = preset.WarpPsVersion is > 0 and < 3 || preset.CompPsVersion is > 0 and < 3;
        var needVs3 = preset.WarpPsVersion >= 3 || preset.CompPsVersion >= 3;
        string? vertexError2 = null;
        string? vertexError3 = null;

        if (needVs2)
        {
            _presetVertexShaderV2 = _backend.CreateVertexShader(
                "preset_vs_2",
                ShaderAssembler.VertexShaderSource,
                out vertexError2,
                "VS",
                "vs_2_0");
        }

        if (needVs3)
        {
            _presetVertexShaderV3 = _backend.CreateVertexShader(
                "preset_vs_3",
                ShaderAssembler.VertexShaderSource,
                out vertexError3,
                "VS",
                "vs_3_0");
        }

        _presetWarpShader = CompilePresetShader(
            include,
            preset.WarpShadersText,
            MilkShaderKind.Warp,
            preset.WarpPsVersion,
            "preset_warp",
            out var warpError,
            out var warpNotes);

        _presetCompShader = CompilePresetShader(
            include,
            preset.CompShadersText,
            MilkShaderKind.Comp,
            preset.CompPsVersion,
            "preset_comp",
            out var compError,
            out var compNotes);

        ShaderError = (needVs2 ? vertexError2 : null)
                      ?? (needVs3 ? vertexError3 : null)
                      ?? warpError
                      ?? compError;

        if (warpNotes.Count > 0 || compNotes.Count > 0)
        {
            var parts = new List<string>(2);
            if (warpNotes.Count > 0)
            {
                parts.Add("warp: " + string.Join(", ", warpNotes));
            }

            if (compNotes.Count > 0)
            {
                parts.Add("comp: " + string.Join(", ", compNotes));
            }

            ShaderNotes = "shader 兼容改写（" + string.Join("; ", parts) + "）";
        }

        // 原版 m_nHighestBlurTexUsedThisFrame：warp/comp 里用到的最高 blur 级别。
        _blurLevelsUsed = Math.Max(
            HighestBlurLevel(_presetWarpShader),
            HighestBlurLevel(_presetCompShader));
    }

    private IShaderProgram? VertexShaderFor(int pixelShaderVersion)
        => pixelShaderVersion >= 3 ? _presetVertexShaderV3 : _presetVertexShaderV2;

    private static int HighestBlurLevel(IShaderProgram? program)
    {
        if (program is null)
        {
            return 0;
        }

        for (var level = 3; level >= 1; level--)
        {
            if (program.TryGetSamplerStage($"sampler_blur{level}", out _))
            {
                return level;
            }
        }

        return 0;
    }

    private IShaderProgram? CompilePresetShader(
        string include,
        string body,
        MilkShaderKind kind,
        int version,
        string name,
        out string? error,
        out IReadOnlyList<string> notes)
    {
        error = null;
        notes = [];
        if (version <= 0 || string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var fixes = _shaderCompat ? ShaderCompatFix.All : ShaderCompatFix.None;
        var source = ShaderAssembler.Assemble(
            include,
            body,
            kind,
            out var assembleError,
            fixes,
            out notes);

        if (source is null)
        {
            error = assembleError;
            return null;
        }

        // D3D9 最高只支持 ps_3_0；PSVERSION=4 的预设也按 ps_3_0 编译。
        var profile = ShaderCompiler.ProfileForVersion(Math.Min(version, 3));
        var shader = _backend.CreatePixelShader(name, source, out error, ShaderAssembler.EntryPoint, profile);

        // 对应 plugin.cpp:3676-3686：ps_2_a 失败时退到 ps_2_b。
        if (shader is null && profile == "ps_2_a")
        {
            shader = _backend.CreatePixelShader(name + "_fallback", source, out error, ShaderAssembler.EntryPoint, "ps_2_b");
        }

        // 兼容层：ps_2_0 的 64 个指令槽不够用（X5608）时升到 ps_2_a / ps_2_b。
        // 仍然是 "2.x"，顶点着色器配对（vs_2_0）不变。
        if (shader is null && _shaderCompat && profile == "ps_2_0")
        {
            foreach (var wider in (string[])["ps_2_a", "ps_2_b"])
            {
                shader = _backend.CreatePixelShader(name + "_" + wider, source, out error, ShaderAssembler.EntryPoint, wider);
                if (shader is not null)
                {
                    break;
                }
            }
        }

        return shader;
    }

    /// <summary>绑定预设 shader 实际用到的采样器（main 变体 + blur + noise + textures/ 里的图片）。</summary>
    private void BindShaderTextures(IShaderProgram program, ITexture mainTexture)
    {
        foreach (var sampler in ShaderSamplers.All)
        {
            if (!program.TryGetSamplerStage(sampler.Name, out var stage))
            {
                continue;
            }

            var texture = ResolveSamplerTexture(sampler.Name, mainTexture);
            _backend.SetTexture(stage, texture, sampler.Wrap, sampler.Bilinear);
        }

        // 预设自己声明的纹理 sampler（sampler_fw_clouds → textures/clouds.jpg）。
        // 原版按名字反射着绑，这里靠装配时写死的 register(sN) 找到阶段号。
        foreach (var name in program.SamplerNames)
        {
            if (ShaderSamplers.TryGet(name, out _)
                || !program.TryGetSamplerStage(name, out var stage)
                || !ShaderSamplers.TryResolveTextureSampler(name, out var info))
            {
                continue;
            }

            // fw_noise_lq / pw_noisevol_hq 这类是"带采样方式前缀的噪点纹理"，直接复用已生成的纹理
            var texture = ResolveNoiseAlias(info.RootName) ?? _presetTextures.Get(info.RootName);
            if (texture is null)
            {
                continue;   // 找不到图片：保持未绑定（原版这里会弹"找不到纹理"的提示）
            }

            _backend.SetTexture(stage, texture, info.Wrap, info.Bilinear);
        }
    }

    /// <summary>去掉 <c>XY_</c> 前缀之后的根名是否就是内置噪点纹理。</summary>
    private ITexture? ResolveNoiseAlias(string rootName) => rootName.ToLowerInvariant() switch
    {
        "noise_lq" => _noiseLq,
        "noise_lq_lite" => _noiseLqLite,
        "noise_mq" => _noiseMq,
        "noise_hq" => _noiseHq,
        "noisevol_lq" => _noiseVolLq,
        "noisevol_hq" => _noiseVolHq,
        _ => null,
    };

    private ITexture? ResolveSamplerTexture(string name, ITexture mainTexture)
    {
        if (name.Contains("noisevol_hq", StringComparison.OrdinalIgnoreCase))
        {
            return _noiseVolHq;
        }

        if (name.Contains("noisevol_lq", StringComparison.OrdinalIgnoreCase))
        {
            return _noiseVolLq;
        }

        if (name.Contains("fft", StringComparison.OrdinalIgnoreCase))
        {
            return _fftTexture;
        }

        if (name.Contains("noise_lq_lite", StringComparison.OrdinalIgnoreCase))
        {
            return _noiseLqLite;
        }

        if (name.Contains("noise_lq", StringComparison.OrdinalIgnoreCase))
        {
            return _noiseLq;
        }

        if (name.Contains("noise_mq", StringComparison.OrdinalIgnoreCase))
        {
            return _noiseMq;
        }

        if (name.Contains("noise_hq", StringComparison.OrdinalIgnoreCase))
        {
            return _noiseHq;
        }

        if (name.Contains("blur3", StringComparison.OrdinalIgnoreCase))
        {
            return _blurTargets[5] ?? mainTexture;
        }

        if (name.Contains("blur2", StringComparison.OrdinalIgnoreCase))
        {
            return _blurTargets[3] ?? mainTexture;
        }

        if (name.Contains("blur1", StringComparison.OrdinalIgnoreCase))
        {
            return _blurTargets[1] ?? mainTexture;
        }

        // sampler_main / fc / pc / fw / pw。
        return mainTexture;
    }

    /// <summary>
    /// 生成内置噪点纹理（<c>AddNoiseTex</c> / <c>AddNoiseVol</c>）。
    /// </summary>
    /// <remarks>
    /// 尺寸与 zoom 全部照抄 plugin.cpp:2369-2375：
    /// <c>noise_lq 256/z1</c>、<c>noise_lq_lite 32/z1</c>、<c>noise_mq 256/z4</c>、<c>noise_hq 256/z8</c>、
    /// <c>noisevol_lq 32³/z1</c>、<c>noisevol_hq 32³/z4</c>。
    /// </remarks>
    private void EnsureNoiseTextures()
    {
        if (_noiseAttempted)
        {
            return;
        }

        _noiseAttempted = true;

        try
        {
            _noiseLq = CreateNoiseTexture(256, 1, seed: 1);
            _noiseLqLite = CreateNoiseTexture(32, 1, seed: 2);
            _noiseMq = CreateNoiseTexture(256, 4, seed: 3);
            _noiseHq = CreateNoiseTexture(256, 8, seed: 4);
            _noiseVolLq = CreateNoiseVolumeTexture(32, 1, seed: 5);
            _noiseVolHq = CreateNoiseVolumeTexture(32, 4, seed: 6);
        }
        catch (Exception ex)
        {
            ShaderError ??= "创建噪点纹理失败: " + ex.Message;
            DisposeNoiseTextures();

            // 不要在每帧重试（失败通常意味着设备状态有问题）。
            _noiseAttempted = true;
        }
    }

    private ITexture CreateNoiseTexture(int size, int zoomFactor, int seed)
    {
        var pixels = NoiseTexture.Create2D(size, zoomFactor, seed);
        return _backend.CreateTexture(size, size, MemoryMarshal.AsBytes(pixels.AsSpan()));
    }

    private ITexture CreateNoiseVolumeTexture(int size, int zoomFactor, int seed)
    {
        var voxels = NoiseTexture.CreateVolume(size, zoomFactor, seed);
        return _backend.CreateVolumeTexture(size, MemoryMarshal.AsBytes(voxels.AsSpan()));
    }

    /// <summary>
    /// 把当前频谱上传成 512×1 的 <c>sampler_fft</c>，供兼容层的 <c>get_fft</c>/<c>get_fft_hz</c> 采样。
    /// </summary>
    /// <remarks>
    /// <c>SpecLeft</c> 是未归一化的 FFT 幅度，直接映射会"静音全黑、大声全白"。
    /// 这里用"峰值缓慢衰减"做自动增益（每帧 ×0.98），把幅度压到 0..1。
    /// MD3 PRO 的确切归一化未知，这是按视觉合理取的近似 —— 所以整个内建只在兼容层打开时启用。
    /// </remarks>
    private void UpdateFftTexture()
    {
        if (!_shaderCompat || _fftUnavailable)
        {
            return;
        }

        var spectrum = _analyzer.MySound.SpecLeft;
        var peak = _fftPeak * 0.98f;

        foreach (var magnitude in spectrum)
        {
            var value = MathF.Abs(magnitude);
            if (value > peak)
            {
                peak = value;
            }
        }

        _fftPeak = MathF.Max(peak, 1e-4f);
        var scale = 1f / _fftPeak;

        for (var i = 0; i < FftTextureWidth; i++)
        {
            var index = i * spectrum.Length / FftTextureWidth;
            var normalized = Math.Clamp(MathF.Abs(spectrum[index]) * scale, 0f, 1f);
            var level = (byte)(normalized * 255f);
            _fftPixels[(i * 4) + 0] = level;
            _fftPixels[(i * 4) + 1] = level;
            _fftPixels[(i * 4) + 2] = level;
            _fftPixels[(i * 4) + 3] = 255;
        }

        try
        {
            if (_fftTexture is null)
            {
                _fftTexture = _backend.CreateTexture(FftTextureWidth, 1, _fftPixels);
            }
            else
            {
                _backend.UpdateTexture(_fftTexture, _fftPixels);
            }
        }
        catch (Exception ex)
        {
            // 上传失败就放弃这条路径，别每帧重试（不改用户设的兼容开关）。
            ShaderNotes ??= "频谱纹理不可用: " + ex.Message;
            DisposeFftTexture();
            _fftUnavailable = true;
        }
    }

    private void DisposePresetShaders()
    {
        _presetVertexShaderV2?.Dispose();
        _presetVertexShaderV2 = null;
        _presetVertexShaderV3?.Dispose();
        _presetVertexShaderV3 = null;
        _presetWarpShader?.Dispose();
        _presetWarpShader = null;
        _presetCompShader?.Dispose();
        _presetCompShader = null;
        _shaderPreset = null;
        _shaderCompileAttempted = false;
    }

    /// <summary>释放兼容层的频谱纹理（设备 Reset 前必须释放）。</summary>
    private void DisposeFftTexture()
    {
        (_fftTexture as IDisposable)?.Dispose();
        _fftTexture = null;
        _fftPeak = 1f;
        _fftUnavailable = false;
    }

    private void DisposeNoiseTextures()
    {
        (_noiseLq as IDisposable)?.Dispose();
        _noiseLq = null;
        (_noiseLqLite as IDisposable)?.Dispose();
        _noiseLqLite = null;
        (_noiseMq as IDisposable)?.Dispose();
        _noiseMq = null;
        (_noiseHq as IDisposable)?.Dispose();
        _noiseHq = null;
        (_noiseVolLq as IDisposable)?.Dispose();
        _noiseVolLq = null;
        (_noiseVolHq as IDisposable)?.Dispose();
        _noiseVolHq = null;
        _noiseAttempted = false;
    }

    /// <summary>对应 C 版 <c>FRAND</c>（<c>(rand() % 7381) / 7380.0f</c>）。</summary>
    private float Frand() => (float)(_shaderRandom.Next(7381) / 7380.0);

    /// <summary>
    /// <c>CPlugin::DrawCustomWaves</c>（milkdropfs.cpp:2496-2680）：把预设里的自定义波形
    /// 叠到已经 warp 好的反馈纹理上（原版的 VS1）。必须在 <see cref="PresetState.RunPerFrame"/>
    /// 之后调用，这样波形 per-frame 代码才能拿到本帧的 <c>q1..q32</c>。
    /// </summary>
    private void DrawPresetWaves(PresetState preset, in PerFrameContext context)
    {
        CurrentStage = PipelineStage.CustomShapes;

        // 原版开头清掉纹理与顶点着色器（固定功能管线）。
        _backend.SetShaders(null, null);

        var waves = preset.Waves;
        var expressions = preset.WaveExprs;

        for (var i = 0; i < waves.Length; i++)
        {
            var wave = waves[i];
            if (wave.Enabled == 0)
            {
                continue;
            }

            var draw = WaveGeometry.Build(
                wave,
                expressions[i],
                preset,
                context,
                Sound,
                1f, // 预设混合（m_bBlending）尚未接入，等价于单次绘制
                _invAspectX,
                _invAspectY,
                _backend.Width,
                _backend.Height,
                _waveVertices,
                _waveSmoothed);

            if (draw.Count <= 0)
            {
                continue;
            }

            var vertices = draw.Smoothed
                ? _waveSmoothed.AsSpan(0, draw.Count)
                : _waveVertices.AsSpan(0, draw.Count);

            // milkdropfs.cpp:2648：加法波形用 ONE，否则用 INVSRCALPHA。
            var blend = draw.Additive ? BlendMode.Additive : BlendMode.Alpha;

            if (draw.UseDots)
            {
                _backend.DrawAdditivePoints(vertices, null, draw.PointSize, blend);
                continue;
            }

            if (vertices.Length < 2)
            {
                continue;
            }

            if (!draw.DrawThick)
            {
                _backend.DrawLineStrip(vertices, null, blend);
                continue;
            }

            // bDrawThick：同一折线画 4 遍，依次偏移 (0,0)/(1,0)/(1,1)/(0,1) 像素
            // （milkdropfs.cpp:2654-2667；C 版在裁剪空间里的 x_inc=2/width 正好是 1 像素）。
            vertices.CopyTo(_waveThick);
            var thick = _waveThick.AsSpan(0, draw.Count);

            _backend.DrawLineStrip(thick, null, blend);
            OffsetVertices(thick, 1f, 0f);
            _backend.DrawLineStrip(thick, null, blend);
            OffsetVertices(thick, 0f, 1f);
            _backend.DrawLineStrip(thick, null, blend);
            OffsetVertices(thick, -1f, 0f);
            _backend.DrawLineStrip(thick, null, blend);
        }
    }

    /// <summary>
    /// <c>CPlugin::DrawCustomShapes</c>（milkdropfs.cpp:2215-2398）：在 blur 之后、波形之前
    /// 把预设的 4 个自定义图形画进当前目标。
    /// </summary>
    /// <param name="feedbackTexture">给 <c>textured</c> 图形采样的上一帧纹理（原版 <c>m_lpVS[0]</c>）。</param>
    private void DrawPresetShapes(PresetState preset, in PerFrameContext context, ITexture? feedbackTexture)
    {
        _backend.SetShaders(null, null);

        var width = _backend.Width;
        var height = _backend.Height;

        for (var i = 0; i < preset.Shapes.Length; i++)
        {
            var shape = preset.Shapes[i];
            if (shape.Enabled == 0)
            {
                continue;
            }

            var expressions = preset.ShapeExprs[i];
            var instances = Math.Clamp(shape.Instances, 0, 128);

            for (var instance = 0; instance < instances; instance++)
            {
                // 原版每个实例都同步一次变量并执行 per-frame 代码。
                expressions.SyncPerFrame(context, preset.PerFrame, shape, instance);
                expressions.RunPerFrame();

                var x = (float)expressions["x"].Value;
                var y = (float)expressions["y"].Value;
                var rad = (float)expressions["rad"].Value;
                var ang = (float)expressions["ang"].Value;
                var texAng = (float)expressions["tex_ang"].Value;
                var texZoom = (float)expressions["tex_zoom"].Value;
                var sides = Math.Clamp((int)expressions["sides"].Value, 3, 100);
                var textured = (int)expressions["textured"].Value != 0;
                var additive = (int)expressions["additive"].Value != 0;
                var thick = (int)expressions["thick"].Value != 0;

                var r = (float)expressions["r"].Value;
                var g = (float)expressions["g"].Value;
                var b = (float)expressions["b"].Value;
                var a = (float)expressions["a"].Value;
                var r2 = (float)expressions["r2"].Value;
                var g2 = (float)expressions["g2"].Value;
                var b2 = (float)expressions["b2"].Value;
                var a2 = (float)expressions["a2"].Value;
                var borderR = (float)expressions["border_r"].Value;
                var borderG = (float)expressions["border_g"].Value;
                var borderB = (float)expressions["border_b"].Value;
                var borderA = (float)expressions["border_a"].Value;

                if (texZoom is > -0.0001f and < 0.0001f)
                {
                    texZoom = 0.0001f;
                }

                var centerX = (x * 2f) - 1f;
                var centerY = (-y * 2f) + 1f;

                var innerColor = PackColor(r, g, b, a);
                var outerColor = PackColor(r2, g2, b2, a2);

                var half = sides + 2;
                var verts = _shapeVerts.AsSpan(0, half);

                verts[0] = new MilkVertex(
                    ClipToPixel(centerX, centerY, width, height),
                    innerColor,
                    new Vector2(0.5f, 0.5f));
                verts[1] = new MilkVertex(
                    default,
                    outerColor,
                    new Vector2(0.5f, 0.5f));

                var twoPi = MathF.Tau;
                for (var j = 1; j <= sides; j++)
                {
                    var t = (j - 1) / (float)sides;
                    var ringAngle = (t * twoPi) + ang + (MathF.PI * 0.25f);
                    var clipX = centerX + (rad * MathF.Cos(ringAngle) * _aspectY);
                    var clipY = centerY + (rad * MathF.Sin(ringAngle));

                    var texAngle = (t * twoPi) + texAng + (MathF.PI * 0.25f);
                    var tu = 0.5f + (0.5f * MathF.Cos(texAngle) / texZoom * _aspectY);
                    var tv = 0.5f + (0.5f * MathF.Sin(texAngle) / texZoom);

                    verts[j] = new MilkVertex(
                        ClipToPixel(clipX, clipY, width, height),
                        outerColor,
                        new Vector2(tu, tv));
                }

                // 收尾顶点：原版复制 v[1] 到 v[sides+1]，给边框闭合用。
                verts[sides + 1] = verts[1];

                var blend = additive ? BlendMode.Additive : BlendMode.Alpha;
                var texture = textured ? feedbackTexture : null;
                _backend.DrawTriangleFan(verts, texture, blend);

                if (borderA > 0f)
                {
                    var borderColor = PackColor(borderR, borderG, borderB, borderA);
                    var borderVerts = _shapeBorder.AsSpan(0, sides + 1);
                    for (var j = 0; j < sides + 1; j++)
                    {
                        var source = verts[j + 1];
                        borderVerts[j] = new MilkVertex(source.Position, borderColor, source.TexCoord, source.UvOrig, source.RadAng);
                    }

                    var iterations = thick ? 4 : 1;
                    for (var pass = 0; pass < iterations; pass++)
                    {
                        if (pass == 1)
                        {
                            OffsetVertices(borderVerts, 1f, 0f);
                        }
                        else if (pass == 2)
                        {
                            OffsetVertices(borderVerts, 0f, 1f);
                        }
                        else if (pass == 3)
                        {
                            OffsetVertices(borderVerts, -1f, 0f);
                        }

                        _backend.DrawLineStrip(borderVerts, null, blend);
                    }
                }
            }
        }
    }

    /// <summary>
    /// <c>CPlugin::DrawWave</c>（milkdropfs.cpp:2682-3298）：预设的"内置"波形，
    /// 支持 mode 0..8。
    /// </summary>
    private void DrawBuiltInWave(PresetState preset, in PerFrameContext context)
    {
        _backend.SetShaders(null, null);

        var pf = preset.PerFrame;
        var my = context.MySound;
        var sound = Sound;
        var width = _backend.Width;
        var height = _backend.Height;

        var cr = Math.Clamp((float)pf["wave_r"].Value, 0f, 1f);
        var cg = Math.Clamp((float)pf["wave_g"].Value, 0f, 1f);
        var cb = Math.Clamp((float)pf["wave_b"].Value, 0f, 1f);
        var wavePosX = ((float)pf["wave_x"].Value * 2f) - 1f;
        var wavePosY = ((float)pf["wave_y"].Value * 2f) - 1f;
        var waveParam = (float)pf["wave_mystery"].Value;

        if (preset.MaximizeWaveColor)
        {
            var max = MathF.Max(cr, MathF.Max(cg, cb));
            if (max > 0.01f)
            {
                cr /= max;
                cg /= max;
                cb /= max;
            }
        }

        var alpha = (float)pf["wave_a"].Value;
        var mode = ((int)pf["wave_mode"].Value % 9) + 9;
        mode %= 9;

        var volumeAlpha = 1f;
        if (preset.ModWaveAlphaByVolume)
        {
            var start = preset.ModWaveAlphaStart.Eval(Time);
            var end = preset.ModWaveAlphaEnd.Eval(Time);
            var denominator = end - start;
            if (MathF.Abs(denominator) > 1e-6f)
            {
                volumeAlpha = ((my.ImmRel[0] + my.ImmRel[1] + my.ImmRel[2]) * 0.3333333f - start) / denominator;
            }
        }

        var waveL = sound.Waveform[0];
        var waveR = sound.Waveform[1];
        var spectrum = sound.Spectrum[0];

        var nVerts = 0;
        var nBreak = -1;
        var sampleOffset = 0;

        var fWaveParam2 = waveParam;
        if ((mode == 0 || mode == 1 || mode == 4) && (fWaveParam2 < -1f || fWaveParam2 > 1f))
        {
            fWaveParam2 = (fWaveParam2 * 0.5f) + 0.5f;
            fWaveParam2 -= MathF.Floor(fWaveParam2);
            fWaveParam2 = MathF.Abs(fWaveParam2);
            fWaveParam2 = (fWaveParam2 * 2f) - 1f;
        }

        var verts = _builtInWave;

        switch (mode)
        {
            case 0:
            {
                nVerts = SoundAnalysis.WaveformSamples / 2;
                sampleOffset = (SoundAnalysis.WaveformSamples - nVerts) / 2;
                alpha = ApplyVolumeAlpha(alpha, volumeAlpha);

                var inv = 1f / (nVerts - 1);
                for (var i = 0; i < nVerts; i++)
                {
                    var rad = 0.5f + (0.4f * waveR[i + sampleOffset]) + fWaveParam2;
                    var ang = (i * inv * 6.28f) + (context.Time * 0.2f);
                    if (i < nVerts / 10)
                    {
                        var mix = i / (nVerts * 0.1f);
                        mix = 0.5f - (0.5f * MathF.Cos(mix * 3.1416f));
                        var rad2 = 0.5f + (0.4f * waveR[i + nVerts + sampleOffset]) + fWaveParam2;
                        rad = (rad2 * (1f - mix)) + (rad * mix);
                    }

                    verts[i] = WaveVertex(
                        (rad * MathF.Cos(ang) * _aspectY) + wavePosX,
                        (rad * MathF.Sin(ang) * _aspectX) + wavePosY,
                        width,
                        height,
                        0xFFFFFFFF);
                }

                // 闭合圆环。
                verts[nVerts] = verts[0];
                nVerts++;
                break;
            }

            case 1:
            {
                nVerts = SoundAnalysis.WaveformSamples / 2;
                alpha = ApplyVolumeAlpha(alpha * 1.25f, volumeAlpha);

                for (var i = 0; i < nVerts; i++)
                {
                    var rad = 0.53f + (0.43f * waveR[i]) + fWaveParam2;
                    var ang = (waveL[i + 32] * 1.57f) + (context.Time * 2.3f);
                    verts[i] = WaveVertex(
                        (rad * MathF.Cos(ang) * _aspectY) + wavePosX,
                        (rad * MathF.Sin(ang) * _aspectX) + wavePosY,
                        width,
                        height,
                        0xFFFFFFFF);
                }

                break;
            }

            case 2:
            case 3:
            {
                nVerts = SoundAnalysis.WaveformSamples;
                if (mode == 2)
                {
                    alpha = ApplyVolumeAlpha(alpha * ResolutionAlpha(width), volumeAlpha);
                }
                else
                {
                    var treble = my.ImmRel[2];
                    alpha = ResolutionAlpha(width) * 1.3f * treble * treble;
                    alpha = ApplyVolumeAlpha(alpha, volumeAlpha);
                }

                for (var i = 0; i < nVerts; i++)
                {
                    verts[i] = WaveVertex(
                        (waveR[i] * _aspectY) + wavePosX,
                        (waveL[i + 32] * _aspectX) + wavePosY,
                        width,
                        height,
                        0xFFFFFFFF);
                }

                break;
            }

            case 4:
            {
                nVerts = Math.Min(SoundAnalysis.WaveformSamples, Math.Max(2, width / 3));
                sampleOffset = (SoundAnalysis.WaveformSamples - nVerts) / 2;
                alpha = ApplyVolumeAlpha(alpha, volumeAlpha);

                var w1 = 0.45f + (0.5f * ((fWaveParam2 * 0.5f) + 0.5f));
                var w2 = 1f - w1;
                var inv = 1f / nVerts;

                for (var i = 0; i < nVerts; i++)
                {
                    var px = -1f + (2f * i * inv) + wavePosX;
                    var py = (waveL[i + sampleOffset] * 0.47f) + wavePosY;
                    px += waveR[i + 25 + sampleOffset] * 0.44f;

                    if (i > 1)
                    {
                        var prevX = verts[i - 1].Position.X;
                        var prevY = verts[i - 1].Position.Y;
                        var prev2X = verts[i - 2].Position.X;
                        var prev2Y = verts[i - 2].Position.Y;

                        var clipX = ClipX(px, width);
                        var clipY = ClipY(py, height);
                        clipX = (clipX * w2) + (w1 * ((ClipX(prevX, width) * 2f) - ClipX(prev2X, width)));
                        clipY = (clipY * w2) + (w1 * ((ClipY(prevY, height) * 2f) - ClipY(prev2Y, height)));
                        px = clipX;
                        py = clipY;
                    }

                    verts[i] = WaveVertex(px, py, width, height, 0xFFFFFFFF);
                }

                break;
            }

            case 5:
            {
                nVerts = SoundAnalysis.WaveformSamples;
                alpha = ApplyVolumeAlpha(alpha * ResolutionAlpha(width), volumeAlpha);

                var cosRot = MathF.Cos(context.Time * 0.3f);
                var sinRot = MathF.Sin(context.Time * 0.3f);

                for (var i = 0; i < nVerts; i++)
                {
                    var x0 = (waveR[i] * waveL[i + 32]) + (waveL[i] * waveR[i + 32]);
                    var y0 = (waveR[i] * waveR[i]) - (waveL[i + 32] * waveL[i + 32]);
                    verts[i] = WaveVertex(
                        (((x0 * cosRot) - (y0 * sinRot)) * _aspectY) + wavePosX,
                        (((x0 * sinRot) + (y0 * cosRot)) * _aspectX) + wavePosY,
                        width,
                        height,
                        0xFFFFFFFF);
                }

                break;
            }

            default:
            {
                // mode 6/7/8：沿一条可旋转的直线铺开。
                nVerts = SoundAnalysis.WaveformSamples / 2;
                if (nVerts > width / 3)
                {
                    nVerts = width / 3;
                }

                if (mode == 8)
                {
                    nVerts = 256;
                }
                else
                {
                    sampleOffset = (SoundAnalysis.WaveformSamples - nVerts) / 2;
                }

                alpha = ApplyVolumeAlpha(alpha, volumeAlpha);

                var lineAng = 1.57f * fWaveParam2;
                var dx = MathF.Cos(lineAng);
                var dy = MathF.Sin(lineAng);
                var edge0X = (wavePosX * MathF.Cos(lineAng + 1.57f)) - (dx * 3f);
                var edge0Y = (wavePosX * MathF.Sin(lineAng + 1.57f)) - (dy * 3f);
                var edge1X = (wavePosX * MathF.Cos(lineAng + 1.57f)) + (dx * 3f);
                var edge1Y = (wavePosX * MathF.Sin(lineAng + 1.57f)) + (dy * 3f);

                var stepX = (edge1X - edge0X) / nVerts;
                var stepY = (edge1Y - edge0Y) / nVerts;
                var perpAng = MathF.Atan2(stepY, stepX) + 1.57f;
                var perpX = MathF.Cos(perpAng);
                var perpY = MathF.Sin(perpAng);

                if (mode == 8)
                {
                    for (var i = 0; i < nVerts; i++)
                    {
                        var f = 0.1f * MathF.Log(spectrum[i * 2] + spectrum[(i * 2) + 1] + 1e-9f);
                        verts[i] = WaveVertex(
                            edge0X + (stepX * i) + (perpX * f),
                            edge0Y + (stepY * i) + (perpY * f),
                            width,
                            height,
                            0xFFFFFFFF);
                    }
                }
                else if (mode == 6)
                {
                    for (var i = 0; i < nVerts; i++)
                    {
                        var offset = 0.25f * waveL[i + sampleOffset];
                        verts[i] = WaveVertex(
                            edge0X + (stepX * i) + (perpX * offset),
                            edge0Y + (stepY * i) + (perpY * offset),
                            width,
                            height,
                            0xFFFFFFFF);
                    }
                }
                else
                {
                    var sep = MathF.Pow((wavePosY * 0.5f) + 0.5f, 2f);
                    for (var i = 0; i < nVerts; i++)
                    {
                        var offset = (0.25f * waveL[i + sampleOffset]) + sep;
                        verts[i] = WaveVertex(
                            edge0X + (stepX * i) + (perpX * offset),
                            edge0Y + (stepY * i) + (perpY * offset),
                            width,
                            height,
                            0xFFFFFFFF);
                    }

                    for (var i = 0; i < nVerts; i++)
                    {
                        var offset = (0.25f * waveR[i + sampleOffset]) - sep;
                        verts[i + nVerts] = WaveVertex(
                            edge0X + (stepX * i) + (perpX * offset),
                            edge0Y + (stepY * i) + (perpY * offset),
                            width,
                            height,
                            0xFFFFFFFF);
                    }

                    nBreak = nVerts;
                    nVerts *= 2;
                }

                break;
            }
        }

        alpha = Math.Clamp(alpha, 0f, 1f);
        if (alpha < 0.004f)
        {
            return;
        }

        var color = PackColor(cr, cg, cb, alpha);
        for (var i = 0; i < nVerts; i++)
        {
            var vertex = verts[i];
            verts[i] = new MilkVertex(vertex.Position, color, vertex.TexCoord, vertex.UvOrig, vertex.RadAng);
        }

        // 平滑 + 描粗/点模式。SmoothWave 需要 nVerts>=2。
        var drawVerts = verts.AsSpan(0, nVerts);
        var smoothed = _builtInWaveSmoothed;
        var count = nVerts;
        var smoothBreak = nBreak;

        if (nVerts >= 2)
        {
            if (nBreak < 0)
            {
                count = SmoothWave(drawVerts, smoothed);
            }
            else
            {
                var oldBreak = nBreak;
                smoothBreak = SmoothWave(drawVerts[..oldBreak], smoothed);
                count = SmoothWave(drawVerts[oldBreak..], smoothed.AsSpan(smoothBreak)) + smoothBreak;
            }

            drawVerts = smoothed.AsSpan(0, count);
        }

        var usedots = pf["wave_usedots"].Value != 0;
        var thickWave = pf["wave_thick"].Value != 0;
        var additive = preset.AdditiveWaves || pf["wave_additive"].Value != 0;
        var blend = additive ? BlendMode.Additive : BlendMode.Alpha;
        var iterations = (thickWave || usedots) && width >= 512 ? 4 : 1;

        var scratch = _waveThick.AsSpan(0, count);
        drawVerts.CopyTo(scratch);

        var pointSize = (width >= 1024 ? 2f : 1f) + (thickWave ? 1f : 0f);

        for (var pass = 0; pass < iterations; pass++)
        {
            if (pass == 1)
            {
                OffsetVertices(scratch, 1f, 0f);
            }
            else if (pass == 2)
            {
                OffsetVertices(scratch, 0f, 1f);
            }
            else if (pass == 3)
            {
                OffsetVertices(scratch, -1f, 0f);
            }

            if (usedots)
            {
                _backend.DrawAdditivePoints(scratch, null, pointSize, blend);
            }
            else if (smoothBreak < 0)
            {
                _backend.DrawLineStrip(scratch, null, blend);
            }
            else
            {
                var breakIndex = Math.Clamp(smoothBreak, 1, scratch.Length - 1);
                _backend.DrawLineStrip(scratch[..breakIndex], null, blend);
                _backend.DrawLineStrip(scratch[breakIndex..], null, blend);
            }
        }
    }

    /// <summary>
    /// <c>CPlugin::DrawMotionVectors</c>（milkdropfs.cpp:1156-1308）：把本帧 warp 网格的
    /// "像素往哪儿走" 场画成一堆短线，直接叠在 warp 结果上（VS0）。
    /// </summary>
    /// <remarks>
    /// 由 per-frame 变量控制：<c>mv_x/mv_y</c>（网格列/行数，上限 64×48）、
    /// <c>mv_dx/mv_dy</c>（整体平移）、<c>mv_l</c>（拖尾倍率）、<c>mv_r/g/b/a</c>（颜色与开关）。
    /// 全库只有少数预设把 <c>mv_a</c> 打开。
    /// </remarks>
    private void DrawMotionVectors(PresetState preset)
    {
        var pf = preset.PerFrame;
        var alpha = (float)pf["mv_a"].Value;
        if (alpha < 0.001f)
        {
            return;
        }

        var mvX = (float)pf["mv_x"].Value;
        var mvY = (float)pf["mv_y"].Value;
        var columns = (int)mvX;
        var rows = (int)mvY;
        var fracX = mvX - columns;
        var fracY = mvY - rows;

        // 原版硬上限 64×48（:1177-1178），超了就把小数部分清零
        if (columns > MotionVectors.MaxColumns)
        {
            columns = MotionVectors.MaxColumns;
            fracX = 0f;
        }

        if (rows > MotionVectors.MaxRows)
        {
            rows = MotionVectors.MaxRows;
            fracY = 0f;
        }

        if (columns <= 0 || rows <= 0)
        {
            return;
        }

        // 小数部分夹到 0..1（:1210-1213）
        fracX = Math.Clamp(fracX, 0f, 1f);
        fracY = Math.Clamp(fracY, 0f, 1f);

        var offsetX = (float)pf["mv_dx"].Value;
        var offsetY = (float)pf["mv_dy"].Value;
        var lengthScale = (float)pf["mv_l"].Value;
        var color = PackColor(
            (float)pf["mv_r"].Value,
            (float)pf["mv_g"].Value,
            (float)pf["mv_b"].Value,
            alpha);

        _backend.SetShaders(null, null);

        var width = _backend.Width;
        var height = _backend.Height;
        var gridX = GridX;
        var gridY = GridY;

        for (var row = 0; row < rows; row++)
        {
            var count = MotionVectors.BuildRow(
                _motionVectorVertices,
                _warpMesh,
                gridX,
                gridY,
                row,
                rows,
                columns,
                fracX,
                fracY,
                offsetX,
                offsetY,
                lengthScale,
                width,
                height,
                color);

            if (count > 0)
            {
                _backend.DrawLineList(_motionVectorVertices.AsSpan(0, count), null, BlendMode.Alpha);
            }
        }
    }

    /// <summary>
    /// <c>CPlugin::ShowToUser_NoShaders</c>（milkdropfs.cpp:3967-4287）：不走 comp shader 的合成。
    /// </summary>
    /// <remarks>
    /// 做三件事，顺序不能换：
    /// ① hue 四角色调（<c>m_fShader</c>，只在固定管线里有）；
    /// ② video echo —— 同一张 warp 结果画两遍（第二遍按 <c>echo_zoom</c> 缩放、<c>echo_orient</c> 翻转、
    ///    <c>echo_alpha</c> 加权），每遍再按 <c>gamma</c> 做 N 次加法重绘来模拟 gamma；
    /// ③ brighten / darken / solarize / invert —— 无纹理全屏四边形 + 针对目标像素的混合因子。
    /// </remarks>
    private void DrawCompNoShaders(PresetState preset, ITexture source)
    {
        var pf = preset.PerFrame;
        var width = _backend.Width;
        var height = _backend.Height;

        // 顶点向外扩 1 像素，避免显卡的 +0.5 texel 偏移把边缘 UV 卷到对面
        // （milkdropfs.cpp:4003-4015）。
        var xExtent = 1f + (1f / width);
        var yExtent = 1f + (1f / height);

        // 宽高比修正（milkdropfs.cpp:4017-4031）：宽屏不动，窄屏把 x 往外撑。
        // aspect = w / (h * invAspectY)；invAspectY 的定义见 UpdateAspect（plugin.cpp:2035-2038）。
        var aspect = width / (height * _invAspectY);
        var xMul = aspect > 1f ? 1f : 1f / aspect;
        var yMul = aspect > 1f ? aspect : 1f;

        _compX0 = (0.5f - (xExtent * xMul * 0.5f)) * width;
        _compX1 = (0.5f + (xExtent * xMul * 0.5f)) * width;
        _compY0 = (0.5f - (yExtent * yMul * 0.5f)) * height;
        _compY1 = (0.5f + (yExtent * yMul * 0.5f)) * height;

        Span<float> shade = stackalloc float[12];
        ComputeCompShade(preset, shade);

        var echoZoom = (float)pf["echo_zoom"].Value;
        var echoAlpha = (float)pf["echo_alpha"].Value;
        // 原版在调用点就取模（milkdropfs.cpp:4066），负数沿用 C 的 % 语义。
        var echoOrientation = (int)pf["echo_orient"].Value % 4;
        var gammaAdj = (float)pf["gamma"].Value;

        if (echoAlpha > 0.001f)
        {
            // 两层：i=0 是正常画面（权重 1-alpha，替换），i=1 是 echo（权重 alpha，加法）。
            for (var layer = 0; layer < 2; layer++)
            {
                var uv = CompFallback.EchoUv(layer == 0 ? 1f : echoZoom, echoOrientation, layer == 1);
                var mix = layer == 1 ? echoAlpha : 1f - echoAlpha;

                DrawCompQuad(source, uv, shade, mix, layer == 0 ? BlendMode.Replace : BlendMode.Additive);

                // gamma 重绘：i=0 那一层在这一步之前已经切成加法混合（milkdropfs.cpp:4125-4129）。
                var redraws = CompFallback.GammaWeights(gammaAdj, echoLayer: true, _gammaWeights);
                for (var i = 0; i < redraws; i++)
                {
                    DrawCompQuad(source, uv, shade, mix * _gammaWeights[i], BlendMode.Additive);
                }
            }
        }
        else
        {
            // 无 echo：第一遍实心，之后加法；gamma 用"遍数"实现（milkdropfs.cpp:4161-4182）。
            var uv = new EchoQuadUv(0f, 0f, 1f, 1f);
            var passes = CompFallback.GammaWeights(gammaAdj, echoLayer: false, _gammaWeights);
            for (var i = 0; i < passes; i++)
            {
                DrawCompQuad(source, uv, shade, _gammaWeights[i], i == 0 ? BlendMode.Replace : BlendMode.Additive);
            }
        }

        // ---- 颜色后处理（milkdropfs.cpp:4199-4283）----
        if (preset.Brighten)
        {
            // 开方滤镜 = 反相 → 平方 → 反相
            DrawCompPostProcess(BlendMode.InvertDest);
            DrawCompPostProcess(BlendMode.MultiplyDest);
            DrawCompPostProcess(BlendMode.InvertDest);
        }

        if (preset.Darken)
        {
            // 平方滤镜
            DrawCompPostProcess(BlendMode.MultiplyDest);
        }

        if (preset.Solarize)
        {
            DrawCompPostProcess(BlendMode.SolarizeInvert);
            DrawCompPostProcess(BlendMode.SolarizeAdd);
        }

        if (preset.Invert)
        {
            DrawCompPostProcess(BlendMode.InvertDest);
        }
    }

    /// <summary>
    /// hue shader 的四角色调（<c>milkdropfs.cpp:4033-4062</c>）。
    /// 没开时四个角都是白色，效果等于原样输出。
    /// </summary>
    private void ComputeCompShade(PresetState preset, Span<float> shade)
    {
        var amount = preset.Shader.Eval(Time);
        if (amount <= 0.001f)
        {
            shade.Fill(1f);
            return;
        }

        for (var i = 0; i < 4; i++)
        {
            // 三个通道的频率/相位都不同，randStart 分别用 [3]/[1]/[2]。
            var r = 0.6f + (0.3f * MathF.Sin((Time * 30f * 0.0143f) + 3f + (i * 21f) + _randStart.W));
            var g = 0.6f + (0.3f * MathF.Sin((Time * 30f * 0.0107f) + 1f + (i * 13f) + _randStart.Y));
            var b = 0.6f + (0.3f * MathF.Sin((Time * 30f * 0.0129f) + 6f + (i * 9f) + _randStart.Z));

            // 归一化到 [0.5, 1]：除以三通道最大值再折半
            var max = MathF.Max(r, MathF.Max(g, b));
            r = 0.5f + (0.5f * (r / max));
            g = 0.5f + (0.5f * (g / max));
            b = 0.5f + (0.5f * (b / max));

            // 按 fShader 在"色调"和"纯白"之间插值
            shade[(i * 3) + 0] = (r * amount) + (1f - amount);
            shade[(i * 3) + 1] = (g * amount) + (1f - amount);
            shade[(i * 3) + 2] = (b * amount) + (1f - amount);
        }
    }

    /// <summary>画一层合成四边形：顶点色 = shade × mix，UV 来自 echo 计算。</summary>
    private void DrawCompQuad(ITexture source, in EchoQuadUv uv, ReadOnlySpan<float> shade, float mix, BlendMode blend)
    {
        var x0 = _compX0;
        var y0 = _compY0;
        var x1 = _compX1;
        var y1 = _compY1;

        _compQuad[0] = new MilkVertex(new Vector3(x0, y0, 0f), ShadeColor(shade, 0, mix), new Vector2(uv.U0, uv.V0));
        _compQuad[1] = new MilkVertex(new Vector3(x1, y0, 0f), ShadeColor(shade, 1, mix), new Vector2(uv.U1, uv.V0));
        _compQuad[2] = new MilkVertex(new Vector3(x0, y1, 0f), ShadeColor(shade, 2, mix), new Vector2(uv.U0, uv.V1));
        _compQuad[3] = new MilkVertex(new Vector3(x1, y1, 0f), ShadeColor(shade, 3, mix), new Vector2(uv.U1, uv.V1));

        _backend.DrawTriangleStrip(_compQuad, source, blend);
    }

    /// <summary>全屏无纹理四边形 + 针对目标像素的混合因子（brighten/darken/solarize/invert）。</summary>
    private void DrawCompPostProcess(BlendMode blend)
    {
        var white = PackColor(1f, 1f, 1f, 1f);
        var uv = Vector2.Zero;

        _compQuad[0] = new MilkVertex(new Vector3(_compX0, _compY0, 0f), white, uv);
        _compQuad[1] = new MilkVertex(new Vector3(_compX1, _compY0, 0f), white, uv);
        _compQuad[2] = new MilkVertex(new Vector3(_compX0, _compY1, 0f), white, uv);
        _compQuad[3] = new MilkVertex(new Vector3(_compX1, _compY1, 0f), white, uv);

        _backend.DrawTriangleStrip(_compQuad, null, blend);
    }

    private static uint ShadeColor(ReadOnlySpan<float> shade, int corner, float mix)
        => PackColor(shade[corner * 3] * mix, shade[(corner * 3) + 1] * mix, shade[(corner * 3) + 2] * mix, 1f);

    /// <summary>
    /// <c>CPlugin::DrawSprites</c>（milkdropfs.cpp:3300-3405）：<c>darken_center</c> + 内外边框。
    /// 它在 warp/blur 之后、composite 之前画进反馈纹理（原版 VS1）。
    /// </summary>
    /// <remarks>
    /// sprite 本体不在这里 —— 原版 <c>DrawUserSprites</c> 是在 composite 之后单独调的（<c>:1131</c>），
    /// 所以"拖尾"效果靠的是它自己往 VS1 里烧一遍，而不是这一帧被 feedback 带下去。
    /// </remarks>
    private void DrawSprites(PresetState preset)
    {
        _backend.SetShaders(null, null);

        var pf = preset.PerFrame;
        var width = _backend.Width;
        var height = _backend.Height;

        // 原版 2D 状态里 alpha 取自顶点色（support.cpp:168-169）；本移植的全局默认是纹理 alpha。
        _backend.SetAlphaSource(AlphaSource.Vertex);

        // ---- darken_center（milkdropfs.cpp:3310-3347）----
        if (pf["darken_center"].Value != 0.0)
        {
            const float halfSize = 0.05f;
            var centerColor = PackColor(0f, 0f, 0f, 3f / 32f);
            const uint edgeColor = 0x00000000;

            _centerVertices[0] = FlatVertex(0f, 0f, centerColor, width, height);
            _centerVertices[1] = FlatVertex(-halfSize * _aspectY, 0f, edgeColor, width, height);
            _centerVertices[2] = FlatVertex(0f, -halfSize, edgeColor, width, height);
            _centerVertices[3] = FlatVertex(halfSize * _aspectY, 0f, edgeColor, width, height);
            _centerVertices[4] = FlatVertex(0f, halfSize, edgeColor, width, height);
            _centerVertices[5] = _centerVertices[1];

            _backend.DrawTriangleFan(_centerVertices, null, BlendMode.Alpha);
        }

        // ---- 内外边框：同一块扇形绕屏幕中心转 90° 画四次（milkdropfs.cpp:3349-3404）----
        var outerSize = (float)pf["ob_size"].Value;
        var innerSize = (float)pf["ib_size"].Value;

        for (var it = 0; it < 2; it++)
        {
            var r = (float)pf[it == 0 ? "ob_r" : "ib_r"].Value;
            var g = (float)pf[it == 0 ? "ob_g" : "ib_g"].Value;
            var b = (float)pf[it == 0 ? "ob_b" : "ib_b"].Value;
            var a = (float)pf[it == 0 ? "ob_a" : "ib_a"].Value;

            if (a <= 0.001f)
            {
                continue;
            }

            var color = PackColor(r, g, b, a);
            var innerRadius = it == 0 ? 1f - outerSize : 1f - outerSize - innerSize;
            var outerRadius = it == 0 ? 1f : 1f - outerSize;

            // 旋转必须在 clip 空间做（x/y 是两根独立的轴），所以先存 clip 坐标再逐次转像素。
            _borderClip[0] = new Vector2(innerRadius, innerRadius);
            _borderClip[1] = new Vector2(outerRadius, outerRadius);
            _borderClip[2] = new Vector2(outerRadius, -outerRadius);
            _borderClip[3] = new Vector2(innerRadius, -innerRadius);

            for (var rot = 0; rot < 4; rot++)
            {
                for (var v = 0; v < 4; v++)
                {
                    var clip = _borderClip[v];
                    _borderVertices[v] = new MilkVertex(
                        ClipToPixel(clip.X, -clip.Y, width, height),
                        color,
                        Vector2.Zero);
                }

                // TRIANGLEFAN 两个三角形 = 一个象限的方框段
                _backend.DrawTriangleFan(_borderVertices, null, BlendMode.Alpha);

                var t = 1.570796327f;
                var cos = MathF.Cos(t);
                var sin = MathF.Sin(t);
                for (var v = 0; v < 4; v++)
                {
                    var x = _borderClip[v].X;
                    var y = _borderClip[v].Y;
                    _borderClip[v] = new Vector2((x * cos) - (y * sin), (x * sin) + (y * cos));
                }
            }
        }

        _backend.SetAlphaSource(AlphaSource.Texture);
    }

    /// <summary>
    /// <c>CPlugin::DrawUserSprites</c>（milkdropfs.cpp:3432-3777）：把 16 个槽里激活的 sprite
    /// 画到当前目标（后备缓冲），<c>burn</c> 非 0 时再往反馈纹理（VS1）烧一遍。
    /// </summary>
    private void DrawUserSprites(PresetState preset)
    {
        CurrentStage = PipelineStage.UserSprites;
        _backend.SetShaders(null, null);

        if (_sprites.ActiveCount == 0)
        {
            return;
        }

        var width = _backend.Width;
        var height = _backend.Height;
        var progress = preset.BlendProgress;

        for (var i = 0; i < _sprites.Slots.Count; i++)
        {
            var slot = _sprites[i];
            if (!slot.IsActive)
            {
                continue;
            }

            // 每帧输入 + 执行 per-frame 代码（milkdropfs.cpp:3479-3496）
            slot.SyncInputs(Time, Fps, _analyzer.MySound, progress, Frame);
            slot.RunFrameCode();

            var kill = slot.Get("done") != 0.0;
            var burnIn = slot.Get("burn") != 0.0;
            var blendMode = Math.Clamp((int)slot.Get("blendmode"), 0, 4);

            var parameters = new SpriteQuadParams(
                slot.Get("x"),
                slot.Get("y"),
                slot.Get("sx"),
                slot.Get("sy"),
                slot.Get("rot"),
                slot.Get("flipx") != 0.0,
                slot.Get("flipy") != 0.0,
                slot.Get("repeatx"),
                slot.Get("repeaty"),
                slot.Get("r"),
                slot.Get("g"),
                slot.Get("b"),
                slot.Get("a"),
                blendMode,
                slot.ImageWidth,
                slot.ImageHeight);

            var blend = SpriteGeometry.BlendFor(blendMode);
            _backend.SetAlphaSource(SpriteGeometry.AlphaSourceFor(blendMode));

            if (burnIn && _feedbackB is not null)
            {
                // 烧进 VS1（下一帧的反馈源）：多一遍 4:3 宽高比修正，这样拖尾不会随窗口比例变形。
                // 注意目标纹理不能同时绑在采样器上，先解绑。
                SpriteGeometry.Build(_spriteBurnVertices, parameters with { BurnInAspect = true }, width, height);

                UnbindAllTextures();
                _backend.PushRenderTarget(_feedbackB);
                _backend.DrawTriangleStrip(_spriteBurnVertices, slot.Texture, blend);
                _backend.SetTexture(0, null, wrap: false, bilinear: false);
                _backend.PopRenderTarget();
            }

            SpriteGeometry.Build(_spriteVertices, parameters, width, height);
            _backend.DrawTriangleStrip(_spriteVertices, slot.Texture, blend);

            if (kill)
            {
                KillSprite(i);
            }
        }

        _backend.SetAlphaSource(AlphaSource.Texture);
        _backend.SetTexture(0, null, wrap: false, bilinear: false);
    }

    /// <summary>无纹理、无 UV 的顶点：clip 输入按原版 y 向下（<c>-1</c> = 顶）。</summary>
    private static MilkVertex FlatVertex(float clipX, float clipY, uint color, int width, int height)
        => new(ClipToPixel(clipX, -clipY, width, height), color, Vector2.Zero);

    private static float ApplyVolumeAlpha(float alpha, float volumeAlpha)
        => Math.Clamp(alpha * volumeAlpha, 0f, 1f);

    private static float ResolutionAlpha(int width) => width switch
    {
        256 => 0.07f,
        512 => 0.09f,
        1024 => 0.11f,
        2048 => 0.13f,
        _ => 1f,
    };

    private static MilkVertex WaveVertex(float clipX, float clipY, int width, int height, uint color)
        => new(ClipToPixel(clipX, clipY, width, height), color, Vector2.Zero);

    private static Vector3 ClipToPixel(float clipX, float clipY, int width, int height)
        => new(((clipX * 0.5f) + 0.5f) * width, (0.5f - (clipY * 0.5f)) * height, 0f);

    private static float ClipX(float pixelX, int width) => ((pixelX / width) * 2f) - 1f;

    private static float ClipY(float pixelY, int height) => 1f - ((pixelY / height) * 2f);

    private static int SmoothWave(ReadOnlySpan<MilkVertex> input, Span<MilkVertex> output)
    {
        const float c1 = -0.15f;
        const float c2 = 1.15f;
        const float c3 = 1.15f;
        const float c4 = -0.15f;
        const float invSum = 1f / (c1 + c2 + c3 + c4);

        var j = 0;
        var iBelow = 0;
        var iAbove2 = 1;

        for (var i = 0; i < input.Length - 1; i++)
        {
            var iAbove = iAbove2;
            iAbove2 = Math.Min(input.Length - 1, i + 2);
            output[j] = input[i];

            var x = ((c1 * input[iBelow].Position.X) + (c2 * input[i].Position.X)
                     + (c3 * input[iAbove].Position.X) + (c4 * input[iAbove2].Position.X)) * invSum;
            var y = ((c1 * input[iBelow].Position.Y) + (c2 * input[i].Position.Y)
                     + (c3 * input[iAbove].Position.Y) + (c4 * input[iAbove2].Position.Y)) * invSum;

            output[j + 1] = new MilkVertex(
                new Vector3(x, y, 0f),
                input[i].Color,
                Vector2.Zero);

            iBelow = i;
            j += 2;
        }

        output[j++] = input[^1];
        return j;
    }

    /// <summary>按 0xAARRGGBB 打包颜色，分量范围 0..1。</summary>
    private static uint PackColor(float r, float g, float b, float a)
        => ((uint)ToByte(a) << 24) | ((uint)ToByte(r) << 16) | ((uint)ToByte(g) << 8) | ToByte(b);

    private static byte ToByte(float value) => (byte)Math.Clamp((int)(value * 255f), 0, 255);

    /// <summary>整体平移一组像素空间顶点（描粗用的 4 遍错位）。</summary>
    private static void OffsetVertices(Span<MilkVertex> vertices, float dx, float dy)
    {
        for (var i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            vertices[i] = new MilkVertex(
                new Vector3(v.Position.X + dx, v.Position.Y + dy, v.Position.Z),
                v.Color,
                v.TexCoord);
        }
    }

    // ---------------- blur 链 ----------------

    private void EnsureBlurShaders()
    {
        if (_blurCompileAttempted)
        {
            return;
        }

        _blurCompileAttempted = true;
        BlurError = null;

        _blurVertexShader = _backend.CreateVertexShader(
            "blur_vs",
            BlurShaderTemplates.VertexShaderSource,
            out var vertexError,
            "VS",
            "vs_2_0");

        _blurHorizontalShader = _backend.CreatePixelShader(
            "blur_h",
            BlurShaderTemplates.HorizontalShaderSource,
            out var horizontalError,
            "PS",
            "ps_2_0");

        _blurVerticalShader = _backend.CreatePixelShader(
            "blur_v",
            BlurShaderTemplates.VerticalShaderSource,
            out var verticalError,
            "PS",
            "ps_2_0");

        BlurError = vertexError ?? horizontalError ?? verticalError;
    }

    /// <summary>
    /// 创建 6 张 blur RT（plugin.cpp:1985-2006）：
    /// [0] = 1/2，[1] = 1/4（blur1），[2] = 1/8，[3] = 1/8（blur2），[4] = 1/16，[5] = 1/16（blur3）。
    /// </summary>
    private void EnsureBlurTargets()
    {
        if (_blurTargets[0] is not null
            && _blurTargetWidth == _backend.Width
            && _blurTargetHeight == _backend.Height)
        {
            return;
        }

        DisposeBlurTargets();
        _blurTargetWidth = _backend.Width;
        _blurTargetHeight = _backend.Height;

        var w = _backend.Width;
        var h = _backend.Height;

        for (var i = 0; i < _blurTargets.Length; i++)
        {
            if ((i & 1) == 0 || i < 2)
            {
                w = Math.Max(16, w / 2);
                h = Math.Max(16, h / 2);
            }

            var w2 = (w + 15) / 16 * 16;
            var h2 = (h + 3) / 4 * 4;
            _blurTargets[i] = _backend.CreateRenderTarget(w2, h2);
        }

        // 新 RT 内容是未定义的，先清黑，避免第一帧采样到垃圾。
        // 注意：RT 不能同时作为纹理绑定在采样器上，先解绑所有阶段。
        foreach (var target in _blurTargets)
        {
            UnbindAllTextures();
            _backend.PushRenderTarget(target!);
            _backend.ClearTarget(0xFF000000);
        }

        _backend.PopRenderTarget();
    }

    /// <summary>
    /// <c>CPlugin::BlurPasses</c>（milkdropfs.cpp:1501-1679）。
    /// 水平/垂直交替，每一对输出一级 blur1..3。
    /// </summary>
    private void BlurPasses(PresetState preset)
    {
        if (_blurLevelsUsed <= 0
            || _blurVertexShader is null
            || _blurHorizontalShader is null
            || _blurVerticalShader is null)
        {
            return;
        }

        var passes = Math.Min(_blurTargets.Length, _blurLevelsUsed * 2);
        ReadOnlySpan<float> weights = [4.0f, 3.8f, 3.5f, 2.9f, 1.9f, 1.2f, 0.7f, 0.3f];

        // warp 阶段可能把 blur RT 绑在采样器上；作为 render target 前必须全部解绑。
        UnbindAllTextures();

        ShaderParams.GetSafeBlurMinMax(preset, out var blurMin, out var blurMax);

        Span<float> scale = stackalloc float[3];
        Span<float> bias = stackalloc float[3];

        scale[0] = 1f / (blurMax[0] - blurMin[0]);
        bias[0] = -blurMin[0] * scale[0];

        var tempMin = (blurMin[1] - blurMin[0]) / (blurMax[0] - blurMin[0]);
        var tempMax = (blurMax[1] - blurMin[0]) / (blurMax[0] - blurMin[0]);
        scale[1] = 1f / (tempMax - tempMin);
        bias[1] = -tempMin * scale[1];

        tempMin = (blurMin[2] - blurMin[1]) / (blurMax[1] - blurMin[1]);
        tempMax = (blurMax[2] - blurMin[1]) / (blurMax[1] - blurMin[1]);
        scale[2] = 1f / (tempMax - tempMin);
        bias[2] = -tempMin * scale[2];

        var edgeDarken = (float)preset.PerFrame["blur1_edge_darken"].Value;

        for (var i = 0; i < passes; i++)
        {
            var target = _blurTargets[i]!;
            var source = i == 0 ? _feedbackB! : _blurTargets[i - 1]!;
            var srcW = i == 0 ? _backend.Width : source.Width;
            var srcH = i == 0 ? _backend.Height : source.Height;

            _backend.PushRenderTarget(target);
            _backend.SetTexture(0, source, wrap: false, bilinear: true);

            var program = (i & 1) == 0 ? _blurHorizontalShader : _blurVerticalShader;
            _backend.SetShaders(_blurVertexShader, program);
            program.SetVector("_c0", srcW, srcH, 1f / srcW, 1f / srcH);

            if ((i & 1) == 0)
            {
                var w1 = weights[0] + weights[1];
                var w2 = weights[2] + weights[3];
                var w3 = weights[4] + weights[5];
                var w4 = weights[6] + weights[7];
                var d1 = 2f * weights[1] / w1;
                var d2 = 2f + 2f * weights[3] / w2;
                var d3 = 4f + 2f * weights[5] / w3;
                var d4 = 6f + 2f * weights[7] / w4;
                var wDiv = 0.5f / (w1 + w2 + w3 + w4);

                program.SetVector("_c1", w1, w2, w3, w4);
                program.SetVector("_c2", d1, d2, d3, d4);
                program.SetVector("_c3", scale[i / 2], bias[i / 2], wDiv, 0f);
            }
            else
            {
                var w1 = weights[0] + weights[1] + weights[2] + weights[3];
                var w2 = weights[4] + weights[5] + weights[6] + weights[7];
                var d1 = 2f * ((weights[2] + weights[3]) / w1);
                var d2 = 2f + 2f * ((weights[6] + weights[7]) / w2);
                var wDiv = 1f / ((w1 + w2) * 2f);

                // 只有第一级垂直 pass 压暗边缘，否则高模糊层会出现黑边（milkdropfs.cpp:1652-1659）。
                var c1 = i == 1 ? 1f - edgeDarken : 1f;
                var c2 = i == 1 ? edgeDarken : 0f;

                program.SetVector("_c5", w1, w2, d1, d2);
                program.SetVector("_c6", wDiv, c1, c2, 5f);
            }

            _backend.DrawFullscreenQuad(source, BlendMode.Replace);
        }

        // 回到 warp 结果（B），后面的 shapes/waves 要画回这里。
        _backend.PushRenderTarget(_feedbackB!);
        _backend.SetShaders(null, null);
    }

    private void UnbindAllTextures()
    {
        // D3D9 最多 16 个采样器阶段；绑定 null 即可解绑。
        for (var stage = 0; stage < 16; stage++)
        {
            _backend.SetTexture(stage, null, wrap: false, bilinear: false);
        }
    }

    private void DisposeBlurTargets()
    {
        for (var i = 0; i < _blurTargets.Length; i++)
        {
            (_blurTargets[i] as IDisposable)?.Dispose();
            _blurTargets[i] = null;
        }

        _blurTargetWidth = 0;
        _blurTargetHeight = 0;
    }

    private void DisposeBlurShaders()
    {
        _blurVertexShader?.Dispose();
        _blurVertexShader = null;
        _blurHorizontalShader?.Dispose();
        _blurHorizontalShader = null;
        _blurVerticalShader?.Dispose();
        _blurVerticalShader = null;
        _blurCompileAttempted = false;
        _blurLevelsUsed = 0;
        BlurError = null;
    }

    private void UpdateAspect()
    {
        // plugin.cpp:2035-2038：长边压到 1，短边按比例缩；inv 用于最后还原宽高比
        var width = _backend.Width;
        var height = _backend.Height;

        _aspectX = height > width ? width / (float)height : 1.0f;
        _aspectY = width > height ? height / (float)width : 1.0f;
        _invAspectX = 1.0f / _aspectX;
        _invAspectY = 1.0f / _aspectY;
    }

    /// <summary>确保两张离屏反馈目标存在且尺寸与后备缓冲一致（尺寸变化时重建）。</summary>
    private bool EnsureFeedbackTargets()
    {
        var width = _backend.Width;
        var height = _backend.Height;

        if (_feedbackA is not null && _feedbackB is not null &&
            _renderTargetWidth == width && _renderTargetHeight == height)
        {
            return true;
        }

        (_feedbackA as IDisposable)?.Dispose();
        (_feedbackB as IDisposable)?.Dispose();

        _feedbackA = _backend.CreateRenderTarget(width, height);
        _feedbackB = _backend.CreateRenderTarget(width, height);
        _renderTargetWidth = width;
        _renderTargetHeight = height;

        // 新建的 RT 内容是未定义的（显存脏数据会让第一帧出现随机色块），先清黑。
        // 对应原版 AllocateRenderTargets 之后的那次 Clear。
        if (_feedbackA is not null && _feedbackB is not null)
        {
            _backend.PushRenderTarget(_feedbackA);
            _backend.ClearTarget(0xFF000000);
            _backend.PushRenderTarget(_feedbackB);
            _backend.ClearTarget(0xFF000000);
            _backend.PopRenderTarget();
            _backend.PopRenderTarget();
        }

        UpdateAspect();
        return _feedbackA is not null && _feedbackB is not null;
    }

    // ---------------- Spike S1 ----------------

    private bool EnsureFeedbackSpike()
    {
        // 目标每帧校验：窗口尺寸变化时在这里重建，着色器本身无需重建。
        if (!EnsureFeedbackTargets())
        {
            LastError = "创建反馈渲染目标失败";
            return false;
        }

        if (_spikeAvailable)
        {
            return true;
        }

        _passthroughVertexShader = _backend.CreateVertexShader("passthrough_vs", PassthroughVertexShader, out var vsError);
        if (_passthroughVertexShader is null)
        {
            LastError = "vs_2_0 编译失败: " + vsError;
            return false;
        }

        _warpShader = _backend.CreatePixelShader("feedback_warp_ps", FeedbackPixelShader, out var psError);
        if (_warpShader is null)
        {
            LastError = "ps_2_0 编译失败: " + psError;
            return false;
        }

        _spikeAvailable = true;
        LastError = null;
        return true;
    }

    private void RenderFeedbackSpike()
    {
        var a = _feedbackA!;
        var b = _feedbackB!;

        // 清后备缓冲（RT 不清，靠 warp pass 全屏覆盖 + 衰减）。
        _backend.Clear(0xFF000000);

        // pass 1：warp（A → B），带衰减/旋转/漂移。
        CurrentStage = PipelineStage.Warp;
        _backend.PushRenderTarget(b);
        _backend.SetShaders(_passthroughVertexShader, _warpShader);
        _warpShader!.SetVector("_tint", 0.985f, 0.99f, 0.995f, 1f);
        _warpShader.SetVector(
            "_warp",
            MathF.Sin(_time * 0.37f) * 0.004f,
            MathF.Cos(_time * 0.29f) * 0.004f,
            0.35f,
            0.997f);
        _backend.DrawFullscreenQuad(a, BlendMode.Replace);

        // pass 2：往 B 里叠一个运动光团（固定功能 + 加法混合）。
        CurrentStage = PipelineStage.CustomShapes;
        _backend.SetShaders(null, null);
        DrawBlob(BlendMode.Additive);
        _backend.PopRenderTarget();

        // pass 3：B → 后备缓冲。
        CurrentStage = PipelineStage.Composite;
        _backend.SetShaders(_passthroughVertexShader, null);
        _backend.DrawFullscreenQuad(b, BlendMode.Replace);
        _backend.SetShaders(null, null);

        // 乒乓交换（对应 milkdropfs.cpp:1134-1136）。
        CurrentStage = PipelineStage.SwapFeedback;
        (_feedbackA, _feedbackB) = (_feedbackB, _feedbackA);
    }

    private void DrawBlob(BlendMode blend)
    {
        var cx = _backend.Width * 0.5f + MathF.Cos(_time * 0.9f) * _backend.Width * 0.22f;
        var cy = _backend.Height * 0.5f + MathF.Sin(_time * 1.31f) * _backend.Height * 0.22f;
        var radius = MathF.Min(_backend.Width, _backend.Height) * 0.09f;

        var count = _blob.Length / 3;
        var hue = _time * 0.11f;
        var color = HsvToArgb(hue, 0.55f, 1f);
        var center = 0xFFFFFFFFu;

        for (var i = 0; i < count; i++)
        {
            var a0 = i * MathF.Tau / (count - 1);
            var a1 = (i + 1) * MathF.Tau / (count - 1);

            _blob[i * 3 + 0] = new MilkVertex(new Vector3(cx, cy, 0f), center, Vector2.Zero);
            _blob[i * 3 + 1] = new MilkVertex(new Vector3(cx + MathF.Cos(a0) * radius, cy + MathF.Sin(a0) * radius, 0f), color, Vector2.Zero);
            _blob[i * 3 + 2] = new MilkVertex(new Vector3(cx + MathF.Cos(a1) * radius, cy + MathF.Sin(a1) * radius, 0f), color, Vector2.Zero);
        }

        _backend.DrawTriangleFan(_blob, null, blend);
    }

    // ---------------- M0 测试图案 ----------------

    private void DrawFanPattern()
    {
        CurrentStage = PipelineStage.Composite;

        var cx = _backend.Width * 0.5f;
        var cy = _backend.Height * 0.5f;
        var radius = MathF.Min(cx, cy) * 0.55f;

        var count = _fan.Length / 3;
        for (var i = 0; i < count; i++)
        {
            var a0 = _time * 0.7f + i * MathF.Tau / (count - 1);
            var a1 = _time * 0.7f + (i + 1) * MathF.Tau / (count - 1);
            var color = HsvToArgb(i / (float)(count - 1), 0.75f, 1f);

            _fan[i * 3 + 0] = new MilkVertex(new Vector3(cx, cy, 0f), color, Vector2.Zero);
            _fan[i * 3 + 1] = new MilkVertex(new Vector3(cx + MathF.Cos(a0) * radius, cy + MathF.Sin(a0) * radius, 0f), color, Vector2.Zero);
            _fan[i * 3 + 2] = new MilkVertex(new Vector3(cx + MathF.Cos(a1) * radius, cy + MathF.Sin(a1) * radius, 0f), color, Vector2.Zero);
        }

        _backend.DrawTriangleFan(_fan, null, BlendMode.Alpha);
    }

    private static uint HsvToArgb(float h, float s, float v)
    {
        h = (h % 1f + 1f) % 1f;
        var i = (int)(h * 6f);
        var f = h * 6f - i;
        var p = v * (1f - s);
        var q = v * (1f - f * s);
        var t = v * (1f - (1f - f) * s);

        var (r, g, b) = (i % 6) switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };

        return 0xFF000000u | ((uint)(r * 255f) << 16) | ((uint)(g * 255f) << 8) | (uint)(b * 255f);
    }

    public void Dispose()
    {
        OnDeviceLost();
        _audioSource.Dispose();
        _backend.Dispose();
    }
}
