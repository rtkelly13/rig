using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace Rig.Core.Desktop.Windows;

/// <summary>
/// <see cref="IVirtualDesktops"/> through the shell's undocumented virtual-desktop COM interfaces, the ones pyvda
/// calls, with source-generated COM rather than pyvda's dynamic dispatch.
/// </summary>
/// <remarks>
/// <see cref="VirtualDesktopBuilds"/> picks the interfaces from the Windows build. Every failing call surfaces as a
/// <see cref="DesktopException"/> carrying the HRESULT; the <see cref="IWindowDesktops"/> reads answer as the Python
/// did instead, so a window the shell has no view for counts as on the current desktop and on no numbered one.
/// </remarks>
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "COM interop with the Windows shell; exercised by the Windows CI job and the live tests, not measurable on the ubuntu coverage run.")]
public sealed class ComVirtualDesktops : IVirtualDesktops
{
    private static readonly Guid ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid VirtualDesktopManagerInternalService = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    private static readonly Guid VirtualDesktopPinnedAppsService = new("B5A399E7-1C87-46B8-88E9-FC5747B171BD");
    private static readonly StrategyBasedComWrappers Wrappers = new();

    private readonly IVirtualDesktopManagerInternal26100 _manager;
    private readonly IApplicationViewCollection _views;
    private readonly IVirtualDesktopPinnedApps _pinned;

    private ComVirtualDesktops(VirtualDesktopBuild build)
    {
        Build = build;
        var shell = CreateShell();
        _manager = Query<IVirtualDesktopManagerInternal26100>(shell, VirtualDesktopManagerInternalService, build.ManagerInternal, build);
        _views = Query<IApplicationViewCollection>(shell, typeof(IApplicationViewCollection).GUID, typeof(IApplicationViewCollection).GUID, build);
        _pinned = Query<IVirtualDesktopPinnedApps>(shell, VirtualDesktopPinnedAppsService, typeof(IVirtualDesktopPinnedApps).GUID, build);
    }

    /// <summary>The interfaces this machine's build uses.</summary>
    public VirtualDesktopBuild Build { get; }

    /// <summary>Connect to the shell's virtual desktops on this machine; a <see cref="DesktopException"/> when they cannot be reached.</summary>
    public static ComVirtualDesktops Open() => Open(Environment.OSVersion.Version.Build);

    /// <summary>Connect to the shell's virtual desktops as Windows build <paramref name="build"/> exposes them.</summary>
    public static ComVirtualDesktops Open(int build)
    {
        var selected = VirtualDesktopBuilds.For(build);
        if (typeof(IVirtualDesktopManagerInternal26100).GUID != selected.ManagerInternal ||
            typeof(IVirtualDesktop26100).GUID != selected.VirtualDesktop)
        {
            throw new DesktopException($"no COM layout is compiled for the {selected.Release} virtual desktop interfaces");
        }

        return new ComVirtualDesktops(selected);
    }

    /// <inheritdoc/>
    public int Count => Call(() => checked((int)_manager.GetCount()));

    /// <inheritdoc/>
    public int CurrentNumber => Call(() => NumberOf(_manager.GetCurrentDesktop().GetId()));

    /// <inheritdoc/>
    public IReadOnlyList<string> Names() => Call(() => Desktops().Select(desktop => ReadHString(desktop.GetName())).ToList());

    /// <inheritdoc/>
    public void SwitchTo(int number) => Call(() => _manager.SwitchDesktop(Desktop(number)));

    /// <inheritdoc/>
    public void Create() => Call(() => _manager.CreateDesktopW());

    /// <inheritdoc/>
    public void Rename(int number, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Call(() =>
        {
            var desktop = Desktop(number);
            var text = ShellNativeMethods.CreateHString(name);
            try
            {
                _manager.SetName(desktop, text);
            }
            finally
            {
                _ = ShellNativeMethods.WindowsDeleteString(text);
            }
        });
    }

    /// <inheritdoc/>
    public void Remove(int number, int fallbackNumber)
    {
        if (number == fallbackNumber)
        {
            throw new DesktopException($"desktop {number} cannot be its own fallback");
        }

        Call(() =>
        {
            var desktops = Desktops();
            _manager.RemoveDesktop(At(desktops, number), At(desktops, fallbackNumber));
        });
    }

    /// <inheritdoc/>
    public void MoveWindow(long handle, int number) =>
        Call(() => _manager.MoveViewToDesktop(_views.GetViewForHwnd((nint)handle), Desktop(number)));

    /// <inheritdoc/>
    public bool IsOnCurrentDesktop(long handle)
    {
        try
        {
            var view = _views.GetViewForHwnd((nint)handle);
            return view.GetVirtualDesktopId() == _manager.GetCurrentDesktop().GetId() || _pinned.IsViewPinned(view) != 0;
        }
        catch (Exception error) when (IsComFailure(error))
        {
            return true;
        }
    }

    /// <inheritdoc/>
    public int? DesktopNumberOf(long handle)
    {
        try
        {
            var number = NumberOf(_views.GetViewForHwnd((nint)handle).GetVirtualDesktopId());
            return number > 0 ? number : null;
        }
        catch (Exception error) when (IsComFailure(error))
        {
            return null;
        }
    }

    private List<IVirtualDesktop26100> Desktops()
    {
        var array = _manager.GetDesktops();
        var count = array.GetCount();
        var iid = typeof(IVirtualDesktop26100).GUID;
        List<IVirtualDesktop26100> desktops = new((int)count);
        for (uint index = 0; index < count; index++)
        {
            Marshal.ThrowExceptionForHR(array.GetAt(index, in iid, out var pointer));
            desktops.Add(Wrap<IVirtualDesktop26100>(pointer));
        }

        return desktops;
    }

    private IVirtualDesktop26100 Desktop(int number) => At(Desktops(), number);

    private static IVirtualDesktop26100 At(List<IVirtualDesktop26100> desktops, int number) =>
        number >= 1 && number <= desktops.Count
            ? desktops[number - 1]
            : throw new DesktopException($"there is no desktop {number}; there are {desktops.Count}");

    // The 1-based position of the desktop with this id, or 0 when none has it.
    private int NumberOf(Guid id) => Desktops().FindIndex(desktop => desktop.GetId() == id) + 1;

    private static string ReadHString(nint text)
    {
        try
        {
            return ShellNativeMethods.ReadHString(text);
        }
        finally
        {
            _ = ShellNativeMethods.WindowsDeleteString(text);
        }
    }

    private static IComServiceProvider CreateShell()
    {
        var shell = ImmersiveShell;
        var iid = typeof(IComServiceProvider).GUID;
        var result = ShellNativeMethods.CoCreateInstance(in shell, 0, ShellNativeMethods.LocalServer, in iid, out var pointer);
        return result >= 0
            ? Wrap<IComServiceProvider>(pointer)
            : throw new DesktopException($"the Windows shell is not reachable (HRESULT 0x{result:X8}); is explorer running in this session?");
    }

    private static T Query<T>(IComServiceProvider shell, Guid service, Guid iid, VirtualDesktopBuild build)
    {
        var result = shell.QueryService(in service, in iid, out var pointer);
        return result >= 0
            ? Wrap<T>(pointer)
            : throw new DesktopException(
                $"the shell refused {typeof(T).Name} {iid:B} (HRESULT 0x{result:X8}) on Windows build {Environment.OSVersion.Version.Build}; " +
                $"rig expected the {build.Release} interfaces, so this build has likely changed them");
    }

    private static T Wrap<T>(nint pointer)
    {
        try
        {
            return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            _ = Marshal.Release(pointer);
        }
    }

    private static bool IsComFailure(Exception error) => error is DesktopException || (error.HResult < 0 && error is COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException or NotImplementedException);

    private static T Call<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception error) when (error is not DesktopException && IsComFailure(error))
        {
            throw new DesktopException($"the shell's virtual desktops failed (HRESULT 0x{error.HResult:X8}): {error.Message}", error);
        }
    }

    private static void Call(Action call) => Call(() =>
    {
        call();
        return 0;
    });
}

// The shell's virtual-desktop interfaces, as pyvda 0.5.0 lays them out. Slots rig does not call are kept as
// parameterless placeholders so the ones it does land at the right vtable offsets; never call a placeholder.
#pragma warning disable CA1707, IDE1006 // Placeholder slots are numbered, not named.

[GeneratedComInterface]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
internal partial interface IComServiceProvider
{
    [PreserveSig]
    int QueryService(in Guid service, in Guid iid, out nint pointer);
}

[GeneratedComInterface]
[Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
internal partial interface IObjectArray
{
    uint GetCount();

    [PreserveSig]
    int GetAt(uint index, in Guid iid, out nint pointer);
}

[GeneratedComInterface]
[Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513")]
internal partial interface IApplicationView
{
    // IInspectable: GetIids, GetRuntimeClassName, GetTrustLevel.
    void Inspectable0();
    void Inspectable1();
    void Inspectable2();

    // SetFocus through SetLastActivationTimestamp.
    void Slot0();
    void Slot1();
    void Slot2();
    void Slot3();
    void Slot4();
    void Slot5();
    void Slot6();
    void Slot7();
    void Slot8();
    void Slot9();
    void Slot10();
    void Slot11();
    void Slot12();
    void Slot13();
    void Slot14();
    void Slot15();
    void Slot16();
    void Slot17();
    void Slot18();

    Guid GetVirtualDesktopId();
}

[GeneratedComInterface]
[Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
internal partial interface IApplicationViewCollection
{
    // GetViews, GetViewsByZOrder, GetViewsByAppUserModelId.
    void Slot0();
    void Slot1();
    void Slot2();

    IApplicationView GetViewForHwnd(nint window);
}

[GeneratedComInterface]
[Guid("4CE81583-1E4C-4632-A621-07A53543148F")]
internal partial interface IVirtualDesktopPinnedApps
{
    // IsAppIdPinned, PinAppID, UnpinAppID.
    void Slot0();
    void Slot1();
    void Slot2();

    int IsViewPinned(IApplicationView view);
}

[GeneratedComInterface]
[Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
internal partial interface IVirtualDesktop26100
{
    // IsViewVisible.
    void Slot0();

    Guid GetId();

    nint GetName();
}

[GeneratedComInterface]
[Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal partial interface IVirtualDesktopManagerInternal26100
{
    uint GetCount();

    void MoveViewToDesktop(IApplicationView view, IVirtualDesktop26100 desktop);

    // CanViewMoveDesktops.
    void Slot2();

    IVirtualDesktop26100 GetCurrentDesktop();

    IObjectArray GetDesktops();

    // GetAdjacentDesktop.
    void Slot5();

    void SwitchDesktop(IVirtualDesktop26100 desktop);

    // SwitchDesktopAndMoveForegroundView: the slot 22631 lacks.
    void Slot7();

    IVirtualDesktop26100 CreateDesktopW();

    // MoveDesktop.
    void Slot9();

    void RemoveDesktop(IVirtualDesktop26100 remove, IVirtualDesktop26100 fallback);

    // FindDesktop, GetDesktopSwitchIncludeExcludeViews.
    void Slot11();
    void Slot12();

    void SetName(IVirtualDesktop26100 desktop, nint name);
}

#pragma warning restore CA1707, IDE1006

[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "Win32 interop; exercised by the Windows CI job, not measurable on the ubuntu coverage run.")]
internal static partial class ShellNativeMethods
{
    public const uint LocalServer = 0x4;

    public static unsafe nint CreateHString(string text)
    {
        fixed (char* chars = text)
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(chars, (uint)text.Length, out var hstring));
            return hstring;
        }
    }

    public static unsafe string ReadHString(nint hstring)
    {
        var chars = WindowsGetStringRawBuffer(hstring, out var length);
        return chars == null ? "" : new string(chars, 0, checked((int)length));
    }

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint pointer);

    [LibraryImport("combase.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int WindowsCreateString(char* chars, uint length, out nint hstring);

    [LibraryImport("combase.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial char* WindowsGetStringRawBuffer(nint hstring, out uint length);
}
