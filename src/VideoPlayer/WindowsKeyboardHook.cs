using VideoPlayer.Core;
using System.Runtime.InteropServices;
using Avalonia.Input;

namespace VideoPlayer;

internal sealed class WindowsKeyboardHook : IDisposable
{
    private const int LowLevelKeyboardHook = 13;
    private const int KeyDownMessage = 0x0100;
    private const int SystemKeyDownMessage = 0x0104;
    private const int ControlKey = 0x11;
    private const int ShiftKey = 0x10;
    private const int AltKey = 0x12;
    private const int LeftWindowsKey = 0x5B;
    private const int RightWindowsKey = 0x5C;

    private readonly IntPtr _targetWindow;
    private readonly Func<ShortcutGesture, bool> _onGesture;
    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hookHandle;
    private int _callbackFailureLogged;

    public WindowsKeyboardHook(IntPtr targetWindow, Func<ShortcutGesture, bool> onGesture)
    {
        _targetWindow = targetWindow;
        _onGesture = onGesture;
        _callback = HandleKeyboardEvent;
        _hookHandle = SetWindowsHookEx(LowLevelKeyboardHook, _callback, GetModuleHandle(null), 0);
        if (_hookHandle == IntPtr.Zero)
            throw new InvalidOperationException(UiText.KeyboardShortcutsCouldNotBeInitialized);
    }

    private IntPtr HandleKeyboardEvent(int code, IntPtr message, IntPtr data)
    {
        try
        {
            if (code >= 0 && (message.ToInt32() == KeyDownMessage || message.ToInt32() == SystemKeyDownMessage)
                && GetForegroundWindow() == _targetWindow)
            {
                var keyData = Marshal.PtrToStructure<KeyboardHookData>(data);
                if (ShortcutGesture.TryFromVirtualKey(keyData.VirtualKey, ReadModifiers(), out var gesture) && _onGesture(gesture))
                    return new IntPtr(1);
            }
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _callbackFailureLogged, 1) == 0)
                _ = Task.Run(() => AppLogger.Error("Keyboard input hook callback failed", exception));
        }

        return CallNextHookEx(_hookHandle, code, message, data);
    }

    private static KeyModifiers ReadModifiers()
    {
        var modifiers = KeyModifiers.None;
        if (IsKeyDown(ControlKey)) modifiers |= KeyModifiers.Control;
        if (IsKeyDown(ShiftKey)) modifiers |= KeyModifiers.Shift;
        if (IsKeyDown(AltKey)) modifiers |= KeyModifiers.Alt;
        if (IsKeyDown(LeftWindowsKey) || IsKeyDown(RightWindowsKey)) modifiers |= KeyModifiers.Meta;
        return modifiers;
    }

    private static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public void Dispose()
    {
        if (_hookHandle == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        GC.KeepAlive(_callback);
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KeyboardHookData
    {
        public readonly int VirtualKey;
        public readonly int ScanCode;
        public readonly int Flags;
        public readonly int Time;
        public readonly IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hookHandle, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
