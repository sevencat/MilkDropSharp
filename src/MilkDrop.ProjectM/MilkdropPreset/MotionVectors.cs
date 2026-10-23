using System.Runtime.InteropServices;
using MilkDrop.ProjectM.Renderer;
using MilkDrop.ProjectM.Rendering;
using Silk.NET.OpenGLES;
using Sampler = MilkDrop.ProjectM.Renderer.Sampler;
using Shader = MilkDrop.ProjectM.Renderer.Shader;
using Texture = MilkDrop.ProjectM.Renderer.Texture;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 把上一帧的 warp UV 贴图读回来，画成一张"运动矢量"线网，用来让画面拖出运动轨迹。
/// </summary>
/// <remarks>
/// <para>对应 <c>MilkdropPreset/MotionVectors.hpp</c> 与 <c>MotionVectors.cpp</c>。</para>
/// <para>
/// 线段的第二个顶点在顶点着色器里按 UV 贴图反查上一帧的位置，从而把"上一帧的像素来自哪里"
/// 画成一条彗尾。
/// </para>
/// </remarks>
public sealed class MotionVectors : RenderItem
{
    /// <summary>线网最大列数（C++ 里硬编码的 64）。</summary>
    private const int MaxGridColumns = 64;

    /// <summary>线网最大行数（C++ 里硬编码的 48）。</summary>
    private const int MaxGridRows = 48;

    // 2 个 float + 1 个 int（索引用 glVertexAttribIPointer 传）。
    private const int VertexStride = 3 * sizeof(float);

    private const int VertexIndexOffset = 2 * sizeof(float);

    [StructLayout(LayoutKind.Sequential)]
    private struct MotionVectorVertex
    {
        public float X;
        public float Y;
        public int Index;
    }

    private readonly PresetState _presetState;
    private readonly Shader _motionVectorShader = new();
    private readonly Sampler _sampler = new((int)GLEnum.ClampToEdge, (int)GLEnum.Linear);

    /// <summary>一行线段的顶点（每行最多 <see cref="MaxGridColumns"/> 条线、每条 2 个顶点）。</summary>
    private readonly MotionVectorVertex[] _lineVertices = new MotionVectorVertex[(MaxGridColumns + 1) * 2];

    private int _lastVertexCount;

    public MotionVectors(PresetState presetState)
    {
        ArgumentNullException.ThrowIfNull(presetState);
        _presetState = presetState;

        MilkdropStaticShaders staticShaders = MilkdropStaticShaders.Get();
        _motionVectorShader.CompileProgram(staticShaders.GetPresetMotionVectorsVertexShader(),
                                           staticShaders.GetUntexturedDrawFragmentShader());

        Init();
    }

    public override unsafe void InitVertexAttrib()
    {
        var gl = GlContext.Gl;

        gl.EnableVertexAttribArray(0);
        gl.DisableVertexAttribArray(1);
        gl.EnableVertexAttribArray(2);

        gl.VertexAttribPointer(0, 2, GLEnum.Float, false, VertexStride, (void*)0);
        gl.VertexAttribIPointer(2, 1, GLEnum.Int, VertexStride, (void*)VertexIndexOffset);
    }

    /// <summary>画运动矢量线网（对应 <c>MotionVectors::Draw</c>）。</summary>
    /// <param name="presetPerFrameContext">已执行过 per-frame 代码的上下文（取 <c>mv_*</c>）。</param>
    /// <param name="motionTexture">上一帧写好的 UV 贴图。</param>
    public unsafe void Draw(PerFrameContext presetPerFrameContext, Texture motionTexture)
    {
        ArgumentNullException.ThrowIfNull(presetPerFrameContext);
        ArgumentNullException.ThrowIfNull(motionTexture);

        // 不可见就不画。
        if ((float)presetPerFrameContext.MvA.Value < 0.0001f)
        {
            return;
        }

        var countX = (int)(float)presetPerFrameContext.MvX.Value;
        var countY = (int)(float)presetPerFrameContext.MvY.Value;

        if (countX <= 0 || countY <= 0)
        {
            return;
        }

        var divertX = (float)presetPerFrameContext.MvX.Value - countX;
        var divertY = (float)presetPerFrameContext.MvY.Value - countY;

        if (countX > MaxGridColumns)
        {
            countX = MaxGridColumns;
            divertX = 0.0f;
        }

        if (countY > MaxGridRows)
        {
            countY = MaxGridRows;
            divertY = 0.0f;
        }

        var divertX2 = (float)presetPerFrameContext.MvDX.Value;
        var divertY2 = (float)presetPerFrameContext.MvDY.Value;

        // X/Y 偏移夹到 0..1。
        divertX = Math.Clamp(divertX, 0.0f, 1.0f);
        divertY = Math.Clamp(divertY, 0.0f, 1.0f);

        RenderContext renderContext = _presetState.RenderContext;

        // 保证线至少比 1 像素长一点，否则线平滑会把一部分线直接抹掉。
        var inverseWidth = 1.25f / renderContext.ViewportSizeX;
        var inverseHeight = 1.25f / renderContext.ViewportSizeY;
        var minimumLength = MathF.Sqrt(inverseWidth * inverseWidth + inverseHeight * inverseHeight);

        var gl = GlContext.Gl;

        gl.Enable(GLEnum.Blend);
        gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

        _motionVectorShader.Bind();
        _motionVectorShader.SetUniformMat4x4("vertex_transformation", PresetState.OrthogonalProjection);
        _motionVectorShader.SetUniformFloat("length_multiplier", (float)presetPerFrameContext.MvL.Value);
        _motionVectorShader.SetUniformFloat("minimum_length", minimumLength);
        _motionVectorShader.SetUniformInt("warp_coordinates", 0);

        motionTexture.Bind(0, _sampler);

        gl.VertexAttrib4(1,
                         (float)presetPerFrameContext.MvR.Value,
                         (float)presetPerFrameContext.MvG.Value,
                         (float)presetPerFrameContext.MvB.Value,
                         (float)presetPerFrameContext.MvA.Value);

        gl.BindVertexArray(VaoId);
        gl.BindBuffer(GLEnum.ArrayBuffer, VboId);

        gl.LineWidth(1);
        // GLES 3.0 没有 glEnable(GL_LINE_SMOOTH)，与 C++ 的 USE_GLES 分支一致：不做线平滑。

        for (var y = 0; y < countY; y++)
        {
            var posY = (y + 0.25f) / (countY + divertY + 0.25f - 1.0f) - divertY2;

            if (posY <= 0.0001f || posY >= 0.9999f)
            {
                continue;
            }

            var vertex = 0;
            for (var x = 0; x < countX; x++)
            {
                var posX = (x + 0.25f) / (countX + divertX + 0.25f - 1.0f) + divertX2;

                if (posX <= 0.0001f || posX >= 0.9999f)
                {
                    continue;
                }

                _lineVertices[vertex].X = posX;
                _lineVertices[vertex].Y = posY;
                _lineVertices[vertex].Index = vertex;

                _lineVertices[vertex + 1] = _lineVertices[vertex];
                _lineVertices[vertex + 1].Index++;

                vertex += 2;
            }

            // 画一整行线段。
            if (vertex == 0)
            {
                continue;
            }

            if (_lastVertexCount >= vertex)
            {
                gl.BufferSubData<MotionVectorVertex>(GLEnum.ArrayBuffer, 0, _lineVertices.AsSpan(0, vertex));
            }
            else
            {
                gl.BufferData<MotionVectorVertex>(GLEnum.ArrayBuffer, _lineVertices.AsSpan(0, vertex), GLEnum.StreamDraw);
                _lastVertexCount = vertex;
            }

            gl.DrawArrays(GLEnum.Lines, 0, (uint)vertex);
        }

        gl.BindBuffer(GLEnum.ArrayBuffer, 0);
        gl.BindVertexArray(0);

        Shader.Unbind();
        gl.Disable(GLEnum.Blend);
    }

    public override void Dispose()
    {
        if (GlContext.IsInitialized)
        {
            _motionVectorShader.Dispose();
            _sampler.Dispose();
        }

        base.Dispose();
    }
}
