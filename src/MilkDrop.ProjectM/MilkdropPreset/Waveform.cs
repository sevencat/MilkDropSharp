using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.Renderer;
using MilkDrop.Rendering.Gles3;
using MilkDrop.ProjectM.MilkdropPreset.Waveforms;
using Silk.NET.OpenGLES;
using Shader = MilkDrop.Rendering.Gles3.Shader;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 主波形（预设的 <c>nWaveMode</c> 决定的 16 种波形之一）。
/// </summary>
/// <remarks>对应 <c>MilkdropPreset/Waveform.hpp</c> 与 <c>Waveform.cpp</c>。</remarks>
public sealed class Waveform : RenderItem
{
    private readonly PresetState _presetState;

    private WaveformMode _mode = WaveformMode.Circle;
    private WaveformMath? _waveformMath;

    private float _tempAlpha;

    public Waveform(PresetState presetState)
    {
        ArgumentNullException.ThrowIfNull(presetState);
        _presetState = presetState;

        Init();
    }

    public override unsafe void InitVertexAttrib()
    {
        var gl = GlContext.Gl;

        gl.EnableVertexAttribArray(0);
        gl.DisableVertexAttribArray(1);

        gl.VertexAttribPointer(0, 2, GLEnum.Float, false, 0, (void*)0);

        // 预分配最大顶点数的缓冲。
        var vertexData = new Point[Math.Max(AudioConstants.SpectrumSamples, AudioConstants.WaveformSamples) * 2 + 2];
        gl.BufferData<Point>(GLEnum.ArrayBuffer, vertexData, GLEnum.StreamDraw);
    }

    /// <summary>
    /// 判断当前 <c>nWaveMode</c> 有没有对应实现：没有时返回一句说明，正常返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <see cref="Draw"/> 遇到没有实现的模式会直接 <c>return</c>（什么都不画，也不报错），
    /// 宿主很难解释"为什么看不到波形"。这里在加载阶段就把原因算出来，交给
    /// <see cref="Preset.LoadWarnings"/> 显示。
    /// </remarks>
    public string? DescribeUnsupportedMode()
    {
        WaveformMode mode = (WaveformMode)(_presetState.WaveMode % (int)WaveformMode.Count);

        if (Factory.IsSupported(mode))
        {
            return null;
        }

        return $"nWaveMode={_presetState.WaveMode}（支持范围 0..{(int)WaveformMode.Count - 1}）"
             + " 没有对应的波形实现，主波形不会绘制。";
    }

    /// <summary>画主波形（对应 <c>Waveform::Draw</c>）。</summary>
    public unsafe void Draw(PerFrameContext presetPerFrameContext)
    {
        ArgumentNullException.ThrowIfNull(presetPerFrameContext);

        _mode = (WaveformMode)(_presetState.WaveMode % (int)WaveformMode.Count);

        if (_waveformMath is null)
        {
            _waveformMath = Factory.Create(_mode);
            if (_waveformMath is null)
            {
                return;
            }
        }

        var gl = GlContext.Gl;

        // GLES 没有 GL_LINE_SMOOTH。
        gl.LineWidth(1);

        Shader shader = _presetState.UntexturedShader;
        shader.Bind();
        shader.SetUniformMat4x4("vertex_transformation", PresetState.OrthogonalProjectionFlipped);
        shader.SetUniformFloat("vertex_point_size", 1.0f);

        gl.BindVertexArray(VaoId);
        gl.BindBuffer(GLEnum.ArrayBuffer, VboId);

        // 叠加式画波形（而不是覆盖）。
        gl.Enable(GLEnum.Blend);
        if (_presetState.AdditiveWaves)
        {
            gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.One);
        }
        else
        {
            gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);
        }

        Point[][] smoothedVertices = _waveformMath.GetVertices(_presetState, presetPerFrameContext);

        foreach (Point[] smoothedWave in smoothedVertices)
        {
            if (smoothedWave.Length == 0)
            {
                continue;
            }

            _tempAlpha = (float)presetPerFrameContext.WaveA.Value;
            MaximizeColors(presetPerFrameContext);

            // 粗线或点始终画 4 遍。
            int iterations = _presetState.WaveThick || _presetState.WaveDots ? 4 : 1;

            float incrementX = 2.0f / _presetState.RenderContext.ViewportSizeX;
            float incrementY = 2.0f / _presetState.RenderContext.ViewportSizeY;

            GLEnum drawType = _presetState.WaveDots
                ? GLEnum.Points
                : _waveformMath.IsLoop
                    ? GLEnum.LineLoop
                    : GLEnum.LineStrip;

            for (var iteration = 0; iteration < iterations; iteration++)
            {
                switch (iteration)
                {
                    case 1:
                        for (var j = 0; j < smoothedWave.Length; j++)
                        {
                            smoothedWave[j].X += incrementX;
                        }

                        break;

                    case 2:
                        for (var j = 0; j < smoothedWave.Length; j++)
                        {
                            smoothedWave[j].Y += incrementY;
                        }

                        break;

                    case 3:
                        for (var j = 0; j < smoothedWave.Length; j++)
                        {
                            smoothedWave[j].X -= incrementX;
                        }

                        break;
                }

                gl.BufferSubData<Point>(GLEnum.ArrayBuffer, 0, smoothedWave);
                gl.DrawArrays(drawType, 0, (uint)smoothedWave.Length);
            }
        }

        gl.Disable(GLEnum.Blend);

        gl.BindBuffer(GLEnum.ArrayBuffer, 0);
        gl.BindVertexArray(0);

        Shader.Unbind();
    }

    /// <summary>按音量调制不透明度（对应 <c>Waveform::ModulateOpacityByVolume</c>）。</summary>
    private void ModulateOpacityByVolume(PerFrameContext presetPerFrameContext)
    {
        float vol = _presetState.AudioData.Vol;

        if (vol <= _presetState.ModWaveAlphaStart)
        {
            _tempAlpha = 0.0f;
        }
        else if (vol >= _presetState.ModWaveAlphaEnd)
        {
            _tempAlpha = (float)presetPerFrameContext.WaveA.Value;
        }
        else
        {
            _tempAlpha = (float)presetPerFrameContext.WaveA.Value *
                         ((vol - _presetState.ModWaveAlphaStart) /
                          (_presetState.ModWaveAlphaEnd - _presetState.ModWaveAlphaStart));
        }
    }

    /// <summary>把颜色最大值拉到 1.0，并按模式调整 alpha（对应 <c>Waveform::MaximizeColors</c>）。</summary>
    private void MaximizeColors(PerFrameContext presetPerFrameContext)
    {
        int texSize = Math.Max(_presetState.RenderContext.ViewportSizeX, _presetState.RenderContext.ViewportSizeY);

        if (_mode is WaveformMode.CenteredSpiro or WaveformMode.ExplosiveHash)
        {
            if (texSize <= 256)
            {
                _tempAlpha *= 0.07f;
            }
            else if (texSize <= 512)
            {
                _tempAlpha *= 0.09f;
            }
            else if (texSize <= 1024)
            {
                _tempAlpha *= 0.11f;
            }
            else if (texSize <= 2048)
            {
                _tempAlpha *= 0.13f;
            }
            else
            {
                // 4K 之类的大屏。
                _tempAlpha *= 0.15f;
            }
        }
        else if (_mode == WaveformMode.CenteredSpiroVolume)
        {
            if (texSize <= 256)
            {
                _tempAlpha *= 0.075f;
            }
            else if (texSize <= 512)
            {
                _tempAlpha *= 0.15f;
            }
            else if (texSize <= 1024)
            {
                _tempAlpha *= 0.22f;
            }
            else if (texSize <= 2048)
            {
                _tempAlpha *= 0.33f;
            }
            else
            {
                // 4K 之类的大屏。
                _tempAlpha *= 0.44f;
            }

            _tempAlpha *= 1.3f;
            _tempAlpha *= MathF.Pow(_presetState.AudioData.Treb, 2.0f);
        }

        if (_presetState.ModWaveAlphaByVolume)
        {
            ModulateOpacityByVolume(presetPerFrameContext);
        }

        _tempAlpha = Math.Clamp(_tempAlpha, 0.0f, 1.0f);

        float waveR = (float)presetPerFrameContext.WaveR.Value;
        float waveG = (float)presetPerFrameContext.WaveG.Value;
        float waveB = (float)presetPerFrameContext.WaveB.Value;

        if (presetPerFrameContext.WaveBrighten.Value > 0)
        {
            const float maximizeWaveColorAmount = 1.0f;

            float max = waveR;
            if (max < waveG)
            {
                max = waveG;
            }

            if (max < waveB)
            {
                max = waveB;
            }

            if (max > 0.01f)
            {
                waveR = waveR / max * maximizeWaveColorAmount + waveR * (1.0f - maximizeWaveColorAmount);
                waveG = waveG / max * maximizeWaveColorAmount + waveG * (1.0f - maximizeWaveColorAmount);
                waveB = waveB / max * maximizeWaveColorAmount + waveB * (1.0f - maximizeWaveColorAmount);
            }
        }

        GlContext.Gl.VertexAttrib4(1, waveR, waveG, waveB, _tempAlpha);
    }
}
