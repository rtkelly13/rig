# Typed System.Text.Json in place of hand-walked JsonNode readers

Evaluation for #344, part of #85. Decision and migration order at the end. Everything below the capability and exception
tables was measured by compiling and running source-generated contexts, not read from documentation.

## What was built

One shared strict boundary, `Rig.Core.Text.JsonProblem.Read<T>(text|element, JsonTypeInfo<T>)`, plus a translator that turns
a `JsonException` into the existing `JsonInputException` (`source: $.path: expected X, found Y`). Two kinds of
`JsonSerializerContext` sit on it:

- Rig-owned: `Strict` defaults, `UnmappedMemberHandling = Disallow`, `AllowDuplicateProperties = false`, respect nullable
  annotations and required constructor parameters. See `RunHistoryJsonContext`.
- Foreign: the same, but `Skip` and `AllowDuplicateProperties = true` (last wins). Fields rig reads are still strictly
  typed. See `OtlpJsonContext`.

Shared converter: `NonEmptyStringConverter` (carries its own `IJsonPhrase` so the translator can say "a non-empty string").
Per-property int64-from-string uses `[JsonNumberHandling(AllowReadingFromString)]`, never a global option.

Two readers were converted: OTLP (`TraceSpans`, `OtlpJson`) and `RunHistory`.

## Inventory

126 files mention JSON reading or writing (`temp/inv.txt` in the worktree; hit count and line count per file). Classified by
a mechanical rule (read-API density plus whether the file already calls a typed `Deserialize`) then by what owns the
format. This is a heuristic, not a per-file audit; the migration slices below name files so each is re-checked when picked
up. Line counts are whole-file lines, so they overstate the reader code.

| Class | Files | Whole-file lines | Notes |
|---|---:|---:|---|
| 1 Reader of a foreign format (GitHub, Vercel, Orca, Chrome, OTLP, registries) | 48 | 14,536 | Skip unknown members; strict on fields read |
| 2 Reader of a rig-owned file (config, state, plans, queue) | 45 | 14,542 | Disallow unknown members |
| 3 Shared helper (`Text/Json*`) | 4 | 426 | Shrinks as slices land |
| 4 Writer or incidental (serialise only, or already typed) | 29 | 6,687 | Nothing to do |

## Capability table (verified)

| Behaviour | STJ under source generation | Needs |
|---|---|---|
| Reject unknown member (rig-owned) | Yes, `UnmappedMemberHandling.Disallow` | none |
| Ignore unknown member (foreign) | Yes, `Skip` | none |
| Reject duplicate member | Yes, `AllowDuplicateProperties = false` | none |
| Last duplicate wins (foreign) | Yes, `AllowDuplicateProperties = true` for typed members and `JsonElement` | `JsonObject` throws `ArgumentException` on duplicate keys, so the last-wins node builder stays |
| Missing required member | Yes (`required` / ctor params) | name only in message text |
| Null into non-nullable | Yes for properties and ctor params with `RespectNullableAnnotations` | **Hole**: null elements of collections of classes are accepted. Struct DTO elements reject null |
| Wrong token type | Yes, `JsonException` with `Path` at the value | translator names expected/found |
| No coercion (`"12"`, `12.0`, `true` into int) | Yes with `Strict` | none |
| String-to-number on one property | Yes, `[JsonNumberHandling(AllowReadingFromString)]` | translator phrase "a whole number or a string of digits" |
| Custom per-field rule | Yes, `[JsonConverter]`; its exception message passes through with `Path` at the property | `NonEmptyStringConverter` |
| Exact path | Yes: `$.a[0].b`, `['odd key']` forms | translator parses it |
| Dictionary keys and values | Yes; path names the key | none |
| Optional member keeps its initializer | Only with `{ get; set; }`. An absent `init` member built by the generated constructor path is assigned null | use `set` for optional members, `required`/`init` only for required |
| Per-element recovery (skip one span, keep the line) | No: one failure aborts the whole `Deserialize` | keep the collection as `List<JsonElement>` and decode each element |
| Syntax errors | `JsonException` without a typed path | translator re-parses with `JsonDocument` to tell syntax from shape |
| Locale-independent messages | No, English text | translator needs prefixes for three kinds only (below) |

## Exception table (raw STJ output)

| Rejection | `Message` (prefix) | `Path` points at |
|---|---|---|
| Unmapped member | `The JSON property 'x' could not be mapped to any .NET member contained in type 'T'.` | the member |
| Duplicate member | `Duplicate property 'job' encountered during deserialization of type 'Env'.` | the member |
| Missing required | `JSON deserialization for type 'X' was missing required properties including: 'job'.` | the parent |
| Null into non-nullable ctor parameter | `The constructor parameter 'Job' on type 'Env' doesn't allow null values...` | the property |
| Wrong token or type | `The JSON value could not be converted to X.` | the value |
| Custom converter throws | converter's own message | the property |

Only the first three need message matching in the translator; everything else is derived from `Path` plus the metadata
(`JsonTypeInfo.Properties`, `Kind`, converters, `NumberHandling`) and the offending `JsonElement`. If a message prefix
changes in a future runtime the fallback is `source: $.path: <parser's words>`, never a crash, and
`JsonProblemTypedTests` fails first.

## Prototype LOC

| File | Before | After | Delta |
|---|---:|---:|---:|
| `Jobs/RunHistory.cs` | 290 | 253 | -37 |
| `Otel/OtlpJson.cs` | 149 | 115 | -34 |
| `Otel/TraceSpans.cs` | 306 | 258 | -48 |
| New: `JsonProblem.Typed.cs` (226), `OtlpJsonContext.cs` (94), `RunHistoryJsonContext.cs` (35) | 0 | 355 | +355 (translator is the shared cost) |
| New tests (184 + 80) | 0 | 264 | +264 |

Net `git diff --shortstat origin/322-strict-json-vault-ops` is in the PR body. The three reader files shrink by 119 lines. The new code is larger than what it removed (+355 source, +264 test): the translator
(226 lines) is paid once and each further reader should delete far more than the context it adds, which is the case to test in
slice 1.

## Changed messages and behaviour

All existing assertions and fixtures pass unmodified (Otel, Jobs, Text, Chrome, Observability namespaces, Web
`JobsRoutesTests`/`McpTests`). Differences found by snapshotting old and new on a corpus of good and bad lines:

1. `RunHistory` rejects a line with an unknown field (`$.unknown: expected a known field, found the number 1`) or a
   duplicate (`$.job: expected a field that appears once, found it repeated`). The old reader accepted both. This is the
   forward-compatibility cost of `Disallow` on a rig-owned file: a newer rig writing a new field makes older rigs skip
   those lines. Either choose `Skip` for history files (recommended while the format is still growing) or version the
   field set. Decision for the controller.
2. OTLP: `{"spans":[good, 5]}` now keeps the good span and reports `spans[1]: expected an object, found the number 5`; the
   old code dropped the whole line.
3. An early prototype typed every attribute and widened strictness to attributes other than `span.incomplete`; that
   broke `TraceAnalyticsTests.A_span_with_a_field_of_the_wrong_type_is_skipped_and_says_where`. Attribute `key` and
   `value` are now raw and decoded only for the `span.incomplete` key, which restores the old behaviour exactly. No
   existing test was edited. My own new cases moved to the `span.incomplete` key.

## Rough timing

Stopwatch, a few runs, shared machine at low priority, so noise is large:

| Workload | Hand-walked | Typed |
|---|---|---|
| 8,000 OTLP spans (3.2 MB) | about 120-230 ms | about 215-320 ms |
| 2,000 RunHistory lines | about 5-16 ms | about 4.5-42 ms |

OTLP is slower by roughly 1.5x because each span is parsed twice (the line, then the span element). RunHistory is
indistinguishable inside the noise. Neither matters at these sizes (a trace file is read once per command); this is a
cost to name, not a blocker. Decoding spans straight into a typed element list would remove the second pass at the price
of per-span recovery.

## Output validation, both senses

- Input: the strictness tests prove `"12"`, `12.0` and `true` are rejected where an integer is required, and that string
  digits are accepted only on the properties marked for it (`RunHistoryStrictnessTests`, `JsonProblemTypedTests`).
- Output: `RunHistory.Line` (writer) is unchanged and the existing round-trip test reads what it writes through the new
  reader. The context for each format is the single place its member names and casing are declared, so a writer that
  moved to the same context would make the schema and the writer unable to drift. That move was not made here.

## Where STJ loses

- Position recovery: one bad element aborts the whole read. Per-element recovery needs `List<JsonElement>` and a second
  parse (the 1.5x above) or a hand reader.
- Messages are English and only partly structured. Three kinds need text matching; missing-required names exist only in
  the message. Not locale-safe, not guaranteed stable across runtimes.
- The nullable-collection-of-class hole forces struct element DTOs, which is an odd shape to explain to the next reader.
- `init`-only optional members silently become null; `{ get; set; }` is required for optional members, which is the
  opposite of the usual immutability habit and easy to get wrong. A test guards it.
- Last-duplicate-wins into a `JsonObject` is not supported; the node builder over `JsonElement` stays for passthrough.
- Dynamic shapes (AnyValue unions, `anyOf`, free-form maps) get no benefit: they are `JsonElement` either way and still
  need a hand decoder. `OtlpJson.AnyValue` remains for `TraceAnalytics` and `ChromeTraces`.
- Native AOT is intent only (ADR 0023); source generation helps there but is not the reason.

## Recommendation

Adopt typed STJ with the shared boundary for fixed-shape readers, foreign and rig-owned, and keep hand decoding only for
union-shaped values and passthrough nodes. The readers get shorter, the schema becomes data (a record), strictness is
declared once per format kind, and the error contract is preserved by one translator that is itself tested against the
raw exception table above. Do not migrate dynamic or write-only code. Gate each slice on: existing tests unmodified, new
strictness tests for each read field, and a snapshot diff of old versus new messages listed in the PR.

## Migration order

Slices are PR-sized (roughly 300-700 lines of reader code each). Hit counts from `temp/inv.txt` are the sizing guide;
re-check each file for union shapes before starting.

1. Remaining OTLP and trace readers: `Otel/Backfill.cs`, `Otel/ReplayOffsets.cs`, `Otel/ClickHouseClient.cs`,
   `Observability/TraceAnalytics.cs` (moves `OtlpJson.AnyValue` onto a typed union converter), `Chrome/ChromeTraces.cs`.
   Finishes the format already begun.
2. Jobs and state: `Jobs/JobState.cs`, `Jobs/JobsSite.cs`, `Estate/WrapupState.cs`, `Estate/DecisionFeed.cs`,
   `Estate/ShelfStore.cs`, `Workspace/MachineConfig.cs`. Rig-owned; decide Skip versus Disallow first (finding 1).
3. Orca: `Orca/OrcaJson.cs`, `OrcaProjects.cs`, `OrcaRuntime.cs`, `OrcaState.cs`, `OrcaHooks.cs`, `OrcaPolicy.cs`.
4. GitHub: `GitHub/PrResolver.cs`, `GitHubRestRequest.cs`, `RepoResolver.cs`, `Work/Prs.cs`, `Work/WorkJson.cs`,
   `Estate/PullRequests.cs`.
5. Packages: `Packages/LivePackageFetcher.cs` (largest single reader), `LiveRegistryInfo.cs`, `PackageBindings.cs`.
6. Governance A: `Governance/Rulesets.cs`, `Triage.cs`, `Environments.cs`, `Resources.cs`, `Labels.cs`, `Pages.cs`.
7. Governance B (Vercel): `VercelGating.cs`, `VercelProjects.cs`, `VercelApi.cs`, `GitHubApps.cs`, `RemoteAgentFiles.cs`.
8. Desktop plans: `Desktop/PlanJson.cs`, `PlanModels.cs`, `PlanFiles.cs`, `WindowPidFile.cs`.
9. Review queue: `ReviewQueue/QueueSnapshot.cs`, `QueueJson.cs`, `QueueGitHub.cs`.
10. Chrome and JXA: `Chrome/MacOS/JxaModels.cs`, `ChromeJxa.cs`, `Chrome/SiteHosts.cs`, `Cli/Chrome/ChromeTabsCommand.cs`.
11. Devenv and config: `Devenv/ObsidianPlugins.cs`, `ClaudeBaseline.cs`, `SessionLedger.cs`, `OpencodeDefaults.cs`,
    `ToolCatalog.cs`, `SettingsFile.cs`, `Config/*`.
12. Everything else in class 1 and 2 (single-hit files, CLI commands), then delete `JsonShape` and the dead parts of
    `JsonProblem` last.

Each slice deletes the hand reader it replaces in the same PR.
