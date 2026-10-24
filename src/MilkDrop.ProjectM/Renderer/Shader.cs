using System.Numerics;
using System.Runtime.InteropServices;
using MilkDrop.ProjectM.Rendering;
using Silk.NET.OpenGLES;

namespace MilkDrop.ProjectM.Renderer;

/// <summary>着色器编译/链接失败。</summary>
/// <remarks>对应 <c>Renderer/Shader.hpp:24-41</c>。</remarks>
public sealed class ShaderException(string message) : Exception(message);

/// <summary>GLSL 版本号（从 <c>GL_SHADING_LANGUAGE_VERSION</c> 解析）。</summary>
/// <remarks>对应 <c>Renderer/Shader.hpp:53-56</c>。</remarks>
public readonly record struct GlslVersion(int Major, int Minor);

/// <summary>
/// 一个 shader program（顶点 + 片元）。
/// </summary>
/// <remarks>
/// <para>对应 <c>Renderer/Shader.hpp:47-193</c> 与 <c>Shader.cpp:10-248</c>。</para>
/// <para>
/// 与 4.1.8 的差异：C++ 每次设置 uniform 都调 <c>glGetUniformLocation</c>。热路径上一个预设每帧要设
/// 上百个 uniform（warp/composite 的 <c>_c0.._c13</c>、24 个 <c>rot_*</c>、<c>_qa.._qh</c>、每个形状实例的
/// 变换矩阵…），每次都查一遍要走驱动侧字符串查找 + ANSI 封送。这里按名字缓存 location，
/// 重新链接时整体失效。
/// </para>
/// </remarks>
public sealed class Shader : IDisposable
{
    private readonly Dictionary<string, int> _uniformLocations = new(StringComparer.Ordinal);

    private uint _shaderProgram;

    public Shader()
    {
        _shaderProgram = GlContext.Gl.CreateProgram();
    }

    /// <summary>program 的 GL 名字。</summary>
    public uint ProgramId => _shaderProgram;

    /// <summary>编译并链接顶点/片元着色器。</summary>
    /// <exception cref="ShaderException">编译或链接失败。</exception>
    public void CompileProgram(string vertexShaderSource, string fragmentShaderSource)
    {
        var vertexShader = CompileShader(vertexShaderSource, GLEnum.VertexShader);
        var fragmentShader = CompileShader(fragmentShaderSource, GLEnum.FragmentShader);

        // 重新链接后 uniform 布局可能全变，缓存整体作废。
        _uniformLocations.Clear();

        var gl = GlContext.Gl;
        gl.AttachShader(_shaderProgram, vertexShader);
        gl.AttachShader(_shaderProgram, fragmentShader);

        gl.LinkProgram(_shaderProgram);

        // 链接完就不需要 shader 对象了。
        gl.DetachShader(_shaderProgram, vertexShader);
        gl.DetachShader(_shaderProgram, fragmentShader);
        gl.DeleteShader(vertexShader);
        gl.DeleteShader(fragmentShader);

        var programLinked = gl.GetProgram(_shaderProgram, GLEnum.LinkStatus);
        if (programLinked != 0)
        {
            return;
        }

        var message = gl.GetProgramInfoLog(_shaderProgram);
        throw new ShaderException("Error compiling shader: " + message);
    }

    /// <summary>用当前状态校验 program 是否可运行。</summary>
    /// <param name="validationMessage">失败时的日志。</param>
    public bool Validate(out string validationMessage)
    {
        var gl = GlContext.Gl;
        gl.ValidateProgram(_shaderProgram);

        var result = gl.GetProgram(_shaderProgram, GLEnum.ValidateStatus);
        var infoLogLength = gl.GetProgram(_shaderProgram, GLEnum.InfoLogLength);

        validationMessage = string.Empty;
        if (infoLogLength > 0)
        {
            validationMessage = gl.GetProgramInfoLog(_shaderProgram);
        }

        return result != 0;
    }

    /// <summary>把 program 设为当前程序。</summary>
    public void Bind()
    {
        if (_shaderProgram > 0)
        {
            GlContext.Gl.UseProgram(_shaderProgram);
        }
    }

    /// <summary>解绑当前程序。</summary>
    public static void Unbind() => GlContext.Gl.UseProgram(0);

    public void SetUniformFloat(string uniform, float value)
    {
        int location = GetLocation(uniform);
        if (location < 0)
        {
            return;
        }

        GlContext.Gl.Uniform1(location, value);
    }

    public void SetUniformInt(string uniform, int value)
    {
        int location = GetLocation(uniform);
        if (location < 0)
        {
            return;
        }

        GlContext.Gl.Uniform1(location, value);
    }

    public void SetUniformFloat2(string uniform, Vector2 values)
    {
        int location = GetLocation(uniform);
        if (location < 0)
        {
            return;
        }

        GlContext.Gl.Uniform2(location, values);
    }

    public void SetUniformInt2(string uniform, int x, int y)
    {
        int location = GetLocation(uniform);
        if (location < 0)
        {
            return;
        }

        GlContext.Gl.Uniform2(location, x, y);
    }

    public void SetUniformFloat3(string uniform, Vector3 values)
    {
        int location = GetLocation(uniform);
        if (location < 0)
        {
            return;
        }

        GlContext.Gl.Uniform3(location, values);
    }

    public void SetUniformInt3(string uniform, int x, int y, int z)
    {
        int location = GetLocation(uniform);
        if (location < 0)
        {
            return;
        }

        GlContext.Gl.Uniform3(location, x, y, z);
    }

    public void SetUniformFloat4(string uniform, Vector4 values)
    {
        int location = GetLocation(uniform);
        if (location < 0)
        {
            return;
        }

        GlContext.Gl.Uniform4(location, values);
    }

    public void SetUniformInt4(string uniform, int x, int y, int z, int w)
    {
        int location = GetLocation(uniform);
        if (location < 0)
        {
            return;
        }

        GlContext.Gl.Uniform4(location, x, y, z, w);
    }

    /// <summary>设置 3x4 矩阵（GLSL <c>mat3x4</c>，列主序）。</summary>
    public void SetUniformMat3x4(string uniform, ReadOnlySpan<float> values)
    {
        int location = GetLocation(uniform);
        if (location < 0)
        {
            return;
        }

        GlContext.Gl.UniformMatrix3x4(location, false, values);
    }

    /// <summary>设置 4x4 矩阵（GLSL <c>mat4</c>，列主序）。</summary>
    public void SetUniformMat4x4(string uniform, ReadOnlySpan<float> values)
    {
        int location = GetLocation(uniform);
        if (location < 0)
        {
            return;
        }

        GlContext.Gl.UniformMatrix4(location, false, values);
    }

    /// <summary>
    /// 解析 <c>GL_SHADING_LANGUAGE_VERSION</c>。取不到或解析失败返回 <c>(0,0)</c>。
    /// </summary>
    /// <remarks>
    /// WebGL/ANGLE 的字符串形如 <c>OpenGL ES GLSL ES 3.00 (ANGLE ...)</c>，
    /// 所以先跳到第一个数字，再截断到第一个空格，最后按小数点的前后做整数解析（与 C++ 一致）。
    /// </remarks>
    public static unsafe GlslVersion GetShaderLanguageVersion()
    {
        var ptr = GlContext.Gl.GetString(StringName.ShadingLanguageVersion);
        if (ptr == null)
        {
            return default;
        }

        var text = Marshal.PtrToStringAnsi((nint)ptr);
        if (string.IsNullOrEmpty(text))
        {
            return default;
        }

        var firstDigit = text.IndexOfAny("0123456789".ToCharArray());
        if (firstDigit > 0)
        {
            text = text[firstDigit..];
        }

        var spacePos = text.IndexOf(' ');
        if (spacePos >= 0)
        {
            text = text[..spacePos];
        }

        var dotPos = text.IndexOf('.');
        if (dotPos < 0)
        {
            return default;
        }

        if (!int.TryParse(text.AsSpan(0, dotPos), out var major)
            || !int.TryParse(text.AsSpan(dotPos + 1), out var minor))
        {
            return default;
        }

        return new GlslVersion(major, minor);
    }

    /// <summary>取 uniform location（首次查驱动，之后走缓存；不存在的不再重复查）。</summary>
    private int GetLocation(string uniform)
    {
        ref int cached = ref CollectionsMarshal.GetValueRefOrAddDefault(_uniformLocations, uniform, out bool exists);
        if (!exists)
        {
            cached = GlContext.Gl.GetUniformLocation(_shaderProgram, uniform);
        }

        return cached;
    }

    public void Dispose()
    {
        if (_shaderProgram == 0)
        {
            return;
        }

        GlContext.Gl.DeleteProgram(_shaderProgram);
        _shaderProgram = 0;
    }

    private uint CompileShader(string source, GLEnum type)
    {
        var gl = GlContext.Gl;
        var shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);

        var compiled = gl.GetShader(shader, GLEnum.CompileStatus);
        if (compiled != 0)
        {
            return shader;
        }

        var message = gl.GetShaderInfoLog(shader);
        gl.DeleteShader(shader);
        throw new ShaderException("Error compiling shader: " + message);
    }
}
