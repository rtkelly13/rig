using System.Text.Json.Nodes;
using Rig.Core.Devenv;

namespace Rig.Core.Tests.Devenv;

/// <summary>The gh scope overlay. Ported from <c>test_github_cli.py</c>.</summary>
public sealed class GhScopesTests : IDisposable
{
    private readonly TempDir _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void The_bundled_overlay_names_each_scope_once_with_a_rationale()
    {
        Assert.Equal(["admin:org", "read:user"], GhScopes.Required);
        Assert.Equal(GhScopes.All.Count, GhScopes.All.Distinct(StringComparer.Ordinal).Count());
        var overlay = JsonNode.Parse(GhScopes.Overlay)!.AsObject();
        Assert.All(
            overlay.SelectMany(section => section.Value!.AsArray()),
            entry => Assert.False(string.IsNullOrWhiteSpace((string?)entry!["rationale"])));
    }

    [Fact]
    public void The_target_is_under_xdg_config_home_when_set()
    {
        Assert.Equal(Path.Join("/x", "devenv", "gh-scopes.json"), GhScopes.Target("/h", "/x"));
        Assert.Equal(Path.Join("/h", ".config", "devenv", "gh-scopes.json"), GhScopes.Target("/h", null));
        Assert.Equal(Path.Join("/h", ".config", "devenv", "gh-scopes.json"), GhScopes.Target("/h", string.Empty));
    }

    [Fact]
    public async Task The_state_compares_the_installed_overlay_as_json()
    {
        var target = GhScopes.Target(_home.Path, null);
        Assert.Equal(OverlayState.Missing, GhScopes.State(target));

        await File.WriteAllTextAsync(_home.File(".config/devenv/gh-scopes.json"), "{not json", TestContext.Current.CancellationToken);
        Assert.Equal(OverlayState.OutOfDate, GhScopes.State(target));

        await File.WriteAllTextAsync(target, """{"required": []}""", TestContext.Current.CancellationToken);
        Assert.Equal(OverlayState.OutOfDate, GhScopes.State(target));

        await File.WriteAllTextAsync(target, JsonNode.Parse(GhScopes.Overlay)!.ToJsonString(), TestContext.Current.CancellationToken);
        Assert.Equal(OverlayState.InSync, GhScopes.State(target));
    }

    [Theory]
    [InlineData(OverlayState.InSync, "in sync")]
    [InlineData(OverlayState.OutOfDate, "out of date")]
    [InlineData(OverlayState.Missing, "not installed")]
    public void Each_state_is_described_as_devenv_did(OverlayState state, string text) => Assert.Equal(text, state.Describe());
}
