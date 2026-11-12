using CsCheck;
using Rig.Core.Desktop;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Desktop;

/// <summary>
/// The rules over the virtual desktops. Replaces the pure half of <c>tests/desktop_manager/test_win32_desktops.py</c>,
/// which drove them through a faked pyvda.
/// </summary>
public sealed class VirtualDesktopOperationsTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void A_desktop_exists_from_1_to_the_count(int number, bool exists) =>
        Assert.Equal(exists, VirtualDesktopOperations.Exists(new FakeVirtualDesktops("a", "b", "c"), number));

    [Fact]
    public void Going_to_a_desktop_makes_it_current()
    {
        var desktops = new FakeVirtualDesktops("a", "b", "c");

        Assert.True(VirtualDesktopOperations.GoTo(desktops, 3));
        Assert.Equal(3, desktops.CurrentNumber);
    }

    [Fact]
    public void Going_to_the_current_desktop_does_not_switch()
    {
        var desktops = new FakeVirtualDesktops("a", "b") { FailOn = nameof(IVirtualDesktops.SwitchTo) };

        Assert.True(VirtualDesktopOperations.GoTo(desktops, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Going_to_a_desktop_that_does_not_exist_stays_put(int number)
    {
        var desktops = new FakeVirtualDesktops("a", "b");

        Assert.False(VirtualDesktopOperations.GoTo(desktops, number));
        Assert.Equal(1, desktops.CurrentNumber);
    }

    [Fact]
    public void A_failed_switch_reports_false()
    {
        var desktops = new FakeVirtualDesktops("a", "b") { FailOn = nameof(IVirtualDesktops.SwitchTo) };

        Assert.False(VirtualDesktopOperations.GoTo(desktops, 2));
    }

    [Fact]
    public void Renaming_names_the_desktop()
    {
        var desktops = new FakeVirtualDesktops("a", "b");

        Assert.True(VirtualDesktopOperations.Rename(desktops, 2, "Work"));
        Assert.Equal(["a", "Work"], desktops.Names());
    }

    [Fact]
    public void A_desktop_already_so_named_is_left_alone()
    {
        var desktops = new FakeVirtualDesktops("a", "Work") { FailOn = nameof(IVirtualDesktops.Rename) };

        Assert.True(VirtualDesktopOperations.Rename(desktops, 2, "Work"));
    }

    [Fact]
    public void A_rename_that_did_not_take_reports_false()
    {
        var desktops = new FakeVirtualDesktops("a") { IgnoreRenames = true };

        Assert.False(VirtualDesktopOperations.Rename(desktops, 1, "Work"));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(2, null)]
    [InlineData(1, nameof(IVirtualDesktops.Rename))]
    [InlineData(1, nameof(IVirtualDesktops.Names))]
    public void Renaming_a_missing_desktop_or_failing_reports_false(int number, string? failOn)
    {
        var desktops = new FakeVirtualDesktops("a") { FailOn = failOn };

        Assert.False(VirtualDesktopOperations.Rename(desktops, number, "Work"));
    }

    [Fact]
    public void Moving_a_window_puts_it_on_the_desktop()
    {
        var desktops = new FakeVirtualDesktops("a", "b");

        Assert.True(VirtualDesktopOperations.MoveWindow(desktops, 42, 2));
        Assert.Equal(2, desktops.DesktopNumberOf(42));
        Assert.False(desktops.IsOnCurrentDesktop(42));
    }

    [Theory]
    [InlineData(3, null)]
    [InlineData(2, nameof(IVirtualDesktops.MoveWindow))]
    public void Moving_a_window_to_a_missing_desktop_or_failing_reports_false(int number, string? failOn)
    {
        var desktops = new FakeVirtualDesktops("a", "b") { FailOn = failOn };

        Assert.False(VirtualDesktopOperations.MoveWindow(desktops, 42, number));
        Assert.Null(desktops.DesktopNumberOf(42));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    public void Ensuring_a_count_adds_desktops_up_to_it(int target, int count)
    {
        var desktops = new FakeVirtualDesktops("a", "b");

        Assert.True(VirtualDesktopOperations.EnsureCount(desktops, target));
        Assert.Equal(count, desktops.Count);
    }

    [Fact]
    public void A_failed_create_reports_false()
    {
        var desktops = new FakeVirtualDesktops("a") { FailOn = nameof(IVirtualDesktops.Create) };

        Assert.False(VirtualDesktopOperations.EnsureCount(desktops, 2));
    }

    [Fact]
    public void Retiring_removes_named_desktops_last_first_onto_the_keep_count_th_left()
    {
        var desktops = new FakeVirtualDesktops("Work", "Old", "Admin", "OLD", "Lab");
        desktops.Windows[7] = desktops.Desktops[1];

        var removed = VirtualDesktopOperations.RetireSwitchedOff(desktops, 2, ["old"]);

        Assert.Equal(["OLD", "Old"], removed);
        Assert.Equal(["Work", "Admin", "Lab"], desktops.Names());
        Assert.Equal([(4, 3), (2, 3)], desktops.Removals);
        Assert.Equal(2, desktops.DesktopNumberOf(7));
    }

    [Fact]
    public void Retiring_falls_back_to_the_last_desktop_left_when_fewer_than_keep_count_remain()
    {
        var desktops = new FakeVirtualDesktops("a", "gone", "b");

        Assert.Equal(["gone"], VirtualDesktopOperations.RetireSwitchedOff(desktops, 9, ["gone"]));
        Assert.Equal([(2, 3)], desktops.Removals);
    }

    [Theory]
    [InlineData(1, new[] { "" })]
    [InlineData(0, new[] { "gone" })]
    [InlineData(1, new[] { "absent" })]
    public void Retiring_nothing_wanted_removes_nothing(int keepCount, string[] retired)
    {
        var desktops = new FakeVirtualDesktops("a", "gone");

        Assert.Empty(VirtualDesktopOperations.RetireSwitchedOff(desktops, keepCount, retired));
        Assert.Equal(2, desktops.Count);
    }

    [Fact]
    public void Retiring_never_removes_every_desktop()
    {
        var desktops = new FakeVirtualDesktops("gone", "Gone");

        Assert.Empty(VirtualDesktopOperations.RetireSwitchedOff(desktops, 1, ["gone"]));
        Assert.Equal(2, desktops.Count);
    }

    [Fact]
    public void A_failed_removal_returns_those_removed_before_it()
    {
        var desktops = new FakeVirtualDesktops("a", "gone", "gone");
        var calls = 0;
        var failing = new FailingSecondRemove(desktops, () => ++calls);

        Assert.Equal(["gone"], VirtualDesktopOperations.RetireSwitchedOff(failing, 1, ["gone"]));
        Assert.Equal(["a", "gone"], desktops.Names());
    }

    [Fact]
    public void Any_retirement_removes_what_the_python_removed_onto_the_desktop_it_chose()
    {
        string[] pool = ["a", "b", "X", "x", ""];
        var names = Gen.Int[0, pool.Length - 1].Select(index => pool[index]);
        Gen.Select(names.Array[1, 7], Gen.Int[0, 8]).Sample((all, keepCount) =>
        {
            var desktops = new FakeVirtualDesktops(all);
            var ids = desktops.Desktops.ToList();

            var removed = VirtualDesktopOperations.RetireSwitchedOff(desktops, keepCount, ["x"]);

            // desktop_manager.win32.desktops.retire_switched_off_desktops, on desktop identities.
            var doomed = keepCount < 1 ? [] : ids.Where(desktop => desktop.Name.Equals("x", StringComparison.OrdinalIgnoreCase)).ToList();
            var kept = ids.Except(doomed).ToList();
            if (doomed.Count == 0 || kept.Count == 0)
            {
                Assert.Empty(removed);
                return;
            }

            var fallback = kept[Math.Min(keepCount, kept.Count) - 1];
            Assert.Equal(doomed.AsEnumerable().Reverse().Select(desktop => desktop.Name), removed);
            Assert.Equal(doomed.AsEnumerable().Reverse().Select(desktop => (desktop.Id, fallback.Id)), desktops.Removals);
            Assert.Equal(kept, desktops.Desktops);
        });
    }

    [Fact]
    public void The_operations_reject_null_desktops()
    {
        Assert.Throws<ArgumentNullException>(() => VirtualDesktopOperations.Exists(null!, 1));
        Assert.Throws<ArgumentNullException>(() => VirtualDesktopOperations.GoTo(null!, 1));
        Assert.Throws<ArgumentNullException>(() => VirtualDesktopOperations.Rename(null!, 1, "a"));
        Assert.Throws<ArgumentNullException>(() => VirtualDesktopOperations.Rename(new FakeVirtualDesktops("a"), 1, null!));
        Assert.Throws<ArgumentNullException>(() => VirtualDesktopOperations.MoveWindow(null!, 1, 1));
        Assert.Throws<ArgumentNullException>(() => VirtualDesktopOperations.EnsureCount(null!, 1));
        Assert.Throws<ArgumentNullException>(() => VirtualDesktopOperations.RetireSwitchedOff(null!, 1, []));
        Assert.Throws<ArgumentNullException>(() => VirtualDesktopOperations.RetireSwitchedOff(new FakeVirtualDesktops("a"), 1, null!));
    }

    // Lets the first removal through and fails the second.
    private sealed class FailingSecondRemove(FakeVirtualDesktops inner, Func<int> count) : IVirtualDesktops
    {
        public int Count => inner.Count;

        public int CurrentNumber => inner.CurrentNumber;

        public IReadOnlyList<string> Names() => inner.Names();

        public void SwitchTo(int number) => inner.SwitchTo(number);

        public void Create() => inner.Create();

        public void Rename(int number, string name) => inner.Rename(number, name);

        public void Remove(int number, int fallbackNumber)
        {
            if (count() > 1)
            {
                throw new DesktopException("remove failed");
            }

            inner.Remove(number, fallbackNumber);
        }

        public void MoveWindow(long handle, int number) => inner.MoveWindow(handle, number);

        public bool IsOnCurrentDesktop(long handle) => inner.IsOnCurrentDesktop(handle);

        public int? DesktopNumberOf(long handle) => inner.DesktopNumberOf(handle);
    }
}
