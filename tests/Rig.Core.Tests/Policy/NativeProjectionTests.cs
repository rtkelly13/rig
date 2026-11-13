using System.Text.Json.Nodes;
using Rig.Core.Policy;

namespace Rig.Core.Tests.Policy;

/// <summary>Ported from the projection cases of the Python <c>tests/test_policy.py</c>; every one writes under a temp home.</summary>
public sealed class NativeProjectionTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Home => Path.Join(_dir.Path, "home");

    private string Claude => Path.Join(Home, ".claude", "settings.json");

    private string Opencode => Path.Join(Home, ".config", "opencode", "opencode.json");

    private string Codex => Path.Join(Home, ".codex", "rules", "default.rules");

    private string Antigravity => Path.Join(Home, ".gemini", "antigravity-cli", "settings.json");

    private static void Seed(string path, string text)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private IReadOnlyList<ProjectionResult> Sync(params string[] systems) =>
        NativeProjection.ApplyAll(PolicyFiles.Load(), systems.Length == 0 ? NativeProjection.Systems : systems, Home, dryRun: false, windows: false);

    private static string[] Rules(string path, string key) =>
        [.. JsonNode.Parse(File.ReadAllText(path))!["permissions"]![key]!.AsArray().Select(n => n!.GetValue<string>())];

    [Fact]
    public void Projections_preserve_user_configuration_and_are_idempotent()
    {
        Seed(Claude, """{"model":"custom","permissions":{"allow":["Bash(custom *)"]}}""" + "\n");
        Seed(Opencode, """{"provider":{"custom":{"api":"keep"}}}""" + "\n");
        Seed(Codex, """prefix_rule(pattern=["custom"], decision="allow")""" + "\n");
        Seed(Antigravity, """{"theme":"dark","permissions":{"allow":["command(custom)"]}}""" + "\n");

        var first = Sync();

        Assert.All(first, r => Assert.Equal(ProjectionStatus.Applied, r.Status));
        var claude = File.ReadAllText(Claude);
        Assert.Contains("\"model\": \"custom\"", claude, StringComparison.Ordinal);
        Assert.Contains("Bash(uv run pytest:*)", claude, StringComparison.Ordinal);
        var opencode = File.ReadAllText(Opencode);
        Assert.Contains("\"api\": \"keep\"", opencode, StringComparison.Ordinal);
        Assert.Contains("\"shell\"", opencode, StringComparison.Ordinal);
        Assert.Contains("\"bash\"", opencode, StringComparison.Ordinal);
        Assert.Contains("\"external_directory\"", opencode, StringComparison.Ordinal);
        Assert.Contains("\"~/code/**\": \"allow\"", opencode, StringComparison.Ordinal);
        Assert.Contains("\"uv run pytest *\": \"allow\"", opencode, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(Path.GetDirectoryName(Opencode), "plugins", "estate-guard.ts")));
        var codex = File.ReadAllText(Codex);
        Assert.Contains("pattern=[\"custom\"]", codex, StringComparison.Ordinal);
        Assert.Contains("BEGIN shared-utilities policy", codex, StringComparison.Ordinal);
        var antigravity = File.ReadAllText(Antigravity);
        Assert.Contains("\"theme\": \"dark\"", antigravity, StringComparison.Ordinal);
        foreach (var rule in new[] { "command(custom)", "command(git push)", "command(git fetch)", "command(git reset --hard)" })
        {
            Assert.Contains(rule, antigravity, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("unsandboxed", antigravity, StringComparison.Ordinal);

        Assert.All(Sync(), r => Assert.Equal(ProjectionStatus.Unchanged, r.Status));
    }

    [Fact]
    public void A_dry_run_reports_what_would_change_and_writes_nothing()
    {
        Seed(Codex, "# mine\n");

        var results = NativeProjection.ApplyAll(PolicyFiles.Load(), NativeProjection.Systems, Home, dryRun: true, windows: false);

        Assert.All(results, r => Assert.Equal(ProjectionStatus.DryRun, r.Status));
        Assert.False(File.Exists(Claude));
        Assert.Equal("# mine\n", File.ReadAllText(Codex));
    }

    [Fact]
    public void A_misshapen_permissions_block_is_reported_and_left_alone()
    {
        const string original = """{"permissions":{"allow":"Bash(git:*)"}}""";
        Seed(Claude, original);

        var result = Assert.Single(Sync("claude"));

        Assert.Equal(ProjectionStatus.Failed, result.Status);
        Assert.Equal("permissions.allow must be an array", result.Detail);
        Assert.Equal(original, File.ReadAllText(Claude));
    }

    [Theory]
    [InlineData("claude", "[1]", "native config root must be an object")]
    [InlineData("claude", """{"permissions": []}""", "permissions must be an object")]
    [InlineData("opencode", """{"permission": {"shell": 1}}""", "permission.shell must be an object")]
    public void A_misshapen_native_file_fails(string system, string content, string detail)
    {
        Seed(system == "claude" ? Claude : Opencode, content);

        var result = Assert.Single(Sync(system));

        Assert.Equal((ProjectionStatus.Failed, detail), (result.Status, result.Detail));
    }

    [Fact]
    public void Unparseable_json_fails_rather_than_being_overwritten()
    {
        Seed(Claude, "{not json");

        Assert.Equal(ProjectionStatus.Failed, Assert.Single(Sync("claude")).Status);
        Assert.Equal("{not json", File.ReadAllText(Claude));
    }

    [Fact]
    public void Antigravity_migrates_legacy_unsandboxed_rules_once_each()
    {
        Seed(Antigravity, """
            {"permissions": {"allow": ["command(custom)", "unsandboxed(dotnet)", "unsandboxed(git push)", 7],
                             "deny": ["unsandboxed(git reset --hard)"]}}
            """);

        Assert.Equal(ProjectionStatus.Applied, Assert.Single(Sync("antigravity")).Status);

        var content = File.ReadAllText(Antigravity);
        Assert.DoesNotContain("unsandboxed", content, StringComparison.Ordinal);
        var allow = JsonNode.Parse(content)!["permissions"]!["allow"]!.AsArray();
        Assert.Contains(allow, n => n!.ToJsonString() == "7");
        var texts = allow.Where(n => n!.GetValueKind() == System.Text.Json.JsonValueKind.String).Select(n => n!.GetValue<string>()).ToList();
        Assert.Single(texts, "command(git push)");
        Assert.Single(texts, "command(dotnet)");
        Assert.Contains("command(custom)", texts);
        Assert.Single(Rules(Antigravity, "deny"), "command(git reset --hard)");
    }

    [Fact]
    public void A_deny_excluded_from_claude_is_removed_there_but_projected_to_antigravity()
    {
        Seed(Claude, """{"permissions":{"deny":["Bash(pnpm --filter:*)","Bash(pnpm -F:*)","Bash(custom)"]}}""" + "\n");

        _ = Sync("claude", "antigravity");

        var deny = Rules(Claude, "deny");
        Assert.DoesNotContain("Bash(pnpm --filter:*)", deny);
        Assert.DoesNotContain("Bash(pnpm -F:*)", deny);
        Assert.Contains("Bash(pnpm --dir:*)", deny);
        Assert.Contains("Bash(git -C:*)", deny);
        Assert.Contains("Bash(custom)", deny);
        var antigravity = File.ReadAllText(Antigravity);
        Assert.Contains("command(pnpm --filter)", antigravity, StringComparison.Ordinal);
        Assert.Contains("command(pnpm -F)", antigravity, StringComparison.Ordinal);
    }

    [Fact]
    public void Claude_keeps_interior_wildcards_and_denies_admin_merges()
    {
        Assert.Equal(ProjectionStatus.Applied, Assert.Single(Sync("claude")).Status);

        var allow = Rules(Claude, "allow");
        var deny = Rules(Claude, "deny");
        foreach (var rule in new[]
        {
            "Bash(gh pr ready:*)", "Bash(gh pr merge * --auto --squash)", "Bash(gh pr merge * --disable-auto)",
            "Bash(gh stack merge * --yes*)", "Bash(gh stack sync)", "Bash(gh stack sync:*)", "Bash(gh run rerun:*)",
        })
        {
            Assert.Contains(rule, allow);
        }

        Assert.Contains("Bash(gh stack sync *--prune*)", deny);
        Assert.Contains("Bash(gh pr merge *--admin*)", deny);
        Assert.DoesNotContain(allow, r => r.StartsWith("Bash(gh pr merge:", StringComparison.Ordinal));
    }

    [Fact]
    public void Prefix_surfaces_hold_back_allows_a_dropped_interior_deny_would_carve_out()
    {
        // A deny with an interior `*` is dropped by prefix-only surfaces, so the allow it narrows is held back too.
        Seed(Antigravity, """{"permissions":{"allow":["command(gh stack sync)","command(find)"]}}""" + "\n");

        Assert.All(Sync("opencode", "codex", "antigravity"), r => Assert.Equal(ProjectionStatus.Applied, r.Status));

        var opencode = File.ReadAllText(Opencode);
        Assert.Contains("\"gh stack sync\": \"allow\"", opencode, StringComparison.Ordinal); // exact: `--prune` does not match it
        Assert.DoesNotContain("\"gh stack sync *\": \"allow\"", opencode, StringComparison.Ordinal);
        Assert.DoesNotContain("\"find *\": \"allow\"", opencode, StringComparison.Ordinal);
        var codex = File.ReadAllText(Codex);
        Assert.DoesNotContain("pattern=[\"gh\", \"stack\", \"sync\"],\n    decision='allow'", codex, StringComparison.Ordinal);
        Assert.DoesNotContain("pattern=[\"find\"],\n    decision='allow'", codex, StringComparison.Ordinal);
        var antigravity = File.ReadAllText(Antigravity);
        Assert.DoesNotContain("command(gh stack sync)", antigravity, StringComparison.Ordinal); // a copy an earlier sync wrote is removed
        Assert.DoesNotContain("command(find)", antigravity, StringComparison.Ordinal);
        Assert.Contains("command(gh run rerun)", antigravity, StringComparison.Ordinal); // allows no deny overlaps still project
    }

    [Fact]
    public void Codex_replaces_its_managed_block_in_place_keeping_what_surrounds_it()
    {
        Seed(Codex, "# before\n\n# BEGIN shared-utilities policy\nstale\n# END shared-utilities policy\n# after\n");

        Assert.Equal(ProjectionStatus.Applied, Assert.Single(Sync("codex")).Status);

        var codex = File.ReadAllText(Codex);
        Assert.StartsWith("# before\n\n# BEGIN shared-utilities policy\nprefix_rule(\n", codex, StringComparison.Ordinal);
        Assert.EndsWith("# END shared-utilities policy\n# after\n", codex, StringComparison.Ordinal);
        Assert.DoesNotContain("stale", codex, StringComparison.Ordinal);
        Assert.Contains("decision='forbidden',\n    justification='Blocked by the estate policy.',", codex, StringComparison.Ordinal);
        Assert.Contains("decision='prompt',\n    justification='Local mutation requires approval.',", codex, StringComparison.Ordinal);
        Assert.Contains("justification='External side effect requires approval.'", codex, StringComparison.Ordinal);
        Assert.Equal(ProjectionStatus.Unchanged, Assert.Single(Sync("codex")).Status);
    }

    [Fact]
    public void Codex_markers_in_the_wrong_order_fail()
    {
        const string original = "# END shared-utilities policy\n# BEGIN shared-utilities policy\n";
        Seed(Codex, original);

        var result = Assert.Single(Sync("codex"));

        Assert.Equal(ProjectionStatus.Failed, result.Status);
        Assert.Equal(original, File.ReadAllText(Codex));
    }

    [Fact]
    public void On_Windows_files_are_written_with_crlf_and_read_back_unchanged()
    {
        var policy = PolicyFiles.Load();

        var first = NativeProjection.ApplyAll(policy, ["claude", "codex"], Home, dryRun: false, windows: true);
        var second = NativeProjection.ApplyAll(policy, ["claude", "codex"], Home, dryRun: false, windows: true);

        Assert.All(first, r => Assert.Equal(ProjectionStatus.Applied, r.Status));
        Assert.All(second, r => Assert.Equal(ProjectionStatus.Unchanged, r.Status));
        Assert.Contains("\r\n", File.ReadAllText(Claude), StringComparison.Ordinal);
        Assert.DoesNotContain("\n", File.ReadAllText(Codex).Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], "opencode.json")]
    [InlineData(new[] { "config.json" }, "config.json")]
    [InlineData(new[] { "config.json", "opencode.jsonc" }, "opencode.jsonc")]
    [InlineData(new[] { "config.json", "opencode.jsonc", "opencode.json" }, "opencode.json")]
    public void Opencode_uses_the_first_config_file_present(string[] present, string chosen)
    {
        foreach (var name in present)
        {
            Seed(Path.Join(Home, ".config", "opencode", name), "{}");
        }

        Assert.Equal(Path.Join(Home, ".config", "opencode", chosen), NativeProjection.For("opencode", Home, windows: false).Path);
    }

    [Fact]
    public void Opencode_mirrors_rules_across_the_shell_and_bash_maps()
    {
        // OpenCode v2 concatenates both maps and applies the last match, so a bash-only deny would lose to the shell
        // map's "*" ask and become a prompt.
        Seed(Opencode, """{"permission":{"bash":{"*":"ask","~/.local/bin/uv *":"deny"},"shell":{"*":"ask"}}}""" + "\n");

        _ = Sync("opencode");

        var permission = JsonNode.Parse(File.ReadAllText(Opencode))!["permission"]!;
        Assert.Equal("deny", (string)permission["bash"]!["~/.local/bin/uv *"]!);
        Assert.Equal("deny", (string)permission["shell"]!["~/.local/bin/uv *"]!);
        Assert.All(Sync("opencode"), r => Assert.Equal(ProjectionStatus.Unchanged, r.Status));
    }

    [Fact]
    public void Opencode_conflicting_shell_maps_resolve_to_the_restrictive_effect()
    {
        // A broader allow in one map must not win over a narrower deny in the other when v2 combines them.
        Seed(Opencode, """{"permission":{"shell":{"*":"ask","acme *":"allow"},"bash":{"*":"ask","acme destroy *":"deny"}}}""" + "\n");

        _ = Sync("opencode");

        var permission = JsonNode.Parse(File.ReadAllText(Opencode))!["permission"]!;
        var bash = permission["bash"]!.AsObject().Select(pair => pair.Key).ToList();
        var shell = permission["shell"]!.AsObject().Select(pair => pair.Key).ToList();
        Assert.Equal(bash, shell);
        Assert.True(shell.IndexOf("acme destroy *") > shell.IndexOf("acme *"));
    }

    [Fact]
    public void An_unknown_system_is_rejected()
    {
        _ = Assert.Throws<ArgumentException>(() => NativeProjection.For("vim", Home, windows: false));
    }

    [Fact]
    public void A_surface_without_a_native_file_is_unsupported()
    {
        var result = new NativeProjection("other", null, false).Apply(PolicyFiles.Load(), dryRun: false);

        Assert.Equal((ProjectionStatus.Unsupported, (string?)null), (result.Status, result.Path));
    }

    [Fact]
    public void A_json_surface_the_projection_does_not_know_fails()
    {
        var result = new NativeProjection("other", Path.Join(Home, "x.json"), false).Apply(PolicyFiles.Load(), dryRun: false);

        Assert.Equal((ProjectionStatus.Failed, "JSON projection is not supported for other"), (result.Status, result.Detail));
    }

    [Theory]
    [InlineData("git status", new[] { "git", "status" }, false)]
    [InlineData("git log*", new[] { "git", "log" }, true)]
    [InlineData("git log *  ", new[] { "git", "log" }, true)]
    [InlineData("echo 'a b'", new[] { "echo", "a b" }, false)]
    public void A_literal_or_trailing_star_glob_has_a_prefix(string pattern, string[] tokens, bool wildcard)
    {
        var prefix = NativeProjection.Prefix(pattern)!;

        Assert.Equal(tokens, prefix.Tokens);
        Assert.Equal(wildcard, prefix.Wildcard);
    }

    [Theory]
    [InlineData("gh pr merge * --auto")]
    [InlineData("git ?")]
    [InlineData("*")]
    [InlineData("   ")]
    [InlineData("echo \"unclosed")]
    public void A_glob_a_prefix_cannot_express_has_none(string pattern) => Assert.Null(NativeProjection.Prefix(pattern));

    [Theory]
    [InlineData("git status", "Bash(git status)")]
    [InlineData("git log*", "Bash(git log:*)")]
    [InlineData("gh  pr merge *  --auto", "Bash(gh pr merge * --auto)")]
    [InlineData("git ?", null)]
    [InlineData("echo * \"x", null)]
    [InlineData("*", "Bash(*)")]
    public void Claude_rules_take_the_prefix_form_or_the_glob_verbatim(string pattern, string? rule) =>
        Assert.Equal(rule, NativeProjection.ClaudeRule(pattern));
}
