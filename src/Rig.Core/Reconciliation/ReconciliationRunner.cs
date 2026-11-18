using System.Text.Json.Nodes;

using Rig.Core.Text;

namespace Rig.Core.Reconciliation;

/// <summary>
/// The standard flags every reconciling command accepts, bound once.
/// </summary>
/// <remarks>
/// A run is a dry run unless <see cref="Apply"/> is set. That default is the safety gate: a command that forgets a flag
/// reports drift instead of mutating anything.
/// </remarks>
public sealed record ReconciliationOptions
{
    /// <summary>Whether the plan is carried out. False means dry run.</summary>
    public bool Apply { get; init; }

    /// <summary>Whether the report is emitted as JSON rather than text.</summary>
    public bool Json { get; init; }

    /// <summary>True when nothing will be written.</summary>
    public bool DryRun => !Apply;

    /// <summary>The options <c>--apply</c> and <c>--json</c> select.</summary>
    /// <param name="apply">Whether to apply the plan.</param>
    /// <param name="json">Whether to emit JSON.</param>
    public static ReconciliationOptions For(bool apply = false, bool json = false) => new() { Apply = apply, Json = json };

    /// <summary>
    /// Binds the usual <c>--dry-run</c>, <c>--apply</c> and <c>--json</c> flags.
    /// </summary>
    /// <param name="dryRun">The <c>--dry-run</c> flag.</param>
    /// <param name="apply">The <c>--apply</c> flag.</param>
    /// <param name="json">The <c>--json</c> flag.</param>
    /// <exception cref="ArgumentException"><paramref name="dryRun"/> and <paramref name="apply"/> are both set.</exception>
    public static ReconciliationOptions FromFlags(bool dryRun, bool apply, bool json)
    {
        if (dryRun && apply)
        {
            throw new ArgumentException("--dry-run and --apply are opposites.", nameof(apply));
        }

        return new() { Apply = apply, Json = json };
    }
}

/// <summary>What one reconciliation run did, or would have done.</summary>
/// <typeparam name="TPlan">The plan the reconciler produced.</typeparam>
/// <typeparam name="TChange">The plan's change row type.</typeparam>
/// <param name="Plan">The plan, with rows updated when <paramref name="Applied"/> is true.</param>
/// <param name="Errors">Errors from applying the plan, in addition to any the plan itself carries.</param>
/// <param name="Applied">Whether <see cref="IReconciler{TTarget,TPlan,TChange}.ApplyAsync"/> was called.</param>
/// <param name="ExitCode">The process exit code: 2 on error, 1 on unapplied drift, 0 otherwise.</param>
public sealed record ReconciliationRun<TPlan, TChange>(TPlan Plan, IReadOnlyList<string> Errors, bool Applied, int ExitCode)
    where TPlan : IPlan<TChange>
    where TChange : IChange
{
    /// <summary>True when nothing failed.</summary>
    public bool Succeeded => ExitCode != 2;

    /// <summary>True when applying the plan reported at least one error.</summary>
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>What one reconciliation run did, or would have done, when driven through a caller's sink.</summary>
/// <param name="Changes">The mutating changes the plan held, in plan order.</param>
/// <param name="Applied">How many changes reached the sink; always zero for a dry run.</param>
/// <param name="DryRun">Whether the run was a dry run.</param>
public sealed record ReconciliationReport(IReadOnlyList<IChange> Changes, int Applied, bool DryRun)
{
    /// <summary>How many mutating changes the plan held.</summary>
    public int Drift => Changes.Count;

    /// <summary>True when nothing reached the sink.</summary>
    public bool MutatedNothing => Applied == 0;
}

/// <summary>
/// Drives a reconciler through the standard CLI lifecycle: plan, optionally apply, then render. A dry run never calls
/// <see cref="IReconciler{TTarget,TPlan,TChange}.ApplyAsync"/>, so it cannot write.
/// </summary>
public static class ReconciliationRunner
{
    /// <summary>
    /// Carries a prebuilt plan through the caller's sink, calling <paramref name="apply"/> once per would-change row
    /// unless <paramref name="dryRun"/> is set.
    /// </summary>
    /// <param name="plan">The comparison to carry out.</param>
    /// <param name="dryRun">Report only; never call <paramref name="apply"/>.</param>
    /// <param name="apply">Receives each would-change row, in plan order.</param>
    /// <typeparam name="TChange">The plan's change row type.</typeparam>
    public static ReconciliationReport Run<TChange>(IPlan<TChange> plan, bool dryRun, Action<TChange> apply)
        where TChange : IChange
    {
        var changes = plan.Changes.Where(change => change.Kind == ChangeKind.WouldChange).ToList();
        if (!dryRun)
        {
            foreach (var change in changes)
            {
                apply(change);
            }
        }

        return new ReconciliationReport([.. changes], dryRun ? 0 : changes.Count, dryRun);
    }

    /// <summary>Plans, applies when asked, renders the result, and returns everything the caller needs to exit.</summary>
    /// <param name="reconciler">The reconciler to drive.</param>
    /// <param name="target">What to reconcile.</param>
    /// <param name="options">The bound flags.</param>
    /// <param name="output">Where the rendered report goes.</param>
    /// <param name="cancellationToken">Stops planning or applying between changes.</param>
    /// <typeparam name="TTarget">What is reconciled.</typeparam>
    /// <typeparam name="TPlan">The plan type.</typeparam>
    /// <typeparam name="TChange">The change row type.</typeparam>
    public static async Task<ReconciliationRun<TPlan, TChange>> RunAsync<TTarget, TPlan, TChange>(
        IReconciler<TTarget, TPlan, TChange> reconciler,
        TTarget target,
        ReconciliationOptions options,
        TextWriter output,
        CancellationToken cancellationToken = default)
        where TPlan : IPlan<TChange>
        where TChange : IChange
    {
        var plan = await reconciler.PlanAsync(target, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> applyErrors = [];
        var applied = options.Apply;
        if (applied)
        {
            applyErrors = await reconciler.ApplyAsync(plan, cancellationToken).ConfigureAwait(false);
        }

        var combinedErrors = plan.Errors.Concat(applyErrors).Distinct(StringComparer.Ordinal).ToList();
        var exitCode = combinedErrors.Count > 0 ? 2 : plan.ExitCode(applied);
        var run = new ReconciliationRun<TPlan, TChange>(plan, combinedErrors, applied, exitCode);
        await RenderAsync(run, options, output, cancellationToken).ConfigureAwait(false);
        return run;
    }

    /// <summary>Plans, applies when asked, renders the result, and returns everything the caller needs to exit.</summary>
    /// <param name="reconciler">The reconciler to drive.</param>
    /// <param name="target">What to reconcile.</param>
    /// <param name="apply">Whether to carry the plan out; false is a dry run.</param>
    /// <param name="json">Whether to render JSON rather than text.</param>
    /// <param name="output">Where the rendered report goes.</param>
    /// <param name="cancellationToken">Stops planning or applying between changes.</param>
    /// <typeparam name="TTarget">What is reconciled.</typeparam>
    /// <typeparam name="TPlan">The plan type.</typeparam>
    /// <typeparam name="TChange">The change row type.</typeparam>
    public static Task<ReconciliationRun<TPlan, TChange>> RunAsync<TTarget, TPlan, TChange>(
        IReconciler<TTarget, TPlan, TChange> reconciler,
        TTarget target,
        bool apply,
        bool json,
        TextWriter output,
        CancellationToken cancellationToken = default)
        where TPlan : IPlan<TChange>
        where TChange : IChange =>
        RunAsync(reconciler, target, ReconciliationOptions.For(apply, json), output, cancellationToken);

    /// <summary>Renders a finished run as text or JSON. Never parses rendered text back.</summary>
    /// <param name="run">The run to render.</param>
    /// <param name="options">The bound flags, which choose text or JSON.</param>
    /// <param name="output">Where the report goes.</param>
    /// <param name="cancellationToken">Stops writing.</param>
    /// <typeparam name="TPlan">The plan type.</typeparam>
    /// <typeparam name="TChange">The change row type.</typeparam>
    public static async Task RenderAsync<TPlan, TChange>(
        ReconciliationRun<TPlan, TChange> run,
        ReconciliationOptions options,
        TextWriter output,
        CancellationToken cancellationToken = default)
        where TPlan : IPlan<TChange>
        where TChange : IChange
    {
        if (options.Json)
        {
            await output.WriteLineAsync(ToJson(run).ToJsonString(JsonOutputOptions.Indented).AsMemory(), cancellationToken).ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync(
            $"Reconciliation ({(run.Applied ? "applied" : "dry run")})".AsMemory(),
            cancellationToken).ConfigureAwait(false);
        if (run.Plan.Changes.Count == 0)
        {
            await output.WriteLineAsync("No changes.".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        foreach (var change in run.Plan.Changes)
        {
            var detail = change.Detail.Length > 0 ? $" — {change.Detail}" : string.Empty;
            await output.WriteLineAsync(
                $"{Name(change.Kind)}  {change.Subject}{detail}".AsMemory(),
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var error in run.Errors)
        {
            await output.WriteLineAsync($"error  {error}".AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The report as a JSON object, for <c>--json</c>.</summary>
    /// <param name="run">The run to serialise.</param>
    /// <typeparam name="TPlan">The plan type.</typeparam>
    /// <typeparam name="TChange">The change row type.</typeparam>
    public static JsonObject ToJson<TPlan, TChange>(ReconciliationRun<TPlan, TChange> run)
        where TPlan : IPlan<TChange>
        where TChange : IChange
    {
        return new JsonObject
        {
            ["applied"] = run.Applied,
            ["exitCode"] = run.ExitCode,
            ["changes"] = new JsonArray([.. run.Plan.Changes.Select(change => (JsonNode)new JsonObject
            {
                ["subject"] = change.Subject,
                ["kind"] = Name(change.Kind),
                ["detail"] = change.Detail,
            })]),
            ["errors"] = new JsonArray([.. run.Errors.Select(error => (JsonNode)error)]),
        };
    }

    /// <summary>How reports spell <paramref name="kind"/>: <c>would-change</c>, <c>changed</c>, and so on.</summary>
    /// <param name="kind">The change kind.</param>
    public static string Name(ChangeKind kind) => kind switch
    {
        ChangeKind.Aligned => "aligned",
        ChangeKind.WouldChange => "would-change",
        ChangeKind.Changed => "changed",
        ChangeKind.Refused => "refused",
        _ => "error",
    };

    /// <summary>The JSON text for a run, indented. Exposed for tests and callers that own the writer.</summary>
    /// <param name="run">The run to serialise.</param>
    /// <typeparam name="TPlan">The plan type.</typeparam>
    /// <typeparam name="TChange">The change row type.</typeparam>
    public static string Json<TPlan, TChange>(ReconciliationRun<TPlan, TChange> run)
        where TPlan : IPlan<TChange>
        where TChange : IChange => ToJson(run).ToJsonString(JsonOutputOptions.Indented);
}
