"""Exercise the legacy inspector with real dynamic registrations in disposable modules."""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import pytest

pytestmark = pytest.mark.ci
PROBE = Path(__file__).resolve().parents[1] / "tools" / "command-ownership" / "python_tree.py"


def _probe(root: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(  # noqa: TID251 -- the probe is a script under test, not rig code
        [sys.executable, str(PROBE), str(root)],
        timeout=30,
        capture_output=True,
        text=True,
        stdin=subprocess.DEVNULL,
        check=False,
    )


def _fixture(tmp_path: Path, source: str) -> Path:
    root = tmp_path / "fixture"
    (root / "src").mkdir(parents=True)
    (root / "src" / "ownership_fixture.py").write_text(source, encoding="utf-8")
    (root / "pyproject.toml").write_text("[project.scripts]\n", encoding="utf-8")
    return root


def test_an_unmounted_dynamic_app_and_its_module_entry_are_discovered(tmp_path: Path) -> None:
    root = _fixture(
        tmp_path,
        'import typer\napp = typer.Typer(name="unregistered")\n'
        'def action():\n    pass\nfor name in ("one", "two"):\n    app.command(name=name)(action)\n'
        'if __name__ == "__main__":\n    app()\n',
    )
    result = _probe(root)
    assert result.returncode == 0, result.stderr
    inventory = json.loads(result.stdout)
    assert [row["Path"] for row in inventory["Commands"]] == ["unregistered one", "unregistered two"]
    assert "module:ownership_fixture" in inventory["Surfaces"]


def test_a_local_command_factory_is_refused_instead_of_silently_omitted(tmp_path: Path) -> None:
    root = _fixture(tmp_path, "import typer\ndef make_app():\n    return typer.Typer()\n")
    result = _probe(root)
    assert result.returncode != 0
    assert "Unsupported local command factory" in result.stderr
    assert not result.stdout


@pytest.mark.parametrize(
    "source",
    [
        "import click\n@click.command()\ndef run():\n    pass\n",
        "from click import group as cli\n@cli()\ndef run():\n    pass\n",
        "import click as c\napp = c.Group()\n",
        "from typer import Typer as CLI\napp = CLI()\n",
        "from click import command\n@command\ndef run():\n    pass\n",
    ],
)
def test_development_cli_factories_require_a_runtime_adapter(tmp_path: Path, source: str) -> None:
    root = _fixture(tmp_path, "def main():\n    pass\n")
    (root / "scripts").mkdir()
    (root / "scripts" / "existing.py").write_text(source, encoding="utf-8")
    result = _probe(root)
    assert result.returncode != 0
    assert "Unsupported development CLI factory" in result.stderr


def test_an_unknown_console_script_needs_an_adapter_even_if_its_target_is_callable(tmp_path: Path) -> None:
    root = _fixture(tmp_path, "def main():\n    pass\n")
    (root / "pyproject.toml").write_text('[project.scripts]\nextra = "ownership_fixture:main"\n', encoding="utf-8")
    result = _probe(root)
    assert result.returncode != 0
    assert "Unsupported console entry point" in result.stderr
    (root / "pyproject.toml").write_text('[project.gui-scripts]\nextra = "ownership_fixture:main"\n', encoding="utf-8")
    result = _probe(root)
    assert result.returncode != 0
    assert "Unsupported GUI/plugin entry points" in result.stderr


@pytest.mark.parametrize(
    "source",
    [
        "import click\ndef helper():\n    import json as click\n@click.command()\ndef run():\n    pass\n",
        "import json as CLI\ndef helper():\n    from typer import Typer as CLI\n    return CLI()\n",
    ],
)
@pytest.mark.parametrize("development_file", [False, True])
def test_conflicting_scoped_imports_cannot_hide_factories(tmp_path: Path, source: str, development_file: bool) -> None:
    root = _fixture(tmp_path, "def main():\n    pass\n" if development_file else source)
    if development_file:
        (root / "scripts").mkdir()
        (root / "scripts" / "existing.py").write_text(source, encoding="utf-8")
    result = _probe(root)
    assert result.returncode != 0
    assert "Unsupported conflicting CLI import binding" in result.stderr
    assert not result.stdout
