using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Rig.Core.Desktop.Windows;

/// <summary>The Win32 writes, through user32 and kernel32.</summary>
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "Win32 interop; the Windows CI job smoke-tests it, the ubuntu coverage run cannot reach it.")]
public sealed unsafe partial class NativeWin32Actions : IWin32Actions
{
    private const int ShowRestore = 9;
    private const int ShowNoActivate = 8;
    private const int ShowMinimize = 6;
    private const uint MouseInput = 0;
    private const uint RightDown = 0x8;
    private const uint RightUp = 0x10;
    private const uint NoSize = 0x1;
    private const uint NoMove = 0x2;
    private const uint NoZOrder = 0x4;
    private const uint NoActivate = 0x10;
    private const uint NoOwnerZOrder = 0x200;
    private const uint CloseMessage = 0x10;
    private const uint KeyboardInput = 1;
    private const uint TerminateAccess = 0x1;
    private const uint SynchronizeAccess = 0x100000;
    private const uint WaitObject0 = 0;

    /// <inheritdoc/>
    public bool Restore(long handle) => Show(handle, ShowRestore);

    /// <inheritdoc/>
    public bool ShowWithoutActivating(long handle) => Show(handle, ShowNoActivate);

    /// <inheritdoc/>
    public bool MoveWindow(long handle, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(rect);
        return MoveWindowNative((nint)handle, rect.Left, rect.Top, rect.Width, rect.Height, true);
    }

    /// <inheritdoc/>
    public bool Reposition(long handle, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(rect);
        return SetWindowPos((nint)handle, 0, rect.Left, rect.Top, rect.Width, rect.Height, NoActivate | NoZOrder | NoOwnerZOrder);
    }

    /// <inheritdoc/>
    public bool Restack(long handle, ZOrder order)
    {
        nint after = order switch
        {
            ZOrder.Topmost => -1,
            ZOrder.NotTopmost => -2,
            _ => 0,
        };
        return SetWindowPos((nint)handle, after, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
    }

    /// <inheritdoc/>
    public bool PostClose(long handle) => PostMessageW((nint)handle, CloseMessage, 0, 0);

    /// <inheritdoc/>
    public bool SetForeground(long handle) => SetForegroundWindow((nint)handle);

    /// <inheritdoc/>
    public bool Minimize(long handle) => Show(handle, ShowMinimize);

    /// <inheritdoc/>
    public bool MoveCursor(int x, int y) => SetCursorPos(x, y);

    /// <inheritdoc/>
    public int RightClick()
    {
        var inputs = stackalloc Input[2];
        inputs[0] = new Input { Type = MouseInput, Union = new InputUnion { Mouse = new MouseInputData { Flags = RightDown } } };
        inputs[1] = new Input { Type = MouseInput, Union = new InputUnion { Mouse = new MouseInputData { Flags = RightUp } } };
        return (int)SendInput(2, inputs, sizeof(Input));
    }

    /// <inheritdoc/>
    public int SendKeys(IReadOnlyList<KeyEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var inputs = new Input[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            inputs[i] = new Input
            {
                Type = KeyboardInput,
                Keyboard = new KeyboardInputData { VirtualKey = events[i].VirtualKey, Scan = events[i].Scan, Flags = events[i].Flags },
            };
        }

        fixed (Input* batch = inputs)
        {
            return (int)SendInput((uint)inputs.Length, batch, sizeof(Input));
        }
    }

    /// <inheritdoc/>
    public bool Terminate(int pid, int exitCode)
    {
        var process = OpenProcess(TerminateAccess, false, (uint)pid);
        if (process == 0)
        {
            return false;
        }

        try
        {
            return TerminateProcess(process, (uint)exitCode);
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    /// <inheritdoc/>
    public bool? WaitForExit(int pid, TimeSpan timeout)
    {
        var process = OpenProcess(SynchronizeAccess, false, (uint)pid);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var milliseconds = (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1);
            return WaitForSingleObject(process, milliseconds) == WaitObject0;
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    // ShowWindow answers whether the window was visible before, not whether the call worked; only a dead window fails.
    private static bool Show(long handle, int command)
    {
        _ = ShowWindow((nint)handle, command);
        return IsWindow((nint)handle);
    }

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "MoveWindow")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveWindowNative(nint window, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint SendInput(uint count, Input* inputs, int size);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    // MOUSEINPUT is the largest member of the union; without it SendInput rejects the struct size.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInputData Keyboard;

        [FieldOffset(0)]
        public MouseInputData Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;

        public KeyboardInputData Keyboard
        {
            readonly get => Union.Keyboard;
            set => Union.Keyboard = value;
        }
    }
}
