using Rig.Core.Chrome.MacOS;

namespace Rig.Core.Tests.Chrome.MacOS;

/// <summary>
/// The accessibility reader over a fake of the native calls: no real element is ever read, since reading Chrome's tree
/// switches its accessibility mode on. The fake counts every copy against its release.
/// </summary>
public sealed class AxReaderTests
{
    private const int Pid = 123;

    private readonly FakeAxApi _api = new();
    private readonly StepClock _clock = new();
    private readonly List<TimeSpan> _sleeps = [];

    private AxReader Reader() => new(_api, _clock, pause =>
    {
        _sleeps.Add(pause);
        _clock.Advance(pause);
    });

    private static FakeAxApi.Element Window(string title, params FakeAxApi.Element[] children) =>
        new(title.Length + 100, new() { ["AXRole"] = "AXWindow", ["AXTitle"] = title, ["AXChildren"] = children });

    [Fact]
    public void Windows_are_decoded_and_every_copy_is_released()
    {
        var tab = new FakeAxApi.Element(7, new()
        {
            ["AXRole"] = "AXRadioButton",
            ["AXSubrole"] = "AXTabButton",
            ["AXTitle"] = "Notes",
            ["AXDescription"] = "tab",
            ["AXIdentifier"] = "id",
            ["AXHelp"] = "help",
            ["AXValue"] = true,
            ["AXExpanded"] = 0L,
        });
        _api.App["AXWindows"] = new[] { Window("Chrome", tab) };

        var window = Assert.Single(Reader().Windows(Pid));

        Assert.Equal(new AxNode(Identity: 106, Role: "AXWindow", Title: "Chrome", Children:
        [
            new AxNode(7, "AXRadioButton", "AXTabButton", "Notes", "tab", "id", "help", Selected: true, Expanded: false),
        ]), window);
        Assert.Equal(2.0f, _api.Timeout);
        Assert.Equal("AXRole", _api.Reads[0]);
        Assert.Empty(_api.Owned);
        Assert.Equal(1, _api.ApplicationsReleased);
        Assert.Empty(_sleeps);
    }

    [Fact]
    public void Without_permission_nothing_is_read()
    {
        _api.IsTrusted = false;
        var error = Assert.Throws<AccessibilityUnavailableException>(() => Reader().Windows(Pid));
        Assert.Equal("Grant Accessibility access to the terminal running chrome-tabs", error.Message);
        Assert.Empty(_api.Reads);
    }

    [Fact]
    public void Without_an_application_element_nothing_is_read()
    {
        _api.NoApplication = true;
        var error = Assert.Throws<AccessibilityUnavailableException>(() => Reader().Windows(Pid));
        Assert.Equal("Chrome accessibility application is unavailable", error.Message);
        Assert.Empty(_api.Reads);
    }

    [Fact]
    public void Window_children_stand_in_for_missing_windows()
    {
        var other = new FakeAxApi.Element(9, new() { ["AXRole"] = "AXMenuBar" });
        _api.App["AXChildren"] = new[] { other, Window("Chrome") };
        Assert.Equal(["Chrome"], Reader().Windows(Pid).Select(node => node.Title));
        Assert.Empty(_api.Owned);
    }

    [Fact]
    public void The_main_and_focused_windows_stand_in_off_the_current_space()
    {
        var window = Window("Chrome");
        _api.App["AXMainWindow"] = window;
        _api.App["AXFocusedWindow"] = window;
        Assert.Equal(["Chrome"], Reader().Windows(Pid).Select(node => node.Title));
        Assert.Empty(_api.Owned);
    }

    [Fact]
    public void A_focused_element_that_is_not_a_window_is_dropped()
    {
        _api.App["AXFocusedWindow"] = new FakeAxApi.Element(5, new() { ["AXRole"] = "AXSheet" });
        Assert.Empty(Reader().Windows(Pid));
        Assert.Empty(_api.Owned);
    }

    [Fact]
    public void No_windows_are_retried_until_the_deadline()
    {
        Assert.Empty(Reader().Windows(Pid));
        Assert.Equal(10, _sleeps.Count);
        Assert.All(_sleeps, pause => Assert.Equal(TimeSpan.FromMilliseconds(100), pause));
        Assert.Equal(1, _api.ApplicationsReleased);
    }

    [Fact]
    public void Windows_that_appear_while_waiting_are_read()
    {
        _api.OnRead = (attribute, count) =>
        {
            if (attribute == "AXWindows" && count == 2)
            {
                _api.App["AXWindows"] = new[] { Window("Late") };
            }
        };
        Assert.Equal(["Late"], Reader().Windows(Pid).Select(node => node.Title));
        Assert.Equal(2, _sleeps.Count);
    }

    [Fact]
    public void Skipped_roles_are_never_entered()
    {
        var content = new FakeAxApi.Element(3, new() { ["AXRole"] = "AXWebArea", ["AXTitle"] = "page", ["AXChildren"] = new FakeAxApi.Failure(-25204) });
        _api.App["AXWindows"] = new[] { Window("Chrome", content) };
        Assert.Equal(new AxNode(Role: "AXWebArea"), Assert.Single(Reader().Windows(Pid)).Children.Single());
        Assert.DoesNotContain("AXTitle@3", _api.Reads);
        Assert.Empty(_api.Owned);
    }

    [Fact]
    public void A_tree_past_the_depth_limit_fails_and_still_releases()
    {
        var deepest = new FakeAxApi.Element(1, new() { ["AXRole"] = "AXGroup" });
        for (var level = 0; level <= AxReader.MaxDepth; level++)
        {
            deepest = new FakeAxApi.Element(level + 2, new() { ["AXRole"] = "AXGroup", ["AXChildren"] = new[] { deepest } });
        }

        _api.App["AXWindows"] = new[] { deepest };
        var error = Assert.Throws<AccessibilityUnavailableException>(() => Reader().Windows(Pid));
        Assert.Equal("AX chrome tree exceeded its read limit", error.Message);
        Assert.Empty(_api.Owned);
        Assert.Equal(1, _api.ApplicationsReleased);
    }

    [Fact]
    public void The_deepest_allowed_tree_reads()
    {
        var deepest = new FakeAxApi.Element(1, new() { ["AXRole"] = "AXGroup" });
        for (var level = 1; level <= AxReader.MaxDepth; level++)
        {
            deepest = new FakeAxApi.Element(level + 1, new() { ["AXRole"] = "AXGroup", ["AXChildren"] = new[] { deepest } });
        }

        _api.App["AXWindows"] = new[] { deepest };
        Assert.Single(Reader().Windows(Pid));
    }

    [Fact]
    public void The_element_budget_holds_across_reads_until_reset()
    {
        var leaves = Enumerable.Range(0, 3000).Select(index => new FakeAxApi.Element(index, new() { ["AXRole"] = "AXButton" })).ToArray();
        _api.App["AXWindows"] = new[] { Window("Chrome", leaves) };
        var reader = Reader();
        _ = reader.Windows(Pid);
        _ = Assert.Throws<AccessibilityUnavailableException>(() => reader.Windows(Pid));
        Assert.Empty(_api.Owned);
        reader.ResetBudget();
        Assert.Equal(3000, Assert.Single(reader.Windows(Pid)).Children.Count);
    }

    [Fact]
    public void A_failed_read_is_reported_with_its_error()
    {
        _api.App["AXWindows"] = new[] { new FakeAxApi.Element(4, new() { ["AXRole"] = "AXWindow", ["AXTitle"] = new FakeAxApi.Failure(-25204) }) };
        var error = Assert.Throws<AccessibilityUnavailableException>(() => Reader().Windows(Pid));
        Assert.Equal("AX read failed (-25204)", error.Message);
        Assert.Empty(_api.Owned);
    }

    [Theory]
    [InlineData(-25205)]
    [InlineData(-25212)]
    public void An_unsupported_or_empty_attribute_is_no_value(int code)
    {
        _api.App["AXWindows"] = new[] { new FakeAxApi.Element(4, new() { ["AXRole"] = "AXWindow", ["AXTitle"] = new FakeAxApi.Failure(code) }) };
        Assert.Equal("", Assert.Single(Reader().Windows(Pid)).Title);
    }

    [Fact]
    public void Values_of_the_wrong_kind_read_as_absent()
    {
        _api.App["AXWindows"] = new[]
        {
            new FakeAxApi.Element(4, new()
            {
                ["AXRole"] = "AXWindow",
                ["AXTitle"] = true,
                ["AXDescription"] = FakeAxApi.Unreadable.Instance,
                ["AXValue"] = "text",
                ["AXExpanded"] = FakeAxApi.Unconvertible.Instance,
                ["AXHelp"] = new object(),
                ["AXSubrole"] = 5L,
            }),
        };
        var node = Assert.Single(Reader().Windows(Pid));
        Assert.Equal(new AxNode(4, "AXWindow"), node);
        Assert.Empty(_api.Owned);
    }

    [Fact]
    public void A_number_flag_is_true_when_nonzero()
    {
        _api.App["AXWindows"] = new[] { new FakeAxApi.Element(4, new() { ["AXRole"] = "AXWindow", ["AXValue"] = 2L, ["AXExpanded"] = false }) };
        var node = Assert.Single(Reader().Windows(Pid));
        Assert.True(node.Selected);
        Assert.False(node.Expanded);
    }

    [Fact]
    public void A_reader_needs_its_native_calls() => Assert.Throws<ArgumentNullException>(() => new AxReader(null!));

    [Fact]
    public void The_unavailable_exception_keeps_its_cause()
    {
        var cause = new InvalidOperationException("stale");
        Assert.Same(cause, new AccessibilityUnavailableException("gone", cause).InnerException);
        Assert.NotEmpty(new AccessibilityUnavailableException().Message);
    }

    [Fact]
    public void The_default_clock_and_sleep_are_real()
    {
        _api.App["AXWindows"] = new[] { Window("Chrome") };
        Assert.Single(new AxReader(_api).Windows(Pid));
    }

    /// <summary>A clock that moves only when the reader sleeps.</summary>
    private sealed class StepClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}

/// <summary>
/// The native accessibility calls over an in-memory element tree. Copies hand out fresh handles the caller must release
/// once; releasing a handle twice, or one never handed out, fails the test.
/// </summary>
internal sealed class FakeAxApi : IAxApi
{
    private readonly Dictionary<nint, object> _handles = [];
    private readonly Dictionary<Element, nint> _elements = [];
    private readonly Dictionary<string, int> _counts = [];
    private nint _next = 1;

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

    /// <summary>Copied handles not yet released.</summary>
    public HashSet<nint> Owned { get; } = [];

    public bool Trusted() => IsTrusted;

    public nint Application(int pid)
    {
        if (NoApplication)
        {
            return 0;
        }

        var handle = _next++;
        _handles[handle] = new Element(0, App);
        _ = Owned.Add(handle);
        return handle;
    }

    public void SetTimeout(nint element, float seconds) => Timeout = seconds;

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

            value = _next++;
            _handles[value] = held;
            _ = Owned.Add(value);
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
        Assert.True(Owned.Remove(value), $"handle {value} released without being owned");
        if (_handles[value] is Element { Hash: 0 })
        {
            ApplicationsReleased++;
        }
    }

    /// <summary>An element: its CFHash and its attributes.</summary>
    public sealed record Element(long Hash, Dictionary<string, object> Attributes)
    {
        public bool Equals(Element? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }

    /// <summary>An attribute whose read fails with <paramref name="Code"/>.</summary>
    public sealed record Failure(int Code);

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
