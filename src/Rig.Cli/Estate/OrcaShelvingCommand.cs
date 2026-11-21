#pragma warning disable RS0030 // Legacy estate group awaits DotMake migration (Issue #234)
using System.CommandLine;
using System.Text.Json;
using Rig.Cli.Workspace;
using Rig.Core.Estate;
using Rig.Core.Orca;
using Rig.Core.Processes;

namespace Rig.Cli.Estate;

/// <summary>The daily rig policy, report and CLI for shelving inactive Orca worktrees.</summary>
internal sealed class OrcaShelvingCommand(EstateHost host, OrcaHost orca, Git git, IProc proc, TimeProvider clock, TextWriter output, TextWriter error)
{
    /// <summary>Builds the explicit-scope, read-only-by-default command.</summary>
    public Command Build()
    {
        var all = new Option<bool>("--all") { Description = "Every project in the selected Orca profile." };
        var repo = new Option<string?>("--repo") { Description = "One Orca project name or checkout path." };
        var data = new Option<string?>("--orca-data") { Description = "Orca profile user-data folder." };
        var config = new Option<string?>("--config") { Description = "Rig shelving policy JSON (default: config/estate-orca-shelving.json)." };
        var days = new Option<int?>("--idle-days") { Description = "Override the policy's inactivity days (default 14)." };
        var apply = new Option<bool>("--apply") { Description = "Shelve, verify, create/label recovery PRs and clean up." };
        var write = new Option<string?>("--write") { Description = "With --apply, persist triage JSON and append-only JSONL history here." };
        var json = new Option<bool>("--json") { Description = "Emit every triage row, including branches without PRs and refusals." };
        var command = new Command("orca-shelve", "Daily rig triage and shelving of inactive Orca worktrees. Read-only unless --apply. "
            + "Requires --all or --repo. Newest file, commit, workspace and open-PR activity must exceed the configured idle period. "
            + "Pushes a TEMP-SHELVED recovery marker without moving the original branch. Existing open PRs get stale and shelved labels; "
            + "otherwise creates a TEMP draft PR labelled shelved. Verifies recovery before cleanup. Pinned/current/busy/locked work and "
            + "non-regenerable ignored files are kept. Exit 2 when Orca is unavailable, 1 on failure; refusals remain in triage.")
        { all, repo, data, config, days, apply, write, json };
        command.SetAction(async (parsed, token) =>
        {
            if (parsed.GetValue(all) == (parsed.GetValue(repo) is not null))
            {
                await error.WriteLineAsync(("Choose --all or --repo <name|path>.").AsMemory(), token).ConfigureAwait(false);
                return 2;
            }

            if (parsed.GetValue(write) is not null && !parsed.GetValue(apply))
            {
                await error.WriteLineAsync(("--write requires --apply; use --json for a read-only preview.").AsMemory(), token).ConfigureAwait(false);
                return 2;
            }

            try
            {
                var path = parsed.GetValue(config) ?? Path.Join(Path.GetDirectoryName(host.ManagedProjectsFile) ?? "config", "estate-orca-shelving.json");
                var policy = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, token).ConfigureAwait(false), OrcaShelvingJson.Default.OrcaShelvingPolicy)
                    ?? throw new InvalidDataException("Empty shelving policy");
                policy = policy with { IdleDays = parsed.GetValue(days) ?? policy.IdleDays };
                policy.Validate();
                var folder = parsed.GetValue(data) is { } chosen ? host.ExpandHome(chosen) : orca.Locations.EstateUserData();
                var runtime = OrcaRuntime.FromMetadata(orca.Locations.RuntimeMetadata(folder), orca.Exchange);
                var scope = parsed.GetValue(repo) is { } wanted ? await OrcaWorktrees.SelectAsync(runtime, wanted, host.Home, token).ConfigureAwait(false) : null;
                if (scope is { Count: not 1 })
                {
                    await error.WriteLineAsync(("--repo must identify exactly one Orca project.").AsMemory(), token).ConfigureAwait(false);
                    return 2;
                }

                var report = parsed.GetValue(write) ?? Path.Join(Path.GetDirectoryName(Decisions.DefaultFeedPath(host.LocalAppData, host.Home))!, "orca-shelving.json");
                FileStream? gate = null;
                try
                {
                    if (parsed.GetValue(apply))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                        gate = new FileStream(report + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    }

                    var current = new Dictionary<string, OrcaShelvingRow>(StringComparer.Ordinal);
                    void Record(OrcaShelvingRow row)
                    {
                        if (!parsed.GetValue(apply))
                        {
                            return;
                        }

                        current[row.WorktreeId] = row;
                        var encoded = JsonSerializer.Serialize(row, OrcaShelvingJson.Default.OrcaShelvingRow);
                        File.AppendAllText(report + ".jsonl", encoded + "\n");
                        File.WriteAllText(report + ".tmp", JsonSerializer.Serialize(current.Values.ToList(), OrcaShelvingJson.Default.ListOrcaShelvingRow) + "\n");
                        File.Move(report + ".tmp", report, overwrite: true);
                    }

                    var service = new OrcaShelving(git, proc, clock, Path.Join(host.Home, ShelveCommand.LedgerName));
                    var rows = await service.RunAsync(runtime, host.WorkingDirectory, policy, parsed.GetValue(apply), Record, scope, token).ConfigureAwait(false);
                    if (parsed.GetValue(json))
                    {
                        await output.WriteLineAsync((JsonSerializer.Serialize(rows.ToList(), OrcaShelvingJson.Default.ListOrcaShelvingRow)).AsMemory(), token).ConfigureAwait(false);
                    }
                    else
                    {
                        foreach (var row in rows.Where(r => r.Outcome != "kept"))
                        {
                            await output.WriteLineAsync(($"{row.Outcome}: {row.Repo} / {row.Branch}: {row.Reason} {row.PrUrl}").AsMemory(), token).ConfigureAwait(false);
                        }

                        await output.WriteLineAsync(($"{rows.Count} worktrees triaged; {rows.Count(r => r.Outcome == "shelved")} shelved; "
                            + $"{rows.Count(r => r.Outcome == "needs-decision")} need decisions; {rows.Count(r => r.Outcome == "failed")} failed.").AsMemory(), token).ConfigureAwait(false);
                        if (parsed.GetValue(apply))
                        {
                            await output.WriteLineAsync(($"Triage and history: {report} (+ .jsonl)").AsMemory(), token).ConfigureAwait(false);
                        }
                    }

                    return rows.Any(row => row.Outcome == "failed") ? 1 : 0;
                }
                finally
                {
                    if (gate is not null)
                    {
                        await gate.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (OrcaUnavailableException e)
            {
                await error.WriteLineAsync((e.Message).AsMemory(), token).ConfigureAwait(false);
                return 2;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
            {
                await error.WriteLineAsync(($"Orca shelving failed: {e.Message}").AsMemory(), token).ConfigureAwait(false);
                return 1;
            }
        });
        return command;
    }
}
