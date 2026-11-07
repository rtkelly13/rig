# type-ratchet

A zero-baseline gate against "type soup" in Python. `ratchet` parses every git-tracked `*.py` file under the
configured paths and fails on any occurrence of the rules below, outside the exemptions you configure. There is no
baseline file: a new occurrence fails the change that adds it.

It has no dependencies beyond the standard library and needs Python 3.12+ and `git`.

## Install and run

From git, without installing anything into the project:

```sh
uvx --from "git+https://github.com/rtkelly13/shared-utilities#subdirectory=packages/type-ratchet" ratchet --report
```

Pin a commit or tag for CI by adding `@<ref>` after the repo URL:

```sh
uvx --from "git+https://github.com/rtkelly13/shared-utilities@<ref>#subdirectory=packages/type-ratchet" ratchet
```

Or add it as a dev dependency so `uv run ratchet` works:

```sh
uv add --dev "type-ratchet @ git+https://github.com/rtkelly13/shared-utilities#subdirectory=packages/type-ratchet"
```

`ratchet` reads `pyproject.toml` at the root of the enclosing git work tree; `--root PATH` points it elsewhere.
`--report` also prints each rule's total. It exits 0 when clean, 1 on violations and 2 on a bad config or when it
is not run inside a git work tree.

## Rules

| Rule | Catches |
| --- | --- |
| `fixed-tuple` | fixed-length tuples in annotations (`tuple[A, B]`, `Tuple[int]`, quoted ones too), `NamedTuple` and `namedtuple`. `tuple[T, ...]` is allowed. |
| `cast` | `typing.cast(...)`, however it is imported or aliased |
| `ignore-comment` | `# type: ignore` and `# pyright: ignore` |
| `noqa` | `# noqa` comments |
| `getattr-family` | `getattr`, `hasattr` and `setattr` calls |
| `long-module` | a module with more than `long-module-lines` lines (800 by default) |
| `str-key-get` | `x.get("literal")`, except on `os.environ` and the `str-key-receivers` |
| `str-key-subscript` | reads such as `x["literal"]`, except on `os.environ`, the `str-key-receivers` and `Literal["..."]`; writes and `del` do not count |
| `isinstance-dict` | `isinstance(x, dict)`, alone or in a tuple of types |
| `json-loads` | any reference to `json.load` or `json.loads`, including aliases and assignments |
| `yaml-load` | any reference to a `yaml.*load*` function |
| `pyright-pragma` | a file-level `# pyright:` comment that lowers the mode (`basic`, `standard`, `off`) or switches a rule down (`reportX=false`, `warning`, ...) |
| `loose-dict` | `dict[str, object]`, `Mapping[str, object]` and `MutableMapping[str, object]` (from `typing` or `collections.abc`, however imported), and any read of a `loose-dict-aliases` name: in annotations, type aliases, quoted annotations and type arguments such as `decode(dict[str, object], raw)` or `default_factory=dict[str, object]`. The alias's own definition does not count. |
| `fake-location` | a class named `Fake*` or `Stub*` (leading underscores too, so `_FakeGh`), outside the `fake-location-allow` paths. Meant for a tests rule set, so fakes live in one shared place |
| `patching` | `mock.patch` in any form (`patch`, `.object`, `.dict`, decorator or context manager, however `unittest.mock` is imported), `monkeypatch.setattr`, and writes to `sys.modules[...]`, outside the `patching-allow` paths |

`json-loads`, `yaml-load`, `isinstance-dict` and `loose-dict` are boundary rules: the `boundary-modules`, where raw
data is decoded into typed shapes, may break them. `pyright-pragma` is exempt only in `pyright-pragma-allow` paths,
and `loose-dict`, `fake-location` and `patching` also in their `*-allow` paths. Every other rule has no per-file
exemption.

## Config

Put the settings in `[tool.type-ratchet]` in the target repo's `pyproject.toml`. The older
`[tool.repo-health.ratchet]` table is read when `[tool.type-ratchet]` is absent; setting both is an error, as is an
unknown key. Every key is optional.

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `paths` | list of strings | `["src"]` | repo-relative directories whose tracked `*.py` files are checked; `"."` means the whole repo |
| `disable` | list of rule names | `[]` | rules still counted by `--report` but never failing the gate |
| `long-module-lines` | positive integer | `800` | the `long-module` threshold |
| `boundary-modules` | list of paths | `[]` | repo-relative files that may break the boundary rules |
| `pyright-pragma-allow` | list of paths | `[]` | repo-relative files that may keep a relaxing file-level pyright pragma |
| `str-key-receivers` | list of strings | `[]` | receivers that are homogeneous string-keyed maps, not records: an exact dotted name (`self.headers`) or `*.attr` for any `x.attr` |
| `loose-dict-aliases` | list of strings | `[]` | type alias names `loose-dict` counts like `dict[str, object]`, matched on the last segment of the resolved name, so `JsonObject`, `typed_json.JsonObject` and `JsonObject as Obj` all count |
| `loose-dict-allow` | list of strings | `[]` | repo-relative paths or `fnmatch` globs (`*` also matches `/`) that may keep loose dicts, such as JSON output serializers and config files rewritten with their unknown keys |
| `fake-location-allow` | list of strings | `[]` | repo-relative paths or `fnmatch` globs that may define `Fake*`/`Stub*` classes |
| `patching-allow` | list of strings | `[]` | repo-relative paths or `fnmatch` globs that may patch: today's files, while they move to fakes |
| `hint` | string | `"Remove them."` | printed after the violations, for example a pointer to your typing guide |
| `rule-sets` | array of tables | `[]` | per-path rule choices; see below |

```toml
[tool.type-ratchet]
paths = ["src", "tools"]
disable = ["noqa"]
long-module-lines = 600
boundary-modules = ["src/app/decode.py"]
str-key-receivers = ["self.headers", "*.attrib"]
loose-dict-aliases = ["JsonObject"]
loose-dict-allow = ["src/app/serialize.py"]
hint = "See docs/python-types.md for the replacements."
```

### Rule sets

Different parts of a repo can gate different rules: tests, say, may patch and cast where `src/` may not. Each
`[[tool.type-ratchet.rule-sets]]` entry names `files`, a list of repo-relative `fnmatch` globs (`*` also matches `/`),
and `rules`, the rules that fail the gate in those files. Both must be non-empty.

A file takes the first set whose `files` match it; order the sets from most to least specific. A file no set matches
gets every rule except `disable`. Set-matched files are checked even outside `paths`, so a set for `tests/*` brings the
tracked tests in. `--report` still counts every rule everywhere, and the exemptions above still apply.

```toml
[tool.type-ratchet]
paths = ["src"]

[[tool.type-ratchet.rule-sets]]
files = ["tests/fakes/*"]
rules = ["patching"]

[[tool.type-ratchet.rule-sets]]
files = ["tests/*"]
rules = ["fake-location", "patching"]
```

Here fakes may be defined only in `tests/fakes/`, and nothing under `tests/` patches. Add `disable =
["fake-location", "patching"]` when application code may legitimately write `sys.modules`.
