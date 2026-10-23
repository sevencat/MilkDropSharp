using System.Runtime.InteropServices;
using MilkDrop.ProjectM.Renderer;
using MilkDrop.ProjectM.Rendering;
using Silk.NET.OpenGLES;
using Shader = MilkDrop.ProjectM.Renderer.Shader;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 最终合成：有 composite shader 时走 32×24 的屏幕网格 + 预设片元着色器；
/// 否则退回 Milkdrop 1.x 的 VideoEcho + Filters 老式效果。
/// </summary>
/// <remarks>对应 <c>MilkdropPreset/FinalComposite.hpp</c> 与 <c>FinalComposite.cpp</c>。</remarks>
public sealed class FinalComposite : RenderItem
{
    /// <summary>合成网格宽（C++ 里固定 32）。</summary>
    private const int CompositeGridWidth = 32;

    /// <summary>合成网格高（C++ 里固定 24）。</summary>
    private const int CompositeGridHeight = 24;

    private const int VertexCount = CompositeGridWidth * CompositeGridHeight;

    /// <summary>去掉中间一行/一列后的四边形数 × 6。</summary>
    private const int IndexCount = (CompositeGridWidth - 2) * (CompositeGridHeight - 2) * 6;

    private const float Pi = 3.1415926535898f;

    /// <summary>预设没有 comp 代码时的默认片元着色器（对应 <c>FinalComposite.cpp:14</c>）。</summary>
    private const string DefaultCompositeShader = "shader_body\n{\nret = tex2D(sampler_main, uv).xyz;\n}";

    [StructLayout(LayoutKind.Sequential)]
    private struct MeshVertex
    {
        public float X;
        public float Y;
        public float R;
        public float G;
        public float B;
        public float A;
        public float U;
        public float V;
        public float Radius;
        public float Angle;
    }

    private readonly MeshVertex[] _vertices = new MeshVertex[VertexCount];
    private readonly int[] _indices = new int[IndexCount];

    private uint _elementBuffer;

    private int _viewportWidth;
    private int _viewportHeight;

    private MilkdropShader? _compositeShader;
    private VideoEcho? _videoEcho;
    private Filters? _filters;

    public FinalComposite()
    {
        Init();
    }

    public override unsafe void InitVertexAttrib()
    {
        var gl = GlContext.Gl;

        // 元素缓冲在 VAO 绑定时创建，这样索引绑定会记进 VAO 状态。
        _elementBuffer = gl.GenBuffer();
        gl.BindBuffer(GLEnum.ElementArrayBuffer, _elementBuffer);

        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);
        gl.EnableVertexAttribArray(2);
        gl.EnableVertexAttribArray(3);

        const int stride = 10 * sizeof(float);
        gl.VertexAttribPointer(0, 2, GLEnum.Float, false, stride, (void*)0);                          // 位置
        gl.VertexAttribPointer(1, 4, GLEnum.Float, false, stride, (void*)(2 * sizeof(float)));        // 颜色
        gl.VertexAttribPointer(2, 2, GLEnum.Float, false, stride, (void*)(6 * sizeof(float)));        // 纹理坐标
        gl.VertexAttribPointer(3, 2, GLEnum.Float, false, stride, (void*)(8 * sizeof(float)));        // radius/angle

        // 预分配顶点与索引缓冲。
        gl.BufferData<MeshVertex>(GLEnum.ArrayBuffer, _vertices, GLEnum.StreamDraw);
        gl.BufferData<int>(GLEnum.ElementArrayBuffer, _indices, GLEnum.StreamDraw);

        // 注意：**不要**在这里解绑 GL_ELEMENT_ARRAY_BUFFER——VAO 绑定时解绑会把索引缓冲从
        // VAO 里摘掉，之后 glDrawElements 会因为"没有索引缓冲"报 INVALID_OPERATION。
        // C++ 的 RenderItem::Init 也只解绑 ARRAY_BUFFER。
    }

    /// <summary>
    /// 载入 composite shader（对应 <c>FinalComposite::LoadCompositeShader</c>）。
    /// 预设没有 comp shader 时创建 VideoEcho（以及需要的 Filters）。
    /// </summary>
    public void LoadCompositeShader(PresetState presetState)
    {
        ArgumentNullException.ThrowIfNull(presetState);

        if (presetState.CompositeShaderVersion > 0)
        {
            var compositeShader = new MilkdropShader(ShaderType.CompositeShader);

            try
            {
                string code = presetState.CompositeShader.Length > 0
                    ? presetState.CompositeShader
                    : DefaultCompositeShader;
                compositeShader.LoadCode(code);
            }
            catch (ShaderException)
            {
                // 退回默认着色器（与 C++ 一致）。
                compositeShader.Dispose();
                compositeShader = new MilkdropShader(ShaderType.CompositeShader);
                compositeShader.LoadCode(DefaultCompositeShader);
            }

            _compositeShader?.Dispose();
            _compositeShader = compositeShader;
        }
        else
        {
            // 视频回声 + 随机色相 gamma 调整。
            _videoEcho = new VideoEcho(presetState);

            if (presetState.Brighten || presetState.Darken || presetState.Solarize || presetState.Invert)
            {
                _filters = new Filters(presetState);
            }
        }
    }

    /// <summary>
    /// 取纹理并编译 composite shader（对应 <c>FinalComposite::CompileCompositeShader</c>）。
    /// </summary>
    public void CompileCompositeShader(PresetState presetState)
    {
        ArgumentNullException.ThrowIfNull(presetState);

        if (_compositeShader is null)
        {
            return;
        }

        try
        {
            _compositeShader.LoadTexturesAndCompile(presetState);
        }
        catch (ShaderException)
        {
            // 退回默认着色器。
            _compositeShader.Dispose();
            _compositeShader = new MilkdropShader(ShaderType.CompositeShader);
            _compositeShader.LoadCode(DefaultCompositeShader);
            _compositeShader.LoadTexturesAndCompile(presetState);
        }
    }

    /// <summary>渲染最终合成（对应 <c>FinalComposite::Draw</c>）。</summary>
    public unsafe void Draw(PresetState presetState, PerFrameContext perFrameContext)
    {
        ArgumentNullException.ThrowIfNull(presetState);
        ArgumentNullException.ThrowIfNull(perFrameContext);

        var gl = GlContext.Gl;

        if (_compositeShader is not null)
        {
            InitializeMesh(presetState);
            ApplyHueShaderColors(presetState);

            // 渲染网格。
            gl.Disable(GLEnum.Blend);
            gl.BindVertexArray(VaoId);
            gl.BindBuffer(GLEnum.ArrayBuffer, VboId);
            gl.BufferSubData<MeshVertex>(GLEnum.ArrayBuffer, 0, _vertices);
            gl.BindBuffer(GLEnum.ArrayBuffer, 0);

            _compositeShader.LoadVariables(presetState, perFrameContext);

            gl.DrawElements(GLEnum.Triangles, IndexCount, GLEnum.UnsignedInt, (void*)0);
        }
        else
        {
            // 老式滤镜。
            _videoEcho?.Draw();
            _filters?.Draw();
        }

        gl.BindVertexArray(0);
        Shader.Unbind();
    }

    /// <summary>是否在使用 composite shader（决定后面要不要再翻一次 Y）。</summary>
    public bool HasCompositeShader => _compositeShader is not null;

    public override void Dispose()
    {
        if (GlContext.IsInitialized)
        {
            _compositeShader?.Dispose();
            _videoEcho?.Dispose();
            _filters?.Dispose();

            if (_elementBuffer != 0)
            {
                GlContext.Gl.DeleteBuffer(_elementBuffer);
                _elementBuffer = 0;
            }
        }

        _compositeShader = null;
        _videoEcho = null;
        _filters = null;

        base.Dispose();
    }

    /// <remarks>对应 <c>FinalComposite::InitializeMesh</c>：视口不变就直接返回。</remarks>
    private unsafe void InitializeMesh(PresetState presetState)
    {
        RenderContext renderContext = presetState.RenderContext;

        if (_viewportWidth == renderContext.ViewportSizeX &&
            _viewportHeight == renderContext.ViewportSizeY)
        {
            return;
        }

        float halfTexelWidth = 0.5f / renderContext.ViewportSizeX;
        float halfTexelHeight = 0.5f / renderContext.ViewportSizeY;

        float dividedByX = 1.0f / (CompositeGridWidth - 2);
        float dividedByY = 1.0f / (CompositeGridHeight - 2);

        for (var gridY = 0; gridY < CompositeGridHeight; gridY++)
        {
            int gridY2 = gridY - gridY / (CompositeGridHeight / 2);
            float v = SquishToCenter(gridY2 * dividedByY, 3.0f);
            float sy = -(v * 2.0f - 1.0f);

            for (var gridX = 0; gridX < CompositeGridWidth; gridX++)
            {
                int gridX2 = gridX - gridX / (CompositeGridWidth / 2);
                float u = SquishToCenter(gridX2 * dividedByX, 3.0f);
                float sx = u * 2.0f - 1.0f;

                ref MeshVertex vertex = ref _vertices[gridX + gridY * CompositeGridWidth];

                vertex.X = sx;
                vertex.Y = sy;

                UvToMathSpace(renderContext.AspectX, renderContext.AspectY, u, v, out float rad, out float ang);

                // 中心四块的接缝修正（数值与 C++ 完全一致）。
                if (gridX == CompositeGridWidth / 2 - 1)
                {
                    if (gridY < CompositeGridHeight / 2 - 1)
                    {
                        ang = Pi * 1.5f;
                    }
                    else if (gridY == CompositeGridHeight / 2 - 1)
                    {
                        ang = Pi * 1.25f;
                    }
                    else if (gridY == CompositeGridHeight / 2)
                    {
                        ang = Pi * 0.75f;
                    }
                    else
                    {
                        ang = Pi * 0.5f;
                    }
                }
                else if (gridX == CompositeGridWidth / 2)
                {
                    if (gridY < CompositeGridHeight / 2 - 1)
                    {
                        ang = Pi * 1.5f;
                    }
                    else if (gridY == CompositeGridHeight / 2 - 1)
                    {
                        ang = Pi * 1.75f;
                    }
                    else if (gridY == CompositeGridHeight / 2)
                    {
                        ang = Pi * 0.25f;
                    }
                    else
                    {
                        ang = Pi * 0.5f;
                    }
                }
                else if (gridY == CompositeGridHeight / 2 - 1)
                {
                    ang = gridX < CompositeGridWidth / 2 - 1 ? Pi * 1.0f : Pi * 2.0f;
                }
                else if (gridY == CompositeGridHeight / 2)
                {
                    ang = gridX < CompositeGridWidth / 2 - 1 ? Pi * 1.0f : Pi * 0.0f;
                }

                vertex.U = u + halfTexelWidth;
                vertex.V = v + halfTexelHeight;
                vertex.Radius = rad;
                vertex.Angle = ang;
            }
        }

        // 组装索引：顺序为插值 'ang' 友好。
        var currentIndex = 0;
        for (var gridY = 0; gridY < CompositeGridHeight - 1; gridY++)
        {
            if (gridY == CompositeGridHeight / 2 - 1)
            {
                continue;
            }

            for (var gridX = 0; gridX < CompositeGridWidth - 1; gridX++)
            {
                if (gridX == CompositeGridWidth / 2 - 1)
                {
                    continue;
                }

                bool leftHalf = gridX < CompositeGridWidth / 2;
                bool topHalf = gridY < CompositeGridHeight / 2;
                bool center4 = gridX == CompositeGridWidth / 2 && gridY == CompositeGridHeight / 2;

                if (((leftHalf ? 1 : 0) + (topHalf ? 1 : 0) + (center4 ? 1 : 0)) % 2 == 1)
                {
                    _indices[currentIndex + 0] = gridY * CompositeGridWidth + gridX;
                    _indices[currentIndex + 1] = gridY * CompositeGridWidth + gridX + 1;
                    _indices[currentIndex + 2] = (gridY + 1) * CompositeGridWidth + gridX + 1;
                    _indices[currentIndex + 3] = (gridY + 1) * CompositeGridWidth + gridX + 1;
                    _indices[currentIndex + 4] = (gridY + 1) * CompositeGridWidth + gridX;
                    _indices[currentIndex + 5] = gridY * CompositeGridWidth + gridX;
                }
                else
                {
                    _indices[currentIndex + 0] = (gridY + 1) * CompositeGridWidth + gridX;
                    _indices[currentIndex + 1] = gridY * CompositeGridWidth + gridX;
                    _indices[currentIndex + 2] = gridY * CompositeGridWidth + gridX + 1;
                    _indices[currentIndex + 3] = gridY * CompositeGridWidth + gridX + 1;
                    _indices[currentIndex + 4] = (gridY + 1) * CompositeGridWidth + gridX + 1;
                    _indices[currentIndex + 5] = (gridY + 1) * CompositeGridWidth + gridX;
                }

                currentIndex += 6;
            }
        }

        var gl = GlContext.Gl;
        gl.BindVertexArray(VaoId);
        gl.BufferSubData<int>(GLEnum.ElementArrayBuffer, 0, _indices);
        gl.BindVertexArray(0);

        _viewportWidth = renderContext.ViewportSizeX;
        _viewportHeight = renderContext.ViewportSizeY;
    }

    /// <summary>向中心挤压的曲线（对应 <c>FinalComposite::SquishToCenter</c>）。</summary>
    internal static float SquishToCenter(float x, float exponent)
    {
        if (x > 0.5f)
        {
            return MathF.Pow(x * 2.0f - 1.0f, exponent) * 0.5f + 0.5f;
        }

        return (1.0f - MathF.Pow(1.0f - x * 2.0f, exponent)) * 0.5f;
    }

    /// <summary>UV → 预设作者熟悉的"数学空间"（半径 + 逆时针角度）。</summary>
    internal static void UvToMathSpace(float aspectX, float aspectY, float u, float v,
        out float rad, out float ang)
    {
        float px = (u * 2.0f - 1.0f) * aspectX;
        float py = (v * 2.0f - 1.0f) * aspectY;

        rad = MathF.Sqrt(px * px + py * py) / MathF.Sqrt(aspectX * aspectX + aspectY * aspectY);
        ang = MathF.Atan2(py, px);
        if (ang < 0)
        {
            ang += 6.2831853071796f;
        }
    }

    /// <remarks>对应 <c>FinalComposite::ApplyHueShaderColors</c>。</remarks>
    private void ApplyHueShaderColors(PresetState presetState)
    {
        var shade = new float[4, 3];

        for (var i = 0; i < 4; i++)
        {
            float time = presetState.RenderContext.Time;

            shade[i, 0] = 0.6f + 0.3f * MathF.Sin(time * 30.0f * 0.0143f + 3 + i * 21 + presetState.HueRandomOffsets[3]);
            shade[i, 1] = 0.6f + 0.3f * MathF.Sin(time * 30.0f * 0.0107f + 1 + i * 13 + presetState.HueRandomOffsets[1]);
            shade[i, 2] = 0.6f + 0.3f * MathF.Sin(time * 30.0f * 0.0129f + 6 + i * 9 + presetState.HueRandomOffsets[2]);

            float max = MathF.Max(shade[i, 0], MathF.Max(shade[i, 1], shade[i, 2]));

            for (var k = 0; k < 3; k++)
            {
                shade[i, k] /= max;
                shade[i, k] = 0.5f + 0.5f * shade[i, k];
            }
        }

        // 双线性插值到每个网格顶点。
        for (var gridY = 0; gridY < CompositeGridHeight; gridY++)
        {
            for (var gridX = 0; gridX < CompositeGridWidth; gridX++)
            {
                ref MeshVertex vertex = ref _vertices[gridX + gridY * CompositeGridWidth];
                float x = vertex.X * 0.5f + 0.5f;
                float y = vertex.Y * 0.5f + 0.5f;

                var color = new float[3];
                for (var col = 0; col < 3; col++)
                {
                    color[col] = shade[0, col] * x * y +
                                 shade[1, col] * (1 - x) * y +
                                 shade[2, col] * x * (1 - y) +
                                 shade[3, col] * (1 - x) * (1 - y);
                }

                vertex.R = color[0];
                vertex.G = color[1];
                vertex.B = color[2];
                vertex.A = 1.0f;
            }
        }
    }
}
