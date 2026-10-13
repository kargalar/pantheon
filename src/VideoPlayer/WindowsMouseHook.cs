using VideoPlayer.Core;
using System.Runtime.InteropServices;

namespace VideoPlayer;

internal enum MouseGesture
{
    LeftClick,
    RightClick,
    MiddleClick,
    WheelUp,
    WheelDown,
    CtrlWheelUp,
    CtrlWheelDown,
    ShiftWheelUp,
    ShiftWheelDown,
    AltWheelUp,
    AltWheelDown
}

internal sealed class WindowsMouseHook : IDisposable
{
    private const int LowLevelMouseHook = 14;
    private const int LeftButtonDown = 0x0201;
    private const int RightButtonDown = 0x0204;
    private const int MiddleButtonDown = 0x0207;
    private const int MouseWheel = 0x020A;
    private const uint RootAncestor = 2;
    private const int ControlKey = 0x11;
    private const int ShiftKey = 0x10;
    private const int AltKey = 0x12;

    private readonly IntPtr _targetWindow;
    private readonly Func<MouseGesture, int, int, bool> _onGesture;
    private readonly LowLevelMouseProc _callback;
    private IntPtr _hookHandle;
    private int _callbackFailureLogged;

    public WindowsMouseHook(
        IntPtr targetWindow,
        Func<MouseGesture, int, int, bool> onGesture)
    {
        _targetWindow = targetWindow;
        _onGesture = onGesture;
        _callback = HandleMouseEvent;
        _hookHandle = SetWindowsHookEx(LowLevelMouseHook, _callback, GetModuleHandle(null), 0);
        if (_hookHandle == IntPtr.Zero)
            throw new InvalidOperationException(UiText.MouseGesturesCouldNotBeInitialized);
    }

    private IntPtr HandleMouseEvent(int code, IntPtr message, IntPtr data)
    {
        try
        {
            var messageId = message.ToInt32();
            if (code < 0 || !IsRelevantMessage(messageId))
                return CallNextHookEx(_hookHandle, code, message, data);

            var mouseData = Marshal.PtrToStructure<MouseHookData>(data);
            var isButtonClick = messageId is LeftButtonDown or RightButtonDown or MiddleButtonDown;
            var isTargetForeground = GetForegroundWindow() == _targetWindow;
            var point = mouseData.Point;
            var pointedWindow = WindowFromPoint(point);
            var pointedRoot = pointedWindow == IntPtr.Zero ? IntPtr.Zero : GetAncestor(pointedWindow, RootAncestor);
            if (pointedRoot != _targetWindow || (!isTargetForeground && !isButtonClick))
                return CallNextHookEx(_hookHandle, code, message, data);

            var gesture = messageId switch
            {
                LeftButtonDown => MouseGesture.LeftClick,
                RightButtonDown => MouseGesture.RightClick,
                MiddleButtonDown => MouseGesture.MiddleClick,
                MouseWheel => WheelGesture(
                    mouseData.MouseData,
                    IsKeyDown(ControlKey),
                    IsKeyDown(ShiftKey),
                    IsKeyDown(AltKey)),
                _ => (MouseGesture?)null
            };
            if (gesture is not null && _onGesture(gesture.Value, mouseData.Point.X, mouseData.Point.Y))
            {
                // Let an inactive window receive the physical click so Windows can activate it.
                if (isTargetForeground)
                    return new IntPtr(1);
            }
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _callbackFailureLogged, 1) == 0)
                _ = Task.Run(() => AppLogger.Error("Mouse input hook callback failed", exception));
        }

        return CallNextHookEx(_hookHandle, code, message, data);
    }

    internal static MouseGesture WheelGesture(uint mouseData, bool altPressed)
        => WheelGesture(mouseData, controlPressed: false, shiftPressed: false, altPressed: altPressed);

    internal static bool IsRelevantMessage(int messageId) =>
        messageId is LeftButtonDown or RightButtonDown or MiddleButtonDown or MouseWheel;

    internal static MouseGesture WheelGesture(uint mouseData, bool controlPressed, bool shiftPressed, bool altPressed)
    {
        var delta = unchecked((short)(mouseData >> 16));
        if (controlPressed) return delta > 0 ? MouseGesture.CtrlWheelUp : MouseGesture.CtrlWheelDown;
        if (shiftPressed) return delta > 0 ? MouseGesture.ShiftWheelUp : MouseGesture.ShiftWheelDown;
        if (altPressed) return delta > 0 ? MouseGesture.AltWheelUp : MouseGesture.AltWheelDown;
        return delta > 0 ? MouseGesture.WheelUp : MouseGesture.WheelDown;
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        if (_hookHandle == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        GC.KeepAlive(_callback);
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MouseHookData
    {
        public readonly NativePoint Point;
        public readonly uint MouseData;
        public readonly int Flags;
        public readonly int Time;
        public readonly IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hookHandle, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
