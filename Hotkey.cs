using System;
using System.Runtime.InteropServices;

namespace OliverAssistant
{
    // Global low-level keyboard hook, scoped to the tilde/backtick key (VK_OEM_3) so
    // Cosmo can offer press-and-hold push-to-talk dictation regardless of which window
    // has focus. Swallows the key so a stray "~" never reaches whatever app you're
    // dictating into. When Gaming Mode is on, also watches Tab (VK_TAB) as an additional
    // trigger - many games bind '~' to their console. Tab is only treated as a trigger
    // via plain WM_KEYDOWN/WM_KEYUP, not WM_SYSKEYDOWN/WM_SYSKEYUP (which is how Tab
    // arrives when Alt is held) - that keeps Alt+Tab window switching working normally
    // even while gaming mode is on.
    public class PushToTalkHotkey : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_OEM_3 = 0xC0; // '~' / '`' key on a US keyboard
        private const int VK_TAB = 0x09; // Tab - gaming mode's extra trigger

        public bool GamingModeEnabled { get; set; }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        public event Action KeyDown;
        public event Action KeyUp;

        // Kept as a field (not a local/lambda) so the GC never collects the delegate
        // out from under user32 while the hook is installed.
        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hookId = IntPtr.Zero;
        private bool _tildeDown;
        private bool _tabDown;

        public PushToTalkHotkey()
        {
            _proc = HookCallback;
        }

        public void Install()
        {
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
            if (_hookId == IntPtr.Zero)
                throw new InvalidOperationException("Failed to install keyboard hook (Win32 error " + Marshal.GetLastWin32Error() + ")");
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                // vkCode is the first DWORD field of KBDLLHOOKSTRUCT.
                var vkCode = Marshal.ReadInt32(lParam);
                var msg = wParam.ToInt32();
                bool isTilde = vkCode == VK_OEM_3;
                // Only plain (non-Alt) Tab counts, so Alt+Tab window switching still works.
                bool isTab = GamingModeEnabled && vkCode == VK_TAB && (msg == WM_KEYDOWN || msg == WM_KEYUP);
                if (isTilde || isTab)
                {
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        // Either key can be held simultaneously; only fire KeyDown on the
                        // 0->1 transition so a hold on both doesn't stop/restart dictation.
                        bool wasDown = _tildeDown || _tabDown;
                        if (isTilde) _tildeDown = true; else _tabDown = true;
                        if (!wasDown)
                        {
                            var handler = KeyDown;
                            if (handler != null) handler();
                        }
                        return (IntPtr)1; // swallow: don't let the key reach the focused app
                    }
                    if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                    {
                        if (isTilde) _tildeDown = false; else _tabDown = false;
                        if (!_tildeDown && !_tabDown)
                        {
                            var handler = KeyUp;
                            if (handler != null) handler();
                        }
                        return (IntPtr)1;
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }
    }
}
