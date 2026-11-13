"""Exercise the legacy inspector with real dynamic registrations in disposable modules."""

from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

from rig import tracing

pytestmark = pytest.mark.ci
PROBE = Path(__file__).resolve().parents[1] / "tools" / "command-ownership" / "python_tree.py"


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
    result = tracing.run(
        [sys.executable, str(PROBE), str(root)], scope="ownership-test", timeout=30, capture_output=True
    )
    assert result.returncode == 0, result.stderr
    inventory = json.loads(result.stdout)
    assert [row["Path"] for row in inventory["Commands"]] == ["unregistered one", "unregistered two"]
    assert "module:ownership_fixture" in inventory["Surfaces"]


def test_a_local_command_factory_is_refused_instead_of_silently_omitted(tmp_path: Path) -> None:
    root = _fixture(tmp_path, "import typer\ndef make_app():\n    return typer.Typer()\n")
    result = tracing.run(
        [sys.executable, str(PROBE), str(root)], scope="ownership-test", timeout=30, capture_output=True
    )
    assert result.returncode != 0
    assert "Unsupported local command factory" in result.stderr
    assert not result.stdout


def test_a_thin_alias_is_discovered_but_added_business_logic_is_an_owner(tmp_path: Path) -> None:
    root = _fixture(
        tmp_path,
        "import typer\nfrom rig import csharp_cli\nfrom desktop_manager.chrome.trace_forward import forward\n"
        'app = typer.Typer(name="compat")\napp.command(name="alias")(forward)\n'
        '@app.command(name="independent")\ndef independent(ctx: typer.Context):\n'
        "    if ctx.args:\n        return\n"
        '    raise typer.Exit(code=csharp_cli.run(["chrome-tabs", "traces", *ctx.args]))\n',
    )
    result = tracing.run(
        [sys.executable, str(PROBE), str(root)], scope="ownership-test", timeout=30, capture_output=True
    )
    assert result.returncode == 0, result.stderr
    rows = json.loads(result.stdout)["Commands"]
    assert rows[0]["Alias"] == "chrome-tabs traces"
    assert rows[1]["Alias"] is None


def test_a_decorated_dispatcher_is_not_a_thin_alias(tmp_path: Path) -> None:
    root = _fixture(
        tmp_path,
        "import typer\nfrom functools import wraps\nfrom desktop_manager.chrome.trace_forward import forward\n"
        "def decorate(fn):\n    @wraps(fn)\n    def wrapped(ctx):\n"
        '        raise RuntimeError("extra behavior")\n    return wrapped\n'
        'app = typer.Typer(name="compat")\napp.command(name="wrapped")(decorate(forward))\n',
    )
    result = tracing.run(
        [sys.executable, str(PROBE), str(root)], scope="ownership-test", timeout=30, capture_output=True
    )
    assert result.returncode == 0, result.stderr
    assert json.loads(result.stdout)["Commands"][0]["Alias"] is None


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
    result = tracing.run(
        [sys.executable, str(PROBE), str(root)], scope="ownership-test", timeout=30, capture_output=True
    )
    assert result.returncode != 0
    assert "Unsupported development CLI factory" in result.stderr


def test_an_unknown_console_script_needs_an_adapter_even_if_its_target_is_callable(tmp_path: Path) -> None:
    root = _fixture(tmp_path, "def main():\n    pass\n")
    (root / "pyproject.toml").write_text('[project.scripts]\nextra = "ownership_fixture:main"\n', encoding="utf-8")
    result = tracing.run(
        [sys.executable, str(PROBE), str(root)], scope="ownership-test", timeout=30, capture_output=True
    )
    assert result.returncode != 0
    assert "Unsupported console entry point" in result.stderr
    (root / "pyproject.toml").write_text('[project.gui-scripts]\nextra = "ownership_fixture:main"\n', encoding="utf-8")
    result = tracing.run(
        [sys.executable, str(PROBE), str(root)], scope="ownership-test", timeout=30, capture_output=True
    )
    assert result.returncode != 0
    assert "Unsupported GUI/plugin entry points" in result.stderr
