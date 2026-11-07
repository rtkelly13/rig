from __future__ import annotations

import textwrap
from pathlib import Path

import pytest
from type_ratchet import ConfigError, RatchetConfig, Rule, count_source, load_config, violations

pytestmark = pytest.mark.ci


def _loose(source: str, aliases: frozenset[str] = frozenset()) -> int:
    return count_source(textwrap.dedent(source), loose_dict_aliases=aliases)[Rule.LOOSE_DICT]


def test_every_annotation_context_is_counted() -> None:
    source = """
        from dataclasses import dataclass, field

        @dataclass
        class Row:
            extra: dict[str, object] = field(default_factory=dict[str, object])

        def f(a: dict[str, object], *rest: list[dict[str, object]]) -> dict[str, object] | None:
            local: dict[str, object] = {}
            return try_decode(dict[str, object], a)

        type Loose = list[dict[str, object]]
        """
    assert _loose(source) == 8


def test_mapping_spellings_are_counted_however_imported() -> None:
    source = """
        import typing
        import collections.abc as abc
        from collections.abc import Mapping
        from collections.abc import MutableMapping as MM
        from typing import Dict

        def f(a: Mapping[str, object], b: MM[str, object], c: Dict[str, object]) -> None: ...
        def g(a: typing.Mapping[str, object], b: abc.MutableMapping[str, object]) -> typing.Dict[str, object]: ...
        """
    assert _loose(source) == 6


def test_quoted_annotations_are_counted() -> None:
    assert _loose('def f(a: "dict[str, object]", b: "list[JsonObject]") -> None: ...', frozenset({"JsonObject"})) == 2


def test_precise_and_homogeneous_maps_are_not_counted() -> None:
    source = """
        from collections.abc import Mapping

        def f(a: dict[str, int], b: Mapping[int, object], c: dict[str, str | None], d: object) -> dict[str, list[str]]:
            return {}
        """
    assert _loose(source) == 0


def test_configured_aliases_are_counted_wherever_they_are_read() -> None:
    source = """
        from shared.typed_json import JsonObject
        from shared.typed_json import JsonObject as Obj
        from shared import typed_json

        def f(a: JsonObject, b: Obj) -> typed_json.JsonObject:
            return decode(JsonObject, a)
        """
    assert _loose(source, frozenset({"JsonObject"})) == 4
    assert _loose(source) == 0


def test_an_alias_definition_is_not_counted() -> None:
    source = """
        from typing import TypeAlias

        type JsonObject = dict[str, object]
        LegacyObject = dict[str, object]
        AnnotatedObject: TypeAlias = dict[str, object]
        """
    assert _loose(source, frozenset({"JsonObject", "LegacyObject", "AnnotatedObject"})) == 0
    assert _loose(source) == 3


def test_allow_globs_exempt_only_loose_dict() -> None:
    config = RatchetConfig(loose_dict_allow=("src/serialize.py", "src/out/*.py"))
    actual = {
        Rule.LOOSE_DICT: {"src/serialize.py": 2, "src/out/rows.py": 1, "src/decode.py": 1},
        Rule.CAST: {"src/serialize.py": 1},
    }
    found = [(v.rule, v.path) for v in violations(actual, config)]
    assert found == [(Rule.CAST, "src/serialize.py"), (Rule.LOOSE_DICT, "src/decode.py")]


def test_boundary_modules_may_hold_loose_dicts() -> None:
    config = RatchetConfig(boundary_modules=frozenset({"src/typed_json.py"}))
    actual = {Rule.LOOSE_DICT: {"src/typed_json.py": 3, "src/app.py": 1}}
    assert [v.path for v in violations(actual, config)] == ["src/app.py"]


def test_loose_dict_keys_are_read(tmp_path: Path) -> None:
    pyproject = tmp_path / "pyproject.toml"
    _ = pyproject.write_text(
        '[tool.type-ratchet]\nloose-dict-aliases = ["JsonObject"]\nloose-dict-allow = ["src/out/*.py", "src/a.py"]\n',
        encoding="utf-8",
    )
    assert load_config(pyproject) == RatchetConfig(
        loose_dict_aliases=frozenset({"JsonObject"}), loose_dict_allow=("src/out/*.py", "src/a.py")
    )


@pytest.mark.parametrize("key", ["loose-dict-aliases", "loose-dict-allow"])
def test_loose_dict_keys_must_be_string_lists(tmp_path: Path, key: str) -> None:
    pyproject = tmp_path / "pyproject.toml"
    _ = pyproject.write_text(f'[tool.type-ratchet]\n{key} = "x"\n', encoding="utf-8")
    with pytest.raises(ConfigError, match=key):
        _ = load_config(pyproject)
