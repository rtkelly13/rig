"""Deriving C4 views from a .NET repository: projects, namespaces, both relationship pathways, determinism."""

from __future__ import annotations

from pathlib import Path

import pytest
from typer.testing import CliRunner

from rig.c4_okf.bundle import load_bundle
from rig.c4_okf.cli import DEFAULT_BUNDLE, app
from rig.c4_okf.derive import Parent, PathwayScore, derive, stale, write
from rig.c4_okf.dotnet_scan import discover, read_source, sources
from rig.c4_okf.graph import Severity, check_bundle

_GEN = (
    "<Project><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework>"
    "<EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules></PropertyGroup>"
    '<ItemGroup><ProjectReference Include="..\\Acme.Runtime\\Acme.Runtime.csproj" /></ItemGroup></Project>'
)
_LEGACY = (
    "<Project><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework>"
    "<IsRoslynComponent>true</IsRoslynComponent></PropertyGroup><ItemGroup>"
    '<Compile Include="..\\Acme.Gen\\Models\\*.cs" Link="Models\\%(Filename)" />'
    '<ProjectReference Include="..\\Acme.Gen\\Acme.Gen.csproj" OutputItemType="Analyzer" /></ItemGroup></Project>'
)
_REPO = {
    "src/Acme.Gen/Acme.Gen.csproj": _GEN,
    "src/Acme.Gen/Gen.cs": (
        "namespace Acme.Gen;\nusing Acme.Gen.Parsing;\n"
        "public sealed class Gen : IIncrementalGenerator { void Run() { Parser.Parse(); } }\n"
    ),
    "src/Acme.Gen/Parsing/Parser.cs": (
        "namespace Acme.Gen.Parsing;\n// Marker is only mentioned in this comment\n"
        '/// <summary>Reads a <see cref="T:Acme.Gen.Models.Model"/> from text. Says Marker.</summary>\n'
        "[Obsolete]\n"
        "public static class Parser {\n"
        '    public static Model Parse() => new Model("Old");\n'
        "    public static int Count { get; }\n"
        "}\n"
    ),
    "src/Acme.Gen/Models/Model.cs": "namespace Acme.Gen.Models;\npublic record Model(string Name);\n",
    "src/Acme.Gen/obj/Junk.cs": "namespace Junk;\nclass Junk {}\n",
    "src/Acme.Runtime/Acme.Runtime.csproj": (
        "<Project><PropertyGroup><TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks></PropertyGroup></Project>"
    ),
    "src/Acme.Runtime/Marker.cs": "namespace Acme.Runtime;\npublic sealed class Marker { }\n",
    "src/Acme.Legacy/Acme.Legacy.csproj": _LEGACY,
    "src/Acme.Legacy/Old.cs": "namespace Acme.Legacy;\npublic sealed class Old { Model m; Marker k; }\n",
    "src/Acme.Legacy/Polyfill.cs": "namespace System.Runtime.CompilerServices;\nclass IsExternalInit {}\n",
}
_CONTEXT = """---
type: C4 Context
title: Acme — system context
---
```mermaid
C4Context
Person(dev, "Developer")
System(acme, "Acme", $link="containers.md")
Rel(dev, acme, "uses")
```
"""


@pytest.fixture
def repo(tmp_path: Path) -> Path:
    root = tmp_path / "acme"
    for name, text in _REPO.items():
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        _ = path.write_text(text, encoding="utf-8")
    graph = tmp_path / "callgraph"
    graph.mkdir()
    _ = (graph / "Acme.Gen.callgraph.txt").write_text(
        "E:Acme.Gen.Gen.Run/() | Acme.Gen.Parsing.Parser.Parse/()\n"
        "E:Acme.Gen.Parsing.Parser.Parse/() | Acme.Gen.Parsing.Parser.Parse/()\n"
        "not an edge\n",
        encoding="utf-8",
    )
    return root


def test_discover_reads_projects_analyzers_and_linked_globs(repo: Path) -> None:
    projects = {p.name: p for p in discover(repo)}
    assert sorted(projects) == ["Acme.Gen", "Acme.Legacy", "Acme.Runtime"]
    assert projects["Acme.Gen"].is_analyzer
    assert projects["Acme.Legacy"].is_analyzer
    assert not projects["Acme.Runtime"].is_analyzer
    assert projects["Acme.Runtime"].frameworks == ("netstandard2.0", "net8.0")
    assert [r.analyzer for r in projects["Acme.Legacy"].refs] == [True]
    linked = {f.path.name for f in sources(projects["Acme.Legacy"])}
    assert linked == {"Old.cs", "Polyfill.cs", "Model.cs"}
    assert "Junk.cs" not in {f.path.name for f in sources(projects["Acme.Gen"])}


def test_read_source_ignores_names_in_comments_and_strings(repo: Path) -> None:
    parsed = read_source(repo / "src/Acme.Gen/Parsing/Parser.cs")
    assert parsed.namespace == "Acme.Gen.Parsing"
    assert parsed.declares == ("Parser",)
    assert "Model" in parsed.names
    assert not {"Marker", "Old", "Gen"} & parsed.names
    assert read_source(repo / "src/Acme.Gen/Gen.cs").is_generator


def test_read_source_reads_kind_summary_members_and_size(repo: Path) -> None:
    parser = read_source(repo / "src/Acme.Gen/Parsing/Parser.cs")
    (info,) = parser.types
    assert (info.kind, info.summary, info.members) == ("class", "Reads a Model from text.", ("Parse()", "Count"))
    assert parser.lines == 8
    (model,) = read_source(repo / "src/Acme.Gen/Models/Model.cs").types
    assert model.kind == "record"


def test_derive_writes_every_level_and_shares_linked_code(repo: Path, tmp_path: Path) -> None:
    derived = derive(repo, tmp_path / "out", parent=None, callgraph=tmp_path / "callgraph")
    assert sorted(derived.files) == [
        "code/gen.md",
        "code/gen_models.md",
        "code/gen_parsing.md",
        "code/legacy.md",
        "code/runtime.md",
        "components/gen.md",
        "components/legacy.md",
        "components/runtime.md",
        "containers.md",
    ]
    containers = derived.files["containers.md"]
    assert 'Rel(csc, gen, "loads into the build"' in containers
    # The analyzer checks the project that loads it.
    assert 'Rel(gen, legacy, "checks at build"' in containers
    assert '"1 source file"' in containers
    assert "IsExternalInit" not in "".join(derived.files.values())
    # Legacy compiles Models in, so both component views zoom into one shared code view.
    assert '$link="/code/gen_models.md"' in derived.files["components/gen.md"]
    assert '$link="/code/gen_models.md"' in derived.files["components/legacy.md"]
    # File details: the code view annotates kinds and members, and notes carry the file and its size.
    parsing = derived.files["code/gen_parsing.md"]
    assert "class Parser {\n  +Parse()\n  +Count\n}" in parsing
    assert "**class** · Reads a Model from text." in parsing
    assert "`Parser.cs` in src/Acme.Gen/Parsing/ · 8 lines" in parsing
    assert "<<record>>" in derived.files["code/gen_models.md"]
    assert "| `Parser.cs` |  | 8 |" in derived.files["components/gen.md"]


def test_pathways_score_type_references_against_the_call_graph(repo: Path, tmp_path: Path) -> None:
    derived = derive(repo, tmp_path / "out", parent=None, callgraph=tmp_path / "callgraph")
    # Gen → Parsing is on both pathways; Parsing → Models only on type references; no call-only edge.
    assert derived.pathways["gen"] == PathwayScore(1, 1, 0)
    assert "1 type, 1 call" in derived.files["components/gen.md"]
    blind = derive(repo, tmp_path / "out", parent=None, callgraph=None)
    assert "## Derivation" in blind.files["components/gen.md"]


def test_derive_is_deterministic_and_check_finds_drift(repo: Path, tmp_path: Path) -> None:
    out = tmp_path / "out"
    first = derive(repo, out, parent=None, callgraph=None)
    assert first.files == derive(repo, out, parent=None, callgraph=None).files
    assert write(out, first.files) == sorted(first.files)
    assert stale(out, first.files) == []
    _ = (repo / "src/Acme.Runtime/Extra.cs").write_text("namespace Acme.Runtime;\nclass Extra {}\n", encoding="utf-8")
    assert "code/runtime.md" in stale(out, derive(repo, out, parent=None, callgraph=None).files)


def test_check_reports_orphans_and_write_removes_only_derived_ones(repo: Path, tmp_path: Path) -> None:
    out = tmp_path / "out"
    derived = derive(repo, out, parent=None, callgraph=None)
    _ = write(out, derived.files)
    # A namespace that has since been renamed away left its derived view behind; a hand-written view sits beside it.
    _ = (out / "code/gone.md").write_text(derived.files["code/runtime.md"], encoding="utf-8")
    hand = out / "notes.md"
    _ = hand.write_text("---\ntype: C4 Context\ngenerated: { by: human:ryan, at: 2026-11-04 }\n---\n", encoding="utf-8")

    assert stale(out, derived.files) == ["code/gone.md"]
    assert write(out, derived.files) == ["code/gone.md"]
    assert not (out / "code/gone.md").exists()
    assert hand.exists()
    assert stale(out, derived.files) == []


def test_derived_views_under_a_parent_pass_the_bundle_checks(repo: Path, tmp_path: Path) -> None:
    out = tmp_path / "out"
    out.mkdir()
    _ = (out / "system-context.md").write_text(_CONTEXT, encoding="utf-8")
    derived = derive(repo, out, parent=Parent("system-context", "acme"), callgraph=tmp_path / "callgraph")
    _ = write(out, derived.files)
    assert "System_Boundary(acme" in derived.files["containers.md"]
    assert 'Person(dev, "Developer")' in derived.files["containers.md"]
    assert "title Acme: projects" in derived.files["containers.md"]
    assert 'System_Boundary(acme, "Acme")' in derived.files["containers.md"]
    findings = check_bundle(load_bundle(out))
    assert [f for f in findings if f.severity is not Severity.INFO] == []


def test_parquet_sample_bundle_has_no_errors_or_warnings() -> None:
    findings = check_bundle(load_bundle(DEFAULT_BUNDLE.parent / "parquet-sourcegenerator"))
    assert [f for f in findings if f.severity is not Severity.INFO] == []


def test_cli_derive_dotnet_writes_then_check_passes_until_a_file_goes_stale(repo: Path, tmp_path: Path) -> None:
    runner = CliRunner()
    out = tmp_path / "bundle"
    _ = _write_context(out)
    args = ["derive", "dotnet", str(repo), str(out), "--parent", "system-context:acme"]
    wrote = runner.invoke(app, [*args, "--callgraph", str(tmp_path / "callgraph")])
    assert wrote.exit_code == 0, wrote.output
    assert "wrote containers.md" in wrote.output
    assert "relationships on both pathways" in wrote.output
    clean = runner.invoke(app, [*args, "--callgraph", str(tmp_path / "callgraph"), "--check"])
    assert clean.exit_code == 0, clean.output
    assert "0 stale" in clean.output
    _ = (out / "containers.md").write_text("edited\n", encoding="utf-8")
    stale_run = runner.invoke(app, [*args, "--callgraph", str(tmp_path / "callgraph"), "--check"])
    assert stale_run.exit_code == 1
    assert "stale containers.md" in stale_run.output


def _write_context(out: Path) -> Path:
    out.mkdir(parents=True)
    path = out / "system-context.md"
    _ = path.write_text(_CONTEXT, encoding="utf-8")
    return path
