using System.Numerics;
using System.Runtime.InteropServices;
using MilkDrop.ProjectM.Renderer;
using MilkDrop.ProjectM.Rendering;
using Silk.NET.OpenGLES;
using Sampler = MilkDrop.ProjectM.Renderer.Sampler;
using Shader = MilkDrop.ProjectM.Renderer.Shader;
using Texture = MilkDrop.ProjectM.Renderer.Texture;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// warp 用的"逐像素"变换网格：每个顶点执行一次 per-pixel 表达式，再由顶点着色器插值出
/// 整屏的纹理坐标，实现缩放/旋转/扭曲/位移/拉伸等运动。
/// </summary>
/// <remarks>
/// <para>对应 <c>MilkdropPreset/PerPixelMesh.hpp</c> 与 <c>PerPixelMesh.cpp</c>。</para>
/// <para>
/// 与 C++ 的差异：预设自己的 warp shader（<c>m_warpShader</c>）还没接——它需要
/// <c>MilkdropShader.LoadVariables</c> 与按 <c>PresetState</c> 取纹理描述符，等 P4 的
/// warp 管线把这两块补上后再启用；当前只走"没有 warp shader"的固定管线。
/// </para>
/// </remarks>
public sealed class PerPixelMesh : RenderItem
{
    /// <summary>单次 draw call 最多提交的顶点数（C++ 里固定 3072）。</summary>
    private const int VerticesPerDrawCall = 1024 * 3;

    private const int VertexFloatCount = 14;
    private const int VertexStride = VertexFloatCount * sizeof(float);

    private readonly Shader _perPixelMeshShader = new();
    private readonly Sampler _perPixelSampler = new((int)GLEnum.ClampToEdge, (int)GLEnum.Linear);

    private MilkdropShader? _warpShader;

    private readonly List<MeshVertex> _vertices = [];
    private readonly List<int> _listIndices = [];
    private MeshVertex[] _drawVertices = [];

    private int _gridSizeX;
    private int _gridSizeY;
    private int _viewportWidth;
    private int _viewportHeight;

    public PerPixelMesh()
    {
        MilkdropStaticShaders staticShaders = MilkdropStaticShaders.Get();
        _perPixelMeshShader.CompileProgram(staticShaders.GetPresetWarpVertexShader(),
                                           staticShaders.GetPresetWarpFragmentShader());

        Init();
    }

    /// <summary>网格 X 分辨率（测试与调试用）。</summary>
    internal int GridSizeX => _gridSizeX;

    /// <summary>网格 Y 分辨率。</summary>
    internal int GridSizeY => _gridSizeY;

    /// <summary>网格顶点（只给测试用，别在渲染路径里改）。</summary>
    internal List<MeshVertex> Vertices => _vertices;

    /// <summary>三角形索引列表（测试用）。</summary>
    internal IReadOnlyList<int> ListIndices => _listIndices;

    /// <summary>当前生效的预设 warp shader；null 表示走默认管线（测试用）。</summary>
    internal MilkdropShader? CurrentWarpShader => _warpShader;

    /// <summary>渲染一帧的 warp 网格（对应 <c>PerPixelMesh::Draw</c>）。</summary>
    public void Draw(PresetState presetState, PerFrameContext perFrameContext, PerPixelContext perPixelContext)
    {
        ArgumentNullException.ThrowIfNull(presetState);
        ArgumentNullException.ThrowIfNull(perFrameContext);
        ArgumentNullException.ThrowIfNull(perPixelContext);

        RenderContext renderContext = presetState.RenderContext;
        if (renderContext.ViewportSizeX == 0 || renderContext.ViewportSizeY == 0 ||
            renderContext.PerPixelMeshX == 0 || renderContext.PerPixelMeshY == 0)
        {
            return;
        }

        InitializeMesh(presetState);
        CalculateMesh(presetState, perFrameContext, perPixelContext);
        WarpedBlit(presetState, perFrameContext);
    }

    public override unsafe void InitVertexAttrib()
    {
        var gl = GlContext.Gl;

        _drawVertices = new MeshVertex[VerticesPerDrawCall];

        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);
        gl.EnableVertexAttribArray(2);
        gl.EnableVertexAttribArray(3);
        gl.EnableVertexAttribArray(4);
        gl.EnableVertexAttribArray(5);

        // 只有位置和纹理坐标是每顶点的；颜色/grid 参数整屏一致（decay 用）。
        gl.VertexAttribPointer(0, 2, GLEnum.Float, false, VertexStride, (void*)0);   // position
        gl.VertexAttribPointer(1, 2, GLEnum.Float, false, VertexStride, (void*)(2 * sizeof(float)));  // radius, angle
        gl.VertexAttribPointer(2, 4, GLEnum.Float, false, VertexStride, (void*)(4 * sizeof(float)));  // zoom..warp
        gl.VertexAttribPointer(3, 2, GLEnum.Float, false, VertexStride, (void*)(8 * sizeof(float)));  // center
        gl.VertexAttribPointer(4, 2, GLEnum.Float, false, VertexStride, (void*)(10 * sizeof(float))); // distance
        gl.VertexAttribPointer(5, 2, GLEnum.Float, false, VertexStride, (void*)(12 * sizeof(float))); // stretch

        gl.BufferData<MeshVertex>(GLEnum.ArrayBuffer, _drawVertices.AsSpan(), GLEnum.StreamDraw);
    }

    /// <summary>
    /// 载入预设自带的 warp shader（对应 <c>LoadWarpShader</c>）。代码加载失败时退回默认管线。
    /// </summary>
    public void LoadWarpShader(PresetState presetState)
    {
        ArgumentNullException.ThrowIfNull(presetState);

        if (presetState.WarpShaderVersion <= 0 || presetState.WarpShader.Length == 0)
        {
            return;
        }

        var warpShader = new MilkdropShader(ShaderType.WarpShader);

        try
        {
            warpShader.LoadCode(presetState.WarpShader);
            _warpShader?.Dispose();
            _warpShader = warpShader;
        }
        catch (ShaderException)
        {
            warpShader.Dispose();
            _warpShader = null;
        }
    }

    /// <summary>
    /// 编译已载入的 warp shader（对应 <c>CompileWarpShader</c>）。失败时退回默认管线。
    /// </summary>
    public void CompileWarpShader(PresetState presetState)
    {
        ArgumentNullException.ThrowIfNull(presetState);

        if (_warpShader is null)
        {
            return;
        }

        try
        {
            _warpShader.LoadTexturesAndCompile(presetState);
        }
        catch (ShaderException)
        {
            _warpShader.Dispose();
            _warpShader = null;
        }
    }

    public override void Dispose()
    {
        if (GlContext.IsInitialized)
        {
            _warpShader?.Dispose();
            _perPixelMeshShader.Dispose();
            _perPixelSampler.Dispose();
        }

        _warpShader = null;
        base.Dispose();
    }

    /// <summary>
    /// 网格尺寸或视口变化时重建静态顶点与三角形索引（对应 <c>InitializeMesh</c>）。
    /// </summary>
    internal void InitializeMesh(PresetState presetState)
    {
        RenderContext renderContext = presetState.RenderContext;

        if (_gridSizeX != renderContext.PerPixelMeshX || _gridSizeY != renderContext.PerPixelMeshY)
        {
            _gridSizeX = renderContext.PerPixelMeshX;
            _gridSizeY = renderContext.PerPixelMeshY;

            _vertices.Clear();
            for (int i = 0; i < (_gridSizeX + 1) * (_gridSizeY + 1); i++)
            {
                _vertices.Add(default);
            }

            _listIndices.Clear();
            for (int i = 0; i < _gridSizeX * _gridSizeY * 6; i++)
            {
                _listIndices.Add(0);
            }
        }
        else if (_viewportWidth == renderContext.ViewportSizeX && _viewportHeight == renderContext.ViewportSizeY)
        {
            return;
        }

        float aspectX = renderContext.AspectX;
        float aspectY = renderContext.AspectY;

        // 网格顶点坐标固定为 [-1, 1]，半径/角度按宽高比修正。
        int vertexIndex = 0;
        for (int gridY = 0; gridY <= _gridSizeY; gridY++)
        {
            for (int gridX = 0; gridX <= _gridSizeX; gridX++)
            {
                MeshVertex vertex = _vertices[vertexIndex];

                vertex.X = (float)gridX / _gridSizeX * 2.0f - 1.0f;
                vertex.Y = (float)gridY / _gridSizeY * 2.0f - 1.0f;

                vertex.Radius = MathF.Sqrt(vertex.X * aspectX * (vertex.X * aspectX) + vertex.Y * aspectY * (vertex.Y * aspectY));
                vertex.Angle = gridY == _gridSizeY / 2 && gridX == _gridSizeX / 2
                    ? 0.0f
                    : MathF.Atan2(vertex.Y * aspectY, vertex.X * aspectX);

                _vertices[vertexIndex] = vertex;
                vertexIndex++;
            }
        }

        // 按四个象限各画 1/4，让旋转的接缝落在中心附近。
        int vertexListIndex = 0;
        for (int quadrant = 0; quadrant < 4; quadrant++)
        {
            for (int slice = 0; slice < _gridSizeY / 2; slice++)
            {
                for (int gridX = 0; gridX < _gridSizeX / 2; gridX++)
                {
                    int xReference = gridX;
                    int yReference = slice;

                    if ((quadrant & 1) != 0)
                    {
                        xReference = _gridSizeX - 1 - xReference;
                    }

                    if ((quadrant & 2) != 0)
                    {
                        yReference = _gridSizeY - 1 - yReference;
                    }

                    int vertex = xReference + yReference * (_gridSizeX + 1);

                    // 0 - 1      3
                    //   /      /
                    // 2      4 - 5
                    _listIndices[vertexListIndex++] = vertex;
                    _listIndices[vertexListIndex++] = vertex + 1;
                    _listIndices[vertexListIndex++] = vertex + _gridSizeX + 1;
                    _listIndices[vertexListIndex++] = vertex + 1;
                    _listIndices[vertexListIndex++] = vertex + _gridSizeX + 1;
                    _listIndices[vertexListIndex++] = vertex + _gridSizeX + 2;
                }
            }
        }

        _viewportWidth = renderContext.ViewportSizeX;
        _viewportHeight = renderContext.ViewportSizeY;
    }

    /// <summary>
    /// 逐顶点执行 per-pixel 代码，算出每个顶点的变换参数（对应 <c>CalculateMesh</c>）。
    /// </summary>
    internal void CalculateMesh(PresetState presetState, PerFrameContext perFrameContext, PerPixelContext perPixelContext)
    {
        float zoom = (float)perFrameContext.Zoom.Value;
        float zoomExp = (float)perFrameContext.ZoomExp.Value;
        float rot = (float)perFrameContext.Rot.Value;
        float warp = (float)perFrameContext.Warp.Value;
        float cx = (float)perFrameContext.Cx.Value;
        float cy = (float)perFrameContext.Cy.Value;
        float dx = (float)perFrameContext.Dx.Value;
        float dy = (float)perFrameContext.Dy.Value;
        float sx = (float)perFrameContext.Sx.Value;
        float sy = (float)perFrameContext.Sy.Value;

        RenderContext renderContext = presetState.RenderContext;
        int vertex = 0;

        for (int y = 0; y <= _gridSizeY; y++)
        {
            for (int x = 0; x <= _gridSizeX; x++)
            {
                MeshVertex currentVertex = _vertices[vertex];

                // 预设带 per-pixel 代码时逐顶点执行（不能并行：可能用 gmegabuf / regXX）。
                if (perPixelContext.PerPixelCodeHandle is not null)
                {
                    perPixelContext.X.Value = currentVertex.X * 0.5f * renderContext.AspectX + 0.5f;
                    perPixelContext.Y.Value = currentVertex.Y * 0.5f * renderContext.AspectY + 0.5f;
                    perPixelContext.Rad.Value = currentVertex.Radius;
                    perPixelContext.Ang.Value = -currentVertex.Angle;
                    perPixelContext.Zoom.Value = perFrameContext.Zoom.Value;
                    perPixelContext.ZoomExp.Value = perFrameContext.ZoomExp.Value;
                    perPixelContext.Rot.Value = perFrameContext.Rot.Value;
                    perPixelContext.Warp.Value = perFrameContext.Warp.Value;
                    perPixelContext.Cx.Value = perFrameContext.Cx.Value;
                    perPixelContext.Cy.Value = perFrameContext.Cy.Value;
                    perPixelContext.Dx.Value = perFrameContext.Dx.Value;
                    perPixelContext.Dy.Value = perFrameContext.Dy.Value;
                    perPixelContext.Sx.Value = perFrameContext.Sx.Value;
                    perPixelContext.Sy.Value = perFrameContext.Sy.Value;

                    perPixelContext.ExecutePerPixelCode();

                    currentVertex.Zoom = (float)perPixelContext.Zoom.Value;
                    currentVertex.ZoomExp = (float)perPixelContext.ZoomExp.Value;
                    currentVertex.Rot = (float)perPixelContext.Rot.Value;
                    currentVertex.Warp = (float)perPixelContext.Warp.Value;
                    currentVertex.CenterX = (float)perPixelContext.Cx.Value;
                    currentVertex.CenterY = (float)perPixelContext.Cy.Value;
                    currentVertex.DistanceX = (float)perPixelContext.Dx.Value;
                    currentVertex.DistanceY = (float)perPixelContext.Dy.Value;
                    currentVertex.StretchX = (float)perPixelContext.Sx.Value;
                    currentVertex.StretchY = (float)perPixelContext.Sy.Value;
                }
                else
                {
                    currentVertex.Zoom = zoom;
                    currentVertex.ZoomExp = zoomExp;
                    currentVertex.Rot = rot;
                    currentVertex.Warp = warp;
                    currentVertex.CenterX = cx;
                    currentVertex.CenterY = cy;
                    currentVertex.DistanceX = dx;
                    currentVertex.DistanceY = dy;
                    currentVertex.StretchX = sx;
                    currentVertex.StretchY = sy;
                }

                _vertices[vertex] = currentVertex;
                vertex++;
            }
        }
    }

    /// <summary>用固定管线把网格画出来（对应 <c>WarpedBlit</c>，暂不含预设 warp shader）。</summary>
    private unsafe void WarpedBlit(PresetState presetState, PerFrameContext perFrameContext)
    {
        RenderContext renderContext = presetState.RenderContext;

        float warpTime = renderContext.Time * presetState.WarpAnimSpeed;
        float warpScaleInverse = 1.0f / presetState.WarpScale;
        var warpFactors = new Vector4(
            11.68f + 4.0f * MathF.Cos(warpTime * 1.413f + 10),
            8.77f + 3.0f * MathF.Cos(warpTime * 1.113f + 7),
            10.54f + 3.0f * MathF.Cos(warpTime * 1.233f + 3),
            11.49f + 4.0f * MathF.Cos(warpTime * 0.933f + 5));

        float decay = MathF.Min((float)perFrameContext.Decay.Value, 1.0f);

        var gl = GlContext.Gl;

        // 预设之间没有混合，直接关掉。
        gl.Disable(GLEnum.Blend);

        var aspect = new Vector4(renderContext.AspectX, renderContext.AspectY,
                                 renderContext.InvAspectX, renderContext.InvAspectY);

        if (_warpShader is null)
        {
            _perPixelMeshShader.Bind();
            _perPixelMeshShader.SetUniformMat4x4("vertex_transformation", PresetState.OrthogonalProjection);
            _perPixelMeshShader.SetUniformInt("texture_sampler", 0);
            _perPixelMeshShader.SetUniformFloat4("aspect", aspect);
            _perPixelMeshShader.SetUniformFloat("warpTime", warpTime);
            _perPixelMeshShader.SetUniformFloat("warpScaleInverse", warpScaleInverse);
            _perPixelMeshShader.SetUniformFloat4("warpFactors", warpFactors);
            _perPixelMeshShader.SetUniformFloat("decay", decay);
        }
        else
        {
            _warpShader.LoadVariables(presetState, perFrameContext);

            Shader warpShader = _warpShader.ShaderProgram;
            warpShader.SetUniformFloat4("aspect", aspect);
            warpShader.SetUniformFloat("warpTime", warpTime);
            warpShader.SetUniformFloat("warpScaleInverse", warpScaleInverse);
            warpShader.SetUniformFloat4("warpFactors", warpFactors);
            warpShader.SetUniformFloat("decay", decay);
        }

        Texture? mainTexture = presetState.MainTexture;
        if (mainTexture is not null)
        {
            mainTexture.Bind(0);
        }

        // 纹理环绕方式决定边缘是重复还是夹边（预设里 wrap=1 时重复）。
        _perPixelSampler.WrapMode = perFrameContext.Wrap.Value > 0.0001f ? (int)GLEnum.Repeat : (int)GLEnum.ClampToEdge;
        _perPixelSampler.Bind(0);

        gl.BindVertexArray(VaoId);
        gl.BindBuffer(GLEnum.ArrayBuffer, VboId);

        int trianglesPerBatch = _drawVertices.Length / 3 - 4;
        int triangleCount = _gridSizeX * _gridSizeY * 2;
        int sourceIndex = 0;

        while (sourceIndex < triangleCount * 3)
        {
            int trianglesQueued = 0;
            int vertex = 0;

            while (trianglesQueued < trianglesPerBatch && sourceIndex < triangleCount * 3)
            {
                for (int i = 0; i < 3; i++)
                {
                    _drawVertices[vertex++] = _vertices[_listIndices[sourceIndex++]];
                }

                trianglesQueued++;
            }

            if (trianglesQueued > 0)
            {
                gl.BufferSubData<MeshVertex>(GLEnum.ArrayBuffer, 0, _drawVertices.AsSpan(0, trianglesQueued * 3));
                gl.DrawArrays(GLEnum.Triangles, 0, (uint)(trianglesQueued * 3));
            }
        }

        gl.BindVertexArray(0);
        gl.BindBuffer(GLEnum.ArrayBuffer, 0);

        Sampler.Unbind(0);
        Shader.Unbind();
    }

    /// <summary>warp 网格顶点（14 个 float，与顶点着色器的 6 个 attribute 对应）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MeshVertex
    {
        public float X;
        public float Y;
        public float Radius;
        public float Angle;

        public float Zoom;
        public float ZoomExp;
        public float Rot;
        public float Warp;

        public float CenterX;
        public float CenterY;

        public float DistanceX;
        public float DistanceY;

        public float StretchX;
        public float StretchY;
    }
}
