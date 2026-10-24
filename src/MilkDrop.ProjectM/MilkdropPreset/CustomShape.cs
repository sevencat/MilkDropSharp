using MilkDrop.ProjectM.Renderer;
using MilkDrop.Rendering.Gles3;
using Silk.NET.OpenGLES;
using Sampler = MilkDrop.Rendering.Gles3.Sampler;
using Shader = MilkDrop.Rendering.Gles3.Shader;
using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 预设自定义形状（<c>shapecode_N_*</c>）：可贴图或渐变色填充的多边形，支持多实例。
/// </summary>
/// <remarks>
/// <para>对应 <c>MilkdropPreset/CustomShape.hpp</c> 与 <c>CustomShape.cpp</c>。</para>
/// <para>
/// 因为"是否贴图"只有到 <see cref="Draw"/> 才知道，这里和 C++ 一样准备了两套 VAO/VBO：
/// 一套带纹理坐标、一套不带；再把基类的 VAO/VBO 留给描边用。
/// </para>
/// </remarks>
public sealed class CustomShape : RenderItem
{
    private const float Pi = 3.141592653589793f;

    private const int MaxSides = 100;

    private readonly PresetState _presetState;
    private readonly ShapePerFrameContext _perFrameContext;

    /// <summary>贴图顶点缓冲/数组。</summary>
    private uint _vboIdTextured;

    private uint _vaoIdTextured;

    /// <summary>不贴图顶点缓冲/数组。</summary>
    private uint _vboIdUntextured;

    private uint _vaoIdUntextured;

    private string _image = string.Empty;

    public CustomShape(PresetState presetState)
    {
        ArgumentNullException.ThrowIfNull(presetState);
        _presetState = presetState;

        _perFrameContext = new ShapePerFrameContext(presetState.GlobalRegisters);

        InitTexturedAndUntexturedBuffers();
        Init();

        _perFrameContext.RegisterBuiltinVariables();
    }

    // ---- 状态（对应 C++ 私有成员；两个上下文要读） ----

    /// <summary>形状在预设里的编号。</summary>
    public int Index { get; private set; }

    internal float[] TValuesAfterInitCode { get; } = new float[Constants.TVarCount];

    public bool Enabled { get; private set; }
    public int Sides { get; private set; } = 4;
    public bool Additive { get; private set; }
    public bool ThickOutline { get; private set; }
    public bool Textured { get; private set; }
    public int Instances { get; private set; } = 1;

    public float X { get; private set; } = 0.5f;
    public float Y { get; private set; } = 0.5f;
    public float Radius { get; private set; } = 0.1f;
    public float Angle { get; private set; }

    public float R { get; private set; } = 1.0f;
    public float G { get; private set; }
    public float B { get; private set; }
    public float A { get; private set; } = 1.0f;

    public float R2 { get; private set; }
    public float G2 { get; private set; } = 1.0f;
    public float B2 { get; private set; }
    public float A2 { get; private set; }

    public float BorderR { get; private set; } = 1.0f;
    public float BorderG { get; private set; } = 1.0f;
    public float BorderB { get; private set; } = 1.0f;
    public float BorderA { get; private set; }

    public float TexAng { get; private set; }
    public float TexZoom { get; private set; } = 1.0f;

    public override unsafe void InitVertexAttrib()
    {
        var gl = GlContext.Gl;

        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, GLEnum.Float, false, 0, (void*)0);
        gl.DisableVertexAttribArray(1);

        var vertexData = new Point[MaxSides];
        gl.BufferData<Point>(GLEnum.ArrayBuffer, vertexData, GLEnum.StreamDraw);
    }

    /// <summary>从预设文件读取初值（对应 <c>CustomShape::Initialize</c>）。</summary>
    public void Initialize(PresetFileParser parsedFile, int index)
    {
        ArgumentNullException.ThrowIfNull(parsedFile);

        string shapecodePrefix = "shapecode_" + index + "_";

        Index = index;
        Enabled = parsedFile.GetBool(shapecodePrefix + "enabled", Enabled);
        Sides = parsedFile.GetInt(shapecodePrefix + "sides", Sides);
        Additive = parsedFile.GetBool(shapecodePrefix + "additive", Additive);
        ThickOutline = parsedFile.GetBool(shapecodePrefix + "thickOutline", ThickOutline);
        Textured = parsedFile.GetBool(shapecodePrefix + "textured", Textured);
        Instances = parsedFile.GetInt(shapecodePrefix + "num_inst", Instances);
        X = parsedFile.GetFloat(shapecodePrefix + "x", X);
        Y = parsedFile.GetFloat(shapecodePrefix + "y", Y);
        Radius = parsedFile.GetFloat(shapecodePrefix + "rad", Radius);
        Angle = parsedFile.GetFloat(shapecodePrefix + "ang", Angle);
        TexAng = parsedFile.GetFloat(shapecodePrefix + "tex_ang", TexAng);
        TexZoom = parsedFile.GetFloat(shapecodePrefix + "tex_zoom", TexZoom);
        R = parsedFile.GetFloat(shapecodePrefix + "r", R);
        G = parsedFile.GetFloat(shapecodePrefix + "g", G);
        B = parsedFile.GetFloat(shapecodePrefix + "b", B);
        A = parsedFile.GetFloat(shapecodePrefix + "a", A);
        R2 = parsedFile.GetFloat(shapecodePrefix + "r2", R2);
        G2 = parsedFile.GetFloat(shapecodePrefix + "g2", G2);
        B2 = parsedFile.GetFloat(shapecodePrefix + "b2", B2);
        A2 = parsedFile.GetFloat(shapecodePrefix + "a2", A2);
        BorderR = parsedFile.GetFloat(shapecodePrefix + "border_r", BorderR);
        BorderG = parsedFile.GetFloat(shapecodePrefix + "border_g", BorderG);
        BorderB = parsedFile.GetFloat(shapecodePrefix + "border_b", BorderB);
        BorderA = parsedFile.GetFloat(shapecodePrefix + "border_a", BorderA);

        // projectM 追加：形状要用的纹理名。
        _image = parsedFile.GetString(shapecodePrefix + "image", string.Empty);
    }

    /// <summary>
    /// 编译代码并执行 init 表达式（对应 <c>CompileCodeAndRunInitExpressions</c>）。
    /// </summary>
    /// <exception cref="MilkdropCompileException">代码编译失败。</exception>
    public void CompileCodeAndRunInitExpressions()
    {
        _perFrameContext.LoadStateVariables(_presetState, this, 0);
        _perFrameContext.EvaluateInitCode(_presetState.CustomShapeInitCode[Index], this);

        for (var t = 0; t < Constants.TVarCount; t++)
        {
            TValuesAfterInitCode[t] = (float)_perFrameContext.TVar(t).Value;
        }

        _perFrameContext.CompilePerFrameCode(_presetState.CustomShapePerFrameCode[Index], this);
    }

    /// <summary>画形状（对应 <c>CustomShape::Draw</c>）。</summary>
    public unsafe void Draw()
    {
        if (!Enabled || Instances <= 0)
        {
            return;
        }

        var gl = GlContext.Gl;
        RenderContext renderContext = _presetState.RenderContext;

        gl.Enable(GLEnum.Blend);

        for (var instance = 0; instance < Instances; instance++)
        {
            _perFrameContext.LoadStateVariables(_presetState, this, instance);
            _perFrameContext.ExecutePerFrameCode();

            int sides = (int)_perFrameContext.Sides.Value;
            sides = Math.Clamp(sides, 3, MaxSides);

            // 叠加绘制还是覆盖绘制。
            bool additive = (int)_perFrameContext.Additive.Value != 0;
            gl.BlendFunc(GLEnum.SrcAlpha, additive ? GLEnum.One : GLEnum.OneMinusSrcAlpha);

            var vertexData = new TexturedPoint[sides + 2];

            vertexData[0].X = (float)(_perFrameContext.X.Value * 2.0 - 1.0);
            vertexData[0].Y = (float)(_perFrameContext.Y.Value * -2.0 + 1.0);
            vertexData[0].U = 0.5f;
            vertexData[0].V = 0.5f;

            vertexData[0].R = ColorModulo(_perFrameContext.R.Value);
            vertexData[0].G = ColorModulo(_perFrameContext.G.Value);
            vertexData[0].B = ColorModulo(_perFrameContext.B.Value);
            vertexData[0].A = ColorModulo(_perFrameContext.A.Value);

            vertexData[1].R = ColorModulo(_perFrameContext.R2.Value);
            vertexData[1].G = ColorModulo(_perFrameContext.G2.Value);
            vertexData[1].B = ColorModulo(_perFrameContext.B2.Value);
            vertexData[1].A = ColorModulo(_perFrameContext.A2.Value);

            for (var i = 1; i < sides + 1; i++)
            {
                float cornerProgress = (i - 1) / (float)sides;
                float angle = cornerProgress * Pi * 2.0f + (float)_perFrameContext.Ang.Value + Pi * 0.25f;

                vertexData[i].X = vertexData[0].X + (float)_perFrameContext.Rad.Value * MathF.Cos(angle) * renderContext.AspectY;
                vertexData[i].Y = vertexData[0].Y + (float)_perFrameContext.Rad.Value * MathF.Sin(angle);

                vertexData[i].R = vertexData[1].R;
                vertexData[i].G = vertexData[1].G;
                vertexData[i].B = vertexData[1].B;
                vertexData[i].A = vertexData[1].A;
            }

            // 复制最后一个顶点闭合三角形扇。
            vertexData[sides + 1] = vertexData[1];

            if ((int)_perFrameContext.Textured.Value != 0)
            {
                DrawTextured(vertexData, sides);
            }
            else
            {
                // 不贴图：中心色 → 边缘色的渐变。
                gl.BindBuffer(GLEnum.ArrayBuffer, _vboIdUntextured);
                gl.BufferSubData<TexturedPoint>(GLEnum.ArrayBuffer, 0, vertexData.AsSpan(0, sides + 2));

                Shader shader = _presetState.UntexturedShader;
                shader.Bind();
                shader.SetUniformMat4x4("vertex_transformation", PresetState.OrthogonalProjection);
                shader.SetUniformFloat("vertex_point_size", 1.0f);

                gl.BindVertexArray(_vaoIdUntextured);
                gl.DrawArrays(GLEnum.TriangleFan, 0, (uint)(sides + 2));
                gl.BindVertexArray(0);
            }

            if (_perFrameContext.BorderA.Value > 0.0001f)
            {
                DrawBorder(vertexData, sides);
            }
        }

        gl.BindBuffer(GLEnum.ArrayBuffer, 0);
        gl.BindVertexArray(0);

        gl.Disable(GLEnum.Blend);

        Shader.Unbind();
    }

    public override void Dispose()
    {
        if (GlContext.IsInitialized)
        {
            var gl = GlContext.Gl;

            if (_vboIdTextured != 0)
            {
                gl.DeleteBuffer(_vboIdTextured);
                _vboIdTextured = 0;
            }

            if (_vaoIdTextured != 0)
            {
                gl.DeleteVertexArray(_vaoIdTextured);
                _vaoIdTextured = 0;
            }

            if (_vboIdUntextured != 0)
            {
                gl.DeleteBuffer(_vboIdUntextured);
                _vboIdUntextured = 0;
            }

            if (_vaoIdUntextured != 0)
            {
                gl.DeleteVertexArray(_vaoIdUntextured);
                _vaoIdUntextured = 0;
            }
        }

        base.Dispose();
    }

    private unsafe void InitTexturedAndUntexturedBuffers()
    {
        var gl = GlContext.Gl;
        var vertexData = new TexturedPoint[MaxSides + 2];

        _vaoIdTextured = gl.GenVertexArray();
        _vboIdTextured = gl.GenBuffer();

        gl.BindVertexArray(_vaoIdTextured);
        gl.BindBuffer(GLEnum.ArrayBuffer, _vboIdTextured);

        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);
        gl.EnableVertexAttribArray(2);

        gl.VertexAttribPointer(0, 2, GLEnum.Float, false, VertexLayout.TexturedPointSize, (void*)0);
        gl.VertexAttribPointer(1, 4, GLEnum.Float, false, VertexLayout.TexturedPointSize,
            (void*)VertexLayout.ColoredPointRgbOffset);
        gl.VertexAttribPointer(2, 2, GLEnum.Float, false, VertexLayout.TexturedPointSize,
            (void*)VertexLayout.TexturedPointUOffset);

        gl.BufferData<TexturedPoint>(GLEnum.ArrayBuffer, vertexData, GLEnum.StreamDraw);

        _vaoIdUntextured = gl.GenVertexArray();
        _vboIdUntextured = gl.GenBuffer();

        gl.BindVertexArray(_vaoIdUntextured);
        gl.BindBuffer(GLEnum.ArrayBuffer, _vboIdUntextured);

        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);

        gl.VertexAttribPointer(0, 2, GLEnum.Float, false, VertexLayout.TexturedPointSize, (void*)0);
        gl.VertexAttribPointer(1, 4, GLEnum.Float, false, VertexLayout.TexturedPointSize,
            (void*)VertexLayout.ColoredPointRgbOffset);

        gl.BufferData<TexturedPoint>(GLEnum.ArrayBuffer, vertexData, GLEnum.StreamDraw);

        gl.BindVertexArray(0);
        gl.BindBuffer(GLEnum.ArrayBuffer, 0);
    }

    /// <remarks>对应 <c>CustomShape::Draw</c> 里的贴图分支。</remarks>
    private unsafe void DrawTextured(TexturedPoint[] vertexData, int sides)
    {
        var gl = GlContext.Gl;
        RenderContext renderContext = _presetState.RenderContext;

        Shader shader = _presetState.TexturedShader;
        shader.Bind();
        shader.SetUniformMat4x4("vertex_transformation", PresetState.OrthogonalProjection);
        shader.SetUniformInt("texture_sampler", 0);

        // 贴图形状：主纹理或 "image" 指定的纹理。
        float textureAspectY = renderContext.AspectY;
        Texture? mainTexture = _presetState.MainTexture;

        if (_image.Length == 0)
        {
            mainTexture?.Bind(0);
        }
        else
        {
            TextureManager? textureManager = renderContext.TextureManager;
            TextureSamplerDescriptor? descriptor = textureManager?.GetTexture(_image);

            if (descriptor is not null && !descriptor.Empty())
            {
                descriptor.Bind(0, shader);
                textureAspectY = 1.0f;
            }
            else
            {
                // 找不到纹理就退回主纹理。
                mainTexture?.Bind(0);
            }
        }

        gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapS, (int)GLEnum.Repeat);
        gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapT, (int)GLEnum.Repeat);

        for (var i = 1; i < sides + 1; i++)
        {
            float cornerProgress = (i - 1) / (float)sides;
            float angle = cornerProgress * Pi * 2.0f + (float)_perFrameContext.TexAng.Value + Pi * 0.25f;
            float texZoom = (float)_perFrameContext.TexZoom.Value;

            vertexData[i].U = 0.5f + 0.5f * MathF.Cos(angle) / texZoom * textureAspectY;
            // 需要垂直翻转。
            vertexData[i].V = 1.0f - (0.5f - 0.5f * MathF.Sin(angle) / texZoom);
        }

        vertexData[sides + 1] = vertexData[1];

        gl.BindBuffer(GLEnum.ArrayBuffer, _vboIdTextured);
        gl.BufferSubData<TexturedPoint>(GLEnum.ArrayBuffer, 0, vertexData.AsSpan(0, sides + 2));

        gl.BindVertexArray(_vaoIdTextured);
        gl.DrawArrays(GLEnum.TriangleFan, 0, (uint)(sides + 2));
        gl.BindVertexArray(0);

        gl.BindTexture(GLEnum.Texture2D, 0);
        Sampler.Unbind(0);
    }

    /// <remarks>对应 <c>CustomShape::Draw</c> 里的描边分支。</remarks>
    private unsafe void DrawBorder(TexturedPoint[] vertexData, int sides)
    {
        var gl = GlContext.Gl;
        RenderContext renderContext = _presetState.RenderContext;

        var points = new Point[sides];
        for (var i = 0; i < sides; i++)
        {
            points[i] = new Point { X = vertexData[i + 1].X, Y = vertexData[i + 1].Y };
        }

        Shader shader = _presetState.UntexturedShader;
        shader.Bind();
        shader.SetUniformMat4x4("vertex_transformation", PresetState.OrthogonalProjection);
        shader.SetUniformFloat("vertex_point_size", 1.0f);

        gl.VertexAttrib4(1,
                         (float)_perFrameContext.BorderR.Value,
                         (float)_perFrameContext.BorderG.Value,
                         (float)_perFrameContext.BorderB.Value,
                         (float)_perFrameContext.BorderA.Value);
        gl.LineWidth(1);

        gl.BindVertexArray(VaoId);
        gl.BindBuffer(GLEnum.ArrayBuffer, VboId);

        int iterations = ThickOutline ? 4 : 1;

        // 这里用 1.0 而不是 Milkdrop 的 2.0，才能得到相同的视觉效果。
        float incrementX = 1.0f / renderContext.ViewportSizeX;
        float incrementY = 1.0f / renderContext.ViewportSizeY;

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            switch (iteration)
            {
                case 1:
                    for (var j = 0; j < sides; j++)
                    {
                        points[j].X += incrementX;
                    }

                    break;

                case 2:
                    for (var j = 0; j < sides; j++)
                    {
                        points[j].Y += incrementY;
                    }

                    break;

                case 3:
                    for (var j = 0; j < sides; j++)
                    {
                        points[j].X -= incrementX;
                    }

                    break;
            }

            gl.BufferSubData<Point>(GLEnum.ArrayBuffer, 0, points);
            gl.DrawArrays(GLEnum.LineLoop, 0, (uint)sides);
        }
    }
}
