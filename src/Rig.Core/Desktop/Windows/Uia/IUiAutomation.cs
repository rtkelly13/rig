namespace Rig.Core.Desktop.Windows.Uia;

/// <summary>The UI Automation control types rig reads (UIA_*ControlTypeId).</summary>
public enum UiaControlType
{
    /// <summary>A type rig does not name.</summary>
    Other = 0,

    /// <summary>A button.</summary>
    Button = 50000,

    /// <summary>An edit box.</summary>
    Edit = 50004,

    /// <summary>A menu item.</summary>
    MenuItem = 50011,

    /// <summary>A tab.</summary>
    TabItem = 50019,

    /// <summary>A document: in Chrome, web content.</summary>
    Document = 50030,

    /// <summary>A window or dialog.</summary>
    Window = 50032,
}

/// <summary>How far below an element a search reaches.</summary>
public enum UiaScope
{
    /// <summary>Its children.</summary>
    Children,

    /// <summary>Everything below it.</summary>
    Descendants,

    /// <summary>The element and everything below it.</summary>
    Subtree,
}

/// <summary>Which tree navigation walks: the control view, or the raw view, which also shows layout views.</summary>
public enum UiaView
{
    /// <summary>The control view.</summary>
    Control,

    /// <summary>The raw view.</summary>
    Raw,
}

/// <summary>What a search matches: every property given must match; none given matches everything.</summary>
/// <param name="ControlType">The control type.</param>
/// <param name="ClassName">The exact class name.</param>
/// <param name="Name">The exact accessible name.</param>
public sealed record UiaCondition(UiaControlType? ControlType = null, string? ClassName = null, string? Name = null);

/// <summary>An element's bounding rectangle in screen pixels.</summary>
/// <param name="Left">Left edge.</param>
/// <param name="Top">Top edge.</param>
/// <param name="Right">Right edge, exclusive.</param>
/// <param name="Bottom">Bottom edge, exclusive.</param>
public readonly record struct UiaRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Whether the rectangle has no area.</summary>
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    /// <summary>Whether (<paramref name="x"/>, <paramref name="y"/>) lies inside it.</summary>
    public bool Contains(int x, int y) => Left <= x && x < Right && Top <= y && y < Bottom;
}

/// <summary>
/// A UI Automation call failed, most often because the element went stale: the application rebuilt that part of its UI
/// under the read. Readers retry on it; it carries the HRESULT for the report.
/// </summary>
public sealed class UiaException : Exception
{
    /// <summary>Creates the exception.</summary>
    public UiaException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public UiaException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and the failure behind it.</summary>
    public UiaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A UI Automation session: the entry point to an application's accessibility tree.</summary>
public interface IUiAutomation
{
    /// <summary>The element for window <paramref name="handle"/>; null when the window has gone or exposes nothing.</summary>
    /// <exception cref="UiaException">The window is there but UI Automation could not read it.</exception>
    IUiaElement? ElementFromHandle(long handle);
}

/// <summary>
/// One live element of an accessibility tree. Every member reads the live UI, so any of them can throw
/// <see cref="UiaException"/> when the element has gone stale.
/// </summary>
public interface IUiaElement
{
    /// <summary>The accessible name, empty when unset.</summary>
    string Name { get; }

    /// <summary>The class name, empty when unset.</summary>
    string ClassName { get; }

    /// <summary>The control type.</summary>
    UiaControlType ControlType { get; }

    /// <summary>The runtime id, unique among live elements; empty when it has none.</summary>
    IReadOnlyList<int> RuntimeId { get; }

    /// <summary>The bounding rectangle.</summary>
    UiaRect BoundingRectangle { get; }

    /// <summary>Whether it is scrolled or clipped out of view.</summary>
    bool IsOffscreen { get; }

    /// <summary>Whether it has keyboard focus.</summary>
    bool HasKeyboardFocus { get; }

    /// <summary>Whether it is selected; throws when it is not a selectable item.</summary>
    bool IsSelected { get; }

    /// <summary>Its parent in <paramref name="view"/>, null at the root.</summary>
    IUiaElement? Parent(UiaView view = UiaView.Control);

    /// <summary>Its first child in <paramref name="view"/>.</summary>
    IUiaElement? FirstChild(UiaView view = UiaView.Control);

    /// <summary>Its next sibling in <paramref name="view"/>.</summary>
    IUiaElement? NextSibling(UiaView view = UiaView.Control);

    /// <summary>Whether <paramref name="other"/> is the same UI element.</summary>
    bool SameAs(IUiaElement other);

    /// <summary>Every element in <paramref name="scope"/> matching <paramref name="condition"/>, in tree order.</summary>
    IReadOnlyList<IUiaElement> FindAll(UiaScope scope, UiaCondition condition);

    /// <summary>The first element in <paramref name="scope"/> matching <paramref name="condition"/>.</summary>
    IUiaElement? FindFirst(UiaScope scope, UiaCondition condition);

    /// <summary>Select it; throws when it is not a selectable item.</summary>
    void SelectItem();

    /// <summary>Invoke it (a click without the mouse); throws when it cannot be invoked.</summary>
    void Invoke();

    /// <summary>Expand it, as a submenu entry; false when it does not expand.</summary>
    bool Expand();

    /// <summary>Scroll it into view; false when it cannot be scrolled to.</summary>
    bool ScrollIntoView();

    /// <summary>Open its context menu, without a right-click.</summary>
    void ShowContextMenu();
}
