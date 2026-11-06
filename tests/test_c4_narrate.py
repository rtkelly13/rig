"""The compiler call graph, the member-level code views drawn from it, guide pages and change walkthroughs."""

from __future__ import annotations

from pathlib import Path

import pytest

from rig.c4_okf.callgraph import CallGraph, MemberKey, member_key
from rig.c4_okf.derive import derive
from rig.c4_okf.narrate import yaml_str
from rig.proc import git

_CSPROJ = "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"
_RUNNER_V1 = (
    "namespace Acme.App;\n"
    "using Acme.App.Parsing;\n"
    "public sealed class Runner {\n"
    "    public void Run() { Parser.Parse(); }\n"
    "    public void Stop() { }\n"
    "}\n"
)
_RUNNER_V2 = _RUNNER_V1.replace("public void Stop() { }", "public void Stop() { Run(); }")
_PARSER = (
    "namespace Acme.App.Parsing;\n"
    "/// <summary>Reads text.</summary>\n"
    "public static class Parser {\n"
    "    public static int Parse() => 1;\n"
    "}\n"
)
# What dotnet/callgraph.cs writes for the repository at HEAD (Runner v2).
_GRAPH = """T:Acme.App.Runner|class|public|src/Acme.App/Runner.cs:3
M:Acme.App.Runner.Run/()|method|public|4
M:Acme.App.Runner.Stop/()|method|public|5
M:Acme.App.Runner..ctor/()|ctor|public|3
T:Acme.App.Parsing.Parser|class|public|src/Acme.App/Parsing/Parser.cs:3
M:Acme.App.Parsing.Parser.Parse/()|method|public|4
E:Acme.App.Runner.Run/() | Acme.App.Parsing.Parser.Parse/()
E:Acme.App.Runner.Stop/() | Acme.App.Runner.Run/()
"""
_GUIDE = """# 4. A walk

`Runner.Run` calls `Parser`, which lives in `Acme.App.Parsing`. `Nothing` is not code.
See [the next page](05-next.md#top) and [an ADR](../adrs/0001.md), or [`Parser`](https://example.com).

```
Runner.Run is not linked inside a fence: `Parser`
```
"""


def _git(repo: Path, *args: str) -> None:
    _ = git(
        "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false", *args, cwd=repo, check=True
    )


@pytest.fixture
def repo(tmp_path: Path) -> Path:
    root = tmp_path / "acme"
    files = {
        "src/Acme.App/Acme.App.csproj": _CSPROJ,
        "src/Acme.App/Runner.cs": _RUNNER_V1,
        "src/Acme.App/Parsing/Parser.cs": _PARSER,
        "docs/guide/04-walk.md": _GUIDE,
        "docs/guide/05-next.md": "# 5. Next\n",
    }
    for name, text in files.items():
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        _ = path.write_text(text, encoding="utf-8", newline="\n")
    _git(root, "init", "-q", "-b", "main")
    _git(root, "add", "-A")
    _git(root, "commit", "-qm", "Start")
    _ = (root / "src/Acme.App/Runner.cs").write_text(_RUNNER_V2, encoding="utf-8", newline="\n")
    _git(root, "commit", "-qam", "Stop runs again (#7)\n\nStop now calls Run.\n\nCo-authored-by: t <t@example.com>")
    graph = tmp_path / "graph"
    graph.mkdir()
    _ = (graph / "Acme.App.callgraph.txt").write_text(_GRAPH, encoding="utf-8")
    return root


def test_call_graph_reads_types_members_calls_and_constructors() -> None:
    graph = CallGraph()
    graph.read([*_GRAPH.splitlines(), "X:Acme.App.IRun.Run/() | Acme.App.Runner.Run/()", "junk"])
    assert graph.types["Acme.App.Runner"].path == "src/Acme.App/Runner.cs"
    assert member_key("Acme.App.Runner..ctor/()") == MemberKey("Acme.App.Runner", "new")
    assert [k.name for k in graph.of_type("Acme.App.Runner")] == ["Run", "Stop", "new"]
    assert (MemberKey("Acme.App.Runner", "Stop"), MemberKey("Acme.App.Runner", "Run")) in graph.calls
    assert graph.implements == {(MemberKey("Acme.App.IRun", "Run"), MemberKey("Acme.App.Runner", "Run"))}
    assert graph.label(MemberKey("Acme.App.Runner", "Run")) == "Run()"


def test_code_view_draws_members_as_nodes_and_calls_as_edges(repo: Path, tmp_path: Path) -> None:
    files = derive(repo, tmp_path / "out", parent=None, callgraph=tmp_path / "graph").files
    view = files["code/app.md"]
    assert view.startswith("---\ntype: C4 Code\n")
    assert 'subgraph n_type_Acme_App_Runner["Runner · class"]' in view
    assert "n_Acme_App_Runner_Stop --> n_Acme_App_Runner_Run" in view
    # A call that leaves the namespace ends in a box that links to the namespace it lands in.
    assert "n_Acme_App_Runner_Run --> n_Acme_App_Parsing_Parser_Parse" in view
    assert 'click n_Acme_App_Parsing_Parser_Parse href "/code/parsing.md"' in view
    assert "| `Stop()` | method | public | `Runner.Run()` |  |" in view


def test_guide_pages_link_code_names_and_keep_other_links_working(repo: Path, tmp_path: Path) -> None:
    files = derive(
        repo,
        tmp_path / "out",
        parent=None,
        callgraph=tmp_path / "graph",
        guides=("docs/guide/04-walk.md", "docs/guide/05-next.md"),
    ).files
    page = files["guide/04-walk.md"]
    assert page.startswith("---\ntype: Guide\ntitle: 4. A walk\n")
    assert "[`Runner.Run`](../code/app.md#runner)" in page
    assert "[`Parser`](../code/parsing.md#parser)" in page
    assert "[`Acme.App.Parsing`](../code/parsing.md)" in page
    assert "`Nothing` is not code" in page
    assert "[the next page](05-next.md#top)" in page
    assert "[`Parser`](https://example.com)" in page
    assert "Runner.Run is not linked inside a fence: `Parser`" in page
    assert "> Copied from [docs/guide/04-walk.md]" in page


def test_change_pages_map_a_diff_to_members_and_order_them(repo: Path, tmp_path: Path) -> None:
    files = derive(repo, tmp_path / "out", parent=None, callgraph=tmp_path / "graph", changes=5).files
    # The first commit has no parent to diff against, so only the second is walked through.
    assert sorted(f for f in files if f.startswith("changes/")) == ["changes/changes.md", "changes/pr-7.md"]
    page = files["changes/pr-7.md"]
    assert 'title: "Change: Stop runs again (#7)"' in page
    assert "class n_Acme_App_Runner_Stop changed" in page
    # Run is one call away from the edit, so it is drawn as a neighbour, not as changed.
    assert "n_Acme_App_Runner_Stop --> n_Acme_App_Runner_Run" in page
    assert "class n_Acme_App_Runner_Run changed" not in page
    assert "> Stop now calls Run." in page
    assert "Co-authored-by" not in page
    assert "1. [`Runner.Stop()`](../code/app.md#runner)" in page
    assert "| `src/Acme.App/Runner.cs` | +1 −1 | to members |" in page
    assert "[Stop runs again (#7)](pr-7.md)" in files["changes/changes.md"]


def test_derive_with_guides_and_changes_is_deterministic(repo: Path, tmp_path: Path) -> None:
    def run() -> dict[str, str]:
        return derive(
            repo,
            tmp_path / "out",
            parent=None,
            callgraph=tmp_path / "graph",
            guides=("docs/guide/04-walk.md",),
            changes=3,
        ).files

    assert run() == run()


@pytest.mark.parametrize(
    ("text", "shown"),
    [("Plain title", "Plain title"), ("Change: a (#1)", '"Change: a (#1)"'), ("It's", '"It\'s"')],
)
def test_yaml_str_quotes_only_when_needed(text: str, shown: str) -> None:
    assert yaml_str(text) == shown
