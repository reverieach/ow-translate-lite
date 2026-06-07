using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace OwTranslateLite.Core;

public sealed class HotKeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint ModShift = 0x0004;
    private const uint VkReturn = 0x0D;
    private const uint VkSpace = 0x20;

    private readonly int _id;
    private HwndSource? _source;
    private nint _handle;
    private bool _registered;

    public HotKeyService(int id)
    {
        _id = id;
    }

    public event EventHandler? Pressed;

    public HotKeyRegistrationResult Apply(nint windowHandle, bool enabled, string gesture)
    {
        Unregister();
        if (!enabled)
        {
            return HotKeyRegistrationResult.Disabled();
        }

        if (!TryParseGesture(gesture, out uint modifiers, out uint key))
        {
            return HotKeyRegistrationResult.InvalidGesture(gesture);
        }

        if (windowHandle == 0)
        {
            return HotKeyRegistrationResult.WindowHandleUnavailable();
        }

        _handle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle);
        _source?.AddHook(WndProc);
        _registered = RegisterHotKey(windowHandle, _id, modifiers, key);
        if (!_registered)
        {
            Unregister();
            return HotKeyRegistrationResult.RegistrationFailed(gesture);
        }

        return HotKeyRegistrationResult.Registered(gesture);
    }

    public void Unregister()
    {
        if (_registered && _handle != 0)
        {
            _ = UnregisterHotKey(_handle, _id);
        }

        _registered = false;
        _handle = 0;
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    public void Dispose()
    {
        Unregister();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == _id)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return 0;
    }

    private static bool TryParseGesture(string value, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        foreach (string part in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "enter":
                    key = VkReturn;
                    break;
                case "space":
                    key = VkSpace;
                    break;
            }
        }

        return modifiers != 0 && key != 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}

public sealed record HotKeyRegistrationResult(HotKeyRegistrationStatus Status, string Gesture)
{
    public static HotKeyRegistrationResult Disabled() => new(HotKeyRegistrationStatus.Disabled, "");

    public static HotKeyRegistrationResult Registered(string gesture) => new(HotKeyRegistrationStatus.Registered, gesture);

    public static HotKeyRegistrationResult InvalidGesture(string gesture) => new(HotKeyRegistrationStatus.InvalidGesture, gesture);

    public static HotKeyRegistrationResult WindowHandleUnavailable() => new(HotKeyRegistrationStatus.WindowHandleUnavailable, "");

    public static HotKeyRegistrationResult RegistrationFailed(string gesture) => new(HotKeyRegistrationStatus.RegistrationFailed, gesture);
}

public enum HotKeyRegistrationStatus
{
    Disabled,
    Registered,
    InvalidGesture,
    WindowHandleUnavailable,
    RegistrationFailed
}
