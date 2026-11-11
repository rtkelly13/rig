# Python types: named shapes only

Every data shape in `src/` gets a named class. Anonymous dicts and positional tuples are being burned down. CI blocks
new fixed tuples, `NamedTuple`, explicit and inferred `Any` (including `dict[str, Any]`), untyped dict records read
by string key and the other gated rules below. The full plan, with the starting counts and hotspots, is the
[Shared-Utilities Type Soup](https://claude.ai/artifact/V1Cr9kS5J1PwFDkfFCcNde) page.

## Banned

- `dict[str, Any]`, `Mapping[str, Any]`, and bare `dict` or `list` used as a record.
- `dict[str, object]`, `Mapping[str, object]`, `MutableMapping[str, object]` and the `JsonObject` aliases, outside the
  output serializers and foreign-config round-trips in `loose-dict-allow`.
- Fixed-length tuples in any annotation: `tuple[A, B]`, `-> tuple[X, Y]`, `list[tuple[str, int]]`, tuple dict keys.
- `NamedTuple` and `namedtuple`. They still allow positional unpacking and index access.
- Explicit `Any` outside a boundary module, and any value whose inferred type is `Any`.
- Reading records by string key (`row["title"]`, `.get("gid")`) past the boundary that decodes the data.

## Allowed

- `@dataclass(slots=True, frozen=True)` for every record and every multi-value return. This is the default.

- A typed decode at each I/O boundary (`gh` JSON, YAML config, SQL rows, Win32 calls) that turns raw data into those
  classes. Use the `shared_utilities.typed_json` and `shared_utilities.typed_yaml` helpers, which validate straight
  into plain dataclasses with pydantic:

  ```python
  policy = decode_yaml(PolicyConfig, text)
  pr = try_decode_json(PullRequest, stdout)
  ```

- `tuple[T, ...]` as an immutable sequence of one type. Frozen dataclass fields need it.

- Dicts with a real key and value type: `dict[TrackId, list[Track]]`. A frozen dataclass is hashable, so it can
  replace a tuple key.

- A recursive `JsonValue` alias for payloads that really are free-form and passed through unchanged.

- `JsonObject` (`dict[str, JsonValue]`) or `dict[str, object]` only where a module builds JSON output from typed
  records (`to_dict`, `to_json`, OTLP or API payloads) or rewrites a config file another tool owns and must keep its
  unknown keys. Those modules are listed in `loose-dict-allow`. Everywhere else, decode into a dataclass.

## Decoding patterns

### `Lenient[T]` for formats you do not own

`typed_json.Lenient[T]` is `T | None` with a `WrapValidator` (`or_none`) that turns a field that fails validation
into `None` instead of failing the whole record. That covers any `ValidationError` from `T`, so a failed constraint
or custom validator is dropped too, not only a wrong type. Decode strictly by default: a config or plan we write ourselves should
fail loudly when it is wrong. Use `Lenient` for data another tool writes and may change under us (Claude Code
transcripts, Chrome traces, the jobs state file), where one odd field should cost that field, not the record. Keep
the fields the record cannot work without strict, so a line that lacks them is still rejected:

```python
@dataclass(frozen=True, slots=True)
class _Registration:
    """One `sessions/<pid>.json` registry file; pid and session id are required."""

    pid: StrictInt
    session_id: Annotated[StrictStr, Field(alias="sessionId")]
    cwd: Lenient[StrictStr] = None
    status: Lenient[StrictStr] = None
```

Pair it with `Strict*` types so `"3"` reads as absent rather than being coerced to `3`, and give every `Lenient`
field a `None` default so a missing key is absent too.

### Readable messages from a `ValidationError`

A raw `ValidationError` is fine in a log but not as an answer to someone who hand-edited a file. Decode
`error.errors()` into a small dataclass, take the first error's `loc[0]` as the field name, and map known fields
to a sentence that says what is expected. `desktop_manager.claude_sessions.plan._decode` does this:

```python
except ValidationError as error:
    first = decode(list[_ErrorDetail], error.errors())[0]
    field = str(first.loc[0]) if first.loc else ""
    problem = problems.get(field, f"`{field}`: {first.msg}" if field else "expected an object")
    raise PlanError(f"{where}: {problem}") from error
```

Callers pass the location and the per-field sentences, so a bad group reads as
``groups[2]: `window` must be a window handle from `terminals`, or "new"``. An empty `loc` means the error is about the
whole value (a wrong shape or a model-level check); `_decode` falls back to "expected an object" there. Keep `from error` so the full pydantic detail stays in the traceback.

## How CI enforces it

| Gate | Covers | Allowance |
| --- | --- | --- |
| basedpyright, strict on every file | `reportExplicitAny` (every explicit `Any`, including `dict[str, Any]`), `reportAny` (every value, argument or return whose inferred type is `Any`), plus `reportImplicitOverride`, `reportUnannotatedClassAttribute`, `reportImportCycles`, `reportUninitializedInstanceVariable`, `reportUnusedParameter`, `reportPrivateLocalImportUsage` | none |
| `uv run ty check src` | ty's own checks, warnings as errors, on every platform at once | none |
| `uv run ratchet` | fixed tuples, `NamedTuple`, `cast()`, ignore comments, `noqa`, `getattr`/`hasattr`/`setattr`, modules over 800 lines | none |
| `uv run ratchet` `pyright-pragma` | file-level `# pyright:` comments that switch a rule off or the mode down (`reportX=false`, `basic`) | `[tool.repo-health.ratchet] pyright-pragma-allow` in `pyproject.toml`, empty today |
| `uv run ratchet` `json-loads`, `yaml-load` | any reference to `json.load`/`json.loads` or a `yaml.*load*` function | `[tool.repo-health.ratchet] boundary-modules`: `typed_json.py` and `typed_yaml.py` |
| `uv run ratchet` `isinstance-dict` | `isinstance(x, dict)`, alone or in a tuple | the same `boundary-modules` |
| `uv run ratchet` `str-key-get`, `str-key-subscript` | reads by string literal: `x.get("k")` and a Load-context `x["k"]` | `os.environ`, and `[tool.repo-health.ratchet] str-key-receivers`: `self.headers`, `handler.headers` |
| `uv run ratchet` `loose-dict` | `dict[str, object]`, `Mapping[str, object]`, `MutableMapping[str, object]` and the `loose-dict-aliases` (`JsonObject`) in any annotation, type alias or type argument (`try_decode(dict[str, object], ...)`) | the `boundary-modules`, and `[tool.repo-health.ratchet] loose-dict-allow`: the JSON output serializers and foreign-config round-trips |
| `uv run ratchet` `fake-location`, `patching` (tests/ only) | `Fake*`/`Stub*` classes outside `tests/fakes/`; `mock.patch`, `monkeypatch.setattr` and `sys.modules[...] =` | `[tool.repo-health.ratchet] fake-location-allow` and `patching-allow`: today's files, shrinking ([testing.md](testing.md#shared-fakes)) |
| ruff `BLE001` | `except Exception` | none |
| ruff `DTZ`, `S602`, `PGH004` | naive datetimes, `shell=True`, blanket `noqa` | none |

Parse JSON and YAML through `shared_utilities.typed_json` (`decode_json`, `try_decode_json`, `load_json`) and
`shared_utilities.typed_yaml` (`decode_yaml`, `try_decode_yaml`, `load_yaml`). Those two modules are the only boundary
modules allowed to touch `json.load`, `json.loads` or any `yaml.*load*` function; a reference anywhere else, even one
assigned to a variable, fails the ratchet.

Decode a record into a frozen dataclass and read it by attribute instead of by string key. Writes such as
`out["k"] = v` build a payload and do not count, and `Literal["k"]` is a type. `str-key-receivers` is for receivers
that really are homogeneous string-keyed maps rather than records: an exact dotted name, or `*.attr` for any `x.attr`.
Keep it short and say why in the PR that adds an entry. `self.headers` is there because `BaseHTTPRequestHandler`
headers are a case-insensitive `email.message.Message`, which a dataclass decode cannot match by name. `uv run ratchet
--report` prints every rule's total.

`loose-dict` catches decoding into `dict[str, object]` and then picking it apart by hand. Decode into a dataclass
instead: `dict[str, Lenient[Record]]` for a map of records, a `BeforeValidator` for an odd shape. A Jinja context
becomes a dataclass too, since templates read attributes and keys the same way. `loose-dict-allow` lists whole files,
so add one only when the loose dict is the module's job: building JSON output from typed records, or editing a config
file another tool owns (Claude `settings.json`, the opencode config, Obsidian plugin data) where unknown keys must
survive. If only one function in a module needs it, move that function to a serializer module rather than listing
the module.

`uv run ratchet` is the `type-ratchet` package in `packages/type-ratchet`; see its
[README](../packages/type-ratchet/README.md) for every rule and config key.

There are no baselines. A new occurrence of a gated rule fails CI in the PR that adds it, so fix it there rather than
suppressing it.

Where a third-party COM, ctypes or C-extension surface is untyped, type it instead of silencing it: install the
`types-*` package, or add a minimal stub under `typings/` (basedpyright's `stubPath`) that declares only the members
the code calls, or route the call through a small typed adapter such as `desktop_manager.win32.dlls.system_dll`.
File-level `# pyright: reportX=false` pragmas are gated by the ratchet's `pyright-pragma` rule. If one truly cannot
be avoided, keep it in one tiny adapter module, add that path to `pyright-pragma-allow` and justify it in the PR. For ty, which is not configured to read `typings/`, the modules listed in `[tool.ty.analysis] replace-imports-with-any` are `Any`; add a Windows-only module there when ty cannot resolve it on the Linux runner.

`reportAny` catches `Any` that a library hands back. Decode JSON and other payloads with `typed_json` instead of
indexing them. For a foreign function that returns `Any` (ctypes, `winreg`, `np.gradient`), pass it through a typed
`Callable` parameter so the result comes back typed: `desktop_manager.win32.dlls.call_int` and `registry_value`, and
`image_drift.numeric` (tools/image-drift). Annotating the variable (`x: object = untyped()`) does not help, because the
narrowed type is still `Any`.

Catch what the guarded code can raise, not `Exception`. Windows calls have shared tuples: `WIN32_ERRORS`
and `PYVDA_ERRORS` in `desktop_manager.win32.errors` (wrap pyvda calls in `pyvda_errors()`, which turns
pyvda's bare `Exception` into a catchable one) and `UIA_ERRORS` in `claude_sessions.wt_uia`. A crash barrier
that must keep a long-running process alive (a restore entry, a scheduled job) may still catch `Exception`, but
only if it logs the traceback (`logger.exception(...)` or `logger.error(..., exc_info=True)`) or re-raises.
