using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;
using MilkDrop.ProjectM.Rendering;
using Silk.NET.OpenGLES;
using Framebuffer = MilkDrop.ProjectM.Renderer.Framebuffer;
using Texture = MilkDrop.ProjectM.Renderer.Texture;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 一个 Milkdrop 预设的完整生命周期与逐帧渲染管线。
/// </summary>
/// <remarks>
/// <para>对应 <c>MilkdropPreset/MilkdropPreset.hpp</c> 与 <c>MilkdropPreset.cpp</c>。</para>
/// <para>
/// 一帧的绘制顺序（照抄 <c>MilkdropPreset.cpp:78-170</c>）：
/// 运动矢量 → 翻转上一帧 → warp 网格（含 UV 副附件）→ 模糊链 →
/// 形状/波形 → 中心暗角 → 边框 → 再翻转 → final composite → 交换双缓冲。
/// </para>
/// <para>
/// 与 C++ 的差异：用户精灵（user sprites）与歌曲标题动画还没移植，其余顺序完全一致。
/// </para>
/// </remarks>
public sealed class MilkdropPreset : Preset
{
    private readonly PresetState _state = new();

    /// <summary>双缓冲：每帧结束交换两个索引。</summary>
    private readonly Framebuffer _framebuffer = new(2);

    private readonly PerFrameContext _perFrameContext;
    private readonly PerPixelContext _perPixelContext;

    private readonly PerPixelMesh _perPixelMesh = new();
    private readonly MotionVectors _motionVectors;
    private readonly Waveform _waveform;
    private readonly DarkenCenter _darkenCenter;
    private readonly Border _border;
    private readonly CopyTexture _flipTexture = new();
    private readonly FinalComposite _finalComposite = new();

    private readonly CustomWaveform[] _customWaveforms = new CustomWaveform[Constants.CustomWaveformCount];
    private readonly CustomShape[] _customShapes = new CustomShape[Constants.CustomShapeCount];

    private TextureAttachment? _motionVectorUVMap;

    private int _currentFrameBuffer;
    private int _previousFrameBuffer = 1;
    private bool _isFirstFrame = true;

    /// <summary>从文件载入预设（对应 <c>MilkdropPreset(const std::string&amp;)</c>）。</summary>
    /// <exception cref="MilkdropPresetLoadException">文件读取或解析失败。</exception>
    public MilkdropPreset(string absoluteFilePath)
        : this()
    {
        ArgumentException.ThrowIfNullOrEmpty(absoluteFilePath);

        FilePath = absoluteFilePath;
        FileName = ParseFilename(absoluteFilePath);

        var parser = new PresetFileParser();
        if (!parser.ReadFile(absoluteFilePath))
        {
            throw new MilkdropPresetLoadException("Could not parse preset file \"" + absoluteFilePath + "\"");
        }

        InitializePreset(parser);
    }

    /// <summary>从流载入预设（对应 <c>MilkdropPreset(std::istream&amp;)</c>）。</summary>
    /// <exception cref="MilkdropPresetLoadException">数据读取或解析失败。</exception>
    public MilkdropPreset(Stream presetData)
        : this()
    {
        ArgumentNullException.ThrowIfNull(presetData);

        using var memory = new MemoryStream();
        presetData.CopyTo(memory);

        var parser = new PresetFileParser();
        if (!memory.TryGetBuffer(out ArraySegment<byte> buffer) || !parser.ReadBytes(buffer.AsSpan()))
        {
            throw new MilkdropPresetLoadException("Could not parse preset data.");
        }

        InitializePreset(parser);
    }

    private MilkdropPreset()
    {
        _perFrameContext = new PerFrameContext(_state.GlobalRegisters);
        _perPixelContext = new PerPixelContext(_state.GlobalRegisters);
        _motionVectors = new MotionVectors(_state);
        _waveform = new Waveform(_state);
        _darkenCenter = new DarkenCenter(_state);
        _border = new Border(_state);
    }

    /// <summary>预设文件路径（流载入时为空）。</summary>
    public string FilePath { get; private set; } = string.Empty;

    /// <summary>底层状态（测试用）。</summary>
    internal PresetState State => _state;

    /// <summary>当前帧缓冲索引（测试用）。</summary>
    internal int CurrentFrameBuffer => _currentFrameBuffer;

    /// <summary>是否还没画过一帧（测试用）。</summary>
    internal bool IsFirstFrame => _isFirstFrame;

    /// <summary>
    /// 准备好 GL 资源并跑一遍 init 表达式（对应 <c>MilkdropPreset::Initialize</c>）。
    /// </summary>
    public override void Initialize(RenderContext renderContext)
    {
        ArgumentNullException.ThrowIfNull(renderContext);

        if (renderContext.TextureManager is null)
        {
            throw new InvalidOperationException("RenderContext.TextureManager 未设置，无法初始化预设。");
        }

        _state.RenderContext.CopyFrom(renderContext);

        // 有了完整的渲染状态后再初始化变量与代码。
        CompileCodeAndRunInitExpressions();

        // 需要时更新 framebuffer 与 UV 贴图尺寸。
        _framebuffer.SetSize(renderContext.ViewportSizeX, renderContext.ViewportSizeY);
        _motionVectorUVMap?.SetSize(renderContext.ViewportSizeX, renderContext.ViewportSizeY);

        if (_state.MainTexture is null)
        {
            _state.MainTexture = _framebuffer.GetColorAttachmentTexture(1, 0);
        }

        _perPixelMesh.CompileWarpShader(_state);
        _finalComposite.CompileCompositeShader(_state);
    }

    /// <summary>渲染一帧（对应 <c>MilkdropPreset::RenderFrame</c>）。</summary>
    public override void RenderFrame(FrameAudioData audioData, RenderContext renderContext)
    {
        ArgumentNullException.ThrowIfNull(audioData);
        ArgumentNullException.ThrowIfNull(renderContext);

        _state.AudioData.CopyFrom(audioData);
        _state.RenderContext.CopyFrom(renderContext);

        // 需要时更新 framebuffer 与 UV 贴图尺寸。
        if (_framebuffer.SetSize(renderContext.ViewportSizeX, renderContext.ViewportSizeY))
        {
            _motionVectorUVMap?.SetSize(renderContext.ViewportSizeX, renderContext.ViewportSizeY);
            _isFirstFrame = true;
        }

        _state.MainTexture = _framebuffer.GetColorAttachmentTexture(_previousFrameBuffer, 0);

        // 先求值 per-frame 代码。
        PerFrameUpdate();

        var gl = GlContext.Gl;
        gl.Viewport(0, 0, (uint)renderContext.ViewportSizeX, (uint)renderContext.ViewportSizeY);

        _framebuffer.Bind(_previousFrameBuffer);

        // 运动矢量场：在 warp 之前画进上一帧纹理；初始化/改尺寸后的第一帧不画。
        if (!_isFirstFrame && _motionVectorUVMap is not null && !_motionVectorUVMap.Texture.Empty)
        {
            _motionVectors.Draw(_perFrameContext, _motionVectorUVMap.Texture);
        }

        // 把上一帧 Y 翻转后当作"主纹理"。
        _flipTexture.Draw(_framebuffer.GetColorAttachmentTexture(_previousFrameBuffer, 0), null, true, false);
        _state.MainTexture = _flipTexture.Texture;

        // 之后都画进当前帧缓冲。
        _framebuffer.Bind(_currentFrameBuffer);

        // 给 warp 挂上运动矢量 UV 贴图作第二个颜色附件（MRT）。
        if (_motionVectorUVMap is not null)
        {
            _framebuffer.SetAttachment(_currentFrameBuffer, 1, _motionVectorUVMap);
        }

        // 用 per-pixel 网格 + warp shader 把上一帧画成"变形后的图像"。
        _perPixelMesh.Draw(_state, _perFrameContext, _perPixelContext);

        // 摘掉 UV 贴图附件。
        _framebuffer.RemoveColorAttachment(_currentFrameBuffer, 1);

        // 更新模糊链。
        Texture? warpedImage = _framebuffer.GetColorAttachmentTexture(_previousFrameBuffer, 0);
        if (warpedImage is not null)
        {
            _state.BlurTexture.Update(warpedImage, _perFrameContext);
        }

        // 与音频相关的绘制：自定义形状 → 自定义波形 → 主波形。
        foreach (CustomShape shape in _customShapes)
        {
            shape?.Draw();
        }

        foreach (CustomWaveform wave in _customWaveforms)
        {
            wave?.Draw(_perFrameContext);
        }

        _waveform.Draw(_perFrameContext);

        // Milkdrop 的 DrawSprites()。
        if (_perFrameContext.DarkenCenter.Value > 0)
        {
            _darkenCenter.Draw();
        }

        _border.Draw(_perFrameContext);

        // 为最终合成再翻一次 Y。
        _flipTexture.Draw(_framebuffer.GetColorAttachmentTexture(_currentFrameBuffer, 0), null, true, false);
        _state.MainTexture = _flipTexture.Texture;

        // 上一帧缓冲已经用完，拿来画最终合成。
        _framebuffer.BindRead(_currentFrameBuffer);
        _framebuffer.BindDraw(_previousFrameBuffer);

        _finalComposite.Draw(_state, _perFrameContext);

        if (!_finalComposite.HasCompositeShader)
        {
            // 老式效果画出来仍是上下颠倒的，再翻一次。
            _flipTexture.Draw(_framebuffer.GetColorAttachmentTexture(_previousFrameBuffer, 0),
                _framebuffer, _previousFrameBuffer, true, false);
        }

        // 交换双缓冲。
        (_currentFrameBuffer, _previousFrameBuffer) = (_previousFrameBuffer, _currentFrameBuffer);

        _isFirstFrame = false;
    }

    /// <summary>
    /// 本帧的输出纹理（对应 <c>MilkdropPreset::OutputTexture</c>）：画完一帧后合成结果在
    /// "当前"帧缓冲里。
    /// </summary>
    public override Texture? OutputTexture() => _framebuffer.GetColorAttachmentTexture(_currentFrameBuffer, 0);

    /// <summary>
    /// 首帧底图：直接翻到上一帧缓冲，下一帧就在它上面 warp（对应 <c>DrawInitialImage</c>）。
    /// </summary>
    public override void DrawInitialImage(Texture? image, RenderContext renderContext)
    {
        ArgumentNullException.ThrowIfNull(renderContext);

        _framebuffer.SetSize(renderContext.ViewportSizeX, renderContext.ViewportSizeY);

        _flipTexture.Draw(image, _framebuffer, _previousFrameBuffer);
    }

    /// <summary>求值 per-frame 代码并夹紧 <c>gamma</c>/<c>echo_zoom</c>（对应 <c>PerFrameUpdate</c>）。</summary>
    public void PerFrameUpdate()
    {
        _perFrameContext.LoadStateVariables(_state);
        _perPixelContext.LoadStateReadOnlyVariables(_state, _perFrameContext);

        _perFrameContext.ExecutePerFrameCode();

        _perPixelContext.LoadPerFrameQVariables(_state, _perFrameContext);

        // 夹紧 gamma 与 echo zoom。
        _perFrameContext.Gamma.Value = Math.Clamp(_perFrameContext.Gamma.Value, 0.0, 8.0);
        _perFrameContext.EchoZoom.Value = Math.Clamp(_perFrameContext.EchoZoom.Value, 0.001, 1000.0);
    }

    public override void Dispose()
    {
        if (GlContext.IsInitialized)
        {
            foreach (CustomShape shape in _customShapes)
            {
                shape?.Dispose();
            }

            foreach (CustomWaveform wave in _customWaveforms)
            {
                wave?.Dispose();
            }

            _waveform.Dispose();
            _finalComposite.Dispose();
            _flipTexture.Dispose();
            _border.Dispose();
            _darkenCenter.Dispose();
            _motionVectors.Dispose();
            _perPixelMesh.Dispose();
            _motionVectorUVMap?.Dispose();
            _framebuffer.Dispose();
            _state.Dispose();
        }

        _motionVectorUVMap = null;
    }

    /// <summary>
    /// 从路径里取文件名（对应 <c>MilkdropPreset.cpp:305-315</c>）。
    /// </summary>
    /// <remarks>
    /// 上游只认 <c>'/'</c>：Windows 反斜杠路径会返回空串。这里保持同样的行为，
    /// 免得影响预设名显示之外的任何逻辑。
    /// </remarks>
    internal static string ParseFilename(string filename)
    {
        int start = filename.LastIndexOf('/');

        if (start < 0 || start >= filename.Length - 1)
        {
            return string.Empty;
        }

        return filename[(start + 1)..];
    }

    /// <remarks>对应 <c>MilkdropPreset::InitializePreset</c>。</remarks>
    private void InitializePreset(PresetFileParser parsedFile)
    {
        // 离屏渲染表面。
        _motionVectorUVMap = new TextureAttachment((int)GLEnum.RG16f, GLEnum.RG, GLEnum.Float, 0, 0);
        _framebuffer.CreateColorAttachment(0, 0); // 主图像 1
        _framebuffer.CreateColorAttachment(1, 0); // 主图像 2

        Framebuffer.Unbind();

        // 把预设初值写进状态。
        _state.Initialize(parsedFile);

        // 注册表达式变量。
        _perFrameContext.RegisterBuiltinVariables();
        _perPixelContext.RegisterBuiltinVariables();

        // 自定义波形。
        for (var i = 0; i < Constants.CustomWaveformCount; i++)
        {
            var wave = new CustomWaveform(_state);
            wave.Initialize(parsedFile, i);
            _customWaveforms[i] = wave;
        }

        // 自定义形状。
        for (var i = 0; i < Constants.CustomShapeCount; i++)
        {
            var shape = new CustomShape(_state);
            shape.Initialize(parsedFile, i);
            _customShapes[i] = shape;
        }

        // 预载着色器代码。
        LoadShaderCode();
    }

    /// <remarks>对应 <c>MilkdropPreset::CompileCodeAndRunInitExpressions</c>。</remarks>
    private void CompileCodeAndRunInitExpressions()
    {
        // per-frame init 与 per-frame 代码。
        _perFrameContext.LoadStateVariables(_state);
        _perFrameContext.EvaluateInitCode(_state);
        _perFrameContext.CompilePerFrameCode(_state.PerFrameCode);

        // per-vertex 代码。
        _perPixelContext.CompilePerPixelCode(_state.PerPixelCode);

        foreach (CustomWaveform wave in _customWaveforms)
        {
            wave?.CompileCodeAndRunInitExpressions(_perFrameContext);
        }

        foreach (CustomShape shape in _customShapes)
        {
            shape?.CompileCodeAndRunInitExpressions();
        }
    }

    /// <remarks>对应 <c>MilkdropPreset::LoadShaderCode</c>。</remarks>
    private void LoadShaderCode()
    {
        _perPixelMesh.LoadWarpShader(_state);
        _finalComposite.LoadCompositeShader(_state);
    }
}
