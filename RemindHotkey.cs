using System;
using System.Runtime.InteropServices;

namespace OliverAssistant
{
    // Global low-level keyboard hook scoped to the Insert key. While enabled, holding
    // Insert (anywhere - not scoped to any Cosmo window, same shape as the tilde dictation
    // hotkey in Hotkey.cs) starts recording a spoken reminder request; releasing it stops
    // recording. Entirely independent of dictation - different key, different feature
    // (scheduling a reminder via Actions.ScheduleReminder, not typing text), and
    // separately toggleable. When disabled, Insert passes through untouched instead of
    // being swallowed, so it still works normally for whatever else (e.g. a text editor's
    // overtype-mode toggle) uses it.
    public class RemindHotkey : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int VK_INSERT = 0x2D;

        public bool Enabled { get; set; }

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
        private bool _insertDown;

        public RemindHotkey()
        {
            _proc = HookCallback;
        }

        public void Install()
        {
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
            if (_hookId == IntPtr.Zero)
                throw new InvalidOperationException("Failed to install Insert-key hook (Win32 error " + Marshal.GetLastWin32Error() + ")");
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var vkCode = Marshal.ReadInt32(lParam);
                var msg = wParam.ToInt32();
                if (vkCode == VK_INSERT && Enabled)
                {
                    if (msg == WM_KEYDOWN)
                    {
                        if (!_insertDown)
                        {
                            _insertDown = true;
                            var handler = KeyDown;
                            if (handler != null) handler();
                        }
                        return (IntPtr)1; // swallow: don't let Insert reach the focused app
                    }
                    if (msg == WM_KEYUP)
                    {
                        _insertDown = false;
                        var handler = KeyUp;
                        if (handler != null) handler();
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
