using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;
using MilkDrop.ProjectM.Rendering;
using Silk.NET.OpenGLES;
using Texture = MilkDrop.ProjectM.Renderer.Texture;

namespace MilkDrop.ProjectM;

/// <summary>
/// 引擎门面：管理预设工厂、纹理管理器、转场、音频分析与逐帧渲染。
/// </summary>
/// <remarks>
/// <para>对应 <c>ProjectM.hpp:47-220</c> 与 <c>ProjectM.cpp</c>。</para>
/// <para>
/// 改名原因：C++ 的 <c>libprojectM::ProjectM</c> 在 C# 里会与命名空间 <c>MilkDrop.ProjectM</c> 同名，
/// 引用时无法区分类型与命名空间，因此叫 <c>ProjectMEngine</c>。
/// </para>
/// <para>
/// 与 C++ 的差异：<c>PresetSwitchRequestedEvent</c>/<c>PresetSwitchFailedEvent</c> 两个虚函数换成
/// <see cref="PresetSwitchRequested"/> / <see cref="PresetSwitchFailed"/> 两个事件；
/// Touch 系列（C++ 里也是空实现）不移植。
/// </para>
/// </remarks>
public sealed class ProjectMEngine : IDisposable
{
    private readonly PresetFactoryManager _presetFactoryManager = new();
    private readonly Pcm _audioStorage = new();
    private readonly List<string> _textureSearchPaths = [];

    private readonly TimeKeeper _timeKeeper;
    private readonly TransitionShaderManager _transitionShaderManager;
    private readonly CopyTexture _textureCopier;

    private TextureManager _textureManager;

    private Preset? _activePreset;
    private Preset? _transitioningPreset;
    private PresetTransition? _transition;

    private uint _meshX = 32;
    private uint _meshY = 24;

    private int _windowWidth;
    private int _windowHeight;

    private double _presetDuration = 30.0;
    private double _softCutDuration = 3.0;
    private double _hardCutDuration = 20.0;
    private bool _hardCutEnabled;
    private float _hardCutSensitivity = 2.0f;
    private float _beatSensitivity = 1.0f;
    private bool _aspectCorrection = true;
    private float _easterEgg = 1.0f;

    private float _previousFrameVolume;

    private int _frameCount;
    private bool _presetLocked;
    private bool _presetChangeNotified;

    /// <summary>最近一次加载的预设来源（文件路径或 <c>idle://</c> URL）。</summary>
    private string _activePresetSource = string.Empty;

    /// <summary>把新预设初始化后再挑选"立即切换 / 软切"两条路径。</summary>
    /// <param name="textureSearchPaths">纹理搜索路径（null = 使用内嵌纹理）。</param>
    /// <exception cref="InvalidOperationException">GL 上下文未初始化。</exception>
    public ProjectMEngine(IEnumerable<string>? textureSearchPaths = null)
    {
        if (!GlContext.IsInitialized)
        {
            throw new InvalidOperationException("ProjectMEngine 必须在 GL 上下文当前线程构造（先调用 GlContext.Initialize）。");
        }

        if (textureSearchPaths is not null)
        {
            _textureSearchPaths.AddRange(textureSearchPaths);
        }

        _textureManager = new TextureManager(_textureSearchPaths);
        _transitionShaderManager = new TransitionShaderManager();
        _textureCopier = new CopyTexture();

        _presetFactoryManager.Initialize();

        _timeKeeper = new TimeKeeper(_presetDuration, _softCutDuration, _hardCutDuration, _easterEgg);

        LoadIdlePreset();
        _timeKeeper.StartPreset();
    }

    /// <summary>引擎请求切换到下一个预设（<c>true</c> = 硬切）。</summary>
    /// <remarks>对应 <c>PresetSwitchRequestedEvent</c>。</remarks>
    public event Action<bool>? PresetSwitchRequested;

    /// <summary>预设加载失败（文件名 + 错误信息）。</summary>
    /// <remarks>对应 <c>PresetSwitchFailedEvent</c>。</remarks>
    public event Action<string, string>? PresetSwitchFailed;

    /// <summary>
    /// 当前预设的显示名。
    /// </summary>
    /// <remarks>
    /// 优先用 <see cref="Preset.FileName"/>；但 <c>ParseFilename</c> 与 C++ 一样只认 <c>/</c>，
    /// Windows 路径会解析出空串，这时回退到加载时用的完整路径/URL。
    /// </remarks>
    public string ActivePresetFileName =>
        string.IsNullOrEmpty(_activePreset?.FileName) ? _activePresetSource : _activePreset!.FileName;

    /// <summary>是否正在做软切。</summary>
    public bool IsTransitioning => _transitioningPreset is not null;

    /// <summary>已渲染帧数。</summary>
    public int FrameCount => _frameCount;

    /// <summary>当前预设的播放进度（0..1）。</summary>
    public double PresetProgress => _timeKeeper.PresetProgressA();

    /// <summary>音频分析缓冲（宿主往里塞 PCM）。</summary>
    public Pcm Pcm => _audioStorage;

    /// <summary>纹理管理器（只读用途，例如列出已加载纹理）。</summary>
    public TextureManager TextureManager => _textureManager;

    /// <summary>加载预设文件并做立即/平滑切换。</summary>
    public void LoadPresetFile(string presetFilename, bool smoothTransition)
    {
        try
        {
            _textureManager.PurgeTextures();
            Preset? preset = _presetFactoryManager.CreatePresetFromFile(presetFilename);
            if (preset is not null)
            {
                _activePresetSource = presetFilename;
            }

            StartPresetTransition(preset, !smoothTransition);
        }
        catch (Exception ex)
        {
            PresetSwitchFailed?.Invoke(presetFilename, ex.Message);
        }
    }

    /// <summary>从流加载预设（按 Milkdrop 格式解析）。</summary>
    public void LoadPresetData(Stream presetData, bool smoothTransition)
    {
        try
        {
            _textureManager.PurgeTextures();
            StartPresetTransition(_presetFactoryManager.CreatePresetFromStream(".milk", presetData),
                !smoothTransition);
        }
        catch (Exception ex)
        {
            PresetSwitchFailed?.Invoke(string.Empty, ex.Message);
        }
    }

    /// <summary>设置渲染尺寸；任一维为 0 时不渲染。</summary>
    public void SetWindowSize(int width, int height)
    {
        _windowWidth = Math.Max(0, width);
        _windowHeight = Math.Max(0, height);
    }

    /// <summary>
    /// 设置纹理搜索路径并重建纹理管理器（会清缓存，可能卡顿）。
    /// </summary>
    public void SetTexturePaths(IEnumerable<string> texturePaths)
    {
        ArgumentNullException.ThrowIfNull(texturePaths);

        _textureSearchPaths.Clear();
        _textureSearchPaths.AddRange(texturePaths);

        RecreateTextureManager();
    }

    /// <summary>按当前路径重建纹理管理器。</summary>
    public void ResetTextures() => RecreateTextureManager();

    /// <summary>渲染一帧。</summary>
    public void RenderFrame()
    {
        // 窗口面积为 0 时不渲染。
        if (_windowWidth == 0 || _windowHeight == 0)
        {
            return;
        }

        _timeKeeper.UpdateTimers();

        _audioStorage.UpdateFrameAudioData(_timeKeeper.SecondsSinceLastFrame, (uint)_frameCount);
        FrameAudioData audioData = _audioStorage.GetFrameAudioData();

        // 预设到期 / 硬切判定（每帧只通知一次）。
        if (!_presetChangeNotified)
        {
            if (_timeKeeper.PresetProgressA() >= 1.0 && !_timeKeeper.IsSmoothing())
            {
                _presetChangeNotified = true;
                PresetSwitchRequested?.Invoke(false);
            }
            else if (_hardCutEnabled &&
                     _frameCount > 50 &&
                     audioData.Vol - _previousFrameVolume > _hardCutSensitivity &&
                     _timeKeeper.CanHardCut())
            {
                _presetChangeNotified = true;
                PresetSwitchRequested?.Invoke(true);
            }
        }

        // 没有任何预设时加载待机预设。
        if (_activePreset is null)
        {
            LoadIdlePreset();
            if (_activePreset is null)
            {
                return;
            }

            _activePreset.Initialize(GetRenderContext());
        }

        if (_timeKeeper.IsSmoothing() && _transitioningPreset is not null && _timeKeeper.SmoothRatio() >= 1.0)
        {
            _timeKeeper.EndSmoothing();
        }

        RenderContext renderContext = GetRenderContext();

        if (_transition is not null && _transitioningPreset is not null)
        {
            if (_transition.IsDone())
            {
                _activePreset?.Dispose();
                _activePreset = _transitioningPreset;
                _transitioningPreset = null;
                _transition.Dispose();
                _transition = null;
            }
            else
            {
                _transitioningPreset.RenderFrame(audioData, renderContext);
            }
        }

        _activePreset.RenderFrame(audioData, renderContext);

        // 画回默认帧缓冲。
        var gl = GlContext.Gl;
        gl.BindFramebuffer(GLEnum.Framebuffer, 0);
        gl.Viewport(0, 0, (uint)renderContext.ViewportSizeX, (uint)renderContext.ViewportSizeY);

        // WebGL2/ANGLE 上预设渲染会污染 FBO 0 的 draw buffer 状态，必须显式设回 GL_BACK。
        GLEnum backBuffer = GLEnum.Back;
        gl.DrawBuffers(1, ref backBuffer);

        if (_transition is not null && _transitioningPreset is not null)
        {
            _transition.Draw(_activePreset, _transitioningPreset, renderContext, audioData);
        }
        else
        {
            _textureCopier.Draw(_activePreset.OutputTexture(), false, false);
        }

        _frameCount++;
        _previousFrameVolume = audioData.Vol;
    }

    /// <summary>设置窗口前先探测的帧率（写进 <c>RenderContext.Fps</c>）。</summary>
    public int TargetFramesPerSecond { get; set; } = 35;

    /// <summary>
    /// 固定时间步长（秒）；大于 0 时不再读真实时钟，每帧按它推进。
    /// </summary>
    /// <remarks>
    /// 批量跑与 AOT/JIT 截图对比必须用它，否则"第 N 帧"在两次运行里对应不同的 <c>time</c>。
    /// </remarks>
    public double FixedTimeStep
    {
        get => _timeKeeper.FixedTimeStep;
        set => _timeKeeper.FixedTimeStep = value;
    }

    public float BeatSensitivity
    {
        get => _beatSensitivity;
        set => _beatSensitivity = Math.Clamp(value, 0.0f, 2.0f);
    }

    public double SoftCutDuration
    {
        get => _softCutDuration;
        set
        {
            _softCutDuration = value;
            _timeKeeper.ChangeSoftCutDuration(value);
        }
    }

    public double HardCutDuration
    {
        get => _hardCutDuration;
        set
        {
            _hardCutDuration = (int)value;
            _timeKeeper.ChangeHardCutDuration(value);
        }
    }

    public bool HardCutEnabled
    {
        get => _hardCutEnabled;
        set => _hardCutEnabled = value;
    }

    public float HardCutSensitivity
    {
        get => _hardCutSensitivity;
        set => _hardCutSensitivity = value;
    }

    public double PresetDuration
    {
        get => _timeKeeper.PresetDuration;
        set
        {
            _presetDuration = value;
            _timeKeeper.ChangePresetDuration(value);
        }
    }

    public bool AspectCorrection
    {
        get => _aspectCorrection;
        set => _aspectCorrection = value;
    }

    public float EasterEgg
    {
        get => _easterEgg;
        set
        {
            _easterEgg = value;
            _timeKeeper.ChangeEasterEgg(value);
        }
    }

    public int WindowWidth => _windowWidth;

    public int WindowHeight => _windowHeight;

    public bool PresetLocked
    {
        get => _presetLocked;
        set
        {
            _presetLocked = value;
            _presetChangeNotified = value;
        }
    }

    /// <summary>读取网格尺寸。</summary>
    public (uint X, uint Y) MeshSize => (_meshX, _meshY);

    /// <summary>设置 per-pixel 网格尺寸（自动取偶数并夹到 [8, 400]）。</summary>
    public void SetMeshSize(uint meshResolutionX, uint meshResolutionY)
    {
        // 必须偶数，否则中缝的横/竖条会缺一条。
        if (meshResolutionX % 2 == 1)
        {
            meshResolutionX++;
        }

        if (meshResolutionY % 2 == 1)
        {
            meshResolutionY++;
        }

        _meshX = Math.Clamp(meshResolutionX, 8u, 400u);
        _meshY = Math.Clamp(meshResolutionY, 8u, 400u);
    }

    public void Dispose()
    {
        if (!GlContext.IsInitialized)
        {
            return;
        }

        _transition?.Dispose();
        _transitioningPreset?.Dispose();
        _activePreset?.Dispose();
        _textureCopier.Dispose();
        _transitionShaderManager.Dispose();
        _textureManager.Dispose();

        _transition = null;
        _transitioningPreset = null;
        _activePreset = null;
    }

    private void StartPresetTransition(Preset? preset, bool hardCut)
    {
        _presetChangeNotified = _presetLocked;

        if (preset is null)
        {
            return;
        }

        preset.Initialize(GetRenderContext());

        // 已经在转场中：立刻把转场目标提为当前预设。
        if (_transitioningPreset is not null)
        {
            _activePreset?.Dispose();
            _activePreset = _transitioningPreset;
            _transitioningPreset = null;
            _transition?.Dispose();
            _transition = null;
        }

        if (_activePreset is not null)
        {
            preset.DrawInitialImage(_activePreset.OutputTexture(), GetRenderContext());
        }

        if (hardCut)
        {
            _activePreset?.Dispose();
            _activePreset = preset;
            _timeKeeper.StartPreset();
        }
        else
        {
            _transitioningPreset?.Dispose();
            _transitioningPreset = preset;
            _timeKeeper.StartSmoothing();
            _transition?.Dispose();
            _transition = new PresetTransition(_transitionShaderManager.RandomTransition(), _softCutDuration);
        }
    }

    private void LoadIdlePreset()
    {
        LoadPresetFile("idle://Geiss & Sperl - Feedback (projectM idle HDR mix).milk", false);
    }

    private RenderContext GetRenderContext()
    {
        float aspectX = _windowHeight > _windowWidth ? _windowWidth / (float)_windowHeight : 1.0f;
        float aspectY = _windowWidth > _windowHeight ? _windowHeight / (float)_windowWidth : 1.0f;

        return new RenderContext
        {
            ViewportSizeX = _windowWidth,
            ViewportSizeY = _windowHeight,
            Time = (float)_timeKeeper.GetRunningTime(),
            Progress = (float)_timeKeeper.PresetProgressA(),
            Fps = TargetFramesPerSecond,
            Frame = _frameCount,
            AspectX = aspectX,
            AspectY = aspectY,
            InvAspectX = 1.0f / aspectX,
            InvAspectY = 1.0f / aspectY,
            PerPixelMeshX = (int)_meshX,
            PerPixelMeshY = (int)_meshY,
            TextureManager = _textureManager,
        };
    }

    private void RecreateTextureManager()
    {
        _textureManager.Dispose();
        _textureManager = new TextureManager(_textureSearchPaths);
    }
}
