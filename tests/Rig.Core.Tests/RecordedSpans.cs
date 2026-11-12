using System.Collections.Concurrent;
using System.Diagnostics;

namespace Rig.Core.Tests;

/// <summary>
/// The spans one test makes. Listeners are process-wide and test classes run in parallel, so this opens a root span
/// and keeps only the stopped spans in its trace: another test's spans start no trace of ours.
/// </summary>
public sealed class RecordedSpans : IDisposable
{
    private const string TestsName = "Rig.Core.Tests.RecordedSpans";

    // Matched by name: the source is created lazily, inside a listener's ShouldListenTo, before this field is set.
    private static readonly ActivitySource Tests = new(TestsName);
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private readonly ActivityListener _listener;
    private readonly Activity _root;

    /// <summary>Starts recording the spans <paramref name="source"/> makes beneath a new root.</summary>
    public RecordedSpans(string source)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = listened => listened.Name == source || listened.Name == TestsName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = Record,
        };
        ActivitySource.AddActivityListener(_listener);
        _root = Tests.StartActivity("test") ?? throw new InvalidOperationException("the recording root was not sampled");
    }

    /// <summary>This test's stopped spans, in the order they stopped.</summary>
    public IReadOnlyList<Activity> Stopped => [.. _stopped];

    /// <summary>Ends the root and stops listening.</summary>
    public void Dispose()
    {
        _root.Dispose();
        _listener.Dispose();
    }

    private void Record(Activity activity)
    {
        if (_root is { } root && activity != root && activity.TraceId == root.TraceId)
        {
            _stopped.Enqueue(activity);
        }
    }
}
