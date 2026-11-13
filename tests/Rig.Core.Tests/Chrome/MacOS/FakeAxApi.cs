using Rig.Core.Chrome.MacOS;

namespace Rig.Core.Tests.Chrome.MacOS;

/// <summary>
/// The native accessibility and input calls over an in-memory element tree. Copies and creates hand out fresh handles
/// holding one reference; <see cref="Retain"/> adds one, and <see cref="Release"/> takes one away. Releasing a handle
/// that holds no reference fails the test, and <see cref="Owned"/> lists every handle still held.
/// </summary>
internal sealed class FakeAxApi : IMacUiApi
{
    private readonly Dictionary<nint, object> _handles = [];
    private readonly Dictionary<Element, nint> _elements = [];
    private readonly Dictionary<string, int> _counts = [];
    private nint _next = 1;
    private int _events;

    public FakeAxApi() => App = [];

    /// <summary>The application element's attributes.</summary>
    public Dictionary<string, object> App { get; }

    public bool IsTrusted { get; set; } = true;

    public bool NoApplication { get; set; }

    public float? Timeout { get; private set; }

    public int ApplicationsReleased { get; private set; }

    /// <summary>Called after each read with the attribute and how often it has now been read.</summary>
    public Action<string, int>? OnRead { get; set; }

    /// <summary>Every attribute read, in order; reads of a numbered element carry <c>@identity</c>.</summary>
    public List<string> Reads { get; } = [];

    /// <summary>Handles still holding a reference, and how many.</summary>
    public Dictionary<nint, int> Owned { get; } = [];

    /// <summary>The system-wide element; null makes it unavailable.</summary>
    public Element? System { get; set; } = new(-1, []);

    /// <summary>What a hit test finds, or null for nothing.</summary>
    public Element? Hit { get; set; }

    /// <summary>The hit test's AXError.</summary>
    public int HitError { get; set; }

    /// <summary>The point of the last hit test.</summary>
    public (float X, float Y)? HitAt { get; private set; }

    /// <summary>The AXError each action answers with; 0 when absent.</summary>
    public Dictionary<string, int> ActionErrors { get; } = [];

    /// <summary>Every action performed, as <c>action@identity</c>.</summary>
    public List<string> Actions { get; } = [];

    /// <summary>Each element's messaging timeout, by identity.</summary>
    public Dictionary<long, float> Timeouts { get; } = [];

    public int FrontmostError { get; set; }

    /// <summary>Every application brought to the front, by identity.</summary>
    public List<long> Fronted { get; } = [];

    /// <summary>The cursor; null when it cannot be read.</summary>
    public (double X, double Y)? CursorAt { get; set; } = (5, 6);

    public int WarpError { get; set; }

    public List<(double X, double Y)> Warps { get; } = [];

    /// <summary>The ordinals, from 0, of event creations that fail.</summary>
    public HashSet<int> RefusedEvents { get; } = [];

    /// <summary>Called with each event as it is posted.</summary>
    public Action<InputEvent>? OnPost { get; set; }

    /// <summary>Every event posted, in order.</summary>
    public List<InputEvent> Posted { get; } = [];

    public bool Trusted() => IsTrusted;

    public nint Application(int pid) => NoApplication ? 0 : Own(new Element(0, App));

    public void SetTimeout(nint element, float seconds)
    {
        var target = (Element)_handles[element];
        Timeouts[target.Hash] = seconds;
        if (target.Hash == 0)
        {
            Timeout = seconds;
        }
    }

    public int CopyValue(nint element, string attribute, out nint value)
    {
        var owner = (Element)_handles[element];
        Reads.Add(owner.Hash == 0 ? attribute : $"{attribute}@{owner.Hash}");
        _counts[attribute] = _counts.GetValueOrDefault(attribute) + 1;
        try
        {
            value = 0;
            if (!owner.Attributes.TryGetValue(attribute, out var held))
            {
                return -25205;
            }

            if (held is Failure failure)
            {
                return failure.Code;
            }

            value = Own(held);
            return 0;
        }
        finally
        {
            OnRead?.Invoke(attribute, _counts[attribute]);
        }
    }

    public AxValueKind KindOf(nint value) => _handles[value] switch
    {
        string or Unreadable => AxValueKind.Text,
        bool => AxValueKind.Flag,
        long or Unconvertible => AxValueKind.Number,
        _ => AxValueKind.Other,
    };

    public string? StringOf(nint value) => _handles[value] as string;

    public bool BooleanOf(nint value) => (bool)_handles[value];

    public long? NumberOf(nint value) => _handles[value] as long?;

    public long Hash(nint value) => ((Element)_handles[value]).Hash;

    public int Count(nint array) => ((Element[])_handles[array]).Length;

    public nint Item(nint array, int index)
    {
        var element = ((Element[])_handles[array])[index];
        if (!_elements.TryGetValue(element, out var handle))
        {
            handle = _next++;
            _elements[element] = handle;
            _handles[handle] = element;
        }

        return handle;
    }

    public void Release(nint value)
    {
        Assert.True(Owned.TryGetValue(value, out var count), $"handle {value} released without being owned");
        if (count == 1)
        {
            _ = Owned.Remove(value);
        }
        else
        {
            Owned[value] = count - 1;
        }

        if (_handles[value] is Element { Hash: 0 })
        {
            ApplicationsReleased++;
        }
    }

    public void Retain(nint value)
    {
        Assert.True(_handles.ContainsKey(value), $"handle {value} retained but never handed out");
        Owned[value] = Owned.GetValueOrDefault(value) + 1;
    }

    public bool Equal(nint first, nint second) => ReferenceEquals(_handles[first], _handles[second]);

    public nint SystemWide() => System is { } system ? Own(system) : 0;

    public int ElementAt(nint system, float x, float y, out nint element)
    {
        Assert.Same(System, _handles[system]);
        HitAt = (x, y);
        element = Hit is { } hit ? Own(hit) : 0;
        return HitError;
    }

    public int Perform(nint element, string action)
    {
        Actions.Add($"{action}@{((Element)_handles[element]).Hash}");
        return ActionErrors.GetValueOrDefault(action);
    }

    public int SetFrontmost(nint application)
    {
        Fronted.Add(((Element)_handles[application]).Hash);
        return FrontmostError;
    }

    public bool Geometry(nint value, AxGeometry kind, out double x, out double y)
    {
        (x, y) = (0, 0);
        if (_handles[value] is not Pair pair || pair.Kind != kind)
        {
            return false;
        }

        (x, y) = (pair.X, pair.Y);
        return true;
    }

    public bool Cursor(out double x, out double y)
    {
        (x, y) = CursorAt ?? (0, 0);
        return CursorAt is not null;
    }

    public int Warp(double x, double y)
    {
        Warps.Add((x, y));
        return WarpError;
    }

    public nint MouseEvent(int kind, double x, double y) => Event($"mouse {kind} at {x},{y}");

    public nint KeyEvent(ushort code, bool down) => Event($"key {code} {(down ? "down" : "up")}");

    public void SetFlags(nint inputEvent, ulong flags) => ((InputEvent)_handles[inputEvent]).Flags = flags;

    public void SetUnicode(nint inputEvent, string text) => ((InputEvent)_handles[inputEvent]).Text = text;

    public void Post(int tap, nint inputEvent) => Deliver(inputEvent, $"tap {tap}");

    public void PostToPid(int pid, nint inputEvent) => Deliver(inputEvent, $"pid {pid}");

    private nint Own(object held)
    {
        var handle = _next++;
        _handles[handle] = held;
        Owned[handle] = 1;
        return handle;
    }

    private nint Event(string description) =>
        RefusedEvents.Contains(_events++) ? 0 : Own(new InputEvent(description));

    private void Deliver(nint inputEvent, string target)
    {
        Assert.True(Owned.ContainsKey(inputEvent), $"event {inputEvent} posted after its release");
        var posted = (InputEvent)_handles[inputEvent];
        posted.Target = target;
        Posted.Add(posted);
        OnPost?.Invoke(posted);
    }

    /// <summary>An element: its CFHash and its attributes. Equal only to itself, as two AXUIElements are.</summary>
    public sealed record Element(long Hash, Dictionary<string, object> Attributes)
    {
        public bool Equals(Element? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => global::System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }

    /// <summary>An attribute whose read fails with <paramref name="Code"/>.</summary>
    public sealed record Failure(int Code);

    /// <summary>An AXValue holding a point or a size.</summary>
    public sealed record Pair(AxGeometry Kind, double X, double Y);

    /// <summary>A Quartz event, with what was set on it and where it went.</summary>
    public sealed class InputEvent(string description)
    {
        public string Description { get; } = description;

        public ulong? Flags { get; set; }

        public string? Text { get; set; }

        public string? Target { get; set; }

        public override string ToString() =>
            $"{Description}{(Text is null ? string.Empty : $" '{Text}'")} flags {Flags?.ToString(global::System.Globalization.CultureInfo.InvariantCulture) ?? "unset"} to {Target}";
    }

    /// <summary>A string that does not read as UTF-8.</summary>
    public sealed class Unreadable
    {
        public static readonly Unreadable Instance = new();
    }

    /// <summary>A number that does not convert to a 64-bit integer.</summary>
    public sealed class Unconvertible
    {
        public static readonly Unconvertible Instance = new();
    }
}
