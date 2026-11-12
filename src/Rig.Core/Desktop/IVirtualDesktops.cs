namespace Rig.Core.Desktop;

/// <summary>
/// The machine's virtual desktops: read, switch, create, name, remove, and move windows between them. Ported from
/// <c>desktop_manager.win32.desktops</c>, which reached them through pyvda.
/// </summary>
/// <remarks>
/// Desktops are numbered from 1 in position order, and a number names whichever desktop holds that position when the
/// call is made, so removing one renumbers those after it. Failures throw <see cref="DesktopException"/>, apart from
/// the <see cref="IWindowDesktops"/> reads, which answer for an unknown window as window inventory expects.
/// <see cref="VirtualDesktopOperations"/> holds the rules rig layers over these calls.
/// </remarks>
public interface IVirtualDesktops : IWindowDesktops
{
    /// <summary>The name of each desktop in position order, <c>""</c> where a desktop has none.</summary>
    IReadOnlyList<string> Names();

    /// <summary>How many desktops there are.</summary>
    int Count { get; }

    /// <summary>The desktop showing now.</summary>
    int CurrentNumber { get; }

    /// <summary>Show desktop <paramref name="number"/>.</summary>
    void SwitchTo(int number);

    /// <summary>Add a desktop after the last one.</summary>
    void Create();

    /// <summary>Name desktop <paramref name="number"/> <paramref name="name"/>.</summary>
    void Rename(int number, string name);

    /// <summary>Remove desktop <paramref name="number"/>, moving its windows, and the view when it shows, to <paramref name="fallbackNumber"/>.</summary>
    void Remove(int number, int fallbackNumber);

    /// <summary>Move window <paramref name="handle"/> to desktop <paramref name="number"/>.</summary>
    void MoveWindow(long handle, int number);
}
