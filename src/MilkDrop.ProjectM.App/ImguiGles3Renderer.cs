using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.OpenGLES;

namespace MilkDrop.ProjectM.App;

/// <summary>
/// ImGui 的 GLES3 渲染后端（对应官方 <c>imgui_impl_opengl3.cpp</c> 的最小实现）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>MilkDrop.Classic.Render.D3D9/ImguiDx9Renderer.cs</c> 同一思路：ImGui.NET 只带 Win32/DX11 之类的
/// native 后端，ANGLE/GLES 下没有现成可用的 C# 后端，所以自己写一份，约 300 行。
/// </para>
/// <para>
/// 着色器是 GLES3（<c>#version 300 es</c> + <c>precision mediump float</c>），
/// 顶点属性位置按官方约定硬绑 0=Position、1=UV、2=Color。
/// </para>
/// </remarks>
public sealed unsafe class ImguiGles3Renderer : IDisposable
{
    private const string VertexShaderSource = """
        #version 300 es
        precision highp float;

        uniform mat4 ProjMtx;

        layout(location = 0) in vec2 Position;
        layout(location = 1) in vec2 UV;
        layout(location = 2) in vec4 Color;

        out vec2 Frag_UV;
        out vec4 Frag_Color;

        void main()
        {
            Frag_UV = UV;
            Frag_Color = Color;
            gl_Position = ProjMtx * vec4(Position.xy, 0.0, 1.0);
        }
        """;

    private const string FragmentShaderSource = """
        #version 300 es
        precision mediump float;

        uniform sampler2D Texture;

        in vec2 Frag_UV;
        in vec4 Frag_Color;

        out vec4 Out_Color;

        void main()
        {
            Out_Color = Frag_Color * texture(Texture, Frag_UV.st);
        }
        """;

    private readonly GL _gl;
    private readonly uint _program;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private readonly uint _fontTexture;
    private readonly int _projMatrixLocation;
    private readonly int _textureLocation;
    private readonly int _vertexStride = Marshal.SizeOf<ImDrawVert>();

    private bool _disposed;

    public ImguiGles3Renderer(GL gl)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));

        _program = CompileProgram(VertexShaderSource, FragmentShaderSource);
        _projMatrixLocation = _gl.GetUniformLocation(_program, "ProjMtx");
        _textureLocation = _gl.GetUniformLocation(_program, "Texture");

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(GLEnum.ElementArrayBuffer, _ebo);

        var stride = (uint)_vertexStride;
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, GLEnum.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, GLEnum.Float, false, stride, (void*)8);
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 4, GLEnum.UnsignedByte, true, stride, (void*)16);

        _gl.BindVertexArray(0);
        _fontTexture = CreateFontTexture();
    }

    /// <summary>上传一帧 ImGui 绘制数据并画到当前绑定的 framebuffer。</summary>
    public void Render(ImDrawDataPtr drawData, int framebufferWidth, int framebufferHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (drawData.CmdListsCount == 0)
        {
            return;
        }

        drawData.ScaleClipRects(Vector2.One);

        // ---- 备份本帧会用到的状态（projectM 渲染完再叠 UI，不能污染它的状态机）----
        var lastProgram = (uint)_gl.GetInteger(GLEnum.CurrentProgram);
        var lastVao = (uint)_gl.GetInteger(GLEnum.VertexArrayBinding);
        var lastActiveTexture = (uint)_gl.GetInteger(GLEnum.ActiveTexture);
        var lastTexture = (uint)_gl.GetInteger(GLEnum.TextureBinding2D);
        var lastBlend = _gl.IsEnabled(GLEnum.Blend);
        var lastCull = _gl.IsEnabled(GLEnum.CullFace);
        var lastDepth = _gl.IsEnabled(GLEnum.DepthTest);
        var lastScissor = _gl.IsEnabled(GLEnum.ScissorTest);
        var viewport = stackalloc int[4];
        _gl.GetInteger(GLEnum.Viewport, viewport);

        _gl.Enable(GLEnum.Blend);
        _gl.BlendEquation(GLEnum.FuncAdd);
        _gl.BlendFuncSeparate(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha, GLEnum.One, GLEnum.OneMinusSrcAlpha);
        _gl.Disable(GLEnum.CullFace);
        _gl.Disable(GLEnum.DepthTest);
        _gl.Disable(GLEnum.ScissorTest);
        _gl.Viewport(0, 0, (uint)framebufferWidth, (uint)framebufferHeight);

        _gl.UseProgram(_program);
        _gl.Uniform1(_textureLocation, 0);

        Span<float> projection = stackalloc float[16];
        BuildOrthoProjection(drawData, projection);
        _gl.UniformMatrix4(_projMatrixLocation, false, projection);

        _gl.ActiveTexture(GLEnum.Texture0);
        _gl.BindVertexArray(_vao);

        for (var listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
        {
            var cmdList = drawData.CmdLists[listIndex];

            var vertexCount = cmdList.VtxBuffer.Size;
            var indexCount = cmdList.IdxBuffer.Size;
            if (vertexCount == 0 || indexCount == 0)
            {
                continue;
            }

            _gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);
            var vertices = new ReadOnlySpan<ImDrawVert>((ImDrawVert*)cmdList.VtxBuffer.Data, vertexCount);
            _gl.BufferData(GLEnum.ArrayBuffer, (nuint)(vertexCount * _vertexStride), (void*)0, GLEnum.StreamDraw);
            _gl.BufferData<ImDrawVert>(GLEnum.ArrayBuffer, vertices, GLEnum.StreamDraw);

            _gl.BindBuffer(GLEnum.ElementArrayBuffer, _ebo);
            var indices = new ReadOnlySpan<ushort>((ushort*)cmdList.IdxBuffer.Data, indexCount);
            _gl.BufferData(GLEnum.ElementArrayBuffer, (nuint)(indexCount * sizeof(ushort)), (void*)0, GLEnum.StreamDraw);
            _gl.BufferData<ushort>(GLEnum.ElementArrayBuffer, indices, GLEnum.StreamDraw);

            for (var cmdIndex = 0; cmdIndex < cmdList.CmdBuffer.Size; cmdIndex++)
            {
                var cmd = cmdList.CmdBuffer[cmdIndex];
                if (cmd.UserCallback != 0)
                {
                    continue;
                }

                var clip = cmd.ClipRect;
                _gl.Scissor(
                    (int)clip.X,
                    (int)(framebufferHeight - clip.W),
                    (uint)Math.Max(0f, clip.Z - clip.X),
                    (uint)Math.Max(0f, clip.W - clip.Y));
                _gl.Enable(GLEnum.ScissorTest);

                _gl.BindTexture(GLEnum.Texture2D, (uint)cmd.GetTexID());

                var indexOffset = (void*)((nuint)cmd.IdxOffset * sizeof(ushort));
                _gl.DrawElements(GLEnum.Triangles, cmd.ElemCount, GLEnum.UnsignedShort, indexOffset);
            }
        }

        // ---- 恢复状态 ----
        _gl.UseProgram(lastProgram);
        _gl.BindVertexArray(lastVao);
        _gl.ActiveTexture((GLEnum)lastActiveTexture);
        _gl.BindTexture(GLEnum.Texture2D, lastTexture);
        _gl.Viewport(viewport[0], viewport[1], (uint)viewport[2], (uint)viewport[3]);
        SetCapability(GLEnum.Blend, lastBlend);
        SetCapability(GLEnum.CullFace, lastCull);
        SetCapability(GLEnum.DepthTest, lastDepth);
        SetCapability(GLEnum.ScissorTest, lastScissor);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gl.DeleteTexture(_fontTexture);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_program);
    }

    private void SetCapability(GLEnum capability, bool enabled)
    {
        if (enabled)
        {
            _gl.Enable(capability);
        }
        else
        {
            _gl.Disable(capability);
        }
    }

    /// <summary>ImGui 的裁剪空间投影：屏幕左上原点 → NDC（与官方 <c>ImGui_ImplOpenGL3_SetupRenderState</c> 一致）。</summary>
    private static void BuildOrthoProjection(ImDrawDataPtr drawData, Span<float> matrix)
    {
        var left = drawData.DisplayPos.X;
        var right = drawData.DisplayPos.X + drawData.DisplaySize.X;
        var top = drawData.DisplayPos.Y;
        var bottom = drawData.DisplayPos.Y + drawData.DisplaySize.Y;

        matrix.Clear();
        matrix[0] = 2f / (right - left);
        matrix[5] = 2f / (top - bottom);
        matrix[10] = -1f;
        matrix[12] = (right + left) / (left - right);
        matrix[13] = (top + bottom) / (bottom - top);
        matrix[15] = 1f;
    }

    private uint CreateFontTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out var width, out var height, out var bytesPerPixel);

        var texture = _gl.GenTexture();
        _gl.BindTexture(GLEnum.Texture2D, texture);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.PixelStore(GLEnum.UnpackRowLength, 1);
        _gl.TexImage2D(GLEnum.Texture2D, 0, (int)GLEnum.Rgba, (uint)width, (uint)height, 0,
            GLEnum.Rgba, GLEnum.UnsignedByte, pixels);
        _gl.BindTexture(GLEnum.Texture2D, 0);

        io.Fonts.SetTexID((nint)texture);
        io.Fonts.ClearTexData();

        Console.WriteLine($"ImGui 字体图集: {width}x{height} ({bytesPerPixel} bpp), texture={texture}");
        return texture;
    }

    private uint CompileProgram(string vertexSource, string fragmentSource)
    {
        var vertexShader = CompileShader(GLEnum.VertexShader, vertexSource);
        var fragmentShader = CompileShader(GLEnum.FragmentShader, fragmentSource);

        var program = _gl.CreateProgram();
        _gl.AttachShader(program, vertexShader);
        _gl.AttachShader(program, fragmentShader);
        _gl.BindAttribLocation(program, 0, "Position");
        _gl.BindAttribLocation(program, 1, "UV");
        _gl.BindAttribLocation(program, 2, "Color");
        _gl.LinkProgram(program);

        _gl.GetProgram(program, GLEnum.LinkStatus, out var status);
        if (status == 0)
        {
            throw new InvalidOperationException("ImGui GLES3 program 链接失败: " + _gl.GetProgramInfoLog(program));
        }

        _gl.DetachShader(program, vertexShader);
        _gl.DetachShader(program, fragmentShader);
        _gl.DeleteShader(vertexShader);
        _gl.DeleteShader(fragmentShader);
        return program;
    }

    private uint CompileShader(GLEnum type, string source)
    {
        var shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);

        _gl.GetShader(shader, GLEnum.CompileStatus, out var status);
        if (status == 0)
        {
            var log = _gl.GetShaderInfoLog(shader);
            _gl.DeleteShader(shader);
            throw new InvalidOperationException($"ImGui GLES3 {type} 编译失败:\n{log}\n--- source ---\n{source}");
        }

        return shader;
    }
}
