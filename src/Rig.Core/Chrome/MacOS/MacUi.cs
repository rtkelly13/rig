using System.Text;

namespace Rig.Core.Chrome.MacOS;

/// <summary>A point in global display coordinates.</summary>
/// <param name="X">Horizontal.</param>
/// <param name="Y">Vertical.</param>
public sealed record ScreenPos(double X, double Y);

/// <summary>The application the user was in, and its focused window if it had one; both retained by the session.</summary>
/// <param name="App">The application element.</param>
/// <param name="Window">Its focused window; null when it would not say.</param>
public sealed record Focus(nint App, nint? Window);

/// <summary>Which geometry an AXValue holds.</summary>
public enum AxGeometry
{
    /// <summary>kAXValueIllegalType: no geometry.</summary>
    Illegal = 0,

    /// <summary>kAXValueCGPointType.</summary>
    Point = 1,

    /// <summary>kAXValueCGSizeType.</summary>
    Size = 2,
}

/// <summary>A retained accessibility element no longer exists: kAXErrorInvalidUIElement.</summary>
public sealed class InvalidUiElementException : AccessibilityUnavailableException
{
    /// <summary>Creates the exception.</summary>
    public InvalidUiElementException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public InvalidUiElementException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and the failure behind it.</summary>
    public InvalidUiElementException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The native calls foreground browser actions add to <see cref="IAxApi"/>: acting on elements, and Quartz input. Each
/// is one native function, or a create-use-release of a key string or event that the caller never sees.
/// </summary>
public interface IMacUiApi : IAxApi
{
    /// <summary>CFRetain.</summary>
    void Retain(nint value);

    /// <summary>CFEqual.</summary>
    bool Equal(nint first, nint second);

    /// <summary>AXUIElementCreateSystemWide, owned; 0 when there is none.</summary>
    nint SystemWide();

    /// <summary>AXUIElementCopyElementAtPosition: the AXError, and the copied element when it is 0.</summary>
    int ElementAt(nint system, float x, float y, out nint element);

    /// <summary>AXUIElementPerformAction with <paramref name="action"/>; the AXError.</summary>
    int Perform(nint element, string action);

    /// <summary>AXUIElementSetAttributeValue of AXFrontmost to kCFBooleanTrue; the AXError.</summary>
    int SetFrontmost(nint application);

    /// <summary>Whether <paramref name="value"/> is an AXValue holding <paramref name="kind"/>, and its two coordinates.</summary>
    bool Geometry(nint value, AxGeometry kind, out double x, out double y);

    /// <summary>The cursor's location from a fresh CGEvent; false when no event could be made.</summary>
    bool Cursor(out double x, out double y);

    /// <summary>CGWarpMouseCursorPosition; the CGError.</summary>
    int Warp(double x, double y);

    /// <summary>CGEventCreateMouseEvent for the right button, owned; 0 when it could not be made.</summary>
    nint MouseEvent(int kind, double x, double y);

    /// <summary>CGEventCreateKeyboardEvent, owned; 0 when it could not be made.</summary>
    nint KeyEvent(ushort code, bool down);

    /// <summary>CGEventSetFlags.</summary>
    void SetFlags(nint inputEvent, ulong flags);

    /// <summary>CGEventKeyboardSetUnicodeString with <paramref name="text"/>'s UTF-16 units.</summary>
    void SetUnicode(nint inputEvent, string text);

    /// <summary>CGEventPost to <paramref name="tap"/>.</summary>
    void Post(int tap, nint inputEvent);

    /// <summary>CGEventPostToPid.</summary>
    void PostToPid(int pid, nint inputEvent);
}

/// <summary>
/// Retained accessibility elements and Quartz input for foreground browser actions. An <see cref="AxNode"/>'s
/// <see cref="AxNode.Identity"/> is metadata, never an action handle: this session retains the element behind every node
/// it decodes, keyed by the node itself, and releases them all on <see cref="Dispose"/>. Scoped to one action, never to
/// construction. Ported from <c>desktop_manager.chrome.drivers.macos_ui</c>.
/// </summary>
public sealed class MacUi : AxReader, IDisposable
{
    /// <summary>The most elements one session retains.</summary>
    public const int RetainLimit = 100000;

    private const int InvalidUiElement = -25202;
    private const int CannotComplete = -25204;
    private const int EscapeKey = 53;
    private const int HidEventTap = 0;
    private const int TitleChunk = 8;
    private static readonly int[] RightMouse = [3, 4];

    private readonly IMacUiApi _ui;
    private readonly Dictionary<AxNode, nint> _handles = new(ReferenceEqualityComparer.Instance);
    private readonly List<nint> _owned = [];

    private MacUi(IMacUiApi api, TimeProvider? time, Action<TimeSpan>? sleep)
        : base(api, time, sleep) => _ui = api;

    /// <summary>A session over <paramref name="api"/>; throws <see cref="AccessibilityUnavailableException"/> without permission.</summary>
    public static MacUi Open(IMacUiApi api, TimeProvider? time = null, Action<TimeSpan>? sleep = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        return api.Trusted()
            ? new MacUi(api, time, sleep)
            : throw new AccessibilityUnavailableException("Grant Accessibility access to the terminal running chrome-tabs");
    }

    /// <summary>Releases every element this session retained, newest first.</summary>
    public void Dispose()
    {
        for (var index = _owned.Count - 1; index >= 0; index--)
        {
            Api.Release(_owned[index]);
        }

        _owned.Clear();
        _handles.Clear();
    }

    /// <summary>The whole application element tree for <paramref name="pid"/>, retained.</summary>
    public AxNode ApplicationTree(int pid)
    {
        ResetBudget();
        var application = Api.Application(pid);
        if (application == 0)
        {
            throw new AccessibilityUnavailableException("Chrome AX application is unavailable");
        }

        try
        {
            Api.SetTimeout(application, 1.0f);
            return Node(application, 0);
        }
        finally
        {
            Api.Release(application);
        }
    }

    /// <summary>Whether <paramref name="pid"/> is frontmost, asked of accessibility, which answers during a menu.</summary>
    public bool Frontmost(int pid)
    {
        var application = Api.Application(pid);
        if (application == 0)
        {
            return false;
        }

        try
        {
            return Flag(application, "AXFrontmost") == true;
        }
        finally
        {
            Api.Release(application);
        }
    }

    /// <summary>Chrome's windows, retained for actions; found even when Chrome is off the current Space.</summary>
    public IReadOnlyList<AxNode> BrowserWindows(int pid)
    {
        ResetBudget();
        return Windows(pid);
    }

    /// <summary>Presses <paramref name="node"/>, which must still be enabled.</summary>
    public void Press(AxNode node)
    {
        var pointer = Handle(node);
        if (Flag(pointer, "AXEnabled") != true)
        {
            throw new AccessibilityUnavailableException("AX control is not enabled");
        }

        Act(pointer, "AXPress");
    }

    /// <summary>
    /// Opens <paramref name="node"/>'s context menu with no mouse. A macOS context menu runs a modal loop, so the action
    /// does not return until it times out: "cannot complete" is the expected reply, not a failure. The caller confirms
    /// the menu by finding it in the tree.
    /// </summary>
    public void ShowMenu(AxNode node)
    {
        var pointer = Handle(node);
        Api.SetTimeout(pointer, 0.5f);
        var error = _ui.Perform(pointer, "AXShowMenu");
        if (error == InvalidUiElement)
        {
            throw new InvalidUiElementException($"AX action AXShowMenu failed ({error})");
        }

        if (error is not 0 and not CannotComplete)
        {
            throw new AccessibilityUnavailableException($"AX action AXShowMenu failed ({error})");
        }
    }

    /// <summary>Whether <paramref name="node"/> has keyboard focus.</summary>
    public bool Focused(AxNode node) => Flag(Handle(node), "AXFocused") == true;

    /// <summary>
    /// Types <paramref name="text"/> into the empty group editor <paramref name="node"/>, so Chrome notifies its
    /// controller: AXSetValue sets the text without that notice, so it does not rename a group. Quartz Unicode input
    /// follows the typing path, eight characters at a time, each chunk after <paramref name="guard"/> and a focus check.
    /// </summary>
    public void SetText(AxNode node, string text, Action guard)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(guard);
        var chunks = Chunks(text);
        var pointer = Handle(node);
        if (Text(pointer, "AXValue").Length != 0)
        {
            throw new AccessibilityUnavailableException("New group title field is no longer empty");
        }

        foreach (var chunk in chunks)
        {
            guard();
            if (!Focused(node))
            {
                throw new AccessibilityUnavailableException("Group title field lost focus");
            }

            foreach (var down in new[] { true, false })
            {
                var inputEvent = _ui.KeyEvent(0, down);
                if (inputEvent == 0)
                {
                    throw new AccessibilityUnavailableException("Cannot create Unicode input event");
                }

                // Post owns the event from here, releasing it whatever happens.
                Post(inputEvent, () => _ui.SetUnicode(inputEvent, chunk));
            }
        }
    }

    /// <summary>
    /// The centre of <paramref name="node"/>, proven to be where a click lands: the element at that point is this one,
    /// or within twelve parents of it.
    /// </summary>
    public ScreenPos ClickPoint(AxNode node)
    {
        var pointer = Handle(node);
        var (x, y) = Geometry(pointer, "AXPosition", AxGeometry.Point);
        var (width, height) = Geometry(pointer, "AXSize", AxGeometry.Size);
        if (!(width > 0 && height > 0) || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height))
        {
            throw new AccessibilityUnavailableException("AX control has no visible rectangle");
        }

        var point = new ScreenPos(x + (width / 2), y + (height / 2));
        var system = _ui.SystemWide();
        if (system == 0)
        {
            throw new AccessibilityUnavailableException("Cannot identify the control at the click point");
        }

        try
        {
            var error = _ui.ElementAt(system, (float)point.X, (float)point.Y, out var current);
            if (error != 0 || current == 0)
            {
                if (current != 0)
                {
                    Api.Release(current);
                }

                throw new AccessibilityUnavailableException("Cannot identify the control at the click point");
            }

            // The walk owns each copied reference in turn.
            try
            {
                for (var hop = 0; hop < 12 && current != 0; hop++)
                {
                    if (_ui.Equal(current, pointer))
                    {
                        return point;
                    }

                    var parent = Copy(current, "AXParent");
                    Api.Release(current);
                    current = parent;
                }
            }
            finally
            {
                if (current != 0)
                {
                    Api.Release(current);
                }
            }

            throw new AccessibilityUnavailableException("A click would land on a different control");
        }
        finally
        {
            Api.Release(system);
        }
    }

    /// <summary>Where the cursor is.</summary>
    public ScreenPos Cursor() =>
        _ui.Cursor(out var x, out var y) ? new ScreenPos(x, y) : throw new AccessibilityUnavailableException("Cannot read cursor position");

    /// <summary>Puts the cursor back at <paramref name="point"/>.</summary>
    public void RestoreCursor(ScreenPos point)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (_ui.Warp(point.X, point.Y) != 0)
        {
            throw new AccessibilityUnavailableException("Cannot restore cursor position");
        }
    }

    /// <summary>A right click at <paramref name="point"/>: button down, then up.</summary>
    public void RightClick(ScreenPos point)
    {
        ArgumentNullException.ThrowIfNull(point);
        foreach (var kind in RightMouse)
        {
            Post(_ui.MouseEvent(kind, point.X, point.Y));
        }
    }

    /// <summary>Presses and releases the key with virtual <paramref name="code"/>.</summary>
    public void Key(ushort code)
    {
        foreach (var down in new[] { true, false })
        {
            Post(_ui.KeyEvent(code, down));
        }
    }

    /// <summary>Escape, sent to <paramref name="pid"/> alone, so only Chrome's UI is dismissed when focus has changed.</summary>
    public void Escape(int pid)
    {
        foreach (var down in new[] { true, false })
        {
            var inputEvent = _ui.KeyEvent(EscapeKey, down);
            if (inputEvent == 0)
            {
                throw new AccessibilityUnavailableException("Cannot create Escape event");
            }

            try
            {
                _ui.SetFlags(inputEvent, 0);
                _ui.PostToPid(pid, inputEvent);
            }
            finally
            {
                Api.Release(inputEvent);
            }
        }
    }

    /// <summary>
    /// The previous application and its focused window, kept alive until <see cref="Dispose"/>. The system-wide query
    /// fails (-25204) when the focused app is slow to answer, as Electron apps can be; <paramref name="frontmostPid"/>,
    /// AppKit's frontmost process, is the fallback. A window that will not say which window has focus is left
    /// unrestored.
    /// </summary>
    public Focus CaptureFocus(Func<int?> frontmostPid)
    {
        ArgumentNullException.ThrowIfNull(frontmostPid);
        var application = FocusedApplication(frontmostPid);
        _owned.Add(application);
        nint window;
        try
        {
            window = Copy(application, "AXFocusedWindow");
        }
        catch (AccessibilityUnavailableException)
        {
            window = 0;
        }

        if (window == 0)
        {
            return new Focus(application, null);
        }

        _owned.Add(window);
        return new Focus(application, window);
    }

    /// <summary>Raises the captured window, if it still exists, and always brings its application back to the front.</summary>
    public void RestoreFocus(Focus focus)
    {
        ArgumentNullException.ThrowIfNull(focus);
        try
        {
            if (focus.Window is { } window)
            {
                try
                {
                    Act(window, "AXRaise");
                }
                catch (InvalidUiElementException)
                {
                    // Moving its last tab may have destroyed the saved window.
                }
            }
        }
        finally
        {
            RestoreApplication(focus.App);
        }
    }

    /// <inheritdoc/>
    protected override AxNode Node(nint element, int depth)
    {
        if (_owned.Count >= RetainLimit)
        {
            throw new AccessibilityUnavailableException("AX action exceeded its retained reference limit");
        }

        var node = base.Node(element, depth);
        _ui.Retain(element);
        _owned.Add(element);
        _handles[node] = element;
        return node;
    }

    // Eight characters a chunk, counted as Python counts them (code points), so no chunk splits a surrogate pair.
    private static List<string> Chunks(string text)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        var count = 0;
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out _, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                throw new AccessibilityUnavailableException("Group title is not valid text");
            }

            _ = current.Append(rest[..consumed]);
            rest = rest[consumed..];
            if (++count == TitleChunk)
            {
                chunks.Add(current.ToString());
                _ = current.Clear();
                count = 0;
            }
        }

        if (count > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

    // The element behind a node this session decoded, provided it still has the node's role and name.
    private nint Handle(AxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_handles.TryGetValue(node, out var pointer))
        {
            throw new AccessibilityUnavailableException("AX element is not owned by this action session");
        }

        if (Text(pointer, "AXRole") != node.Role)
        {
            throw new AccessibilityUnavailableException("AX element changed role");
        }

        var title = Text(pointer, "AXTitle");
        var name = title.Length > 0 ? title : Text(pointer, "AXDescription");
        return name != node.Name ? throw new AccessibilityUnavailableException("AX element changed name") : pointer;
    }

    private void Act(nint pointer, string action)
    {
        var error = _ui.Perform(pointer, action);
        if (error == InvalidUiElement)
        {
            throw new InvalidUiElementException($"AX action {action} failed ({error})");
        }

        if (error != 0)
        {
            throw new AccessibilityUnavailableException($"AX action {action} failed ({error})");
        }
    }

    private (double X, double Y) Geometry(nint pointer, string attribute, AxGeometry kind)
    {
        var value = Copy(pointer, attribute);
        if (value == 0)
        {
            throw new AccessibilityUnavailableException($"Missing {attribute}");
        }

        try
        {
            return _ui.Geometry(value, kind, out var x, out var y) ? (x, y) : throw new AccessibilityUnavailableException($"Invalid {attribute}");
        }
        finally
        {
            Api.Release(value);
        }
    }

    // Posts an owned event to the HID tap with no modifiers, after an optional preparation; always releases it.
    private void Post(nint inputEvent, Action? prepare = null)
    {
        if (inputEvent == 0)
        {
            throw new AccessibilityUnavailableException("Cannot create native input event");
        }

        try
        {
            prepare?.Invoke();
            _ui.SetFlags(inputEvent, 0);
            _ui.Post(HidEventTap, inputEvent);
        }
        finally
        {
            Api.Release(inputEvent);
        }
    }

    private void RestoreApplication(nint application)
    {
        if (_ui.SetFrontmost(application) != 0)
        {
            throw new AccessibilityUnavailableException("Cannot restore foreground application");
        }
    }

    private nint FocusedApplication(Func<int?> frontmostPid)
    {
        var system = _ui.SystemWide();
        nint application = 0;
        if (system != 0)
        {
            try
            {
                application = Copy(system, "AXFocusedApplication");
            }
            catch (AccessibilityUnavailableException)
            {
                application = 0;
            }
            finally
            {
                Api.Release(system);
            }
        }

        if (application != 0)
        {
            return application;
        }

        application = frontmostPid() is { } pid ? Api.Application(pid) : 0;
        return application != 0 ? application : throw new AccessibilityUnavailableException("Cannot capture foreground application");
    }
}
