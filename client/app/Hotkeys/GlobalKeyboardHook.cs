// rgas-source global hotkeys — SPEC C-6, C-7, C-9.
//
// A WH_KEYBOARD_LL low-level keyboard hook. While Armed, Space / 1-4 / H are
// intercepted system-wide — even with the app unfocused or minimized — and
// CONSUMED (return 1) so the focused app never sees them (C-6). While a text
// input inside this app has focus, SuppressCheck returns true and keys pass
// through untouched (C-7). Disarmed, the hook passes everything through; the
// window's normal PreviewKeyDown/Up handles focused-mode keys instead.
//
// Key-ups are consumed too, and OS auto-repeat is filtered (a held Space must
// not toggle playback repeatedly). H reports both edges — the horn is
// hold-to-sound (C-9).
//
// Install on the UI thread (the hook callback needs its message pump).
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RgasSoundboard.Hotkeys;

public enum HotkeyAction { Space, Mode1, Mode2, Mode3, Mode4, Horn, Announce }

public sealed class GlobalKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly LowLevelKeyboardProc _proc; // kept alive: GC of this delegate kills the hook
    private readonly HashSet<int> _down = new(); // auto-repeat filter
    private IntPtr _hook = IntPtr.Zero;
    private bool _armed;

    public bool Armed
    {
        get => _armed;
        set { _armed = value; if (!value) _down.Clear(); }
    }

    /// <summary>C-7: return true to let keys through (text input focused in-app).</summary>
    public Func<bool>? SuppressCheck { get; set; }

    /// <summary>(action, isDown). Space/modes fire on down only; Horn fires both edges.</summary>
    public event Action<HotkeyAction, bool>? Hotkey;

    public GlobalKeyboardHook()
    {
        _proc = Callback;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("failed to install keyboard hook (C-6)");
    }

    private static HotkeyAction? Map(int vk) => vk switch
    {
        0x20 => HotkeyAction.Space,
        0x31 or 0x61 => HotkeyAction.Mode1, // main row or numpad
        0x32 or 0x62 => HotkeyAction.Mode2,
        0x33 or 0x63 => HotkeyAction.Mode3,
        0x34 or 0x64 => HotkeyAction.Mode4,
        0x48 => HotkeyAction.Horn, // H
        0x41 => HotkeyAction.Announce, // A (C-42)
        _ => null,
    };

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _armed && !(SuppressCheck?.Invoke() ?? false))
        {
            int msg = (int)wParam;
            bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;
            if (isDown || isUp)
            {
                int vk = Marshal.ReadInt32(lParam); // first field of KBDLLHOOKSTRUCT
                var action = Map(vk);
                if (action is not null)
                {
                    if (isDown)
                    {
                        if (_down.Add(vk)) // first edge only; swallow auto-repeats
                            Hotkey?.Invoke(action.Value, true);
                    }
                    else
                    {
                        _down.Remove(vk);
                        Hotkey?.Invoke(action.Value, false);
                    }
                    return 1; // consume (C-6): the focused app never sees the key
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
