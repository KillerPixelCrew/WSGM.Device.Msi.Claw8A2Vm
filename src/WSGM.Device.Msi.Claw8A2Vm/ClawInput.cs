using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Msi.Claw8A2Vm;

/// <summary>
/// Carries an OEM button press from the firmware's WMI event into the controller sample stream.
/// </summary>
/// <remarks>
/// The Claw's two front buttons are physical controller buttons, and they belong on the virtual
/// target as its Steam and Quick Access buttons — that is what they are printed for, and Steam
/// answers its own controller natively. They were reaching WSGM as semantic OEM events and going no
/// further, so the virtual Steam Deck had neither button: the controller configurator listed no
/// such controls, nothing was bound to them, and no glyph could appear for a control Steam did not
/// believe existed.
/// <para>
/// A latch is needed because the firmware does not put them in the DirectInput report at all. They
/// arrive as MSI WMI events — one event per press, with no release — while samples are produced by
/// the pad reader at about 125 Hz. Holding the bit for <see cref="HoldDuration"/> turns that single
/// event into a press and a release the virtual pad can actually deliver.
/// </para>
/// </remarks>
internal sealed class ClawOemButtonLatch
{
    /// <summary>How long a latched button stays down.</summary>
    /// <remarks>
    /// Long enough to survive a dropped or coalesced sample at the reader's rate, short enough to
    /// stay a tap rather than becoming a long press in whatever is reading it.
    /// </remarks>
    internal static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(120);

    private readonly object _gate = new();
    private DateTimeOffset _guideUntil;
    private DateTimeOffset _quickAccessUntil;

    /// <summary>Latches one button down for <see cref="HoldDuration"/>.</summary>
    /// <param name="button">The canonical button the press maps to.</param>
    /// <param name="now">Current time.</param>
    internal void Press(CanonicalButtons button, DateTimeOffset now)
    {
        lock (_gate)
        {
            DateTimeOffset until = now + HoldDuration;
            if ((button & CanonicalButtons.Guide) != 0)
            {
                _guideUntil = until;
            }

            if ((button & CanonicalButtons.QuickAccess) != 0)
            {
                _quickAccessUntil = until;
            }
        }
    }

    /// <summary>Returns the buttons that should be down in a sample taken now.</summary>
    /// <param name="now">The sample's timestamp.</param>
    /// <returns>The latched buttons, or none once the hold has elapsed.</returns>
    internal CanonicalButtons Current(DateTimeOffset now)
    {
        lock (_gate)
        {
            CanonicalButtons held = CanonicalButtons.None;
            held |= now < _guideUntil ? CanonicalButtons.Guide : CanonicalButtons.None;
            held |= now < _quickAccessUntil ? CanonicalButtons.QuickAccess : CanonicalButtons.None;
            return held;
        }
    }
}

internal static class ClawControllerCodec
{
    public static CanonicalControllerSample Decode(
        ReadOnlySpan<byte> report,
        long sequence,
        long cycleGeneration,
        DateTimeOffset timestamp,
        SampleQuality quality = SampleQuality.Good,
        ClawOemButtonLatch? oemButtons = null)
    {
        if (report.Length != 64 || report[0] != 0x01)
        {
            throw new ArgumentException("The A2VM DirectInput source requires a 64-byte report 0x01.", nameof(report));
        }

        CanonicalButtons buttons = CanonicalButtons.None;
        buttons |= IsSet(report[5], 4) ? CanonicalButtons.X : 0;
        buttons |= IsSet(report[5], 5) ? CanonicalButtons.A : 0;
        buttons |= IsSet(report[5], 6) ? CanonicalButtons.B : 0;
        buttons |= IsSet(report[5], 7) ? CanonicalButtons.Y : 0;
        buttons |= IsSet(report[6], 0) ? CanonicalButtons.LeftShoulder : 0;
        buttons |= IsSet(report[6], 1) ? CanonicalButtons.RightShoulder : 0;
        buttons |= IsSet(report[6], 4) ? CanonicalButtons.View : 0;
        buttons |= IsSet(report[6], 5) ? CanonicalButtons.Menu : 0;
        buttons |= IsSet(report[6], 6) ? CanonicalButtons.LeftStick : 0;
        buttons |= IsSet(report[6], 7) ? CanonicalButtons.RightStick : 0;
        // Measured on MS-1T52: byte 7 bit 4 is the LEFT paddle (M1), and bit 3 is
        // the RIGHT paddle (M2). Handheld Companion has these two assignments reversed.
        buttons |= IsSet(report[7], 4) ? CanonicalButtons.RearPaddle1 : 0;
        buttons |= IsSet(report[7], 3) ? CanonicalButtons.RearPaddle2 : 0;
        buttons |= DecodeHat(report[5] & 0x0F);

        // The two front OEM buttons are not in this report — the firmware delivers them as WMI
        // events — so they are merged in from the latch that receives those events.
        buttons |= oemButtons?.Current(timestamp) ?? CanonicalButtons.None;

        return new CanonicalControllerSample
        {
            Sequence = sequence,
            CycleGeneration = cycleGeneration,
            Timestamp = timestamp,
            Buttons = buttons,
            LeftStickX = Axis(report[1]),
            LeftStickY = -Axis(report[2]),
            RightStickX = Axis(report[3]),
            RightStickY = -Axis(report[4]),
            LeftTrigger = report[8] / 255f,
            RightTrigger = report[9] / 255f,
            Quality = quality,
        };
    }

    public static byte[] EncodeRumble(byte weak, byte strong, int reportLength = 11)
    {
        if (reportLength < 11)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reportLength),
                "The Claw rumble payload needs at least 11 bytes.");
        }

        byte[] report = new byte[reportLength];
        report[0] = 0x05;
        report[1] = 0x01;
        report[4] = weak;
        report[5] = strong;
        return report;
    }

    private static bool IsSet(byte value, int bit) => (value & (1 << bit)) != 0;

    private static float Axis(byte value) => Math.Clamp((value - 128) / 127f, -1, 1);

    private static CanonicalButtons DecodeHat(int hat) => hat switch
    {
        0 => CanonicalButtons.DPadUp,
        1 => CanonicalButtons.DPadUp | CanonicalButtons.DPadRight,
        2 => CanonicalButtons.DPadRight,
        3 => CanonicalButtons.DPadRight | CanonicalButtons.DPadDown,
        4 => CanonicalButtons.DPadDown,
        5 => CanonicalButtons.DPadDown | CanonicalButtons.DPadLeft,
        6 => CanonicalButtons.DPadLeft,
        7 => CanonicalButtons.DPadLeft | CanonicalButtons.DPadUp,
        _ => CanonicalButtons.None,
    };
}

internal readonly record struct ChordDecision(
    bool Suppress,
    bool ReleaseLeftWindows,
    bool ReleaseRightWindows);

internal sealed class FirmwareChordStateMachine
{
    private bool _leftWindowsDown;
    private bool _rightWindowsDown;
    private bool _leftWindowsReleased;
    private bool _rightWindowsReleased;
    private bool _controlDown;
    private bool _altDown;
    private bool _shiftDown;
    private bool _gDown;
    private bool _gSuppressed;
    private bool _pendingGSuppression;
    private bool _tabDown;

    public ChordDecision Observe(uint virtualKey, bool keyDown, bool injected)
    {
        if (injected)
        {
            return default;
        }

        switch (virtualKey)
        {
            case NativeKeyboard.VK_LWIN:
                return ObserveWindows(left: true, keyDown);
            case NativeKeyboard.VK_RWIN:
                return ObserveWindows(left: false, keyDown);
            case NativeKeyboard.VK_CONTROL:
            case NativeKeyboard.VK_LCONTROL:
            case NativeKeyboard.VK_RCONTROL:
                _controlDown = keyDown;
                return default;
            case NativeKeyboard.VK_MENU:
            case NativeKeyboard.VK_LMENU:
            case NativeKeyboard.VK_RMENU:
                _altDown = keyDown;
                return default;
            case NativeKeyboard.VK_SHIFT:
            case NativeKeyboard.VK_LSHIFT:
            case NativeKeyboard.VK_RSHIFT:
                _shiftDown = keyDown;
                return default;
            case NativeKeyboard.VK_G:
                return ObserveG(keyDown);
            case NativeKeyboard.VK_TAB:
                return ObserveTarget(ref _tabDown, keyDown);
            default:
                return default;
        }
    }

    public void CommitSyntheticReleases(bool leftAccepted, bool rightAccepted)
    {
        _leftWindowsReleased |= leftAccepted;
        _rightWindowsReleased |= rightAccepted;
        if (_pendingGSuppression)
        {
            _gSuppressed = leftAccepted || rightAccepted;
            _pendingGSuppression = false;
        }
    }

    public void SynchronizeModifiers(bool controlDown, bool altDown, bool shiftDown)
    {
        _controlDown = controlDown;
        _altDown = altDown;
        _shiftDown = shiftDown;
    }

    public void Reset()
    {
        _leftWindowsDown = false;
        _rightWindowsDown = false;
        _leftWindowsReleased = false;
        _rightWindowsReleased = false;
        _controlDown = false;
        _altDown = false;
        _shiftDown = false;
        _gDown = false;
        _gSuppressed = false;
        _pendingGSuppression = false;
        _tabDown = false;
    }

    public void InitializePreexisting(
        bool leftWindowsDown,
        bool rightWindowsDown,
        bool controlDown,
        bool altDown,
        bool shiftDown,
        bool gDown,
        bool tabDown)
    {
        Reset();
        _leftWindowsDown = leftWindowsDown;
        _rightWindowsDown = rightWindowsDown;
        _controlDown = controlDown;
        _altDown = altDown;
        _shiftDown = shiftDown;
        _gDown = gDown;
        _tabDown = tabDown;
    }

    private ChordDecision ObserveWindows(bool left, bool keyDown)
    {
        if (left)
        {
            if (!keyDown && _leftWindowsReleased)
            {
                _leftWindowsReleased = false;
                _leftWindowsDown = false;
                return new ChordDecision(true, false, false);
            }

            _leftWindowsDown = keyDown;
        }
        else
        {
            if (!keyDown && _rightWindowsReleased)
            {
                _rightWindowsReleased = false;
                _rightWindowsDown = false;
                return new ChordDecision(true, false, false);
            }

            _rightWindowsDown = keyDown;
        }

        return default;
    }

    private ChordDecision ObserveG(bool keyDown)
    {
        if (_gSuppressed)
        {
            _gDown = keyDown;
            _gSuppressed = keyDown;
            return new ChordDecision(true, false, false);
        }

        // Like HC, intercept the initial G down before Windows can activate Game Bar.
        // The hook cannot distinguish the OEM button from ordinary keyboard Win+G.
        if (keyDown && !_gDown && (_leftWindowsDown || _rightWindowsDown))
        {
            _gDown = true;
            _pendingGSuppression = (_leftWindowsDown && !_leftWindowsReleased)
                || (_rightWindowsDown && !_rightWindowsReleased);
            _gSuppressed = !_pendingGSuppression;
            return new ChordDecision(
                true,
                _leftWindowsDown && !_leftWindowsReleased,
                _rightWindowsDown && !_rightWindowsReleased);
        }

        return ObserveTarget(ref _gDown, keyDown);
    }

    private ChordDecision ObserveTarget(ref bool targetDown, bool keyDown)
    {
        if (keyDown)
        {
            targetDown = true;
            return default;
        }

        bool hadDown = targetDown;
        targetDown = false;
        if (hadDown
            || (!_leftWindowsDown && !_rightWindowsDown)
            || _controlDown
            || _altDown
            || _shiftDown)
        {
            return default;
        }

        return new ChordDecision(
            Suppress: true,
            ReleaseLeftWindows: _leftWindowsDown && !_leftWindowsReleased,
            ReleaseRightWindows: _rightWindowsDown && !_rightWindowsReleased);
    }
}

internal sealed class FirmwareChordSuppressor : IFirmwareChordSuppressor
{
    private const uint Marker = 0x5753474D;
    private readonly object _gate = new();
    private readonly FirmwareChordStateMachine _state = new();
    private readonly NativeKeyboard.Input[] _batch = new NativeKeyboard.Input[4];
    private readonly NativeKeyboard.Input[] _cleanup = new NativeKeyboard.Input[1];
    private readonly NativeKeyboard.HookProcedure _hookProcedure;
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private Action<Exception>? _fault;
    private int _stopping;

    public FirmwareChordSuppressor()
    {
        _hookProcedure = HookCallback;
    }

    public async ValueTask<bool> StartAsync(
        Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fault);
        TaskCompletionSource<bool> started;
        lock (_gate)
        {
            if (_thread is not null)
            {
                return _hook != 0;
            }

            started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _fault = fault;
            Volatile.Write(ref _stopping, 0);
            _thread = new Thread(() => RunHook(started))
            {
                IsBackground = true,
                Name = "WSGM Claw firmware chord suppressor",
            };
            _thread.Start();
        }

        try
        {
            return await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                ReportFault(exception);
            }

            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Thread? thread;
        uint threadId;
        lock (_gate)
        {
            thread = _thread;
            threadId = _threadId;
        }

        if (thread is null)
        {
            return;
        }

        Volatile.Write(ref _stopping, 1);

        if (threadId != 0)
        {
            _ = NativeKeyboard.PostThreadMessage(threadId, NativeKeyboard.WM_QUIT, 0, 0);
        }

        bool joined = await Task.Run(() => thread.Join(TimeSpan.FromSeconds(1)), CancellationToken.None)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!joined)
        {
            throw new TimeoutException("The firmware chord hook thread did not stop within one second.");
        }

        lock (_gate)
        {
            if (_thread is null)
            {
                _fault = null;
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private void RunHook(TaskCompletionSource<bool> started)
    {
        _threadId = NativeKeyboard.GetCurrentThreadId();
        _state.InitializePreexisting(
            NativeKeyboard.IsKeyDown(NativeKeyboard.VK_LWIN),
            NativeKeyboard.IsKeyDown(NativeKeyboard.VK_RWIN),
            NativeKeyboard.IsKeyDown(NativeKeyboard.VK_CONTROL),
            NativeKeyboard.IsKeyDown(NativeKeyboard.VK_MENU),
            NativeKeyboard.IsKeyDown(NativeKeyboard.VK_SHIFT),
            NativeKeyboard.IsKeyDown(NativeKeyboard.VK_G),
            NativeKeyboard.IsKeyDown(NativeKeyboard.VK_TAB));
        _hook = NativeKeyboard.SetWindowsHookEx(NativeKeyboard.WH_KEYBOARD_LL, _hookProcedure, 0, 0);
        if (_hook == 0)
        {
            started.TrySetResult(false);
            ClearThread();
            return;
        }

        started.TrySetResult(true);
        try
        {
            int messageResult = 0;
            while (Volatile.Read(ref _stopping) == 0
                && (messageResult = NativeKeyboard.GetMessage(
                       out NativeKeyboard.Message message,
                       0,
                       0,
                       0)) > 0)
            {
                _ = NativeKeyboard.TranslateMessage(in message);
                _ = NativeKeyboard.DispatchMessage(in message);
            }

            if (Volatile.Read(ref _stopping) == 0 && messageResult < 0)
            {
                ReportFault(new InvalidOperationException(
                    $"The firmware chord hook message loop failed with Win32 {Marshal.GetLastWin32Error()}."));
            }
            else if (Volatile.Read(ref _stopping) == 0)
            {
                ReportFault(new InvalidOperationException(
                    "The firmware chord hook thread exited without a stop request."));
            }
        }
        finally
        {
            _ = NativeKeyboard.UnhookWindowsHookEx(_hook);
            _hook = 0;
            _state.Reset();
            ClearThread();
        }
    }

    private nint HookCallback(int code, nuint message, nint data)
    {
        if (code < 0)
        {
            return NativeKeyboard.CallNextHookEx(_hook, code, message, data);
        }

        NativeKeyboard.KeyboardHookData keyboard = Marshal.PtrToStructure<NativeKeyboard.KeyboardHookData>(data);
        bool keyDown = message is NativeKeyboard.WM_KEYDOWN or NativeKeyboard.WM_SYSKEYDOWN;
        bool keyUp = message is NativeKeyboard.WM_KEYUP or NativeKeyboard.WM_SYSKEYUP;
        if (!keyDown && !keyUp)
        {
            return NativeKeyboard.CallNextHookEx(_hook, code, message, data);
        }

        bool injected = (keyboard.Flags & NativeKeyboard.LLKHF_INJECTED) != 0
            || keyboard.ExtraInfo == Marker;
        if (!injected && keyUp
            && keyboard.VirtualKey is NativeKeyboard.VK_G or NativeKeyboard.VK_TAB)
        {
            _state.SynchronizeModifiers(
                NativeKeyboard.IsKeyDown(NativeKeyboard.VK_CONTROL),
                NativeKeyboard.IsKeyDown(NativeKeyboard.VK_MENU),
                NativeKeyboard.IsKeyDown(NativeKeyboard.VK_SHIFT));
        }

        ChordDecision decision = _state.Observe(keyboard.VirtualKey, keyDown, injected);
        if (!decision.Suppress)
        {
            return NativeKeyboard.CallNextHookEx(_hook, code, message, data);
        }

        if (!decision.ReleaseLeftWindows && !decision.ReleaseRightWindows)
        {
            return 1;
        }

        int count = 0;
        _batch[count++] = NativeKeyboard.KeyInput(NativeKeyboard.VK_DUMMY, keyUp: false, Marker);
        _batch[count++] = NativeKeyboard.KeyInput(NativeKeyboard.VK_DUMMY, keyUp: true, Marker);
        int leftIndex = -1;
        int rightIndex = -1;
        if (decision.ReleaseLeftWindows)
        {
            leftIndex = count;
            _batch[count++] = NativeKeyboard.KeyInput(NativeKeyboard.VK_LWIN, keyUp: true, Marker);
        }

        if (decision.ReleaseRightWindows)
        {
            rightIndex = count;
            _batch[count++] = NativeKeyboard.KeyInput(NativeKeyboard.VK_RWIN, keyUp: true, Marker);
        }

        uint sent = NativeKeyboard.SendInput(checked((uint)count), _batch, Marshal.SizeOf<NativeKeyboard.Input>());
        if (sent == 1)
        {
            _cleanup[0] = NativeKeyboard.KeyInput(NativeKeyboard.VK_DUMMY, keyUp: true, Marker);
            _ = NativeKeyboard.SendInput(1, _cleanup, Marshal.SizeOf<NativeKeyboard.Input>());
        }

        bool leftReleased = leftIndex >= 0 && sent > leftIndex;
        bool rightReleased = rightIndex >= 0 && sent > rightIndex;
        _state.CommitSyntheticReleases(leftReleased, rightReleased);
        return leftReleased || rightReleased
            ? 1
            : NativeKeyboard.CallNextHookEx(_hook, code, message, data);
    }

    private void ClearThread()
    {
        lock (_gate)
        {
            _thread = null;
            _threadId = 0;
            _fault = null;
        }
    }

    private void ReportFault(Exception exception)
    {
        try
        {
            _fault?.Invoke(exception);
        }
        catch (Exception callbackException) when (callbackException is not OutOfMemoryException)
        {
            PluginTrace.Failure(
                "keyboard",
                "Firmware chord hook fault reporting failed",
                callbackException);
        }
    }
}

internal static partial class NativeKeyboard
{
    public const int WH_KEYBOARD_LL = 13;
    public const uint WM_QUIT = 0x0012;
    public const nuint WM_KEYDOWN = 0x0100;
    public const nuint WM_KEYUP = 0x0101;
    public const nuint WM_SYSKEYDOWN = 0x0104;
    public const nuint WM_SYSKEYUP = 0x0105;
    public const uint LLKHF_INJECTED = 0x10;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint INPUT_KEYBOARD = 1;
    public const uint VK_TAB = 0x09;
    public const uint VK_SHIFT = 0x10;
    public const uint VK_CONTROL = 0x11;
    public const uint VK_MENU = 0x12;
    public const uint VK_LSHIFT = 0xA0;
    public const uint VK_RSHIFT = 0xA1;
    public const uint VK_LCONTROL = 0xA2;
    public const uint VK_RCONTROL = 0xA3;
    public const uint VK_LMENU = 0xA4;
    public const uint VK_RMENU = 0xA5;
    public const uint VK_LWIN = 0x5B;
    public const uint VK_RWIN = 0x5C;
    public const uint VK_G = 0x47;
    public const uint VK_DUMMY = 0xFF;

    public delegate nint HookProcedure(int code, nuint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    // INPUT reserves the full mouse-sized union even for keyboard events: 32 bytes on x64.
    // A keyboard-only union makes cbSize 32 instead of 40, so SendInput rejects every release.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Message
    {
        public nint Window;
        public uint Value;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    public static bool IsKeyDown(uint virtualKey) =>
        (GetAsyncKeyState(checked((int)virtualKey)) & 0x8000) != 0;

    public static Input KeyInput(uint virtualKey, bool keyUp, nuint extraInfo) => new()
    {
        Type = INPUT_KEYBOARD,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = checked((ushort)virtualKey),
                Flags = (keyUp ? KEYEVENTF_KEYUP : 0)
                    | (virtualKey is VK_LWIN or VK_RWIN ? KEYEVENTF_EXTENDEDKEY : 0),
                ExtraInfo = extraInfo,
            },
        },
    };

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookEx(
        int hookId,
        HookProcedure procedure,
        nint module,
        uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(in Message message);

    [DllImport("user32.dll")]
    public static extern nint DispatchMessage(in Message message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();
}
