using System.CommandLine;
using System.Globalization;
using Rig.Core.Processes;
using Rig.Core.Vault;

namespace Rig.Cli.Vault;

/// <summary>What <c>rig vault</c> needs from the machine.</summary>
/// <param name="Environment">Reads an environment variable, or returns null.</param>
/// <param name="Home">The user's home folder.</param>
internal sealed record VaultHost(Func<string, string?> Environment, string Home);

/// <summary>
/// <c>rig vault</c>: link health, orphans, metadata and filename maintenance for the Obsidian vault, plus the shared
/// OpenKnowledge server (<see cref="KnowledgeServerCommand"/>). Ported from <c>rig.obsidian.cli</c>. Everything reports
/// before it repairs: each <c>--apply</c> command changes nothing without the flag. Maintenance targets the git copy of
/// the vault (<see cref="VaultPath"/>).
/// </summary>
internal sealed class VaultCommand(IProc proc, VaultHost host, KnowledgeServerCommand knowledgeServer, TextWriter output, TextWriter error)
{
    private const string LintHelp = """
        Scan for duplicate URLs and other vault inconsistencies. Codes: TVS000-TVS004 (error) invalid frontmatter, missing
        type, missing or empty required field or list; TVS005 (warning) unknown note type; TVS101-TVS108 (warning) filename
        is URL-shaped, non-ASCII, has mixed or repeated separators, dots in the stem, a handle prefix, can be simplified, or
        is not portable to Windows; TVS201 (error) duplicate note group. A note silences codes with lint_ignore (or
        lint-ignore) in its frontmatter: one code, a list of codes, TVS for every TVS code, or ALL. Prefer fixing the note.
        """;

    private string Vault => VaultPath.Resolve(host.Environment, host.Home);

    /// <summary>The <c>rig vault</c> command.</summary>
    public Command Build()
    {
        var paths = new Command("paths", "Show which vault maintenance targets, and where the Drive copy is.");
        paths.SetAction(_ =>
        {
            output.WriteLine($"maintained  {Vault}");
            output.WriteLine($"drive copy  {VaultPath.ResolveDrive(host.Environment, host.Home)}");
        });

        return new Command("vault", "Obsidian vault maintenance: links, orphans, metadata, filenames.")
        {
            paths,
            Graph(),
            knowledgeServer.Build(),
        };
    }

    private Command Graph()
    {
        var links = new Command("links", "Broken-link discovery and repair commands.")
        {
            Run("list", "Identify dead wikilinks in the vault.", () =>
            {
                var report = NewGraph().ListLinks();
                if (report.Broken.Count == 0 && report.Unlinked.Count == 0)
                {
                    Say("✓ No broken links or unlinked mentions found.");
                }
            }),
            Applying("fix", "Repair dead wikilinks in the vault.", "Apply fixes to files", "write fixes", () =>
            {
                var result = NewGraph().FixLinks();
                Say($"✓ Fixed {result.Fixed} of {result.BrokenFound} broken links.");
            }),
        };

        var orphans = new Command("orphans", "Orphan discovery and linking commands.")
        {
            RunAsync("list", "Find notes with no incoming links.", async ct =>
            {
                if ((await NewGraph().ListOrphansAsync(ct).ConfigureAwait(false)).Count == 0)
                {
                    Say("✓ No orphan notes found.");
                }
            }),
            Applying("link", "Automatically link orphans to PARA hub notes.", "Apply changes to files", "link orphan notes", async ct =>
            {
                var result = await NewGraph().LinkOrphansAsync(ct).ConfigureAwait(false);
                Say($"✓ Linked {result.Linked} of {result.OrphansFound} orphan notes.");
            }),
        };

        var json = new Option<bool>("--json") { Description = "Emit machine-readable JSON" };
        var lint = new Command("lint", LintHelp) { json };
        // The Python discarded the lint's verdict, so findings still exit 0; --json's "ok" carries it.
        lint.SetAction(parsed =>
        {
            var report = new VaultLinter(Vault).Report();
            output.Write(parsed.GetValue(json) ? report.ToJson() + "\n" : report.ToText());
        });

        return new Command("graph", "Graph: manage link health, orphans, and vault structure.")
        {
            links,
            Metrics(),
            orphans,
            Repair(),
            Applying("hubs", "Create or maintain PARA root hub notes (Home, Areas, etc).", "Create or update PARA root hubs", "create and update hub notes", async ct =>
            {
                var result = await NewGraph().RootHubsAsync(ct).ConfigureAwait(false);
                Say($"✓ Created {result.HubsCreated} hubs and added {result.LinksAdded} root links.");
            }),
            lint,
        };
    }

    private Command Repair() => new("repair", "Metadata, filename and structure repair commands.")
    {
        Repairing("duplicates", "Remove redundant URLs from social posts and consolidate duplicate resources.", "Apply cleanup fixes", (repair, dryRun) =>
        {
            Say("Phase 1: Social Media Metadata Cleanup");
            repair.CleanupSocialDuplicates(dryRun);
            Say("\nPhase 2: Resource Consolidation");
            repair.CleanupResourceDuplicates(dryRun);
        }),
        Repairing("backfill-types", "Automatically add 'type' field to notes based on folder location.", "Apply type backfills", (repair, dryRun) =>
            repair.BackfillTypes(dryRun)),
        Repairing("metadata", "Repair missing metadata across the vault.", "Apply metadata repairs", (repair, dryRun) =>
            repair.RepairMetadata(dryRun)),
        Repairing("normalize", "Sync note metadata based on PARA folder location.", "Apply normalization changes", (repair, dryRun) =>
        {
            repair.BackfillTypes(dryRun);
            repair.RepairMetadata(dryRun);
        }),
        Repairing("filenames", "Rename ugly note filenames and update wikilinks.", "Apply filename repairs", (repair, dryRun) =>
            repair.CleanupFilenames(dryRun)),
    };

    private Command Metrics() => new("metrics", "Vault density and folder health reporting.")
    {
        Run("folder-scores", "Display health scores for each folder based on linting failures.", () =>
        {
            var table = new TextTable("Vault Folder Health Scores", [("Folder", false), ("Notes", true), ("Errors", true), ("Dupes", true), ("Score", true)]);
            foreach (var (folder, s) in new VaultLinter(Vault).FolderStats().OrderBy(pair => pair.Value.Score))
            {
                table.Add(folder, Number(s.TotalNotes), Number(s.MetadataErrors), Number(s.Duplicates), s.Score.ToString("F1", CultureInfo.InvariantCulture));
            }

            output.Write(table.Render());
        }),
        Run("health", "Assess the health of vault folders based on density and organization.", () =>
        {
            var table = new TextTable("Vault Health by Folder", [("Folder", false), ("Density (Notes)", true), ("Status", false)]);
            foreach (var (folder, count) in VaultLinter.VaultStats(Vault))
            {
                table.Add(folder, Number(count), Density(count));
            }

            output.Write(table.Render());
        }),
    };

    /// <summary>How a folder of <paramref name="count"/> notes is described by <c>metrics health</c>.</summary>
    internal static string Density(int count) => count switch
    {
        0 => "Empty",
        > 100 => "Overcrowded",
        > 50 => "Dense",
        _ => "Healthy",
    };

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private VaultGraph NewGraph() => new(Vault, Say, new ObsidianOrphans(proc, host.Home));

    private void Say(string line) => output.WriteLine(line);

    private static Command Run(string name, string description, Action run)
    {
        var command = new Command(name, description);
        command.SetAction(_ => run());
        return command;
    }

    private static Command RunAsync(string name, string description, Func<CancellationToken, Task> run)
    {
        var command = new Command(name, description);
        command.SetAction((_, ct) => run(ct));
        return command;
    }

    // A command that only says it is a dry run without --apply, as the Python's link, fix and hub commands did.
    private Command Applying(string name, string description, string applyHelp, string what, Func<CancellationToken, Task> apply)
    {
        var flag = new Option<bool>("--apply") { Description = applyHelp };
        var command = new Command(name, description) { flag };
        command.SetAction(async (parsed, ct) =>
        {
            if (!parsed.GetValue(flag))
            {
                await output.WriteLineAsync($"Dry run only. Re-run with --apply to {what}.").ConfigureAwait(false);
                return;
            }

            await apply(ct).ConfigureAwait(false);
        });
        return command;
    }

    private Command Applying(string name, string description, string applyHelp, string what, Action apply) =>
        Applying(name, description, applyHelp, what, _ =>
        {
            apply();
            return Task.CompletedTask;
        });

    // A repair that previews its changes without --apply.
    private Command Repairing(string name, string description, string applyHelp, Action<VaultRepair, bool> run)
    {
        var flag = new Option<bool>("--apply") { Description = applyHelp };
        var command = new Command(name, description) { flag };
        command.SetAction(parsed => run(new VaultRepair(Vault, Say, error.WriteLine), !parsed.GetValue(flag)));
        return command;
    }
}
