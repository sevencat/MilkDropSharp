using System.Runtime.InteropServices;
using Silk.NET.OpenGLES;

namespace MilkDrop.Rendering.Gles3;

/// <summary>
/// 渲染网格基类：持有 VAO/VBO，并定义几种标准顶点结构。
/// </summary>
/// <remarks>对应 <c>Renderer/RenderItem.hpp:50-121</c> 与 <c>RenderItem.cpp:6-24</c>。</remarks>
public abstract class RenderItem : IDisposable
{
    /// <summary>简单二维点。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Point(float pointX, float pointY)
    {
        public float X = pointX;
        public float Y = pointY;
    }

    /// <summary>带颜色、不带纹理坐标的顶点。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ColoredPoint
    {
        public float X;
        public float Y;
        public float R;
        public float G;
        public float B;
        public float A;
    }

    /// <summary>带颜色 + 纹理坐标的顶点（拷贝纹理、最终合成等用）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TexturedPoint
    {
        public float X;
        public float Y;
        public float R;
        public float G;
        public float B;
        public float A;
        public float U;
        public float V;
    }

    protected uint VboId;

    protected uint VaoId;

    /// <summary>
    /// 把数值折回 <c>[0, 1]</c>。
    /// </summary>
    /// <remarks>
    /// 原版把颜色量化成 8 位再用 <c>&amp; 0xFF</c> 回绕；projectM 改用浮点，等价于
    /// <c>f' = f % (256/255)</c> 的欧几里得取模（见 <c>RenderItem.hpp:9-42</c> 的推导）。
    /// </remarks>
    public static float ColorModulo(float x)
    {
        const float m = 256f / 255f;
        var remainder = x % m;
        return (remainder + m) % m;
    }

    /// <inheritdoc cref="ColorModulo(float)"/>
    public static float ColorModulo(double x) => ColorModulo((float)x);

    /// <summary>派生类里设置各顶点属性指针。</summary>
    public abstract void InitVertexAttrib();

    public virtual void Dispose()
    {
        if (!GlContext.IsInitialized)
        {
            return;
        }

        var gl = GlContext.Gl;
        if (VboId != 0)
        {
            gl.DeleteBuffer(VboId);
            VboId = 0;
        }

        if (VaoId != 0)
        {
            gl.DeleteVertexArray(VaoId);
            VaoId = 0;
        }
    }

    /// <summary>创建 VAO/VBO 并调用 <see cref="InitVertexAttrib"/>（由派生类构造函数调用）。</summary>
    protected void Init()
    {
        var gl = GlContext.Gl;
        VaoId = gl.GenVertexArray();
        VboId = gl.GenBuffer();

        gl.BindVertexArray(VaoId);
        gl.BindBuffer(GLEnum.ArrayBuffer, VboId);

        InitVertexAttrib();

        gl.BindVertexArray(0);
        gl.BindBuffer(GLEnum.ArrayBuffer, 0);
    }
}

/// <summary>顶点结构的字节布局（对应 C++ 的 <c>offsetof</c>/<c>sizeof</c>）。</summary>
public static class VertexLayout
{
    /// <summary><c>sizeof(TexturedPoint)</c> = 8 个 float。</summary>
    public const int TexturedPointSize = 8 * sizeof(float);

    /// <summary><c>sizeof(ColoredPoint)</c> = 6 个 float。</summary>
    public const int ColoredPointSize = 6 * sizeof(float);

    /// <summary><c>offsetof(TexturedPoint, u)</c>。</summary>
    public const int TexturedPointUOffset = 6 * sizeof(float);

    /// <summary><c>offsetof(ColoredPoint, r)</c>。</summary>
    public const int ColoredPointRgbOffset = 2 * sizeof(float);
}
