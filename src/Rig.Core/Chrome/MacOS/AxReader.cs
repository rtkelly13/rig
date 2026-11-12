namespace Rig.Core.Chrome.MacOS;

/// <summary>What a Core Foundation value holds, as far as reading Chrome's UI needs.</summary>
public enum AxValueKind
{
    /// <summary>Anything else; it reads as no value.</summary>
    Other,

    /// <summary>A CFString.</summary>
    Text,

    /// <summary>A CFBoolean.</summary>
    Flag,

    /// <summary>A CFNumber.</summary>
    Number,
}

/// <summary>
/// The macOS accessibility and Core Foundation calls <see cref="AxReader"/> makes, one per native function, on raw
/// handles. A copied value is the caller's to <see cref="Release"/>; an array item is borrowed from its array.
/// </summary>
public interface IAxApi
{
    /// <summary>AXIsProcessTrusted: this process may read accessibility. Never prompts.</summary>
    bool Trusted();

    /// <summary>AXUIElementCreateApplication: <paramref name="pid"/>'s application element, owned; 0 when there is none.</summary>
    nint Application(int pid);

    /// <summary>AXUIElementSetMessagingTimeout on <paramref name="element"/>.</summary>
    void SetTimeout(nint element, float seconds);

    /// <summary>AXUIElementCopyAttributeValue: the AXError, and the copied value when it is 0.</summary>
    int CopyValue(nint element, string attribute, out nint value);

    /// <summary>CFGetTypeID, against the string, boolean and number type ids.</summary>
    AxValueKind KindOf(nint value);

    /// <summary>A CFString's text, or null when it cannot be read as UTF-8.</summary>
    string? StringOf(nint value);

    /// <summary>CFBooleanGetValue.</summary>
    bool BooleanOf(nint value);

    /// <summary>CFNumberGetValue as a 64-bit integer, or null when it does not convert.</summary>
    long? NumberOf(nint value);

    /// <summary>CFHash.</summary>
    long Hash(nint value);

    /// <summary>CFArrayGetCount.</summary>
    int Count(nint array);

    /// <summary>CFArrayGetValueAtIndex: borrowed while <paramref name="array"/> lives.</summary>
    nint Item(nint array, int index);

    /// <summary>CFRelease.</summary>
    void Release(nint value);
}

/// <summary>
/// Reads an application's browser UI through accessibility, read-only and without prompting. Every copied value is
/// released by the method that copied it; borrowed children are read while their array lives. Page content and the
/// menu bar are never entered, and one read decodes at most <see cref="Budget"/> elements, <see cref="MaxDepth"/>
/// deep. Ported from <c>desktop_manager.chrome.drivers.macos_ax</c>.
/// </summary>
public class AxReader
{
    /// <summary>The most elements one read decodes.</summary>
    public const int Budget = 5000;

    /// <summary>The deepest a read goes below the element it starts at.</summary>
    public const int MaxDepth = 30;

    /// <summary>Subtrees never read: page content, and the menu bar's own large tree.</summary>
    public static readonly IReadOnlySet<string> SkippedRoles = new HashSet<string>(["AXWebArea", "AXMenuBar"], StringComparer.Ordinal);

    // kAXErrorAttributeUnsupported and kAXErrorNoValue: the element has no such value, which is an answer.
    private static readonly int[] NoValue = [-25205, -25212];
    private static readonly TimeSpan WindowsDeadline = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WindowsPause = TimeSpan.FromMilliseconds(100);

    private readonly TimeProvider _time;
    private readonly Action<TimeSpan> _sleep;
    private int _remaining = Budget;

    /// <summary>Creates a reader over <paramref name="api"/>.</summary>
    public AxReader(IAxApi api, TimeProvider? time = null, Action<TimeSpan>? sleep = null)
    {
        Api = api ?? throw new ArgumentNullException(nameof(api));
        _time = time ?? TimeProvider.System;
        _sleep = sleep ?? Thread.Sleep;
    }

    /// <summary>The native calls.</summary>
    protected IAxApi Api { get; }

    /// <summary>Allows another full read of <see cref="Budget"/> elements.</summary>
    public void ResetBudget() => _remaining = Budget;

    /// <summary>
    /// Chrome's windows for <paramref name="pid"/>. Throws <see cref="AccessibilityUnavailableException"/> without
    /// permission, without an application element, or when the tree is past the read limit.
    /// </summary>
    public IReadOnlyList<AxNode> Windows(int pid)
    {
        if (!Api.Trusted())
        {
            throw new AccessibilityUnavailableException("Grant Accessibility access to the terminal running chrome-tabs");
        }

        var application = Api.Application(pid);
        if (application == 0)
        {
            throw new AccessibilityUnavailableException("Chrome accessibility application is unavailable");
        }

        try
        {
            Api.SetTimeout(application, 2.0f);

            // Chromium switches native accessibility on when a client asks for the application's role; AXWindows alone
            // need not.
            _ = Text(application, "AXRole");
            var started = _time.GetTimestamp();
            while (true)
            {
                var windows = Children(application, "AXWindows", 0);
                if (windows.Count == 0)
                {
                    windows = [.. Children(application, "AXChildren", 0).Where(child => child.Role == "AXWindow")];
                }

                if (windows.Count == 0)
                {
                    // Off the current Space, or behind another app, AXWindows can be empty while the main and focused
                    // windows still resolve.
                    windows = Elements(application, "AXMainWindow", "AXFocusedWindow");
                }

                if (windows.Count > 0 || _time.GetElapsedTime(started) >= WindowsDeadline)
                {
                    return windows;
                }

                _sleep(WindowsPause);
            }
        }
        finally
        {
            Api.Release(application);
        }
    }

    /// <summary>
    /// Decodes <paramref name="element"/> and its children, <paramref name="depth"/> below where the read started.
    /// Throws <see cref="AccessibilityUnavailableException"/> past the depth or the element budget.
    /// </summary>
    protected virtual AxNode Node(nint element, int depth)
    {
        _remaining--;
        if (depth > MaxDepth || _remaining < 0)
        {
            throw new AccessibilityUnavailableException("AX chrome tree exceeded its read limit");
        }

        var role = Text(element, "AXRole");
        if (SkippedRoles.Contains(role))
        {
            return new AxNode(Role: role);
        }

        var selected = Scalar(element, "AXValue");
        var expanded = Scalar(element, "AXExpanded");
        return new AxNode(
            Identity: Api.Hash(element),
            Role: role,
            Subrole: Text(element, "AXSubrole"),
            Title: Text(element, "AXTitle"),
            Description: Text(element, "AXDescription"),
            Identifier: Text(element, "AXIdentifier"),
            Help: Text(element, "AXHelp"),
            Selected: selected.Flag,
            Expanded: expanded.Flag,
            Children: Children(element, "AXChildren", depth + 1));
    }

    /// <summary>A copied attribute value the caller releases, or 0 when the element has none.</summary>
    protected nint Copy(nint element, string attribute)
    {
        var error = Api.CopyValue(element, attribute, out var value);
        if (NoValue.Contains(error))
        {
            return 0;
        }

        return error != 0 ? throw new AccessibilityUnavailableException($"AX read failed ({error})") : value;
    }

    /// <summary>A text attribute; "" when it is absent or not text.</summary>
    protected string Text(nint element, string attribute) => Scalar(element, attribute).Text ?? string.Empty;

    // A string, or a flag from a boolean or a number, as Python's scalar read them; neither for anything else.
    private (string? Text, bool? Flag) Scalar(nint element, string attribute)
    {
        var value = Copy(element, attribute);
        if (value == 0)
        {
            return default;
        }

        try
        {
            return Api.KindOf(value) switch
            {
                AxValueKind.Text => (Api.StringOf(value), null),
                AxValueKind.Flag => (null, Api.BooleanOf(value)),
                AxValueKind.Number => (null, Api.NumberOf(value) is { } number ? number != 0 : null),
                _ => default,
            };
        }
        finally
        {
            Api.Release(value);
        }
    }

    private List<AxNode> Children(nint element, string attribute, int depth)
    {
        var value = Copy(element, attribute);
        if (value == 0)
        {
            return [];
        }

        try
        {
            var count = Api.Count(value);
            var nodes = new List<AxNode>(count);
            for (var index = 0; index < count; index++)
            {
                nodes.Add(Node(Api.Item(value, index), depth));
            }

            return nodes;
        }
        finally
        {
            Api.Release(value);
        }
    }

    // Single-element attributes, each distinct element once, keeping only windows.
    private List<AxNode> Elements(nint element, params string[] attributes)
    {
        var nodes = new List<AxNode>();
        var seen = new HashSet<long>();
        foreach (var attribute in attributes)
        {
            var value = Copy(element, attribute);
            if (value == 0)
            {
                continue;
            }

            try
            {
                if (seen.Add(Api.Hash(value)))
                {
                    var node = Node(value, 0);
                    if (node.Role == "AXWindow")
                    {
                        nodes.Add(node);
                    }
                }
            }
            finally
            {
                Api.Release(value);
            }
        }

        return nodes;
    }
}
