using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinTileLauncher.Services;

internal sealed class GlobalKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkLwin = 0x5B;
    private const int VkRwin = 0x5C;
    private const int LlkhfInjected = 0x10;
    private const int LlkhfExtended = 0x01;
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const long SyntheticInputMarker = 0x543130;

    private readonly Action _windowsKeyPressed;
    private readonly bool _processInjectedInput;
    private readonly LowLevelKeyboardProc _callback;
    private readonly WindowsKeyStateMachine _state = new();
    private IntPtr _hookHandle;

    public GlobalKeyboardHook(Action windowsKeyPressed, bool processInjectedInput = false)
    {
        _windowsKeyPressed = windowsKeyPressed;
        _processInjectedInput = processInjectedInput;
        _callback = HookCallback;
    }

    public void Install()
    {
        if (_hookHandle != IntPtr.Zero)
            return;

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = GetModuleHandle(module?.ModuleName);
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _callback, moduleHandle, 0);
        if (_hookHandle == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code < 0)
            return CallNextHookEx(_hookHandle, code, message, data);

        var keyboardData = Marshal.PtrToStructure<LowLevelKeyboardInput>(data);
        if ((keyboardData.Flags & LlkhfInjected) != 0)
        {
            if (keyboardData.ExtraInfo.ToInt64() == SyntheticInputMarker || !_processInjectedInput)
                return CallNextHookEx(_hookHandle, code, message, data);
        }

        var isKeyDown = message == (IntPtr)WmKeyDown || message == (IntPtr)WmSysKeyDown;
        var isKeyUp = message == (IntPtr)WmKeyUp || message == (IntPtr)WmSysKeyUp;
        var isWindowsKey = keyboardData.VirtualKey == VkLwin || keyboardData.VirtualKey == VkRwin;
        var transition = _state.Process(keyboardData.VirtualKey, isKeyDown, isKeyUp, isWindowsKey);
        if (transition.InjectWindowsKeyDown && transition.ReinjectCurrentKeyDown)
            SendWindowsShortcutKeyDown(transition.WindowsKey, keyboardData);
        else if (transition.InjectWindowsKeyDown)
            SendWindowsKey(transition.WindowsKey, keyUp: false);
        if (transition.InjectWindowsKeyUp)
            SendWindowsKey(transition.WindowsKey, keyUp: true);
        if (transition.OpenLauncher)
            _windowsKeyPressed();

        return transition.SuppressCurrentEvent
            ? (IntPtr)1
            : CallNextHookEx(_hookHandle, code, message, data);
    }

    internal static bool RunStateMachineSelfTest()
    {
        var expectedInputSize = IntPtr.Size == 8 ? 40 : 28;
        if (Marshal.SizeOf<Input>() != expectedInputSize)
            return false;

        var state = new WindowsKeyStateMachine();
        var down = state.Process(VkLwin, true, false, true);
        var up = state.Process(VkLwin, false, true, true);
        var letterAfter = state.Process(0x49, true, false, false);
        if (!down.SuppressCurrentEvent || !up.SuppressCurrentEvent || !up.OpenLauncher ||
            letterAfter.SuppressCurrentEvent || letterAfter.InjectWindowsKeyDown)
            return false;

        return VerifyForwardedShortcut(0x52) && VerifyForwardedShortcut(0x49);
    }

    private static bool VerifyForwardedShortcut(int shortcutKey)
    {
        var state = new WindowsKeyStateMachine();
        var chordWinDown = state.Process(VkLwin, true, false, true);
        var chordLetterDown = state.Process(shortcutKey, true, false, false);
        var chordWinUp = state.Process(VkLwin, false, true, true);
        return chordWinDown.SuppressCurrentEvent && chordLetterDown.InjectWindowsKeyDown &&
               chordLetterDown.ReinjectCurrentKeyDown && chordLetterDown.SuppressCurrentEvent &&
               chordWinUp.SuppressCurrentEvent &&
               chordWinUp.InjectWindowsKeyUp && !chordWinUp.OpenLauncher;
    }

    private readonly record struct WindowsKeyTransition(
        bool SuppressCurrentEvent,
        bool InjectWindowsKeyDown,
        bool ReinjectCurrentKeyDown,
        bool InjectWindowsKeyUp,
        bool OpenLauncher,
        int WindowsKey);

    private sealed class WindowsKeyStateMachine
    {
        private bool _windowsKeyDown;
        private bool _shortcutForwarded;
        private int _activeWindowsKey = VkLwin;

        public WindowsKeyTransition Process(int virtualKey, bool isKeyDown, bool isKeyUp, bool isWindowsKey)
        {
            if (isWindowsKey && isKeyDown)
            {
                if (!_windowsKeyDown)
                {
                    _windowsKeyDown = true;
                    _shortcutForwarded = false;
                    _activeWindowsKey = virtualKey;
                }
                return new(true, false, false, false, false, _activeWindowsKey);
            }

            if (_windowsKeyDown && isKeyDown && !isWindowsKey)
            {
                var injectDown = !_shortcutForwarded;
                _shortcutForwarded = true;
                return new(injectDown, injectDown, injectDown, false, false, _activeWindowsKey);
            }

            if (isWindowsKey && isKeyUp && _windowsKeyDown)
            {
                var openLauncher = !_shortcutForwarded;
                var injectUp = _shortcutForwarded;
                var windowsKey = _activeWindowsKey;
                _windowsKeyDown = false;
                _shortcutForwarded = false;
                return new(true, false, false, injectUp, openLauncher, windowsKey);
            }

            return new(false, false, false, false, false, _activeWindowsKey);
        }
    }

    public void Dispose()
    {
        if (_hookHandle == IntPtr.Zero)
            return;
        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    private static void SendWindowsKey(int virtualKey, bool keyUp)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = (ushort)virtualKey,
                    Flags = KeyEventExtendedKey | (keyUp ? KeyEventKeyUp : 0),
                    ExtraInfo = new UIntPtr((ulong)SyntheticInputMarker)
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static void SendWindowsShortcutKeyDown(int windowsKey,
        LowLevelKeyboardInput shortcutKey)
    {
        var shortcutFlags = (shortcutKey.Flags & LlkhfExtended) != 0
            ? KeyEventExtendedKey
            : 0;
        var inputs = new[]
        {
            CreateKeyboardInput(windowsKey, KeyEventExtendedKey),
            CreateKeyboardInput(shortcutKey.VirtualKey, shortcutFlags)
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static Input CreateKeyboardInput(int virtualKey, uint flags) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = (ushort)virtualKey,
                Flags = flags,
                ExtraInfo = new UIntPtr((ulong)SyntheticInputMarker)
            }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardInput
    {
        public int VirtualKey;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProc callback,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hookHandle, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
