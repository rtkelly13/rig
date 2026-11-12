using Rig.Core.Otel;

namespace Rig.Web.Tests;

/// <summary>The Python routes run as a child the host spawns, waits for and stops; nothing else is signalled.</summary>
public sealed class PythonRoutesTests
{
    private readonly List<TimeSpan> _waits = [];

    [Fact]
    public async Task Ready_once_the_child_listens_on_the_port_it_was_given()
    {
        var child = new FakeChild();
        var probe = new Probe { ListeningAfter = 3 };
        int? spawnedOn = null;
        await using var routes = new PythonRoutes(port => { spawnedOn = port; return child; }, probe, Delay);

        var failure = await routes.StartAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Null(failure);
        Assert.Equal(new Uri($"http://127.0.0.1:{spawnedOn}"), routes.Address);
        Assert.All(probe.Asked, p => Assert.Equal(spawnedOn, p));
        Assert.Equal(3, _waits.Count);
    }

    [Fact]
    public async Task A_child_that_exits_on_start_leaves_no_address()
    {
        var child = new FakeChild { HasExited = true };
        await using var routes = new PythonRoutes(_ => child, new Probe(), Delay);

        var failure = await routes.StartAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("the Python web routes exited on start", failure);
        Assert.Null(routes.Address);
    }

    [Fact]
    public async Task A_child_that_cannot_be_started_is_reported_and_leaves_no_address()
    {
        await using var routes = new PythonRoutes(_ => throw new System.ComponentModel.Win32Exception("uv not found"), new Probe(), Delay);

        var failure = await routes.StartAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("the Python web routes did not start: uv not found", failure);
        Assert.Null(routes.Address);
    }

    [Fact]
    public async Task A_child_that_exits_after_start_takes_its_address_with_it()
    {
        var child = new FakeChild();
        await using var routes = new PythonRoutes(_ => child, new Probe(), Delay);
        _ = await routes.StartAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(routes.Address);

        child.HasExited = true;

        Assert.Null(routes.Address);
    }

    [Fact]
    public async Task A_child_that_never_listens_times_out()
    {
        await using var routes = new PythonRoutes(_ => new FakeChild(), new Probe { ListeningAfter = int.MaxValue }, Delay);

        var failure = await routes.StartAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.StartsWith("the Python web routes did not answer on port ", failure, StringComparison.Ordinal);
        Assert.EndsWith(" within 1s", failure, StringComparison.Ordinal);
        Assert.Equal(5, _waits.Count);
    }

    [Fact]
    public async Task Disposing_stops_the_running_child_with_a_grace_period()
    {
        var child = new FakeChild();
        var routes = new PythonRoutes(_ => child, new Probe(), Delay);
        _ = await routes.StartAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        await routes.DisposeAsync();

        Assert.Equal(TimeSpan.FromSeconds(5), child.StoppedWith);
        Assert.Null(routes.Address);
    }

    [Fact]
    public async Task Disposing_leaves_an_exited_child_alone()
    {
        var child = new FakeChild();
        var routes = new PythonRoutes(_ => child, new Probe(), Delay);
        _ = await routes.StartAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        child.HasExited = true;

        await routes.DisposeAsync();

        Assert.Null(child.StoppedWith);
    }

    [Fact]
    public async Task Disposing_before_start_does_nothing()
    {
        var routes = new PythonRoutes(_ => throw new InvalidOperationException("never spawned"), new Probe(), Delay);

        await routes.DisposeAsync();

        Assert.Null(routes.Address);
    }

    private Task Delay(TimeSpan wait, CancellationToken cancellationToken)
    {
        _waits.Add(wait);
        return Task.CompletedTask;
    }

    private sealed class FakeChild : IChildServer
    {
        public bool HasExited { get; set; }

        public TimeSpan? StoppedWith { get; private set; }

        public Task StopAsync(TimeSpan grace)
        {
            StoppedWith = grace;
            return Task.CompletedTask;
        }
    }

    private sealed class Probe : IPortProbe
    {
        public int ListeningAfter { get; init; }

        public List<int> Asked { get; } = [];

        public Task<bool> ListeningAsync(int port, string host = CollectorConfig.Host, CancellationToken cancellationToken = default)
        {
            Asked.Add(port);
            return Task.FromResult(Asked.Count > ListeningAfter);
        }
    }
}
