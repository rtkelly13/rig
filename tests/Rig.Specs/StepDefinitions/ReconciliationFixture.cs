using Rig.Core.Reconciliation;

namespace Rig.Specs.StepDefinitions;

/// <summary>
/// The desired and observed states a scenario describes, and the sink the reconciliation pipeline writes through. In
/// memory the sink mutates a dictionary; for a <c>@real</c> scenario it writes and deletes real files, so a dry run can
/// be checked for leaving the filesystem byte-identical.
/// </summary>
public sealed class ReconciliationFixture : IDisposable
{
    private readonly SpecScratch _scratch = new();
    private readonly ResourceReconciler _reconciler;
    private readonly Dictionary<string, string> _desired = new(StringComparer.Ordinal);
    private Dictionary<string, string> _observed = new(StringComparer.Ordinal);
    private Dictionary<string, string> _before = new(StringComparer.Ordinal);
    private readonly bool _real;

    /// <summary>Creates a fixture that writes real files when <paramref name="real"/> is set.</summary>
    public ReconciliationFixture(bool real)
    {
        _real = real;
        _reconciler = new ResourceReconciler(Apply);
    }

    /// <summary>The plan the last evaluation produced.</summary>
    public IPlan<ResourceChange> Plan { get; private set; } = new ResourcePlan([]);

    /// <summary>How many changes reached the sink.</summary>
    public int Mutations { get; private set; }

    /// <summary>What the last pipeline run did, or would have done.</summary>
    public ReconciliationReport? Report { get; private set; }

    /// <summary>The desired state, keyed by relative path.</summary>
    public void SetDesired(IEnumerable<KeyValuePair<string, string>> rows)
    {
        _desired.Clear();
        foreach (var (path, content) in rows)
        {
            _desired[path] = content;
        }
    }

    /// <summary>The observed state, written to disk when this is a real scenario.</summary>
    public void SetObserved(IEnumerable<KeyValuePair<string, string>> rows)
    {
        var observed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, content) in rows)
        {
            observed[path] = content;
            if (_real)
            {
                _ = _scratch.Write(path, content);
            }
        }

        _observed = observed;
    }

    /// <summary>Builds the plan and remembers the observed state a dry run must not change.</summary>
    public async Task EvaluateAsync()
    {
        _before = new Dictionary<string, string>(_observed, StringComparer.Ordinal);
        Plan = await _reconciler.PlanAsync(new ResourceTarget(_desired, _observed), CancellationToken.None);
    }

    /// <summary>Runs the plan: report only for a dry run, otherwise mutate through the sink.</summary>
    public void Run(bool dryRun) => Report = ReconciliationRunner.Run(Plan, dryRun, Apply);

    /// <summary>Asserts the dry run left the observed state exactly as it was.</summary>
    public void AssertObservedUnchanged()
    {
        var after = _real ? ReadDisk() : _observed;
        Assert.Equal(Sorted(_before), Sorted(after));
    }

    /// <summary>Asserts the observed state now matches the desired state.</summary>
    public void AssertConverged()
    {
        var after = _real ? ReadDisk() : _observed;
        Assert.Equal(Sorted(_desired), Sorted(after));
    }

    /// <inheritdoc />
    public void Dispose() => _scratch.Dispose();

    private void Apply(IChange change)
    {
        Mutations++;
        if (!_desired.TryGetValue(change.Subject, out var content))
        {
            _ = _observed.Remove(change.Subject);
            if (_real)
            {
                File.Delete(Path.Join(_scratch.Path, change.Subject));
            }

            return;
        }

        _observed[change.Subject] = content;
        if (_real)
        {
            _ = _scratch.Write(change.Subject, content);
        }
    }

    private Dictionary<string, string> ReadDisk()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(_scratch.Path))
        {
            return found;
        }

        foreach (var file in Directory.EnumerateFiles(_scratch.Path, "*", SearchOption.AllDirectories))
        {
            found[Path.GetRelativePath(_scratch.Path, file).Replace('\\', '/')] = File.ReadAllText(file);
        }

        return found;
    }

    private static List<KeyValuePair<string, string>> Sorted(Dictionary<string, string> state) =>
        [.. state.OrderBy(entry => entry.Key, StringComparer.Ordinal)];
}
