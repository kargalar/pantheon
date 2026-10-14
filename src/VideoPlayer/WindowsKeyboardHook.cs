using VideoPlayer.Core;
using System.Runtime.InteropServices;
using Avalonia.Input;

namespace VideoPlayer;

internal sealed class WindowsKeyboardHook : IDisposable
{
    public WindowsKeyboardHook(IntPtr targetWindow, Func<ShortcutGesture, bool> onGesture)
    {
        // System-wide WH_KEYBOARD_LL hook is disabled so that user-defined global shortcuts
        // and window management tools are not intercepted or overwritten.
        // App shortcuts are handled directly via the Avalonia window KeyDown tunneling event.
    }

    public void Dispose()
    {
    }
}
