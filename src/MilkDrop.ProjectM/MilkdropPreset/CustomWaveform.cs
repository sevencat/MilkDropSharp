using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;
using MilkDrop.Rendering.Gles3;
using Silk.NET.OpenGLES;
using Shader = MilkDrop.Rendering.Gles3.Shader;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 预设自定义波形（<c>wavecode_N_*</c>）：顶点由 per-point 表达式逐点算出。
/// </summary>
/// <remarks>对应 <c>MilkdropPreset/CustomWaveform.hpp</c> 与 <c>CustomWaveform.cpp</c>。</remarks>
public sealed class CustomWaveform : RenderItem
{
    /// <summary>可用的最大输入样本数（示波器 480 与频谱 512 取大者）。</summary>
    private static readonly int MaxSamples = Math.Max(AudioConstants.WaveformSamples, AudioConstants.SpectrumSamples);

    private readonly PresetState _presetState;

    private readonly WaveformPerFrameContext _perFrameContext;
    private readonly WaveformPerPointContext _perPointContext;

    private ColoredPoint[] _points = [];

    public CustomWaveform(PresetState presetState)
    {
        ArgumentNullException.ThrowIfNull(presetState);
        _presetState = presetState;

        _perFrameContext = new WaveformPerFrameContext(presetState.GlobalRegisters);
        _perPointContext = new WaveformPerPointContext(presetState.GlobalRegisters);

        Init();

        _perFrameContext.RegisterBuiltinVariables();
        _perPointContext.RegisterBuiltinVariables();
    }

    // ---- 状态（对应 C++ 的私有成员；这里是给两个上下文读的） ----

    /// <summary>波形在预设里的编号。</summary>
    public int Index { get; private set; }

    internal float[] TValuesAfterInitCode { get; } = new float[Constants.TVarCount];

    /// <summary>顶点数（per-frame 代码可改）。</summary>
    public int Samples { get; private set; } = Constants.WaveformMaxPoints;

    public float X { get; private set; } = 0.5f;
    public float Y { get; private set; } = 0.5f;
    public float R { get; private set; } = 1.0f;
    public float G { get; private set; } = 1.0f;
    public float B { get; private set; } = 1.0f;
    public float A { get; private set; } = 1.0f;

    private int _enabled;
    private int _separation;
    private float _scaling = 1.0f;
    private float _smoothing = 0.5f;
    private bool _spectrum;
    private bool _useDots;
    private bool _drawThick;
    private bool _additive;

    public override unsafe void InitVertexAttrib()
    {
        var gl = GlContext.Gl;

        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);

        gl.VertexAttribPointer(0, 2, GLEnum.Float, false, VertexLayout.ColoredPointSize, (void*)0);
        gl.VertexAttribPointer(1, 4, GLEnum.Float, false, VertexLayout.ColoredPointSize,
            (void*)VertexLayout.ColoredPointRgbOffset);

        var vertexData = new ColoredPoint[MaxSamples * 2 + 2];
        gl.BufferData<ColoredPoint>(GLEnum.ArrayBuffer, vertexData, GLEnum.StreamDraw);
    }

    /// <summary>从预设文件读取初值（对应 <c>Initialize</c>）。</summary>
    public void Initialize(PresetFileParser parsedFile, int index)
    {
        ArgumentNullException.ThrowIfNull(parsedFile);

        string wavecodePrefix = "wavecode_" + index + "_";

        Index = index;
        _enabled = parsedFile.GetInt(wavecodePrefix + "enabled", _enabled);
        Samples = parsedFile.GetInt(wavecodePrefix + "samples", Samples);
        _separation = parsedFile.GetInt(wavecodePrefix + "sep", _separation);
        _spectrum = parsedFile.GetBool(wavecodePrefix + "bSpectrum", _spectrum);
        _useDots = parsedFile.GetBool(wavecodePrefix + "bUseDots", _useDots);
        _drawThick = parsedFile.GetBool(wavecodePrefix + "bDrawThick", _drawThick);
        _additive = parsedFile.GetBool(wavecodePrefix + "bAdditive", _additive);
        _scaling = parsedFile.GetFloat(wavecodePrefix + "scaling", _scaling);
        _smoothing = parsedFile.GetFloat(wavecodePrefix + "smoothing", _smoothing);
        R = parsedFile.GetFloat(wavecodePrefix + "r", R);
        G = parsedFile.GetFloat(wavecodePrefix + "g", G);
        B = parsedFile.GetFloat(wavecodePrefix + "b", B);
        A = parsedFile.GetFloat(wavecodePrefix + "a", A);
    }

    /// <summary>
    /// 编译全部代码并执行 init 表达式（对应 <c>CompileCodeAndRunInitExpressions</c>）。
    /// </summary>
    /// <exception cref="MilkdropCompileException">代码编译失败。</exception>
    public void CompileCodeAndRunInitExpressions(PerFrameContext presetPerFrameContext)
    {
        ArgumentNullException.ThrowIfNull(presetPerFrameContext);

        _perFrameContext.LoadStateVariables(_presetState, presetPerFrameContext, this);
        _perFrameContext.EvaluateInitCode(_presetState.CustomWaveInitCode[Index], this);

        for (var t = 0; t < Constants.TVarCount; t++)
        {
            TValuesAfterInitCode[t] = (float)_perFrameContext.TVar(t).Value;
        }

        _perFrameContext.CompilePerFrameCode(_presetState.CustomWavePerFrameCode[Index], this);
        _perPointContext.CompilePerPointCode(_presetState.CustomWavePerPointCode[Index], this);
    }

    /// <summary>画波形（对应 <c>CustomWaveform::Draw</c>）。</summary>
    public unsafe void Draw(PerFrameContext presetPerFrameContext)
    {
        ArgumentNullException.ThrowIfNull(presetPerFrameContext);

        if (_enabled == 0)
        {
            return;
        }

        int maxInputSampleCount = _spectrum ? AudioConstants.SpectrumSamples : AudioConstants.WaveformSamples;

        // 先跑 per-frame 代码。
        LoadPerFrameEvaluationVariables(presetPerFrameContext);
        _perFrameContext.ExecutePerFrameCode();

        // 把 Q/T 变量拷给 per-point 上下文。
        InitPerPointEvaluationVariables();

        int sampleCount = Math.Min(MaxSamples, Math.Max(0, (int)_perFrameContext.Samples.Value));
        int separation = Math.Min(sampleCount - 1, Math.Max(0, _separation));

        // 点或线都画不出来的情况直接跳过。
        if ((_useDots && sampleCount < 1) || sampleCount < 2)
        {
            return;
        }

        float[] pcmL = _spectrum ? _presetState.AudioData.SpectrumLeft : _presetState.AudioData.WaveformLeft;
        float[] pcmR = _spectrum ? _presetState.AudioData.SpectrumRight : _presetState.AudioData.WaveformRight;

        float mult = _scaling * _presetState.WaveScale * (_spectrum ? 0.15f : 0.004f);

        // PCM 数据平滑。
        var scaling = 1.0f;
        if (_spectrum)
        {
            // 频谱模式里 sep 表示右侧要去掉多少样本，剩下的按请求数缩放。
            scaling = (maxInputSampleCount - separation) / (float)sampleCount;
        }
        else if (sampleCount > maxInputSampleCount)
        {
            // 示波器模式忽略 sep，用缩放系数把 480 个样本拉长。
            scaling = maxInputSampleCount / (float)sampleCount;
        }

        float mix1 = MathF.Pow(_smoothing * 0.98f, 0.5f);
        float mix2 = 1.0f - mix1;

        var sampleDataL = new float[MaxSamples];
        var sampleDataR = new float[MaxSamples];

        sampleDataL[0] = pcmL[0];
        sampleDataR[0] = pcmR[0];

        // 正向平滑。
        for (var sample = 1; sample < sampleCount; sample++)
        {
            int pcmSample = (int)(sample * scaling);
            pcmSample = Math.Clamp(pcmSample, 0, maxInputSampleCount - 1);

            sampleDataL[sample] = pcmL[pcmSample] * mix2 + sampleDataL[sample - 1] * mix1;
            sampleDataR[sample] = pcmR[pcmSample] * mix2 + sampleDataR[sample - 1] * mix1;
        }

        // 反向平滑（消掉首尾的不对称）。
        for (var sample = sampleCount - 2; sample >= 0; sample--)
        {
            sampleDataL[sample] = sampleDataL[sample] * mix2 + sampleDataL[sample + 1] * mix1;
            sampleDataR[sample] = sampleDataR[sample] * mix2 + sampleDataR[sample + 1] * mix1;
        }

        // 缩放到最终大小。
        for (var sample = 0; sample < sampleCount; sample++)
        {
            sampleDataL[sample] *= mult;
            sampleDataR[sample] *= mult;
        }

        var pointsTransformed = new ColoredPoint[sampleCount];

        float sampleMultiplicator = sampleCount > 1 ? 1.0f / (sampleCount - 1) : 0.0f;
        for (var sample = 0; sample < sampleCount; sample++)
        {
            float sampleIndex = sample * sampleMultiplicator;
            LoadPerPointEvaluationVariables(sampleIndex, sampleDataL[sample], sampleDataR[sample]);

            _perPointContext.ExecutePerPointCode();

            pointsTransformed[sample] = new ColoredPoint
            {
                X = (float)((_perPointContext.X.Value * 2.0 - 1.0) * _presetState.RenderContext.InvAspectX),
                Y = (float)((_perPointContext.Y.Value * -2.0 + 1.0) * _presetState.RenderContext.InvAspectY),
                R = ColorModulo(_perPointContext.R.Value),
                G = ColorModulo(_perPointContext.G.Value),
                B = ColorModulo(_perPointContext.B.Value),
                A = ColorModulo(_perPointContext.A.Value),
            };
        }

        ColoredPoint[] pointsSmoothed = SmoothWave(pointsTransformed);
        int smoothedVertexCount = pointsSmoothed.Length;

        var gl = GlContext.Gl;

        gl.LineWidth(1);

        // 叠加式画波形（而不是覆盖）。
        gl.Enable(GLEnum.Blend);
        if (_additive)
        {
            gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.One);
        }
        else
        {
            gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);
        }

        Shader shader = _presetState.UntexturedShader;
        shader.Bind();
        shader.SetUniformMat4x4("vertex_transformation", PresetState.OrthogonalProjection);
        shader.SetUniformFloat("vertex_point_size", _drawThick ? 2.0f : 1.0f);

        int iterations = _drawThick && !_useDots ? 4 : 1;

        // 这里用 1.0 而不是 Milkdrop 的 2.0，才能得到相同的视觉效果。
        float incrementX = 1.0f / _presetState.RenderContext.ViewportSizeX;
        float incrementY = 1.0f / _presetState.RenderContext.ViewportSizeX;

        GLEnum drawType = _useDots ? GLEnum.Points : GLEnum.LineStrip;

        gl.BindVertexArray(VaoId);
        gl.BindBuffer(GLEnum.ArrayBuffer, VboId);

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            switch (iteration)
            {
                case 1:
                    for (var j = 0; j < smoothedVertexCount; j++)
                    {
                        pointsSmoothed[j].X += incrementX;
                    }

                    break;

                case 2:
                    for (var j = 0; j < smoothedVertexCount; j++)
                    {
                        pointsSmoothed[j].Y += incrementY;
                    }

                    break;

                case 3:
                    for (var j = 0; j < smoothedVertexCount; j++)
                    {
                        pointsSmoothed[j].X -= incrementX;
                    }

                    break;
            }

            gl.BufferSubData<ColoredPoint>(GLEnum.ArrayBuffer, 0, pointsSmoothed.AsSpan(0, smoothedVertexCount));
            gl.DrawArrays(drawType, 0, (uint)smoothedVertexCount);
        }

        gl.BindBuffer(GLEnum.ArrayBuffer, 0);
        gl.BindVertexArray(0);

        Shader.Unbind();

        gl.Disable(GLEnum.Blend);
    }

    /// <remarks>对应 <c>LoadPerFrameEvaluationVariables</c>。</remarks>
    private void LoadPerFrameEvaluationVariables(PerFrameContext presetPerFrameContext)
    {
        _perFrameContext.LoadStateVariables(_presetState, presetPerFrameContext, this);
        _perPointContext.LoadReadOnlyStateVariables(presetPerFrameContext);
    }

    /// <remarks>对应 <c>InitPerPointEvaluationVariables</c>。</remarks>
    private void InitPerPointEvaluationVariables()
    {
        for (var q = 0; q < Constants.QVarCount; q++)
        {
            _perPointContext.QVar(q).Value = _perFrameContext.QVar(q).Value;
        }

        for (var t = 0; t < Constants.TVarCount; t++)
        {
            _perPointContext.TVar(t).Value = _perFrameContext.TVar(t).Value;
        }
    }

    /// <remarks>对应 <c>LoadPerPointEvaluationVariables</c>。</remarks>
    private void LoadPerPointEvaluationVariables(float sample, float value1, float value2)
    {
        _perPointContext.Sample.Value = sample;
        _perPointContext.Value1.Value = value1;
        _perPointContext.Value2.Value = value2;
        _perPointContext.X.Value = 0.5 + value1;
        _perPointContext.Y.Value = 0.5 + value2;
        _perPointContext.R.Value = _perFrameContext.R.Value;
        _perPointContext.G.Value = _perFrameContext.G.Value;
        _perPointContext.B.Value = _perFrameContext.B.Value;
        _perPointContext.A.Value = _perFrameContext.A.Value;
    }

    /// <summary>对彩色点做雅可比平滑（对应 <c>CustomWaveform::SmoothWave</c>）。</summary>
    private static ColoredPoint[] SmoothWave(ColoredPoint[] inputVertices)
    {
        const float c1 = -0.15f;
        const float c2 = 1.15f;
        const float c3 = 1.15f;
        const float c4 = -0.15f;
        const float inverseSum = 1.0f / (c1 + c2 + c3 + c4);

        int vertexCount = inputVertices.Length;
        var outputVertices = new ColoredPoint[vertexCount * 2];

        var outputIndex = 0;
        var iBelow = 0;
        var iAbove2 = 1;

        for (var inputIndex = 0; inputIndex < vertexCount - 1; inputIndex++)
        {
            int iAbove = iAbove2;
            iAbove2 = Math.Min(vertexCount - 1, inputIndex + 2);

            outputVertices[outputIndex] = inputVertices[inputIndex];
            outputVertices[outputIndex + 1] = inputVertices[inputIndex];
            outputVertices[outputIndex + 1].X =
                (c1 * inputVertices[iBelow].X + c2 * inputVertices[inputIndex].X +
                 c3 * inputVertices[iAbove].X + c4 * inputVertices[iAbove2].X) * inverseSum;
            outputVertices[outputIndex + 1].Y =
                (c1 * inputVertices[iBelow].Y + c2 * inputVertices[inputIndex].Y +
                 c3 * inputVertices[iAbove].Y + c4 * inputVertices[iAbove2].Y) * inverseSum;

            iBelow = inputIndex;
            outputIndex += 2;
        }

        outputVertices[outputIndex] = inputVertices[vertexCount - 1];

        return outputVertices[..(outputIndex + 1)];
    }
}
