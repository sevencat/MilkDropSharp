using Silk.NET.Input;

namespace MilkDrop.ShaderToy.Channels;

/// <summary>
/// GLFW 的 <see cref="Key"/> → 浏览器 <c>keyCode</c>。
/// </summary>
/// <remarks>
/// <para>
/// Shadertoy 的键盘通道按 <c>event.keyCode</c> 索引（<c>A</c>=65、<c>0</c>=48、方向键 37..40 等）。
/// 上游 desktop-shadertoy 直接用 GLFW 的键值写进纹理（<c>viewport.cpp</c> 的 keyboard 部分），
/// 结果就是"在桌面端能按、搬回网页端对不上"，这里按浏览器编码来。
/// </para>
/// <para>
/// 只覆盖常用键（字母/数字/功能键/方向键/修饰键/标点）。没映射到的键返回 0，不写进纹理。
/// </para>
/// </remarks>
public static class KeyboardKeyMap
{
    /// <summary>取浏览器 keyCode；没有对应值时返回 0。</summary>
    public static int ToKeyCode(Key key) => key switch
    {
        // 字母
        Key.A => 65, Key.B => 66, Key.C => 67, Key.D => 68, Key.E => 69, Key.F => 70, Key.G => 71,
        Key.H => 72, Key.I => 73, Key.J => 74, Key.K => 75, Key.L => 76, Key.M => 77, Key.N => 78,
        Key.O => 79, Key.P => 80, Key.Q => 81, Key.R => 82, Key.S => 83, Key.T => 84, Key.U => 85,
        Key.V => 86, Key.W => 87, Key.X => 88, Key.Y => 89, Key.Z => 90,

        // 数字（主键盘区）
        Key.Number0 => 48, Key.Number1 => 49, Key.Number2 => 50, Key.Number3 => 51, Key.Number4 => 52,
        Key.Number5 => 53, Key.Number6 => 54, Key.Number7 => 55, Key.Number8 => 56, Key.Number9 => 57,

        // 小键盘
        Key.Keypad0 => 96, Key.Keypad1 => 97, Key.Keypad2 => 98, Key.Keypad3 => 99, Key.Keypad4 => 100,
        Key.Keypad5 => 101, Key.Keypad6 => 102, Key.Keypad7 => 103, Key.Keypad8 => 104, Key.Keypad9 => 105,
        Key.KeypadMultiply => 106, Key.KeypadAdd => 107, Key.KeypadSubtract => 109,
        Key.KeypadDecimal => 110, Key.KeypadDivide => 111,

        // 控制键
        Key.Backspace => 8, Key.Tab => 9, Key.Enter => 13, Key.ShiftLeft => 16, Key.ControlLeft => 17,
        Key.AltLeft => 18, Key.CapsLock => 20, Key.Escape => 27, Key.Space => 32, Key.PageUp => 33,
        Key.PageDown => 34, Key.End => 35, Key.Home => 36, Key.Left => 37, Key.Up => 38,
        Key.Right => 39, Key.Down => 40, Key.Insert => 45, Key.Delete => 46,
        Key.ShiftRight => 16, Key.ControlRight => 17, Key.AltRight => 18,

        // 功能键
        Key.F1 => 112, Key.F2 => 113, Key.F3 => 114, Key.F4 => 115, Key.F5 => 116, Key.F6 => 117,
        Key.F7 => 118, Key.F8 => 119, Key.F9 => 120, Key.F10 => 121, Key.F11 => 122, Key.F12 => 123,

        // 标点（US 布局）
        Key.Semicolon => 186, Key.Equal => 187, Key.Comma => 188, Key.Minus => 189,
        Key.Period => 190, Key.Slash => 191, Key.GraveAccent => 192,
        Key.LeftBracket => 219, Key.BackSlash => 220, Key.RightBracket => 221, Key.Apostrophe => 222,

        _ => 0,
    };
}
