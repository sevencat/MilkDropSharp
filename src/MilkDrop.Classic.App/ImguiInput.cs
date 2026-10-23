using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;

namespace MilkDrop.Classic.App;

/// <summary>把 Silk.NET 的输入事件转发给 ImGui（对应 <c>imgui_impl_win32</c> 的职责）。</summary>
public sealed class ImguiInput : IDisposable
{
    private readonly IInputContext _input;
    private Vector2 _mousePosition;

    public ImguiInput(IInputContext input)
    {
        _input = input;

        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
            keyboard.KeyUp += OnKeyUp;
            keyboard.KeyChar += OnKeyChar;
        }

        foreach (var mouse in input.Mice)
        {
            mouse.MouseMove += OnMouseMove;
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
            mouse.Scroll += OnScroll;
        }
    }

    public bool WantCaptureKeyboard => ImGui.GetIO().WantCaptureKeyboard;

    public bool WantCaptureMouse => ImGui.GetIO().WantCaptureMouse;

    public void NewFrame(int width, int height, double deltaSeconds)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(Math.Max(1, width), Math.Max(1, height));
        io.DeltaTime = deltaSeconds > 0 ? (float)deltaSeconds : 1f / 60f;
        io.AddMousePosEvent(_mousePosition.X, _mousePosition.Y);

        ImGui.NewFrame();
    }

    public void Dispose()
    {
        foreach (var keyboard in _input.Keyboards)
        {
            keyboard.KeyDown -= OnKeyDown;
            keyboard.KeyUp -= OnKeyUp;
            keyboard.KeyChar -= OnKeyChar;
        }

        foreach (var mouse in _input.Mice)
        {
            mouse.MouseMove -= OnMouseMove;
            mouse.MouseDown -= OnMouseDown;
            mouse.MouseUp -= OnMouseUp;
            mouse.Scroll -= OnScroll;
        }
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        var io = ImGui.GetIO();
        UpdateModifiers(keyboard, io);

        if (TryMapKey(key, out var imguiKey))
        {
            io.AddKeyEvent(imguiKey, true);
        }
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int scancode)
    {
        var io = ImGui.GetIO();
        UpdateModifiers(keyboard, io);

        if (TryMapKey(key, out var imguiKey))
        {
            io.AddKeyEvent(imguiKey, false);
        }
    }

    private static void OnKeyChar(IKeyboard keyboard, char c) => ImGui.GetIO().AddInputCharacter(c);

    private void OnMouseMove(IMouse mouse, Vector2 position) => _mousePosition = position;

    private static void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (TryMapButton(button, out var imguiButton))
        {
            ImGui.GetIO().AddMouseButtonEvent(imguiButton, true);
        }
    }

    private static void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (TryMapButton(button, out var imguiButton))
        {
            ImGui.GetIO().AddMouseButtonEvent(imguiButton, false);
        }
    }

    private static void OnScroll(IMouse mouse, ScrollWheel wheel)
        => ImGui.GetIO().AddMouseWheelEvent(wheel.X, wheel.Y);

    private static void UpdateModifiers(IKeyboard keyboard, ImGuiIOPtr io)
    {
        io.AddKeyEvent(ImGuiKey.ModCtrl, keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight));
        io.AddKeyEvent(ImGuiKey.ModShift, keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight));
        io.AddKeyEvent(ImGuiKey.ModAlt, keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight));
        io.AddKeyEvent(ImGuiKey.ModSuper, keyboard.IsKeyPressed(Key.SuperLeft) || keyboard.IsKeyPressed(Key.SuperRight));
    }

    private static bool TryMapButton(MouseButton button, out int imguiButton)
    {
        imguiButton = button switch
        {
            MouseButton.Left => 0,
            MouseButton.Right => 1,
            MouseButton.Middle => 2,
            _ => -1,
        };

        return imguiButton >= 0;
    }

    private static bool TryMapKey(Key key, out ImGuiKey result)
    {
        result = key switch
        {
            Key.Escape => ImGuiKey.Escape,
            Key.Enter => ImGuiKey.Enter,
            Key.Tab => ImGuiKey.Tab,
            Key.Backspace => ImGuiKey.Backspace,
            Key.Delete => ImGuiKey.Delete,
            Key.Space => ImGuiKey.Space,
            Key.Left => ImGuiKey.LeftArrow,
            Key.Right => ImGuiKey.RightArrow,
            Key.Up => ImGuiKey.UpArrow,
            Key.Down => ImGuiKey.DownArrow,
            Key.Home => ImGuiKey.Home,
            Key.End => ImGuiKey.End,
            Key.PageUp => ImGuiKey.PageUp,
            Key.PageDown => ImGuiKey.PageDown,
            Key.Insert => ImGuiKey.Insert,
            Key.F1 => ImGuiKey.F1,
            Key.F2 => ImGuiKey.F2,
            Key.F3 => ImGuiKey.F3,
            Key.F4 => ImGuiKey.F4,
            Key.F5 => ImGuiKey.F5,
            Key.F6 => ImGuiKey.F6,
            Key.F7 => ImGuiKey.F7,
            Key.F8 => ImGuiKey.F8,
            Key.F9 => ImGuiKey.F9,
            Key.F10 => ImGuiKey.F10,
            Key.F11 => ImGuiKey.F11,
            Key.F12 => ImGuiKey.F12,
            _ => ImGuiKey.None,
        };

        return result != ImGuiKey.None;
    }
}
