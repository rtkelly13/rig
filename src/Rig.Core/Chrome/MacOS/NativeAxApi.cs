using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Rig.Core.Chrome.MacOS;

/// <summary>
/// <see cref="IMacUiApi"/> through Core Foundation, ApplicationServices and Core Graphics, one native call per member;
/// the logic lives in <see cref="AxReader"/> and <see cref="MacUi"/>, where tests reach it through a fake. Never prompts for permission.
/// </summary>
[SupportedOSPlatform("macos")]
[ExcludeFromCodeCoverage(Justification = "macOS accessibility and input interop; no CI job runs macOS, reading Chrome's tree switches its accessibility mode on, and input would reach the real session, so no test calls it. Checked by hand at the s15 cutover.")]
public sealed unsafe partial class NativeAxApi : IMacUiApi
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const uint Utf8Encoding = 0x08000100;
    private const uint RightButton = 1; // kCGMouseButtonRight
    private const nint SInt64Type = 4;

    private readonly nuint _stringType = CFStringGetTypeID();
    private readonly nuint _booleanType = CFBooleanGetTypeID();
    private readonly nuint _numberType = CFNumberGetTypeID();

    /// <inheritdoc/>
    public bool Trusted() => AXIsProcessTrusted();

    /// <inheritdoc/>
    public nint Application(int pid) => AXUIElementCreateApplication(pid);

    /// <inheritdoc/>
    public void SetTimeout(nint element, float seconds) => _ = AXUIElementSetMessagingTimeout(element, seconds);

    /// <inheritdoc/>
    public int CopyValue(nint element, string attribute, out nint value)
    {
        var key = Key(attribute);
        try
        {
            nint copied = 0;
            var error = AXUIElementCopyAttributeValue(element, key, &copied);
            value = copied;
            return error;
        }
        finally
        {
            CFRelease(key);
        }
    }

    /// <inheritdoc/>
    public AxValueKind KindOf(nint value)
    {
        var type = CFGetTypeID(value);
        return type == _stringType ? AxValueKind.Text
            : type == _booleanType ? AxValueKind.Flag
            : type == _numberType ? AxValueKind.Number
            : AxValueKind.Other;
    }

    /// <inheritdoc/>
    public string? StringOf(nint value)
    {
        var size = (CFStringGetLength(value) * 4) + 1;
        var buffer = new byte[size];
        fixed (byte* start = buffer)
        {
            if (!CFStringGetCString(value, start, size, Utf8Encoding))
            {
                return null;
            }
        }

        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    /// <inheritdoc/>
    public bool BooleanOf(nint value) => CFBooleanGetValue(value);

    /// <inheritdoc/>
    public long? NumberOf(nint value)
    {
        long number = 0;
        return CFNumberGetValue(value, SInt64Type, &number) ? number : null;
    }

    /// <inheritdoc/>
    public long Hash(nint value) => unchecked((long)CFHash(value));

    /// <inheritdoc/>
    public int Count(nint array) => checked((int)CFArrayGetCount(array));

    /// <inheritdoc/>
    public nint Item(nint array, int index) => CFArrayGetValueAtIndex(array, index);

    /// <inheritdoc/>
    public void Release(nint value) => CFRelease(value);

    /// <inheritdoc/>
    public void Retain(nint value) => _ = CFRetain(value);

    /// <inheritdoc/>
    public bool Equal(nint first, nint second) => CFEqual(first, second);

    /// <inheritdoc/>
    public nint SystemWide() => AXUIElementCreateSystemWide();

    /// <inheritdoc/>
    public int ElementAt(nint system, float x, float y, out nint element)
    {
        nint copied = 0;
        var error = AXUIElementCopyElementAtPosition(system, x, y, &copied);
        element = copied;
        return error;
    }

    /// <inheritdoc/>
    public int Perform(nint element, string action)
    {
        var key = Key(action);
        try
        {
            return AXUIElementPerformAction(element, key);
        }
        finally
        {
            CFRelease(key);
        }
    }

    /// <inheritdoc/>
    public int SetFrontmost(nint application)
    {
        // kCFBooleanTrue is a data symbol, not a function: read the pointer it holds.
        var library = NativeLibrary.Load(CoreFoundation);
        var booleanTrue = *(nint*)NativeLibrary.GetExport(library, "kCFBooleanTrue");
        var key = Key("AXFrontmost");
        try
        {
            return AXUIElementSetAttributeValue(application, key, booleanTrue);
        }
        finally
        {
            CFRelease(key);
        }
    }

    /// <inheritdoc/>
    public bool Geometry(nint value, AxGeometry kind, out double x, out double y)
    {
        // CGPoint and CGSize are both two doubles.
        var pair = default(CGPoint);
        var read = CFGetTypeID(value) == AXValueGetTypeID() && AXValueGetValue(value, (int)kind, &pair);
        (x, y) = (pair.X, pair.Y);
        return read;
    }

    /// <inheritdoc/>
    public bool Cursor(out double x, out double y)
    {
        (x, y) = (0, 0);
        var inputEvent = CGEventCreate(0);
        if (inputEvent == 0)
        {
            return false;
        }

        try
        {
            var location = CGEventGetLocation(inputEvent);
            (x, y) = (location.X, location.Y);
            return true;
        }
        finally
        {
            CFRelease(inputEvent);
        }
    }

    /// <inheritdoc/>
    public int Warp(double x, double y) => CGWarpMouseCursorPosition(new CGPoint(x, y));

    /// <inheritdoc/>
    public nint MouseEvent(int kind, double x, double y) => CGEventCreateMouseEvent(0, (uint)kind, new CGPoint(x, y), RightButton);

    /// <inheritdoc/>
    public nint KeyEvent(ushort code, bool down) => CGEventCreateKeyboardEvent(0, code, down);

    /// <inheritdoc/>
    public void SetFlags(nint inputEvent, ulong flags) => CGEventSetFlags(inputEvent, flags);

    /// <inheritdoc/>
    public void SetUnicode(nint inputEvent, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        fixed (char* units = text)
        {
            CGEventKeyboardSetUnicodeString(inputEvent, (nuint)text.Length, units);
        }
    }

    /// <inheritdoc/>
    public void Post(int tap, nint inputEvent) => CGEventPost((uint)tap, inputEvent);

    /// <inheritdoc/>
    public void PostToPid(int pid, nint inputEvent) => CGEventPostToPid(pid, inputEvent);

    // A new CFString naming an attribute or action, which the caller releases.
    private static nint Key(string name)
    {
        var key = CFStringCreateWithCString(0, name, Utf8Encoding);
        return key != 0 ? key : throw new AccessibilityUnavailableException($"Cannot name {name}");
    }

    [LibraryImport(CoreFoundation)]
    private static partial nint CFRetain(nint value);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFEqual(nint first, nint second);

    [LibraryImport(ApplicationServices)]
    private static partial nint AXUIElementCreateSystemWide();

    [LibraryImport(ApplicationServices)]
    private static partial int AXUIElementCopyElementAtPosition(nint application, float x, float y, nint* element);

    [LibraryImport(ApplicationServices)]
    private static partial int AXUIElementPerformAction(nint element, nint action);

    [LibraryImport(ApplicationServices)]
    private static partial int AXUIElementSetAttributeValue(nint element, nint attribute, nint value);

    [LibraryImport(ApplicationServices)]
    private static partial nuint AXValueGetTypeID();

    [LibraryImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool AXValueGetValue(nint value, int type, CGPoint* pair);

    [LibraryImport(CoreGraphics)]
    private static partial nint CGEventCreate(nint source);

    [LibraryImport(CoreGraphics)]
    private static partial CGPoint CGEventGetLocation(nint inputEvent);

    [LibraryImport(CoreGraphics)]
    private static partial int CGWarpMouseCursorPosition(CGPoint point);

    [LibraryImport(CoreGraphics)]
    private static partial nint CGEventCreateMouseEvent(nint source, uint type, CGPoint point, uint button);

    [LibraryImport(CoreGraphics)]
    private static partial nint CGEventCreateKeyboardEvent(nint source, ushort code, [MarshalAs(UnmanagedType.U1)] bool down);

    [LibraryImport(CoreGraphics)]
    private static partial void CGEventSetFlags(nint inputEvent, ulong flags);

    [LibraryImport(CoreGraphics)]
    private static partial void CGEventKeyboardSetUnicodeString(nint inputEvent, nuint length, char* units);

    [LibraryImport(CoreGraphics)]
    private static partial void CGEventPost(uint tap, nint inputEvent);

    [LibraryImport(CoreGraphics)]
    private static partial void CGEventPostToPid(int pid, nint inputEvent);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGPoint(double X, double Y);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFGetTypeID(nint value);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFHash(nint value);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFStringGetTypeID();

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string text, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetLength(nint text);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFStringGetCString(nint text, byte* buffer, nint size, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetCount(nint array);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetValueAtIndex(nint array, nint index);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFBooleanGetTypeID();

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFBooleanGetValue(nint value);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFNumberGetTypeID();

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFNumberGetValue(nint number, nint type, long* value);

    [LibraryImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool AXIsProcessTrusted();

    [LibraryImport(ApplicationServices)]
    private static partial nint AXUIElementCreateApplication(int pid);

    [LibraryImport(ApplicationServices)]
    private static partial int AXUIElementSetMessagingTimeout(nint element, float seconds);

    [LibraryImport(ApplicationServices)]
    private static partial int AXUIElementCopyAttributeValue(nint element, nint attribute, nint* value);
}
