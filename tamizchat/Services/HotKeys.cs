using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace TamizChat.Services;

/// <summary>The actions a key can be bound to.</summary>
public enum HotKeyAction
{
    ToggleMute,
    PushToTalk,
    ToggleDeafen,
    StopSound,
}

/// <summary>
/// Global keyboard shortcuts, live even when the app is not focused.
///
/// A low-level keyboard hook rather than `RegisterHotKey`, for one reason that
/// decides it: push-to-talk needs to know when the key is *released*, and
/// RegisterHotKey only ever reports a press. The hook sees both edges.
///
/// The hook never swallows anything — every key is passed straight on to
/// whatever has focus. A voice client that ate a key while somebody was playing
/// a game would be worse than having no shortcuts at all.
/// </summary>
public sealed class HotKeys
{
    private const int WhKeyboardLowLevel = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly HashSet<int> _down = [];

    private nint _hook;
    private LowLevelKeyboardProc? _callback;

    public static HotKeys Instance { get; } = new();

    /// <summary>
    /// Starts listening. Safe to call more than once.
    ///
    /// The delegate is stored in a field on purpose: the hook holds an unmanaged
    /// pointer to it, and a local would be collected while Windows still had the
    /// address — which crashes the process on the next keystroke rather than
    /// simply not working.
    /// </summary>
    public void Start()
    {
        if (_hook != nint.Zero)
        {
            return;
        }

        try
        {
            _callback = OnKey;
            _hook = SetWindowsHookEx(WhKeyboardLowLevel, _callback, nint.Zero, 0);
        }
        catch (Exception)
        {
            // Some environments refuse a global hook. Shortcuts stopping working
            // is a nuisance; the app failing to open because of it is not
            // acceptable.
            _callback = null;
            _hook = nint.Zero;
        }
    }

    public void Stop()
    {
        if (_hook == nint.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_hook);
        _hook = nint.Zero;
        _callback = null;
        _down.Clear();
    }

    private nint OnKey(int code, nint wParam, nint lParam)
    {
        if (code < 0)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        var key = Marshal.ReadInt32(lParam);
        var message = (int)wParam;
        var pressed = message is WmKeyDown or WmSysKeyDown;
        var released = message is WmKeyUp or WmSysKeyUp;

        // Windows repeats a held key. Only the first press and the final release
        // are interesting, or push-to-talk would retrigger dozens of times a
        // second while the key is down.
        if (pressed && !_down.Add(key))
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        if (released)
        {
            _down.Remove(key);
        }

        foreach (var (action, bound) in SettingsStore.Current.HotKeys)
        {
            if (bound != key || !Enum.TryParse<HotKeyAction>(action, out var parsed))
            {
                continue;
            }

            if (pressed)
            {
                _ui.TryEnqueue(() => Fire(parsed, down: true));
            }
            else if (released)
            {
                _ui.TryEnqueue(() => Fire(parsed, down: false));
            }
        }

        // Always passed on. See the note on the class.
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static void Fire(HotKeyAction action, bool down)
    {
        var voice = VoiceService.Instance;

        switch (action)
        {
            case HotKeyAction.ToggleMute when down:
                _ = voice.ToggleMuteAsync();
                break;

            // The only action that cares about the release: held means live.
            case HotKeyAction.PushToTalk:
                _ = voice.SetMutedAsync(!down);
                break;

            case HotKeyAction.ToggleDeafen when down:
                voice.SetDeafened(!voice.IsDeafened);
                break;

            case HotKeyAction.StopSound when down:
                voice.StopEffect();
                break;
        }
    }

    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, nint module, uint thread);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
}
