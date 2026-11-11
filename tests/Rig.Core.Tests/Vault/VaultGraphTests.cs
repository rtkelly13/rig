using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;
using Rig.Core.Vault;

namespace Rig.Core.Tests.Vault;

/// <summary>
/// What the oracle cannot show: where <see cref="VaultGraph"/> parts from the Python on purpose, the Python's quirks it keeps
/// until they are fixed on their own, the output cap, and the desktop app's orphan list, which the oracle stubbed out.
/// </summary>
public sealed class VaultGraphTests : IDisposable
{
    private readonly string parent = RepoTemp.NewVaultParent("rig-graph");
    private readonly List<string> said = [];
    private readonly FakeProc proc = new();

    public VaultGraphTests()
    {
        Vault = Directory.CreateDirectory(Path.Combine(parent, "Vault")).FullName;
        Home = Directory.CreateDirectory(Path.Combine(parent, "home")).FullName;
    }

    private string Vault { get; }

    private string Home { get; }

    public void Dispose() => Directory.Delete(parent, recursive: true);

    [Fact]
    public void Fixing_a_link_keeps_the_notes_line_endings()
    {
        Write("Resources/Inner.md", "inner\n");
        var note = WriteBytes("Resources/Crlf.md", "﻿one\r\ntwo [[Old/Inner]]\r\nthree\r"u8.ToArray());

        var result = Graph().FixLinks();

        Assert.Equal(new FixLinksResult(1, 1), result);
        Assert.Equal("﻿one\r\ntwo [[Resources/Inner]]\r\nthree\r", Encoding.UTF8.GetString(File.ReadAllBytes(note)));
    }

    [Fact]
    public void A_note_that_is_not_utf8_is_reported_and_the_others_are_still_fixed()
    {
        Write("Resources/Inner.md", "inner\n");
        byte[] original = [0xFF, .. " [[Old/Inner]]\n"u8];
        var bad = WriteBytes("Resources/Bad.md", original);
        var fine = Write("Resources/Fine.md", "[[Old/Inner]]\n");

        var result = Graph().FixLinks();

        Assert.Equal(new FixLinksResult(2, 1), result);
        Assert.Equal(original, File.ReadAllBytes(bad));
        Assert.Equal("[[Resources/Inner]]\n", File.ReadAllText(fine));
        Assert.Contains("Skipped Resources/Bad.md, its links left as they were: it is not UTF-8", said);
    }

    [Fact]
    public void A_note_that_cannot_be_written_is_reported_and_not_counted()
    {
        Write("Resources/Inner.md", "inner\n");
        var locked = Write("Resources/Locked.md", "[[Old/Inner]]\n");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        try
        {
            Assert.Equal(new FixLinksResult(1, 0), Graph().FixLinks());
        }
        finally
        {
            File.SetAttributes(locked, FileAttributes.Normal);
        }

        Assert.Equal("[[Old/Inner]]\n", File.ReadAllText(locked));
        Assert.Contains(said, l => l.StartsWith("Skipped Resources/Locked.md, its links left as they were: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Linking_orphans_twice_appends_the_link_twice_as_the_python_did()
    {
        // A known flaw kept on purpose: the append neither looks for the link first nor stops the note being an orphan,
        // since a link written with its .md is one the scan does not count. Fixing it is its own change.
        var hub = Write("Projects/Build.md", "# Build");
        Write("Projects/Build/Deep/Leaf.md", "leaf\n");

        Assert.Equal(new OrphanLinkResult(2, 1), await Graph().LinkOrphansAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new OrphanLinkResult(2, 1), await Graph().LinkOrphansAsync(TestContext.Current.CancellationToken));

        Assert.Equal("# Build\n- [[Projects/Build/Deep/Leaf.md]]\n- [[Projects/Build/Deep/Leaf.md]]", await File.ReadAllTextAsync(hub, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_orphan_whose_hub_cannot_be_written_is_not_linked()
    {
        Directory.CreateDirectory(Path.Join(Vault, "Projects", "Build.md"));
        Write("Projects/Build/Leaf.md", "leaf\n");

        var result = await Graph().LinkOrphansAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new OrphanLinkResult(1, 0), result);
        Assert.Empty(said);
    }

    [Fact]
    public void Unlinked_mentions_past_twenty_are_summed_up()
    {
        Write("Areas/Lighthouse.md", "a title\n");
        for (var i = 0; i < VaultGraph.MentionsShown + 2; i++)
        {
            Write($"Areas/N{i:00}.md", "See Lighthouse here.\n");
        }

        var report = Graph().ListLinks();

        Assert.Equal(22, report.Unlinked.Count);
        Assert.Equal(["\nUnlinked Mentions:", .. Enumerable.Range(0, 20).Select(i => $"  Areas/N{i:00}.md mentions [[Lighthouse]]"), "  ... and 2 more."], said);
    }

    [Fact]
    public void A_title_two_notes_share_points_at_the_later_one()
    {
        Write("A/Shared Title.md", "one\n");
        Write("B/Shared Title.md", "two\n");
        Write("C.md", "about Shared Title here\n");

        var mention = Assert.Single(VaultGraph.FindUnlinkedMentions(Vault));

        Assert.Equal(new UnlinkedMention("C.md", "Shared Title", "B/Shared Title"), mention);
    }

    [Fact]
    public void A_note_named_md_at_the_root_does_not_resolve_a_link_to_its_title()
    {
        // The Python keyed it by its path with every .md removed, which is empty, and an empty match read as no match.
        Write(".md", "dot\n");
        Write("Note.md", "[[.md]]\n");

        Assert.Equal(".md", Assert.Single(VaultGraph.FindBrokenLinks(Vault)).Target);
    }

    [Fact]
    public async Task The_desktop_app_answers_for_the_vault_it_has_open()
    {
        Registry(Vaults((Path.Combine(parent, "Other"), false), (Vault, true)));
        proc.Answer(new Ran(0, "Projects/A.md\r\n  \n Templates/T.md\nB.md\n", string.Empty), "obsidian", "orphans");

        var orphans = await Graph().FindOrphansAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["B.md", "Projects/A.md"], orphans);
        Assert.Equal(ObsidianOrphans.Timeout, Assert.Single(proc.Requests).Timeout);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"vaults": []}""")]
    [InlineData("""{"vaults": {"a": {"path": "VAULT", "open": "true"}, "b": {"path": 1, "open": true}, "c": [], "d": {"path": "", "open": true}}}""")]
    [InlineData("""{"vaults": {"a": {"path": "VAULT"}}}""")]
    [InlineData("""{"other": 1}""")]
    public async Task Without_a_usable_open_vault_the_app_is_not_asked(string registry)
    {
        Registry(registry.Replace("VAULT", Escape(Vault), StringComparison.Ordinal));
        Write("Lone.md", "alone\n");

        Assert.Equal(["Lone.md"], await Graph().FindOrphansAsync(TestContext.Current.CancellationToken));
        Assert.Empty(proc.Requests);
    }

    [Fact]
    public async Task When_the_app_fails_the_vault_is_scanned()
    {
        Registry(Vaults((Vault, true)));
        proc.Answer(new Ran(Ran.TimedOut, "Stale.md\n", string.Empty), "obsidian", "orphans");
        Write("Lone.md", "alone\n");

        Assert.Equal(["Lone.md"], await Graph().FindOrphansAsync(TestContext.Current.CancellationToken));
        Assert.Single(proc.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [UnsupportedOSPlatform("windows")]
    public async Task The_open_vault_is_matched_through_symlinks(bool absolute)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symlinks needs developer mode on Windows.");
        var link = Path.Combine(parent, "Linked");
        Directory.CreateSymbolicLink(link, absolute ? parent : Path.Combine("..", Path.GetFileName(parent)));
        Registry(Vaults((Path.Combine(link, ".", "Vault"), true)));
        proc.Answer(new Ran(0, "Native.md\n", string.Empty), "obsidian", "orphans");

        Assert.Equal(["Native.md"], await Graph().FindOrphansAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Links_stay_in_the_vault_when_its_vault_folder_is_a_dangling_symlink()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symlinks needs developer mode on Windows.");
        File.CreateSymbolicLink(Path.Join(Vault, "Vault"), "nowhere");
        Write("Note.md", "[[Gone]]\n");

        Assert.Equal("Gone", Assert.Single(Graph().ListLinks().Broken).Target);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_symlink_loop_neither_hangs_nor_counts_as_a_path()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symlinks needs developer mode on Windows.");
        File.CreateSymbolicLink(Path.Join(Vault, "Vault"), "Vault");
        var loop = Path.Combine(parent, "loop");
        File.CreateSymbolicLink(loop, "loop");
        Registry(Vaults((Path.Combine(loop, "Vault"), true)));
        Write("Note.md", "[[Gone]]\n");

        Assert.Equal("Gone", Assert.Single(Graph().ListLinks().Broken).Target);
        Assert.Equal(["Note.md"], await Graph().FindOrphansAsync(TestContext.Current.CancellationToken));
        Assert.Empty(proc.Requests);
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("a\n", new[] { "a" })]
    [InlineData("a\n\nb", new[] { "a", "", "b" })]
    [InlineData("a\r\nb\rc", new[] { "a", "b", "c" })]
    [InlineData("a\vb\fc\u001cd\u001de\u001ef\u0085g\u2028h\u2029i", new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i" })]
    [InlineData("a\u001fb\tc", new[] { "a\u001fb\tc" })]
    public void SplitLines_breaks_where_python_does(string text, string[] expected)
    {
        Assert.Equal(expected, PyText.SplitLines(text));
    }

    private static string Vaults(params (string Path, bool Open)[] vaults) =>
        new JsonObject
        {
            ["vaults"] = new JsonObject(vaults.Select((v, i) => KeyValuePair.Create($"v{i}", (JsonNode?)new JsonObject { ["path"] = v.Path, ["open"] = v.Open }))),
        }.ToJsonString();

    private static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    private VaultGraph Graph() => new(Vault, said.Add, new ObsidianOrphans(proc, Home));

    private void Registry(string json)
    {
        var folder = Directory.CreateDirectory(Path.Join(Home, "Library", "Application Support", "obsidian")).FullName;
        File.WriteAllText(Path.Join(folder, "obsidian.json"), json);
    }

    private string Write(string relative, string text) => WriteBytes(relative, new UTF8Encoding(false).GetBytes(text));

    private string WriteBytes(string relative, byte[] data)
    {
        var full = Path.GetFullPath(Path.Join(Vault, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
        return full;
    }
}
