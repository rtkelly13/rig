using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Rig.Core.Chrome.MacOS;

/// <summary>
/// Chrome automation on macOS failed: osascript could not run, or its reply did not fit. Callers must not continue
/// with a partial snapshot.
/// </summary>
public sealed class ChromeMacException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ChromeMacException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public ChromeMacException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and the failure behind it.</summary>
    public ChromeMacException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One tab from Chrome's scripting dictionary.</summary>
/// <param name="Id">Chrome's scripting id for the tab, stable while the tab lives.</param>
/// <param name="Title">The page title.</param>
/// <param name="Address">The page address exactly as Chrome reports it: kept as text, since a stale check compares it byte for byte and <see cref="Uri"/> would normalise it.</param>
public sealed record JxaTab(long Id, string Title, string Address);

/// <summary>A window's bounds in screen points.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public sealed record JxaBounds(int X, int Y, int Width, int Height);

/// <summary>One window from Chrome's scripting dictionary.</summary>
/// <param name="Id">Chrome's scripting id for the window.</param>
/// <param name="Index">Front-to-back order; the lowest is the front window.</param>
/// <param name="Title">The window title.</param>
/// <param name="Active">One-based index of the active tab.</param>
/// <param name="Minimized">Minimised.</param>
/// <param name="Mode"><c>normal</c> or <c>incognito</c>.</param>
/// <param name="Tabs">Its tabs, in strip order.</param>
/// <param name="Bounds">Its bounds; null when Chrome reported none it could be read from.</param>
public sealed record JxaWindow(
    long Id,
    int Index,
    string Title,
    long Active,
    bool Minimized,
    string Mode,
    IReadOnlyList<JxaTab> Tabs,
    JxaBounds? Bounds = null)
{
    /// <summary>The active tab, or null when the index points past the tabs read.</summary>
    public JxaTab? ActiveTab => Active <= Tabs.Count ? Tabs[(int)(Active - 1)] : null;
}

/// <summary>Chrome's windows and tabs as one osascript call read them.</summary>
/// <param name="Pid">Chrome's process id; 0 when Chrome is not running.</param>
/// <param name="Frontmost">Chrome is the active application.</param>
/// <param name="Windows">Its windows, in the scripting dictionary's order.</param>
public sealed record JxaSnapshot(int Pid, bool Frontmost, IReadOnlyList<JxaWindow> Windows)
{
    /// <summary>
    /// The one window at the front of Chrome's stacking order, or null when that is unclear. Chrome can hold hidden
    /// internal windows ahead of the browser window, so the front is the lowest index, which need not be 1.
    /// </summary>
    public JxaWindow? FrontWindow()
    {
        if (Windows.Count == 0)
        {
            return null;
        }

        var first = Windows.Min(window => window.Index);
        var front = Windows.Where(window => window.Index == first).ToList();
        return front.Count == 1 ? front[0] : null;
    }
}

/// <summary>One profile in Chrome's <c>Local State</c> <c>info_cache</c>; Chrome owns the format.</summary>
/// <param name="Name">The profile's display name.</param>
/// <param name="GaiaName">The signed-in account's full name.</param>
/// <param name="GaiaGivenName">The signed-in account's given name.</param>
/// <param name="UserName">The signed-in account's address.</param>
public sealed record ProfileInfo(string? Name = null, string? GaiaName = null, string? GaiaGivenName = null, string? UserName = null)
{
    /// <summary>The display name and account names this profile's avatar may show.</summary>
    public IReadOnlySet<string> Aliases() =>
        new HashSet<string>(new[] { Name, GaiaName, GaiaGivenName, UserName }.OfType<string>().Where(alias => alias.Length > 0), StringComparer.Ordinal);
}

/// <summary>The scripting ids an action on a tab names.</summary>
/// <param name="Window">The window's scripting id.</param>
/// <param name="Tab">The tab's scripting id.</param>
public sealed record ActionIds(long Window, long Tab)
{
    /// <summary>
    /// The ids of <paramref name="tab"/>: actual positive integers JXA holds exactly, or a
    /// <see cref="ChromeMacException"/>.
    /// </summary>
    public static ActionIds Of(ChromeTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.RuntimeId.Parts.Count != 1)
        {
            throw new ChromeMacException("Invalid Chrome tab identity");
        }

        var ids = new ActionIds(tab.WindowHandle, tab.RuntimeId.Parts[0]);
        if (!JxaDecode.InRange(ids.Window) || !JxaDecode.InRange(ids.Tab))
        {
            throw new ChromeMacException("Invalid Chrome action ID");
        }

        return ids;
    }
}

/// <summary>
/// Decoding osascript's JSON into the records above, as strictly as Python's pydantic models did: a string is a JSON
/// string, a number an integer, a flag a JSON boolean, and keys the records do not name are ignored. Anything that does
/// not fit throws <see cref="ChromeMacException"/>.
/// </summary>
public static class JxaDecode
{
    /// <summary>The largest integer JXA holds exactly; an id beyond it could name another tab.</summary>
    public const long MaxJxaInteger = (1L << 53) - 1;

    /// <summary>Whether <paramref name="value"/> is a usable scripting id.</summary>
    public static bool InRange(long value) => value is > 0 and <= MaxJxaInteger;

    /// <summary>A snapshot from <see cref="JxaScripts.Snapshot"/>'s reply.</summary>
    public static JxaSnapshot Snapshot(JsonElement reply) => Decode(reply, "snapshot", static root => new JxaSnapshot(
        Int(Field(root, "pid")),
        Bool(Field(root, "frontmost")),
        [.. Array(Field(root, "windows")).Select(Window)]));

    /// <summary>The frontmost application's process from <see cref="JxaScripts.Frontmost"/>'s reply; null when there is none.</summary>
    public static int? FrontmostPid(JsonElement reply)
    {
        var pid = Decode(reply, "frontmost application", static root => Int(Field(root, "pid")));
        return pid > 0 ? pid : null;
    }

    /// <summary>The <c>status</c> an action script replied with.</summary>
    public static string Status(JsonElement reply) => Decode(reply, "action result", static root => Str(Field(root, "status")));

    private static T Decode<T>(JsonElement reply, string what, Func<JsonElement, T> read)
    {
        try
        {
            return read(Object(reply));
        }
        catch (FormatException ex)
        {
            throw new ChromeMacException($"Invalid Chrome {what}", ex);
        }
    }

    private static JxaWindow Window(JsonElement element)
    {
        var window = Object(element);
        return new JxaWindow(
            ScriptId(Field(window, "id")),
            Int(Field(window, "index")),
            Str(Field(window, "title")),
            ScriptId(Field(window, "active")),
            Bool(Field(window, "minimized")),
            Str(Field(window, "mode")),
            [.. Array(Field(window, "tabs")).Select(Tab)],
            window.TryGetProperty("bounds", out var bounds) ? Bounds(bounds) : null);
    }

    private static JxaTab Tab(JsonElement element)
    {
        var tab = Object(element);
        return new JxaTab(ScriptId(Field(tab, "id")), Str(Field(tab, "title")), Str(Field(tab, "url")));
    }

    // Bounds Chrome reports in a shape it should not read as none; the rest of the window still counts.
    private static JxaBounds? Bounds(JsonElement element)
    {
        try
        {
            var bounds = Object(element);
            return new JxaBounds(Int(Field(bounds, "x")), Int(Field(bounds, "y")), Int(Field(bounds, "width")), Int(Field(bounds, "height")));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A scripting id: a positive decimal integer JXA holds exactly, sent as text or as a number.</summary>
    private static long ScriptId(JsonElement element)
    {
        var text = element.ValueKind switch
        {
            JsonValueKind.String => Text(element),
            JsonValueKind.Number => element.GetRawText(),
            _ => throw new FormatException("not an ID"),
        };
        if (text.Length == 0 || !text.All(char.IsAsciiDigit))
        {
            throw new FormatException("not a positive ID");
        }

        var value = BigInteger.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        if (value <= 0 || value > MaxJxaInteger)
        {
            throw new FormatException("not a positive ID");
        }

        return (long)value;
    }

    internal static JsonElement Object(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object ? element : throw new FormatException("not an object");

    internal static JsonElement Field(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : throw new FormatException($"missing {name}");

    private static JsonElement.ArrayEnumerator Array(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : throw new FormatException("not an array");

    private static int Int(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) ? value : throw new FormatException("not an integer");

    private static bool Bool(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FormatException("not a boolean"),
    };

    internal static string Str(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? Text(element) : throw new FormatException("not a string");

    /// <summary>
    /// A JSON string's value, as Python's <c>json.loads</c> reads it. Page titles are the web's to choose, and JXA's
    /// <c>JSON.stringify</c> writes an unpaired surrogate as <c>\ud83d</c>, which <see cref="JsonElement.GetString"/>
    /// refuses; Python keeps it, so one odd title does not refuse the whole snapshot.
    /// </summary>
    internal static string Text(JsonElement element)
    {
        try
        {
            return element.GetString()!;
        }
        catch (InvalidOperationException)
        {
            return Unescape(element.GetRawText());
        }
    }

    // The raw text is a quoted JSON string the parser has already validated, so only the escapes need reading.
    private static string Unescape(string raw)
    {
        var text = new System.Text.StringBuilder(raw.Length);
        for (var i = 1; i < raw.Length - 1; i++)
        {
            if (raw[i] != '\\')
            {
                text.Append(raw[i]);
                continue;
            }

            var escape = raw[++i];
            if (escape == 'u')
            {
                text.Append((char)int.Parse(raw.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                i += 4;
                continue;
            }

            text.Append(escape switch
            {
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => escape,
            });
        }

        return text.ToString();
    }
}
