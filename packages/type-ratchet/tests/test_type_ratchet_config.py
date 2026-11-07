from __future__ import annotations

from pathlib import Path

import pytest
from type_ratchet import ConfigError, RatchetConfig, Rule, RuleSet, load_config

pytestmark = pytest.mark.ci


def _write(tmp_path: Path, text: str) -> Path:
    pyproject = tmp_path / "pyproject.toml"
    _ = pyproject.write_text(text, encoding="utf-8")
    return pyproject


def test_legacy_table_is_read(tmp_path: Path) -> None:
    pyproject = _write(
        tmp_path,
        '[tool.repo-health.ratchet]\npyright-pragma-allow = ["src/p.py"]\n'
        'boundary-modules = ["src/a.py"]\nstr-key-receivers = ["*.attrib"]\n',
    )
    assert load_config(pyproject) == RatchetConfig(
        pyright_pragma_allow=frozenset({"src/p.py"}),
        boundary_modules=frozenset({"src/a.py"}),
        str_key_receivers=frozenset({"*.attrib"}),
    )


def test_every_key_is_read_from_the_type_ratchet_table(tmp_path: Path) -> None:
    pyproject = _write(
        tmp_path,
        '[tool.type-ratchet]\npaths = ["lib", "tools"]\ndisable = ["noqa", "long-module"]\n'
        'long-module-lines = 500\nhint = "See CONTRIBUTING.md."\n',
    )
    assert load_config(pyproject) == RatchetConfig(
        paths=("lib", "tools"),
        disabled_rules=frozenset({Rule.NOQA, Rule.LONG_MODULE}),
        long_module_lines=500,
        hint="See CONTRIBUTING.md.",
    )


def test_missing_table_or_file_means_defaults(tmp_path: Path) -> None:
    assert load_config(tmp_path / "pyproject.toml") == RatchetConfig()
    assert load_config(_write(tmp_path, '[project]\nname = "x"\n')) == RatchetConfig()


@pytest.mark.parametrize(
    "text",
    [
        '[tool.type-ratchet]\ndisable = ["no-such-rule"]\n',
        '[tool.type-ratchet]\npaths = "src"\n',
        "[tool.type-ratchet]\npaths = []\n",
        "[tool.type-ratchet]\nlong-module-lines = 0\n",
        "[tool.type-ratchet]\nlong-module-lines = true\n",
        "[tool.type-ratchet]\nhint = 3\n",
        "[tool.type-ratchet]\nboundary-module = []\n",
        '[tool]\ntype-ratchet = "x"\n',
        "[tool.type-ratchet]\n[tool.repo-health.ratchet]\n",
        "[tool.type-ratchet\n",
        '[tool.type-ratchet]\nrule-sets = "tests"\n',
        '[[tool.type-ratchet.rule-sets]]\nrules = ["cast"]\n',
        '[[tool.type-ratchet.rule-sets]]\nfiles = []\nrules = ["cast"]\n',
        '[[tool.type-ratchet.rule-sets]]\nfiles = ["tests/*"]\n',
        '[[tool.type-ratchet.rule-sets]]\nfiles = ["tests/*"]\nrules = []\n',
        '[[tool.type-ratchet.rule-sets]]\nfiles = ["tests/*"]\nrules = ["nope"]\n',
        '[[tool.type-ratchet.rule-sets]]\nfiles = ["tests/*"]\nrules = ["cast"]\ndisable = []\n',
    ],
)
def test_malformed_config_is_rejected(tmp_path: Path, text: str) -> None:
    with pytest.raises(ConfigError):
        _ = load_config(_write(tmp_path, text))


def test_rule_sets_are_read_in_order(tmp_path: Path) -> None:
    pyproject = _write(
        tmp_path,
        '[tool.repo-health.ratchet]\ndisable = ["noqa"]\n'
        '[[tool.repo-health.ratchet.rule-sets]]\nfiles = ["tests/fakes/*"]\nrules = ["cast"]\n'
        '[[tool.repo-health.ratchet.rule-sets]]\nfiles = ["tests/*", "scripts/*"]\nrules = ["cast", "noqa"]\n',
    )
    assert load_config(pyproject) == RatchetConfig(
        disabled_rules=frozenset({Rule.NOQA}),
        rule_sets=(
            RuleSet(files=("tests/fakes/*",), rules=frozenset({Rule.CAST})),
            RuleSet(files=("tests/*", "scripts/*"), rules=frozenset({Rule.CAST, Rule.NOQA})),
        ),
    )


@pytest.mark.parametrize(
    ("path", "gated"),
    [
        ("src/a.py", frozenset(Rule) - {Rule.NOQA}),
        ("tests/fakes/gh.py", frozenset({Rule.CAST})),
        ("tests/test_a.py", frozenset({Rule.CAST, Rule.NOQA})),
        ("tests/deep/test_b.py", frozenset({Rule.CAST, Rule.NOQA})),
    ],
)
def test_the_first_matching_rule_set_decides_the_gated_rules(path: str, gated: frozenset[Rule]) -> None:
    config = RatchetConfig(
        disabled_rules=frozenset({Rule.NOQA}),
        rule_sets=(
            RuleSet(files=("tests/fakes/*",), rules=frozenset({Rule.CAST})),
            RuleSet(files=("tests/*",), rules=frozenset({Rule.CAST, Rule.NOQA})),
        ),
    )
    assert config.gated_rules(path) == gated
