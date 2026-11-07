from __future__ import annotations

import textwrap

import pytest
from type_ratchet import RatchetConfig, Rule, count_source, violations

pytestmark = pytest.mark.ci


def _count(source: str) -> dict[Rule, int]:
    return dict(count_source(textwrap.dedent(source)))


def test_fixed_tuples_in_annotations_are_counted() -> None:
    counts = _count(
        """
        from typing import Tuple

        def f(a: tuple[int, str], b: list[tuple[str, str]]) -> Tuple[int]: ...
        x: dict[tuple[int, int], str] = {}
        type Pair = tuple[str, str]
        """
    )
    assert counts[Rule.FIXED_TUPLE] == 5


def test_homogeneous_tuples_and_runtime_tuples_are_allowed() -> None:
    counts = _count(
        """
        def f(items: tuple[str, ...]) -> tuple[int, ...]:
            pair = (1, 2)
            return pair
        """
    )
    assert counts.get(Rule.FIXED_TUPLE, 0) == 0


def test_named_tuples_are_counted() -> None:
    counts = _count(
        """
        from collections import namedtuple
        from typing import NamedTuple

        class Point(NamedTuple):
            x: int

        Pair = namedtuple("Pair", "a b")
        """
    )
    assert counts[Rule.FIXED_TUPLE] == 2


def test_casts_comments_and_reflection_are_counted() -> None:
    counts = _count(
        """
        import typing
        from typing import cast

        a = cast(int, 1)  # type: ignore[misc]
        b = typing.cast(str, "")  # pyright: ignore[reportUnknownMemberType]
        c = getattr(a, "x", None)  # noqa: B009
        hasattr(a, "y")
        """
    )
    assert counts[Rule.CAST] == 2
    assert counts[Rule.IGNORE_COMMENT] == 2
    assert counts[Rule.NOQA] == 1
    assert counts[Rule.GETATTR_FAMILY] == 2


def test_string_key_access_counts_but_literal_does_not() -> None:
    counts = _count(
        """
        import json
        from typing import Literal

        row = json.loads("{}")
        mode: Literal["fast"] = "fast"
        title = row["title"]
        gid = row.get("gid")
        if isinstance(row, (list, dict)):
            pass
        """
    )
    assert counts[Rule.STR_KEY_SUBSCRIPT] == 1
    assert counts[Rule.STR_KEY_GET] == 1
    assert counts[Rule.ISINSTANCE_DICT] == 1
    assert counts[Rule.JSON_LOADS] == 1


def test_aliased_imports_and_quoted_annotations_are_counted() -> None:
    counts = _count(
        """
        import typing as t
        from collections import namedtuple as nt
        from typing import Tuple as T, cast as type_cast

        a = type_cast(int, 1)
        b = t.cast(str, "")
        Pair = nt("Pair", "a b")
        def f(x: "tuple[int, str]", y: "list[T[int]]") -> t.Tuple[int, int]: ...
        """
    )
    assert counts[Rule.CAST] == 2
    assert counts[Rule.FIXED_TUPLE] == 4


def test_long_module_counts_once() -> None:
    assert _count("x = 1\n" * 801)[Rule.LONG_MODULE] == 1
    assert _count("x = 1\n" * 800 + "x = 1")[Rule.LONG_MODULE] == 1
    assert _count("x = 1\n" * 800).get(Rule.LONG_MODULE, 0) == 0


def test_any_occurrence_is_a_violation() -> None:
    found = violations({Rule.CAST: {"b.py": 1, "a.py": 2, "c.py": 0}})
    assert [(v.rule, v.path, v.count) for v in found] == [(Rule.CAST, "a.py", 2), (Rule.CAST, "b.py", 1)]


def test_string_key_reads_and_dict_narrowing_gate() -> None:
    actual = {Rule.STR_KEY_GET: {"a.py": 1}, Rule.STR_KEY_SUBSCRIPT: {"a.py": 2}, Rule.ISINSTANCE_DICT: {"a.py": 1}}
    found = violations(actual)
    assert {v.rule for v in found} == {Rule.STR_KEY_GET, Rule.STR_KEY_SUBSCRIPT, Rule.ISINSTANCE_DICT}


def test_boundary_modules_do_not_exempt_string_key_reads() -> None:
    config = RatchetConfig(boundary_modules=frozenset({"src/typed_json.py"}))
    actual = {Rule.ISINSTANCE_DICT: {"src/typed_json.py": 1}, Rule.STR_KEY_GET: {"src/typed_json.py": 1}}
    assert [v.rule for v in violations(actual, config)] == [Rule.STR_KEY_GET]


def test_file_level_pyright_pragmas_that_relax_checking_are_counted() -> None:
    counts = _count(
        """
        # pyright: reportAny=false, reportUnknownMemberType=false
        # pyright: basic
        # pyright: reportPrivateUsage=warning
        x = 1
        """
    )
    assert counts[Rule.PYRIGHT_PRAGMA] == 3


def test_strict_pragmas_and_inline_ignores_are_not_pyright_pragmas() -> None:
    counts = _count(
        """
        # pyright: strict
        # pyright: reportAny=error, reportUnusedImport=true
        x = 1  # pyright: ignore[reportAny]
        y = 2  # pyright: reportAny=false
        """
    )
    assert counts.get(Rule.PYRIGHT_PRAGMA, 0) == 0
    assert counts[Rule.IGNORE_COMMENT] == 1


def test_allowlisted_pragma_paths_do_not_gate_other_rules_still_do() -> None:
    actual = {Rule.PYRIGHT_PRAGMA: {"a.py": 1, "b.py": 1}, Rule.CAST: {"a.py": 1}}
    found = violations(actual, RatchetConfig(pyright_pragma_allow=frozenset({"a.py"})))
    assert [(v.rule, v.path) for v in found] == [(Rule.CAST, "a.py"), (Rule.PYRIGHT_PRAGMA, "b.py")]


def test_json_and_yaml_loaders_count_every_reference() -> None:
    counts = _count(
        """
        import json
        import yaml as y
        from json import load as read_json

        parse = json.loads
        doc = read_json(open("a"))
        cfg = y.safe_load("")
        docs = list(y.load_all(""))
        """
    )
    assert counts[Rule.JSON_LOADS] == 2
    assert counts[Rule.YAML_LOAD] == 2


def test_boundary_modules_may_parse_and_narrow_raw_data() -> None:
    config = RatchetConfig(boundary_modules=frozenset({"src/typed_json.py"}))
    actual = {
        Rule.JSON_LOADS: {"src/typed_json.py": 1, "src/app.py": 1},
        Rule.CAST: {"src/typed_json.py": 1},
    }
    found = violations(actual, config)
    assert [(v.rule, v.path) for v in found] == [(Rule.CAST, "src/typed_json.py"), (Rule.JSON_LOADS, "src/app.py")]


def test_string_key_writes_and_the_environment_are_not_reads() -> None:
    counts = _count(
        """
        import os
        from os import environ

        out = {}
        out["title"] = "x"
        out["n"] += 1
        del out["n"]
        home = os.environ["HOME"]
        user = environ.get("USER")
        """
    )
    assert counts.get(Rule.STR_KEY_SUBSCRIPT, 0) == 0
    assert counts.get(Rule.STR_KEY_GET, 0) == 0


def test_configured_receivers_are_maps_not_records() -> None:
    source = textwrap.dedent(
        """
        d = elem.attrib["d"]
        length = self.headers.get("Content-Length")
        title = row["title"]
        """
    )
    counts = count_source(source, frozenset({"*.attrib", "self.headers"}))
    assert counts[Rule.STR_KEY_SUBSCRIPT] == 1
    assert counts.get(Rule.STR_KEY_GET, 0) == 0


def test_loader_aliases_reused_across_functions_are_all_counted() -> None:
    counts = _count(
        """
        def a(text):
            from json import loads as parse
            return parse(text)

        def b(text):
            import yaml as parse
            return parse.safe_load(text)
        """
    )
    assert counts[Rule.JSON_LOADS] == 1
    assert counts[Rule.YAML_LOAD] == 1


def test_long_module_threshold_is_configurable() -> None:
    assert count_source("x = 1\n" * 11, long_module_lines=10)[Rule.LONG_MODULE] == 1
    assert count_source("x = 1\n" * 10, long_module_lines=10).get(Rule.LONG_MODULE, 0) == 0


def test_disabled_rules_never_gate() -> None:
    actual = {Rule.NOQA: {"a.py": 3}, Rule.CAST: {"a.py": 1}}
    found = violations(actual, RatchetConfig(disabled_rules=frozenset({Rule.NOQA})))
    assert [v.rule for v in found] == [Rule.CAST]


@pytest.mark.parametrize(
    ("source", "expected"),
    [
        ("class FakeGh: ...", 1),
        ("class _FakeGh: ...", 1),
        ("class Stub(Base): ...", 1),
        ("class StubRunner: ...", 1),
        ("class Fakery: ...", 0),
        ("class Stubborn: ...", 0),
        ("class GhFake: ...", 0),
        ("def FakeGh(): ...", 0),
    ],
)
def test_fake_and_stub_classes_are_counted(source: str, expected: int) -> None:
    assert _count(source).get(Rule.FAKE_LOCATION, 0) == expected


@pytest.mark.parametrize(
    ("source", "expected"),
    [
        ("from unittest import mock\nmock.patch('a.b')", 1),
        ("from unittest import mock\nmock.patch.object(A, 'b', 1)", 1),
        ("from unittest.mock import patch\n@patch('a.b')\ndef f(): ...", 1),
        ("from unittest.mock import patch as p\nwith p.dict('os.environ', {}): ...", 1),
        ("import unittest.mock\nunittest.mock.patch('a')", 1),
        ("def test(monkeypatch):\n    monkeypatch.setattr(mod, 'f', g)", 1),
        ("import sys\nsys.modules['x'] = object()", 1),
        ("from unittest.mock import patch", 0),
        ("def test(monkeypatch):\n    monkeypatch.setenv('A', '1')\n    monkeypatch.setitem(d, 'k', 1)", 0),
        ("import sys\nm = sys.modules['x']\nsys.modules.get('y')", 0),
        ("setattr(obj, 'a', 1)", 0),
        ("from unittest.mock import MagicMock\nm = MagicMock()", 0),
    ],
)
def test_patching_is_counted(source: str, expected: int) -> None:
    assert _count(source).get(Rule.PATCHING, 0) == expected
