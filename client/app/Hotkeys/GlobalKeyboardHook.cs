// rgas-source background hotkeys — SPEC C-6, C-7, C-9, C-45, C-46.
//
// A WH_KEYBOARD_LL low-level keyboard hook. While Enabled, Space / 1-4 / H / A
// are observed system-wide — even with the app unfocused or minimized — but this
// is a PASSIVE subscription: every event is always passed on to CallNextHookEx,
// so the focused app (e.g. a scorekeeping program that also binds Space) still
// receives every key exactly as if the hook weren't installed (C-6). While a
// text input inside this app has focus, SuppressCheck returns true and the hook
// does not fire its own action either (C-7). Disabled, the hook still passes
// everything through untouched and fires no actions; the window's normal
// PreviewKeyDown/Up handles focused-mode keys instead.
//
// OS auto-repeat is filtered so a held Space doesn't toggle playback repeatedly.
// H reports both edges — the horn is hold-to-sound (C-9); Ctrl+H is a toggle tap
// that switches the active horn (C-46). O is only a hotkey (open mic, C-45) with
// Ctrl held at the moment of the key-down; otherwise it's an ordinary keystroke.
//
// Install on the UI thread (the hook callback needs its message pump).
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RgasSoundboard.Hotkeys;

public enum HotkeyAction { Space, Mode1, Mode2, Mode3, Mode4, Horn, HornToggle, Announce, Skip, OpenMic }

public sealed class GlobalKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_CONTROL = 0x11;
    private const int VK_H = 0x48;

    private readonly LowLevelKeyboardProc _proc; // kept alive: GC of this delegate kills the hook
    private readonly HashSet<int> _down = new(); // auto-repeat filter
    private IntPtr _hook = IntPtr.Zero;
    private bool _enabled;
    private bool _hIsToggle; // the current H hold began as Ctrl+H (a horn toggle), not a horn blast

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; if (!value) _down.Clear(); }
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
        0x4B => HotkeyAction.Skip, // K (C-44)
        0x4F => HotkeyAction.OpenMic, // O — only a hotkey with Ctrl held, gated below (C-45)
        _ => null,
    };

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _enabled && !(SuppressCheck?.Invoke() ?? false))
        {
            int msg = (int)wParam;
            bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;
            if (isDown || isUp)
            {
                int vk = Marshal.ReadInt32(lParam); // first field of KBDLLHOOKSTRUCT
                var action = Map(vk);
                // O is only a hotkey while Ctrl is held — otherwise it's an
                // ordinary keystroke (typing "o" elsewhere must never toggle the mic).
                if (action == HotkeyAction.OpenMic && (GetAsyncKeyState(VK_CONTROL) & 0x8000) == 0)
                    action = null;
                if (action is not null)
                {
                    // C-46: H alone = the hold-to-sound horn; Ctrl+H = a toggle tap.
                    // Decide at key-down (from the live Ctrl state) and remember it,
                    // so the matching key-up fires the same action's edge.
                    if (vk == VK_H)
                    {
                        if (isDown)
                        {
                            if (_down.Add(vk))
                            {
                                _hIsToggle = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                                Hotkey?.Invoke(_hIsToggle ? HotkeyAction.HornToggle : HotkeyAction.Horn, true);
                            }
                        }
                        else
                        {
                            _down.Remove(vk);
                            Hotkey?.Invoke(_hIsToggle ? HotkeyAction.HornToggle : HotkeyAction.Horn, false);
                        }
                    }
                    else if (isDown)
                    {
                        if (_down.Add(vk)) // first edge only; swallow auto-repeats
                            Hotkey?.Invoke(action.Value, true);
                    }
                    else
                    {
                        _down.Remove(vk);
                        Hotkey?.Invoke(action.Value, false);
                    }
                }
            }
        }
        // Always pass through (C-6): this is a passive subscription, never a
        // capture — the focused app receives every key exactly as normal.
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey); // C-45/C-46: live Ctrl state
}
