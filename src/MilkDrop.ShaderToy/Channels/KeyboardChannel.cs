using MilkDrop.Rendering.Gles3;
using Silk.NET.OpenGLES;
using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ShaderToy.Channels;

/// <summary>
/// 键盘通道：256×3 的 RGBA8 纹理，对应 Shadertoy 的 <c>keyboard</c> 输入。
/// </summary>
/// <remarks>
/// <para>
/// 约定（网页端）：纹理第 0 行是"当前是否有按下"，第 1 行是"这一帧是否刚按下"
/// （<c>pressed</c>），第 2 行是"这一帧是否刚松开"。作品里最常见的是
/// <c>texture(iChannel0, vec2((65.5)/256.0, 0.5/3.0)).x</c> 判断 A 键。
/// </para>
/// <para>
/// x 轴就是 keyCode 本身（0..255），所以 GLFW 的键值要映射到浏览器 <c>keyCode</c>
/// （见 <see cref="KeyboardKeyMap"/>）。
/// </para>
/// </remarks>
public sealed class KeyboardChannel : IDisposable
{
    public const int Width = 256;

    public const int Height = 3;

    /// <summary>行索引：按住。</summary>
    public const int RowPressed = 0;

    /// <summary>行索引：本帧刚按下。</summary>
    public const int RowDown = 1;

    /// <summary>行索引：本帧刚松开。</summary>
    public const int RowUp = 2;

    private readonly Texture _texture;
    private readonly byte[] _pixels = new byte[Width * Height * 4];
    private bool _dirty;

    public KeyboardChannel()
    {
        _texture = new Texture("keyboard", Width, Height, (int)GLEnum.Rgba8, GLEnum.Rgba, GLEnum.UnsignedByte, false);

        // alpha 恒为 255，方便作品直接读 .a
        for (var i = 3; i < _pixels.Length; i += 4)
        {
            _pixels[i] = 255;
        }

        Upload();
    }

    public Texture Texture => _texture;

    /// <summary>当前是否按住（0..255 的 keyCode）。</summary>
    public bool IsKeyPressed(int keyCode) => keyCode is >= 0 and < Width && _pixels[(RowPressed * Width) + keyCode * 4] != 0;

    /// <summary>设置某个键的状态；只记录"状态变化"，帧末由 <see cref="EndFrame"/> 清掉边沿行。</summary>
    public void SetKey(int keyCode, bool down)
    {
        if (keyCode is < 0 or >= Width)
        {
            return;
        }

        int pressedOffset = ((RowPressed * Width) + keyCode) * 4;
        bool wasDown = _pixels[pressedOffset] != 0;
        if (wasDown == down)
        {
            return;
        }

        _pixels[pressedOffset] = down ? (byte)255 : (byte)0;
        _pixels[(((down ? RowDown : RowUp) * Width) + keyCode) * 4] = 255;
        _dirty = true;
    }

    /// <summary>帧首：把当前累积的按键状态推上 GPU（渲染前调用）。</summary>
    public void BeginFrame() => Upload();

    /// <summary>帧尾：清掉"刚按下/刚松开"两行，下一帧重新累积（只改 CPU 侧，不额外上传）。</summary>
    public void EndFrame()
    {
        Array.Clear(_pixels, RowDown * Width * 4, Width * 4 * 2);
        _dirty = true;
    }

    public void Dispose() => _texture.Dispose();

    private void Upload()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;

        var gl = GlContext.Gl;
        gl.BindTexture(GLEnum.Texture2D, _texture.TextureId);
        gl.PixelStore(GLEnum.UnpackAlignment, 4);
        gl.TexSubImage2D(GLEnum.Texture2D, 0, 0, 0, Width, Height, GLEnum.Rgba, GLEnum.UnsignedByte, _pixels);
        gl.BindTexture(GLEnum.Texture2D, 0);
    }
}
