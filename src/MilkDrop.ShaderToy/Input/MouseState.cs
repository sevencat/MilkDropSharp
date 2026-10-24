using System.Numerics;

namespace MilkDrop.ShaderToy.Input;

/// <summary>
/// <c>iMouse</c> 的状态机。
/// </summary>
/// <remarks>
/// <para>
/// 坐标是"像素 + 左下原点"（与 <c>gl_FragCoord</c> 一致），所以喂进来的窗口坐标要先翻转 Y。
/// </para>
/// <para>
/// 语义按 Shadertoy 网页端：<c>xy</c> 只在按住时跟随；<c>zw</c> 是按下那一刻的位置，
/// **松开后取负**（<c>z &gt; 0</c> 即"正在拖拽"，这是所有作品判断拖拽状态的方式）。
/// </para>
/// <para>
/// 上游 desktop-shadertoy 在松开时直接写 <c>(-1, -1)</c>（<c>viewport.cpp:655-659</c>），
/// 拿不到"点在哪里松开的"，与网页端不一致，这里不照抄。
/// </para>
/// </remarks>
public sealed class MouseState
{
    private Vector2 _position;
    private Vector2 _clickPosition;
    private bool _pressed;

    /// <summary>当前 <c>iMouse</c> 值。</summary>
    public Vector4 Value { get; private set; } = new(0f, 0f, -1f, -1f);

    /// <summary>是否处于按住状态。</summary>
    public bool IsPressed => _pressed;

    /// <summary>当前的左下原点像素坐标。</summary>
    public Vector2 Position => _position;

    /// <summary>回到初始状态（<c>zw = -1</c>，即"从未点击"）。</summary>
    public void Reset()
    {
        _position = Vector2.Zero;
        _clickPosition = Vector2.Zero;
        _pressed = false;
        Value = new Vector4(0f, 0f, -1f, -1f);
    }

    /// <summary>鼠标移动（<paramref name="position"/> 已翻转成左下原点像素）。</summary>
    public void Move(Vector2 position)
    {
        _position = position;

        if (_pressed)
        {
            Value = new Vector4(position.X, position.Y, Value.Z, Value.W);
        }
    }

    /// <summary>按下：<c>xy = zw = 当前位置</c>。</summary>
    public void Press(Vector2 position)
    {
        _position = position;
        _clickPosition = position;
        _pressed = true;
        Value = new Vector4(position.X, position.Y, position.X, position.Y);
    }

    /// <summary>松开：<c>zw</c> 变成负的点击位置，<c>xy</c> 停在最后位置。</summary>
    public void Release()
    {
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        Value = new Vector4(Value.X, Value.Y, -_clickPosition.X, -_clickPosition.Y);
    }

    /// <summary>把窗口坐标（左上原点）换成 <c>gl_FragCoord</c> 口径（左下原点、像素）。</summary>
    public static Vector2 FromWindowPoint(Vector2 windowPoint, Vector2 windowSize, Vector2 framebufferSize)
    {
        float scaleX = windowSize.X > 0f ? framebufferSize.X / windowSize.X : 1f;
        float scaleY = windowSize.Y > 0f ? framebufferSize.Y / windowSize.Y : 1f;

        float x = windowPoint.X * scaleX;
        float y = framebufferSize.Y - (windowPoint.Y * scaleY) - 1f;
        return new Vector2(x, MathF.Max(0f, y));
    }
}
