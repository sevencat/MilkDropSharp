using Silk.NET.Input;

namespace MilkDrop.ProjectM.App;

/// <summary>快捷键动作。</summary>
internal enum HotkeyCommand
{
    TogglePause,
    NextPreset,
    PreviousPreset,
    RandomPreset,
    ToggleLock,
    Screenshot,
    ToggleUi,
    ToggleFullscreen,
    ToggleHelp,
    Quit,
}

/// <summary>
/// 直接把键盘事件翻成 <see cref="HotkeyCommand"/>。
/// </summary>
/// <remarks>
/// 不走 ImGui 的输入队列：<see cref="ImguiInput"/> 只映射了功能键，字母/数字没进 ImGui，
/// 用它做全局快捷键会漏键。这里与 ImguiInput 并存，互不影响。
/// </remarks>
internal sealed class Hotkeys : IDisposable
{
    private readonly IInputContext _input;

    public Hotkeys(IInputContext input)
    {
        _input = input;

        foreach (IKeyboard keyboard in input.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
        }
    }

    /// <summary>按下快捷键时触发（在窗口事件线程上同步调用）。</summary>
    public event Action<HotkeyCommand>? Triggered;

    public void Dispose()
    {
        foreach (IKeyboard keyboard in _input.Keyboards)
        {
            keyboard.KeyDown -= OnKeyDown;
        }
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        switch (key)
        {
            case Key.Space:
                Triggered?.Invoke(HotkeyCommand.TogglePause);
                break;
            case Key.N:
            case Key.Right:
                Triggered?.Invoke(HotkeyCommand.NextPreset);
                break;
            case Key.P:
            case Key.Left:
                Triggered?.Invoke(HotkeyCommand.PreviousPreset);
                break;
            case Key.R:
                Triggered?.Invoke(HotkeyCommand.RandomPreset);
                break;
            case Key.L:
                Triggered?.Invoke(HotkeyCommand.ToggleLock);
                break;
            case Key.S:
                Triggered?.Invoke(HotkeyCommand.Screenshot);
                break;
            case Key.F:
                Triggered?.Invoke(HotkeyCommand.ToggleUi);
                break;
            case Key.F11:
                Triggered?.Invoke(HotkeyCommand.ToggleFullscreen);
                break;
            case Key.H:
                Triggered?.Invoke(HotkeyCommand.ToggleHelp);
                break;
            case Key.Escape:
            case Key.Q:
                Triggered?.Invoke(HotkeyCommand.Quit);
                break;
        }
    }
}
