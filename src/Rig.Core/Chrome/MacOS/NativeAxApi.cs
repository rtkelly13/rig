using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Rig.Core.Chrome.MacOS;

/// <summary>
/// <see cref="IAxApi"/> through Core Foundation and ApplicationServices, one native call per member and nothing more;
/// the reading logic lives in <see cref="AxReader"/>, where tests reach it through a fake. Never prompts for permission.
/// </summary>
[SupportedOSPlatform("macos")]
[ExcludeFromCodeCoverage(Justification = "macOS accessibility interop; no CI job runs macOS, and reading Chrome's tree switches its accessibility mode on, so no test calls it. Checked by hand at the s15 cutover.")]
public sealed unsafe partial class NativeAxApi : IAxApi
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const uint Utf8Encoding = 0x08000100;
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
        var key = CFStringCreateWithCString(0, attribute, Utf8Encoding);
        if (key == 0)
        {
            throw new AccessibilityUnavailableException($"Cannot name the {attribute} attribute");
        }

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
