using MilkDrop.ProjectM.Renderer;
using MilkDrop.ProjectM.Rendering;
using Silk.NET.OpenGLES;
using Sampler = MilkDrop.ProjectM.Renderer.Sampler;
using Shader = MilkDrop.ProjectM.Renderer.Shader;
using Texture = MilkDrop.ProjectM.Renderer.Texture;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 老式（无 composite shader）预设的收尾效果：视频回声 + 逐次叠加的 gamma 提亮。
/// </summary>
/// <remarks>
/// <para>对应 <c>MilkdropPreset/VideoEcho.hpp</c> 与 <c>VideoEcho.cpp</c>。</para>
/// <para>
/// Milkdrop 的 gamma 不是一次乘法，而是把整屏重复画 N 次做加法累积（N = floor(gammaAdj)），
/// 最后一次用小数部分——这样能得到与 D3D9 固定管线一致的"过曝"观感。
/// </para>
/// </remarks>
public sealed class VideoEcho : RenderItem
{
    private readonly PresetState _presetState;
    private readonly Sampler _sampler = new((int)GLEnum.ClampToEdge, (int)GLEnum.Linear);

    /// <summary>四角颜色（随时间缓慢变化）。</summary>
    private readonly float[,] _shade = new float[4, 3];

    private readonly TexturedPoint[] _vertices = new TexturedPoint[4];

    public VideoEcho(PresetState presetState)
    {
        ArgumentNullException.ThrowIfNull(presetState);
        _presetState = presetState;

        Init();
    }

    public override unsafe void InitVertexAttrib()
    {
        var gl = GlContext.Gl;

        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);
        gl.EnableVertexAttribArray(2);

        gl.VertexAttribPointer(0, 2, GLEnum.Float, false, VertexLayout.TexturedPointSize, (void*)0);
        gl.VertexAttribPointer(1, 4, GLEnum.Float, false, VertexLayout.TexturedPointSize,
            (void*)VertexLayout.ColoredPointRgbOffset);
        gl.VertexAttribPointer(2, 2, GLEnum.Float, false, VertexLayout.TexturedPointSize,
            (void*)VertexLayout.TexturedPointUOffset);
    }

    /// <summary>画视频回声 / gamma 调整（对应 <c>VideoEcho::Draw</c>）。</summary>
    public unsafe void Draw()
    {
        var gl = GlContext.Gl;
        RenderContext renderContext = _presetState.RenderContext;

        float aspect = renderContext.ViewportSizeX / (renderContext.ViewportSizeY * renderContext.InvAspectY);
        var aspectMultX = 1.0f;
        var aspectMultY = 1.0f;

        if (aspect > 1)
        {
            aspectMultY = aspect;
        }
        else
        {
            aspectMultX = 1.0f / aspect;
        }

        var onePlusInvWidth = 1.0f + 1.0f / renderContext.ViewportSizeX;
        var onePlusInvHeight = 1.0f + 1.0f / renderContext.ViewportSizeY;

        _vertices[0].X = -onePlusInvWidth * aspectMultX;
        _vertices[1].X = onePlusInvWidth * aspectMultX;
        _vertices[2].X = -onePlusInvWidth * aspectMultX;
        _vertices[3].X = onePlusInvWidth * aspectMultX;
        _vertices[0].Y = onePlusInvHeight * aspectMultY;
        _vertices[1].Y = onePlusInvHeight * aspectMultY;
        _vertices[2].Y = -onePlusInvHeight * aspectMultY;
        _vertices[3].Y = -onePlusInvHeight * aspectMultY;

        for (var i = 0; i < 4; i++)
        {
            _shade[i, 0] = 0.6f + 0.3f * MathF.Sin(renderContext.Time * 30.0f * 0.0143f + 3 + i * 21 + _presetState.HueRandomOffsets[3]);
            _shade[i, 1] = 0.6f + 0.3f * MathF.Sin(renderContext.Time * 30.0f * 0.0107f + 1 + i * 13 + _presetState.HueRandomOffsets[1]);
            _shade[i, 2] = 0.6f + 0.3f * MathF.Sin(renderContext.Time * 30.0f * 0.0129f + 6 + i * 9 + _presetState.HueRandomOffsets[2]);

            float max = MathF.Max(_shade[i, 0], MathF.Max(_shade[i, 1], _shade[i, 2]));

            for (var k = 0; k < 3; k++)
            {
                _shade[i, k] /= max;
                _shade[i, k] = 0.5f + 0.5f * _shade[i, k];
            }

            _vertices[i].R = _shade[i, 0];
            _vertices[i].G = _shade[i, 1];
            _vertices[i].B = _shade[i, 2];
            _vertices[i].A = 1.0f;
        }

        Shader shader = _presetState.TexturedShader;
        shader.Bind();
        shader.SetUniformMat4x4("vertex_transformation", PresetState.OrthogonalProjection);
        shader.SetUniformInt("texture_sampler", 0);

        Texture? mainTexture = _presetState.MainTexture;
        if (mainTexture is not null)
        {
            mainTexture.Bind(0);
            _sampler.Bind(0);
        }

        gl.BindVertexArray(VaoId);
        gl.BindBuffer(GLEnum.ArrayBuffer, VboId);

        if (_presetState.VideoEchoAlpha > 0.001f)
        {
            DrawVideoEcho();
        }
        else
        {
            DrawGammaAdjustment();
        }

        gl.BindBuffer(GLEnum.ArrayBuffer, 0);
        gl.BindVertexArray(0);

        gl.Disable(GLEnum.Blend);

        Shader.Unbind();

        if (mainTexture is not null)
        {
            mainTexture.Unbind(0);
            Sampler.Unbind(0);
        }
    }

    public override void Dispose()
    {
        if (GlContext.IsInitialized)
        {
            _sampler.Dispose();
        }

        base.Dispose();
    }

    /// <remarks>对应 <c>VideoEcho::DrawVideoEcho</c>。</remarks>
    private unsafe void DrawVideoEcho()
    {
        var gl = GlContext.Gl;

        float videoEchoZoom = _presetState.VideoEchoZoom;
        float videoEchoAlpha = _presetState.VideoEchoAlpha;
        int videoEchoOrientation = _presetState.VideoEchoOrientation % 4;
        float gammaAdj = _presetState.GammaAdj;

        gl.Enable(GLEnum.Blend);
        gl.BlendFunc(GLEnum.One, GLEnum.Zero);

        for (var pass = 0; pass < 2; pass++)
        {
            float zoom = pass == 0 ? 1.0f : videoEchoZoom;

            float tempLow = 0.5f - 0.5f / zoom;
            float tempHigh = 0.5f + 0.5f / zoom;
            _vertices[0].U = tempLow;
            _vertices[0].V = tempLow;
            _vertices[1].U = tempHigh;
            _vertices[1].V = tempLow;
            _vertices[2].U = tempLow;
            _vertices[2].V = tempHigh;
            _vertices[3].U = tempHigh;
            _vertices[3].V = tempHigh;

            // 翻转。
            if (pass == 1)
            {
                for (var vertex = 0; vertex < 4; vertex++)
                {
                    if (videoEchoOrientation % 2 == 1)
                    {
                        _vertices[vertex].U = 1.0f - _vertices[vertex].U;
                    }

                    if (videoEchoOrientation >= 2)
                    {
                        _vertices[vertex].V = 1.0f - _vertices[vertex].V;
                    }
                }
            }

            float mix = pass == 1 ? videoEchoAlpha : 1.0f - videoEchoAlpha;
            for (var vertex = 0; vertex < 4; vertex++)
            {
                _vertices[vertex].R = mix * _shade[vertex, 0];
                _vertices[vertex].G = mix * _shade[vertex, 1];
                _vertices[vertex].B = mix * _shade[vertex, 2];
                _vertices[vertex].A = 1.0f;
            }

            gl.BufferData<TexturedPoint>(GLEnum.ArrayBuffer, _vertices, GLEnum.DynamicDraw);
            gl.DrawArrays(GLEnum.TriangleStrip, 0, 4);

            if (pass == 0)
            {
                gl.BlendFunc(GLEnum.One, GLEnum.One);
            }

            if (gammaAdj <= 0.001f)
            {
                continue;
            }

            var redrawCount = (int)(gammaAdj - 0.0001f);

            for (var redraw = 0; redraw < redrawCount; redraw++)
            {
                float gamma;
                if (redraw == redrawCount - 1)
                {
                    gamma = gammaAdj - (int)(gammaAdj - 0.0001f);
                }
                else
                {
                    gamma = 1.0f;
                }

                for (var vertex = 0; vertex < 4; vertex++)
                {
                    _vertices[vertex].R = gamma * mix * _shade[vertex, 0];
                    _vertices[vertex].G = gamma * mix * _shade[vertex, 1];
                    _vertices[vertex].B = gamma * mix * _shade[vertex, 2];
                    _vertices[vertex].A = 1.0f;
                }

                gl.BufferData<TexturedPoint>(GLEnum.ArrayBuffer, _vertices, GLEnum.DynamicDraw);
                gl.DrawArrays(GLEnum.TriangleStrip, 0, 4);
            }
        }
    }

    /// <remarks>对应 <c>VideoEcho::DrawGammaAdjustment</c>。</remarks>
    private unsafe void DrawGammaAdjustment()
    {
        var gl = GlContext.Gl;

        _vertices[0].U = 0.0f;
        _vertices[0].V = 0.0f;
        _vertices[1].U = 1.0f;
        _vertices[1].V = 0.0f;
        _vertices[2].U = 0.0f;
        _vertices[2].V = 1.0f;
        _vertices[3].U = 1.0f;
        _vertices[3].V = 1.0f;

        gl.Disable(GLEnum.Blend);
        gl.BlendFunc(GLEnum.One, GLEnum.Zero);

        float gammaAdj = _presetState.GammaAdj;
        var redrawCount = (int)(gammaAdj - 0.0001f) + 1;

        for (var redraw = 0; redraw < redrawCount; redraw++)
        {
            float gamma = redraw == redrawCount - 1 ? gammaAdj - redraw : 1.0f;

            for (var vertex = 0; vertex < 4; vertex++)
            {
                _vertices[vertex].R = gamma * _shade[vertex, 0];
                _vertices[vertex].G = gamma * _shade[vertex, 1];
                _vertices[vertex].B = gamma * _shade[vertex, 2];
                _vertices[vertex].A = 1.0f;
            }

            gl.BufferData<TexturedPoint>(GLEnum.ArrayBuffer, _vertices, GLEnum.DynamicDraw);
            gl.DrawArrays(GLEnum.TriangleStrip, 0, 4);

            if (redraw == 0)
            {
                gl.Enable(GLEnum.Blend);
                gl.BlendFunc(GLEnum.One, GLEnum.One);
            }
        }
    }
}
