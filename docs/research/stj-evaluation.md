# Typed System.Text.Json in place of hand-walked JsonNode readers

Evaluation for #344, part of #85. Decision and migration order at the end. Everything below the capability and exception
tables was measured by compiling and running source-generated contexts, not read from documentation.

## What was built

One shared strict boundary, `Rig.Core.Text.JsonProblem.Read<T>(text|element, JsonTypeInfo<T>)`, plus a translator that turns
a failure into the existing `JsonInputException` (`source: $.path: expected X, found Y`). Two kinds of
`JsonSerializerContext` sit on it:

- Rig-owned: `Strict` defaults, `UnmappedMemberHandling = Skip` (a newer rig may add fields), `AllowDuplicateProperties = false`,
  respect nullable annotations and required constructor parameters, and an integer `version` member (see Format version).
  See `RunHistoryJsonContext`.
- Foreign: the same, but `Skip` and `AllowDuplicateProperties = true` (last wins). Fields rig reads are still strictly
  typed. See `OtlpJsonContext`.

Shared converter: `NonEmptyStringConverter` (carries its own `IJsonPhrase` so the translator can say "a non-empty string").
Per-property int64-from-string uses `[JsonNumberHandling(AllowReadingFromString)]`, never a global option.

Two readers were converted: OTLP (`TraceSpans`, `OtlpJson`) and `RunHistory`.

## Inventory

126 files hand-walk JSON (per-file table in the appendix: hit count, line count, class). The split is the one the issue
asks for, reader or writer by rig-owned or foreign format. It was computed on the base tree
(`origin/322-strict-json-vault-ops`), so the three files converted here (`RunHistory`, `OtlpJson`, `TraceSpans`) count as
readers. A file is a reader when it has three or more read-API hits (`JsonNode`, `JsonElement`, `JsonDocument`,
`Utf8JsonReader`, `GetProperty`, `JsonProblem`, `JsonShape`), otherwise a writer or incidental user. Owner is by
directory plus a spot check of the top files in each class: GitHub, Vercel, Orca application state, Chrome, OTLP,
registries, Gmail and project manifests are foreign; rig config, policy, plans, queue, jobs, wrap-up and estate state are
rig-owned. Several readers also write (the appendix shows write hits). Line counts are whole-file, so they overstate the
reader code; read hits are the better sizing guide.

| Reader or writer | Owner | Files | Whole-file lines |
|---|---|---:|---:|
| reader | owned | 45 | 15196 |
| reader | foreign | 49 | 14171 |
| reader | rig-owned helper | 4 | 426 |
| writer | owned | 18 | 4071 |
| writer | foreign | 9 | 2271 |
| writer | rig-owned helper | 1 | 55 |



## Capability table (verified)

| Behaviour | STJ under source generation | Needs |
|---|---|---|
| Reject unknown member (where a type chooses `Disallow`) | Yes, `UnmappedMemberHandling.Disallow` | rig's own pre-pass names the member (no message read) |
| Ignore unknown member (foreign) | Yes, `Skip` | none |
| Reject duplicate member | Yes, `AllowDuplicateProperties = false` | none |
| Last duplicate wins (foreign) | Yes, `AllowDuplicateProperties = true` for typed members and `JsonElement` | `JsonObject` throws `ArgumentException` on duplicate keys, so the last-wins node builder stays |
| Missing required member | Yes (`required` / ctor params) | rig's pre-pass reads `JsonPropertyInfo.IsRequired` and names it |
| Null into non-nullable | Yes for properties and ctor params with `RespectNullableAnnotations` | **Hole**: null elements of collections of classes are accepted. Struct DTO elements reject null |
| Wrong token type | Yes, `JsonException` with `Path` at the value | translator names expected/found |
| No coercion (`"12"`, `12.0`, `true` into int) | Yes with `Strict` | none |
| String-to-number on one property | Yes, `[JsonNumberHandling(AllowReadingFromString)]` | translator phrase "a whole number or a string of digits" |
| Custom per-field rule | Yes, `[JsonConverter]`; its exception message passes through with `Path` at the property | `NonEmptyStringConverter` |
| Exact path | Yes: `$.a[0].b`, `['odd key']` forms | translator parses it |
| Dictionary keys and values | Yes; path names the key | none |
| Optional member keeps its initializer | Only with `{ get; set; }`. An absent `init` member built by the generated constructor path is assigned null | use `set` for optional members, `required`/`init` only for required |
| Per-element recovery (skip one span, keep the line) | No: one failure aborts the whole `Deserialize` | keep the collection as `List<JsonElement>` and decode each element |
| Polymorphism | Yes, `[JsonPolymorphic]` + `[JsonDerivedType]` under source generation; unknown member in a derived type is rejected with the derived type's name; unknown discriminator is a `JsonException` (`Read unrecognized type discriminator id 'bird'.`, Path `$`) | **A missing discriminator throws `NotSupportedException`, not `JsonException`** (message `... must specify a type discriminator.`, no `Path` property), so a translator must catch both |
| .NET 10 strict preset | Yes, `JsonSerializerDefaults.Strict`. Printed from the runtime: `UnmappedMemberHandling=Disallow`, `RespectNullableAnnotations=true`, `RespectRequiredConstructorParameters=true`, `AllowDuplicateProperties=false`, `NumberHandling=Strict`, no comments, no trailing commas, case-sensitive names | The foreign context overrides `UnmappedMemberHandling` and `AllowDuplicateProperties`; the explicit flags in both contexts are kept so `JsonBoundaryTests`-style checks stay meaningful |
| JSON schema for what rig writes | Yes, `JsonSerializerOptions.GetJsonSchemaAsNode(type)` works against a source-generated context: `{"type":["object","null"],"properties":{"port":{"type":"integer"}},"required":["port"],"additionalProperties":false}`; polymorphic types give `anyOf` with a `const` discriminator | schema carries no ranges |
| Syntax errors | `JsonException` without a typed path | translator re-parses with `JsonDocument` to tell syntax from shape |
| Range and cross-field rules (`port` 1-65535, start before end) | No: `{"port":99999999}` and `{"port":-5}` both deserialize; the schema has no `minimum`/`maximum` | validate after deserialising, on the record (see Output validation) |
| Locale-independent messages | No, English text | not needed: the three structural checks (unknown, duplicate, missing) are done by a typed pre-pass over the `JsonTypeInfo` metadata, so no .NET wording is matched |

## Exception table (raw STJ output)

| Rejection | `Message` (prefix) | `Path` points at |
|---|---|---|
| Unmapped member | `The JSON property 'x' could not be mapped to any .NET member contained in type 'T'.` | the member |
| Duplicate member | `Duplicate property 'job' encountered during deserialization of type 'Env'.` | the member |
| Missing required | `JSON deserialization for type 'X' was missing required properties including: 'job'.` | the parent |
| Null into non-nullable ctor parameter | `The constructor parameter 'Job' on type 'Env' doesn't allow null values...` | the property |
| Wrong token or type | `The JSON value could not be converted to X.` | the value |
| Custom converter throws | converter's own message | the property |
| Missing polymorphic discriminator | `NotSupportedException`: `The JSON payload for polymorphic interface or abstract type 'Pet' must specify a type discriminator.` | none (not a `JsonException`) |
| Unknown discriminator | `Read unrecognized type discriminator id 'bird'.` | the type |

No message is matched. When STJ throws (a `JsonException` or the `NotSupportedException` of a missing discriminator) the
text is re-parsed as a `JsonDocument` and a small pre-pass, driven by the source-generated `JsonTypeInfo` (`Properties`,
`IsRequired`, names, `PolymorphismOptions`, `UnmappedMemberHandling`, `Options.AllowDuplicateProperties`), finds a repeated
member, an unknown member where `Disallow` applies, a missing required member, or a missing or unknown discriminator. If
it finds none, `JsonException.Path` plus the metadata and the offending `JsonElement` give the type mismatch. A syntax
failure is `not valid JSON: ...`. The only runtime wording that can ever appear is the fallback `source: $.path: <parser's
words>`, never a crash.

## Prototype LOC

| File | Before | After | Delta |
|---|---:|---:|---:|
| `Jobs/RunHistory.cs` | 290 | 253 | -37 |
| `Otel/OtlpJson.cs` | 149 | 115 | -34 |
| `Otel/TraceSpans.cs` | 306 | 258 | -48 |
| New: `JsonProblem.Typed.cs` (209), `OtlpJsonContext.cs` (94), `RunHistoryJsonContext.cs` (35) | 0 | 338 | +338 (translator is the shared cost) |
| New tests (184 + 80) | 0 | 264 | +264 |

Net `git diff --shortstat origin/322-strict-json-vault-ops` is in the PR body. The three reader files shrink by 119 lines. The new code is larger than what it removed (+355 source, +264 test): the translator
(209 lines, down from 226 once message matching was replaced by the pre-pass) is paid once and each further reader should delete far more than the context it adds, which is the case to test in
slice 1.

## Changed messages and behaviour

All existing assertions and fixtures pass unmodified (Otel, Jobs, Text, Chrome, Observability namespaces, Web
`JobsRoutesTests`/`McpTests`). Differences found by snapshotting old and new on a corpus of good and bad lines:

1. `RunHistory` now rejects a line with a repeated member (`$.job: expected a field that appears once, found it repeated`);
   the old reader accepted it. Unknown fields are ignored, as the old reader did (decision: rig-owned formats use `Skip`, so
   a newer rig's extra field does not make older rigs skip lines). A line with no `version` reads as 1; a `version` above
   the build's `RunHistory.FormatVersion` (or below 1) is skipped as `$.version: expected a version from 1 to N, found the
   number M`; a non-integer `version` is a type error. These version rules are new.
2. OTLP: `{"spans":[good, 5]}` now keeps the good span and reports `spans[1]: expected an object, found the number 5`; the
   old code dropped the whole line.
3. An early prototype typed every attribute and widened strictness to attributes other than `span.incomplete`; that broke
   `TraceAnalyticsTests.A_span_with_a_field_of_the_wrong_type_is_skipped_and_says_where`. Attribute `key` and `value` are
   now raw and decoded only for the `span.incomplete` key. Verified by re-running the old/new snapshot on the final
   build: every attribute case in the corpus (key 5, value 5 on another key, boolValue "x" on another key, `attributes:
   [5]`, `[null]`, `span.incomplete` with value 5, null, `{"boolValue":1}`, `"yes"` and absent) is byte-identical to
   the old reader. The only differences in the whole corpus are items 1 and 2. No existing test was edited; my own cases
   moved to the `span.incomplete` key.

## Rough timing

Stopwatch, a few runs, shared machine at low priority, so noise is large:

| Workload | Hand-walked | Typed |
|---|---|---|
| 8,000 OTLP spans (3.2 MB) | about 120-230 ms | about 140-280 ms (final build, six rounds) |
| 2,000 RunHistory lines | about 5-16 ms | about 4.5-42 ms |

OTLP is slower by roughly 1.2-1.5x because each span is parsed twice (the line, then the span element). RunHistory is
indistinguishable inside the noise. Neither matters at these sizes (a trace file is read once per command); this is a
cost to name, not a blocker. Decoding spans straight into a typed element list would remove the second pass at the price
of per-span recovery.

## Output validation, both senses

**The JSON rig writes.** `JsonSchemaExporter` (`GetJsonSchemaAsNode`) works against a source-generated context and gives
a usable schema, including `required`, `additionalProperties: false` for `Disallow` contexts and `anyOf` with a `const`
discriminator for polymorphic types (verified above). So a rig-owned format could export its schema from the same context
that reads it, and a test could validate every writer's output against it; that would make reader, writer and schema one
declaration. Not done here: `RunHistory.Line` is still hand-written (it matches serde_json's escaping byte for byte, which
the serializer does not guarantee) and the existing round-trip test reads what it writes through the new reader. A strict
round-trip is the cheaper check and is what exists; the schema check is a follow-up for formats rig writes with the
serializer.

**Values after deserialising.** The serializer checks shape and type only. `{"port":99999999}` and `{"port":-5}` both
deserialize and the exported schema carries no range. Ranges and cross-field rules belong on the record, as a method or a
validating constructor that throws, so the check runs after the translator and can name the same path
(`$.port: expected a whole number from 1 to 65535, found 99999999`). The translator already throws
`JsonInputException` for converter failures; a field converter (like `NonEmptyStringConverter`) is the right home for
single-field rules because the path comes free, and a post-read `Validate()` is the home for cross-field rules. Neither
prototype has a cross-field rule, so none was written; `RunHistory` keeps its tolerant mapping of null output and trace
ids to empty strings in `StoredRun.ToRun()`.

## Where STJ loses

- Position recovery: one bad element aborts the whole read. Per-element recovery needs `List<JsonElement>` and a second
  parse (the 1.5x above) or a hand reader.
- Messages are English and only partly structured, so rig does not read them: unknown, duplicate and missing-required
  are found by the metadata pre-pass. The pre-pass is extra code (it replaced the prefix matching and is smaller), and it
  walks the document a second time, but only on a failure.
- The nullable-collection-of-class hole forces struct element DTOs, which is an odd shape to explain to the next reader.
- `init`-only optional members silently become null; `{ get; set; }` is required for optional members, which is the
  opposite of the usual immutability habit and easy to get wrong.
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

## Decisions

1. **Unknown fields and format version.** Rig-owned formats such as `RunHistory` use `UnmappedMemberHandling.Skip` and carry
   an integer `version` member. A missing `version` reads as 1, so existing files load. A version above the build's maximum
   (or below 1) is rejected with a `JsonInputException` at `$.version`, naming the expected range and the value found.
   Wrong types and missing required members stay errors, and duplicate keys are still rejected. Format version for
   RunHistory: `version` is the first member of each JSONL line, held in `StoredRun.Version` (default 1) and checked by
   `JsonProblem.RequireVersion`. The writer emits it only when `RunHistory.FormatVersion` is above 1: the existing exact-line
   tests and `jobs-oracle.json` pin the version-1 bytes, and a missing version already means 1, so version 1 stays
   byte-identical and the member appears from the first format change.
2. **No English-message matching.** The three structural checks are rig's own, in a pre-pass over the `JsonTypeInfo`
   metadata; a missing polymorphic discriminator (`NotSupportedException`) is translated too.

### Format version convention (boundary for later slices)

Each rig-owned record or file carries an integer `version`, placed first in a JSONL line and at the top level of a JSON
document, with the DTO default `Version = 1` so absence reads as 1. The reader calls `JsonProblem.RequireVersion(stored.Version, Format.Newest)`
before using the record; the writer emits `version` once the format's version is above 1. A format changes version only for
a change an older reader cannot ignore (additions are covered by Skip). Foreign formats (OTLP) have no `version`.

## Migration order

Slices are PR-sized by read hits (hits are the sizing guide; whole-file line counts are in parentheses and overstate the
reader code). Slices 6, 11 and 12 have more than a dozen files: split them by directory when filing. Re-check each file for
union shapes (AnyValue-style) before starting; those stay hand-decoded. Each slice deletes the hand reader it replaces in the
same PR, and finishes with the old-versus-new snapshot diff listed in the PR body. Slice 1 also moves
`OtlpJson.AnyValue` onto a typed union converter and is the first real test that a reader deletes more than its context adds.
Rig-owned formats follow the decisions below (Skip plus a version), so slice 2 needs no further decision.

1. **OTLP and trace readers left**: 7 files, 63 read hits, 2630 whole-file lines
   - `Core/Chrome/ChromeTraces.cs` (627 lines, 17 hits, reader/foreign)
   - `Core/Observability/TraceAnalytics.cs` (582 lines, 11 hits, reader/foreign)
   - `Core/Otel/Backfill.cs` (392 lines, 9 hits, reader/foreign)
   - `Core/Otel/ReplayOffsets.cs` (127 lines, 8 hits, reader/foreign)
   - `Web/Ops/Telemetry.cs` (393 lines, 8 hits, reader/owned)
   - `Cli/Traces/TracesCommand.cs` (314 lines, 6 hits, reader/owned)
   - `Core/Otel/ClickHouseClient.cs` (195 lines, 4 hits, reader/foreign)
2. **Jobs and wrap-up state (rig-owned)**: 9 files, 82 read hits, 3101 whole-file lines
   - `Core/Jobs/JobState.cs` (323 lines, 28 hits, reader/owned)
   - `Core/Estate/Wrapup.cs` (1021 lines, 17 hits, reader/owned)
   - `Core/Estate/WrapupState.cs` (275 lines, 13 hits, reader/owned)
   - `Core/Workspace/MachineConfig.cs` (85 lines, 9 hits, reader/owned)
   - `Core/Estate/DecisionFeed.cs` (95 lines, 8 hits, reader/owned)
   - `Core/Estate/Decisions.cs` (433 lines, 2 hits, writer/owned)
   - `Core/Estate/ShelfStore.cs` (295 lines, 2 hits, writer/owned)
   - `Core/Jobs/JobsSite.cs` (106 lines, 2 hits, writer/owned)
   - `Cli/Estate/WrapupCommand.cs` (468 lines, 1 hits, writer/owned)
3. **Orca**: 13 files, 110 read hits, 4114 whole-file lines
   - `Core/Orca/OrcaJson.cs` (122 lines, 27 hits, reader/foreign)
   - `Core/Orca/OrcaProjects.cs` (184 lines, 22 hits, reader/foreign)
   - `Core/Estate/OrcaLayout.cs` (769 lines, 16 hits, reader/owned)
   - `Core/Orca/OrcaRuntime.cs` (280 lines, 15 hits, reader/foreign)
   - `Core/Estate/OrcaWorktrees.cs` (766 lines, 7 hits, reader/owned)
   - `Core/Orca/OrcaHooks.cs` (153 lines, 6 hits, reader/foreign)
   - `Core/Orca/OrcaState.cs` (214 lines, 4 hits, reader/foreign)
   - `Core/Orca/OrcaPolicy.cs` (122 lines, 4 hits, reader/owned)
   - `Core/Desktop/Windows/OrcaUpdate.cs` (446 lines, 3 hits, reader/owned)
   - `Core/Orca/OrcaSync.cs` (331 lines, 2 hits, writer/foreign)
   - `Cli/Workspace/OrcaCommand.cs` (223 lines, 2 hits, writer/owned)
   - `Cli/Estate/OrcaWorktreesCommand.cs` (293 lines, 1 hits, writer/owned)
   - `Cli/Estate/OrcaLayoutCommand.cs` (211 lines, 1 hits, writer/owned)
4. **GitHub and PR readers**: 10 files, 162 read hits, 2515 whole-file lines
   - `Core/GitHub/PrResolver.cs` (372 lines, 50 hits, reader/foreign)
   - `Core/Work/Prs.cs` (468 lines, 28 hits, reader/foreign)
   - `Core/GitHub/RepoResolver.cs` (224 lines, 18 hits, reader/foreign)
   - `Core/Workspace/UrlOwners.cs` (240 lines, 14 hits, reader/foreign)
   - `Core/Workspace/CiTelemetry.cs` (151 lines, 14 hits, reader/foreign)
   - `Core/Work/WorkJson.cs` (102 lines, 13 hits, reader/foreign)
   - `Core/Estate/PullRequests.cs` (219 lines, 8 hits, reader/foreign)
   - `Core/GitHub/GitHubFunctions.cs` (473 lines, 7 hits, reader/foreign)
   - `Core/GitHub/GitHubRestRequest.cs` (155 lines, 6 hits, reader/foreign)
   - `Core/Estate/GitHubArchived.cs` (111 lines, 4 hits, reader/foreign)
5. **Packages**: 5 files, 107 read hits, 982 whole-file lines
   - `Core/Packages/LivePackageFetcher.cs` (395 lines, 67 hits, reader/foreign)
   - `Core/Packages/PackageBindings.cs` (127 lines, 21 hits, reader/foreign)
   - `Core/Packages/LiveRegistryInfo.cs` (158 lines, 15 hits, reader/foreign)
   - `Cli/Workspace/PackagesCommand.cs` (286 lines, 2 hits, writer/owned)
   - `Core/Packages/RegistryJson.cs` (16 lines, 2 hits, writer/foreign)
6. **Governance A (repo settings)**: 15 files, 99 read hits, 4902 whole-file lines
   - `Core/Governance/Rulesets.cs` (521 lines, 31 hits, reader/foreign)
   - `Core/Governance/Triage.cs` (462 lines, 18 hits, reader/foreign)
   - `Core/Governance/Environments.cs` (254 lines, 13 hits, reader/foreign)
   - `Core/Governance/Resources.cs` (217 lines, 10 hits, reader/foreign)
   - `Cli/Governance/GovernanceCommand.cs` (244 lines, 6 hits, reader/foreign)
   - `Core/Governance/Labels.cs` (225 lines, 5 hits, reader/foreign)
   - `Core/Governance/RepoPolicies.cs` (802 lines, 3 hits, reader/owned)
   - `Core/Governance/Pages.cs` (222 lines, 3 hits, reader/foreign)
   - `Core/Governance/GovernedRepos.cs` (216 lines, 2 hits, writer/foreign)
   - `Core/Governance/BranchCleanup.cs` (179 lines, 2 hits, writer/foreign)
   - `Core/Governance/PolicyFiles.cs` (179 lines, 2 hits, writer/owned)
   - `Cli/Governance/GovernanceCommand.Sync.cs` (421 lines, 1 hits, writer/foreign)
   - `Cli/Governance/GovernanceCommand.Triage.cs` (349 lines, 1 hits, writer/foreign)
   - `Core/Governance/Conventions.cs` (316 lines, 1 hits, writer/foreign)
   - `Cli/Governance/GovernanceCommand.Files.cs` (295 lines, 1 hits, writer/foreign)
7. **Governance B (Vercel, apps, audit)**: 6 files, 63 read hits, 2168 whole-file lines
   - `Core/Governance/VercelGating.cs` (359 lines, 18 hits, reader/foreign)
   - `Core/Governance/AgentsMdAudit.cs` (825 lines, 16 hits, reader/foreign)
   - `Core/Governance/RemoteAgentFiles.cs` (218 lines, 14 hits, reader/foreign)
   - `Core/Governance/VercelProjects.cs` (302 lines, 8 hits, reader/foreign)
   - `Core/Governance/VercelApi.cs` (183 lines, 4 hits, reader/foreign)
   - `Core/Governance/GitHubApps.cs` (281 lines, 3 hits, reader/foreign)
8. **Desktop plans**: 5 files, 81 read hits, 1476 whole-file lines
   - `Core/Desktop/PlanJson.cs` (319 lines, 40 hits, reader/owned)
   - `Core/Desktop/PlanModels.cs` (417 lines, 24 hits, reader/owned)
   - `Core/Desktop/PlanFiles.cs` (201 lines, 14 hits, reader/owned)
   - `Core/Desktop/WindowPidFile.cs` (72 lines, 2 hits, writer/owned)
   - `Core/Desktop/WindowReuse.cs` (467 lines, 1 hits, writer/owned)
9. **Review queue**: 3 files, 67 read hits, 411 whole-file lines
   - `Core/ReviewQueue/QueueSnapshot.cs` (147 lines, 43 hits, reader/owned)
   - `Core/ReviewQueue/QueueGitHub.cs` (206 lines, 19 hits, reader/foreign)
   - `Core/ReviewQueue/QueueJson.cs` (58 lines, 5 hits, reader/owned)
10. **Chrome and JXA**: 5 files, 52 read hits, 1544 whole-file lines
   - `Cli/Chrome/ChromeTabsCommand.cs` (835 lines, 20 hits, reader/foreign)
   - `Core/Chrome/MacOS/JxaModels.cs` (300 lines, 18 hits, reader/foreign)
   - `Core/Chrome/MacOS/ChromeJxa.cs` (134 lines, 8 hits, reader/foreign)
   - `Core/Chrome/SiteHosts.cs` (127 lines, 4 hits, reader/foreign)
   - `Core/Chrome/MacOS/JxaScripts.cs` (148 lines, 2 hits, writer/foreign)
11. **Devenv, config and policy**: 16 files, 117 read hits, 3334 whole-file lines
   - `Core/Devenv/ObsidianPlugins.cs` (256 lines, 21 hits, reader/owned)
   - `Core/Policy/NativeProjection.cs` (508 lines, 16 hits, reader/owned)
   - `Core/Devenv/ClaudeBaseline.cs` (165 lines, 11 hits, reader/owned)
   - `Core/Devenv/SessionLedger.cs` (190 lines, 11 hits, reader/owned)
   - `Core/Devenv/OpencodeDefaults.cs` (97 lines, 11 hits, reader/owned)
   - `Core/Devenv/ToolCatalog.cs` (117 lines, 8 hits, reader/owned)
   - `Core/Devenv/TerminalSetup.cs` (117 lines, 8 hits, reader/owned)
   - `Core/Config/ConfigJsonConverters.cs` (173 lines, 6 hits, reader/owned)
   - `Core/Devenv/GitSetup.cs` (126 lines, 5 hits, reader/owned)
   - `Core/Config/EstateConfigs.cs` (473 lines, 4 hits, reader/owned)
   - `Cli/Agent/AgentLauncher.cs` (362 lines, 4 hits, reader/owned)
   - `Core/Reconciliation/ReconciliationRunner.cs` (256 lines, 4 hits, reader/owned)
   - `Core/Devenv/SettingsFile.cs` (59 lines, 3 hits, reader/owned)
   - `Core/Config/ConfigLoader.cs` (189 lines, 2 hits, writer/owned)
   - `Core/Devenv/GhScopes.cs` (79 lines, 2 hits, writer/owned)
   - `Core/Policy/ProjectAudit.cs` (167 lines, 1 hits, writer/owned)
12. **Work, vault, skills, mail, connectivity**: 16 files, 111 read hits, 4630 whole-file lines
   - `Core/Work/Transcripts.cs` (334 lines, 10 hits, reader/foreign)
   - `Cli/Workspace/WorkspaceCommand.cs` (537 lines, 10 hits, reader/owned)
   - `Cli/Models/ModelDiscovery.cs` (259 lines, 10 hits, reader/foreign)
   - `Core/Runtime/RuntimeManager.cs` (659 lines, 10 hits, reader/owned)
   - `Core/Skills/SkillVendors.cs` (265 lines, 10 hits, reader/owned)
   - `Core/Work/ClaudeSessions.cs` (262 lines, 9 hits, reader/foreign)
   - `Core/Vault/ObsidianOrphans.cs` (161 lines, 8 hits, reader/owned)
   - `Core/Mail/MailCoverage.cs` (170 lines, 7 hits, reader/foreign)
   - `Core/Connectivity/EstateGraph.cs` (293 lines, 7 hits, reader/foreign)
   - `Core/Workspace/EstateSync.cs` (128 lines, 6 hits, reader/owned)
   - `Core/Connectivity/Manifests.cs` (412 lines, 6 hits, reader/foreign)
   - `Core/Vault/KnowledgeServer.cs` (301 lines, 5 hits, reader/owned)
   - `Core/Web/EstateCatalogue.cs` (102 lines, 5 hits, reader/owned)
   - `Core/Workspace/ManagedProjects.cs` (152 lines, 4 hits, reader/owned)
   - `Core/Vault/Frontmatter.cs` (227 lines, 3 hits, reader/owned)
   - `Core/Skills/SkillCatalog.cs` (368 lines, 1 hits, writer/owned)
13. **Remaining CLI and estate commands**: 8 files, 35 read hits, 3157 whole-file lines
   - `Cli/Estate/EstateCommand.cs` (800 lines, 13 hits, reader/owned)
   - `Core/Estate/CloneDiscovery.cs` (205 lines, 7 hits, reader/owned)
   - `Cli/Estate/VolumeCommand.cs` (204 lines, 5 hits, reader/owned)
   - `Core/Estate/Shelve.cs` (1213 lines, 4 hits, reader/owned)
   - `Cli/Estate/ShelveCommand.cs` (500 lines, 3 hits, reader/owned)
   - `Cli/Estate/PruneCachesCommand.cs` (91 lines, 1 hits, writer/owned)
   - `Cli/Estate/AppsCommand.cs` (76 lines, 1 hits, writer/owned)
   - `Cli/Estate/DownloadsCommand.cs` (68 lines, 1 hits, writer/owned)
14. **Shared helpers (delete last)**: 5 files, 62 read hits, 481 whole-file lines
   - `Core/Text/JsonShape.cs` (182 lines, 38 hits, reader/helper)
   - `Core/Text/JsonProblem.cs` (186 lines, 14 hits, reader/helper)
   - `Core/Text/JsonFile.cs` (34 lines, 5 hits, reader/helper)
   - `Core/Text/JsonOutput.cs` (24 lines, 4 hits, reader/helper)
   - `Core/Text/QuotedText.cs` (55 lines, 1 hits, writer/helper)

## Appendix: per-file inventory

Base tree contents. Class is reader or writer then owner; reads and writes are hit counts; lines are whole-file.

| Class | Reads | Writes | Lines | File |
|---|---:|---:|---:|---|
| reader/foreign | 67 | 0 | 395 | `Core/Packages/LivePackageFetcher.cs` |
| reader/foreign | 50 | 0 | 372 | `Core/GitHub/PrResolver.cs` |
| reader/foreign | 35 | 2 | 306 | `Core/Otel/TraceSpans.cs` |
| reader/foreign | 31 | 1 | 521 | `Core/Governance/Rulesets.cs` |
| reader/foreign | 28 | 0 | 149 | `Core/Otel/OtlpJson.cs` |
| reader/foreign | 28 | 1 | 468 | `Core/Work/Prs.cs` |
| reader/foreign | 27 | 0 | 122 | `Core/Orca/OrcaJson.cs` |
| reader/foreign | 22 | 0 | 184 | `Core/Orca/OrcaProjects.cs` |
| reader/foreign | 21 | 0 | 127 | `Core/Packages/PackageBindings.cs` |
| reader/foreign | 20 | 4 | 835 | `Cli/Chrome/ChromeTabsCommand.cs` |
| reader/foreign | 19 | 0 | 206 | `Core/ReviewQueue/QueueGitHub.cs` |
| reader/foreign | 18 | 0 | 300 | `Core/Chrome/MacOS/JxaModels.cs` |
| reader/foreign | 18 | 0 | 462 | `Core/Governance/Triage.cs` |
| reader/foreign | 18 | 0 | 359 | `Core/Governance/VercelGating.cs` |
| reader/foreign | 18 | 1 | 224 | `Core/GitHub/RepoResolver.cs` |
| reader/foreign | 17 | 0 | 627 | `Core/Chrome/ChromeTraces.cs` |
| reader/foreign | 16 | 1 | 825 | `Core/Governance/AgentsMdAudit.cs` |
| reader/foreign | 15 | 1 | 280 | `Core/Orca/OrcaRuntime.cs` |
| reader/foreign | 15 | 0 | 158 | `Core/Packages/LiveRegistryInfo.cs` |
| reader/foreign | 14 | 0 | 240 | `Core/Workspace/UrlOwners.cs` |
| reader/foreign | 14 | 0 | 218 | `Core/Governance/RemoteAgentFiles.cs` |
| reader/foreign | 14 | 0 | 151 | `Core/Workspace/CiTelemetry.cs` |
| reader/foreign | 13 | 0 | 254 | `Core/Governance/Environments.cs` |
| reader/foreign | 13 | 1 | 102 | `Core/Work/WorkJson.cs` |
| reader/foreign | 11 | 0 | 582 | `Core/Observability/TraceAnalytics.cs` |
| reader/foreign | 10 | 0 | 334 | `Core/Work/Transcripts.cs` |
| reader/foreign | 10 | 0 | 259 | `Cli/Models/ModelDiscovery.cs` |
| reader/foreign | 10 | 0 | 217 | `Core/Governance/Resources.cs` |
| reader/foreign | 9 | 1 | 392 | `Core/Otel/Backfill.cs` |
| reader/foreign | 9 | 0 | 262 | `Core/Work/ClaudeSessions.cs` |
| reader/foreign | 8 | 0 | 302 | `Core/Governance/VercelProjects.cs` |
| reader/foreign | 8 | 0 | 134 | `Core/Chrome/MacOS/ChromeJxa.cs` |
| reader/foreign | 8 | 0 | 219 | `Core/Estate/PullRequests.cs` |
| reader/foreign | 8 | 0 | 127 | `Core/Otel/ReplayOffsets.cs` |
| reader/foreign | 7 | 0 | 170 | `Core/Mail/MailCoverage.cs` |
| reader/foreign | 7 | 1 | 473 | `Core/GitHub/GitHubFunctions.cs` |
| reader/foreign | 7 | 1 | 293 | `Core/Connectivity/EstateGraph.cs` |
| reader/foreign | 6 | 1 | 244 | `Cli/Governance/GovernanceCommand.cs` |
| reader/foreign | 6 | 1 | 155 | `Core/GitHub/GitHubRestRequest.cs` |
| reader/foreign | 6 | 0 | 153 | `Core/Orca/OrcaHooks.cs` |
| reader/foreign | 6 | 0 | 412 | `Core/Connectivity/Manifests.cs` |
| reader/foreign | 5 | 0 | 225 | `Core/Governance/Labels.cs` |
| reader/foreign | 4 | 0 | 111 | `Core/Estate/GitHubArchived.cs` |
| reader/foreign | 4 | 0 | 183 | `Core/Governance/VercelApi.cs` |
| reader/foreign | 4 | 0 | 214 | `Core/Orca/OrcaState.cs` |
| reader/foreign | 4 | 1 | 127 | `Core/Chrome/SiteHosts.cs` |
| reader/foreign | 4 | 0 | 195 | `Core/Otel/ClickHouseClient.cs` |
| reader/foreign | 3 | 0 | 281 | `Core/Governance/GitHubApps.cs` |
| reader/foreign | 3 | 0 | 222 | `Core/Governance/Pages.cs` |
| reader/helper | 38 | 0 | 182 | `Core/Text/JsonShape.cs` |
| reader/helper | 14 | 1 | 186 | `Core/Text/JsonProblem.cs` |
| reader/helper | 5 | 0 | 34 | `Core/Text/JsonFile.cs` |
| reader/helper | 4 | 4 | 24 | `Core/Text/JsonOutput.cs` |
| reader/owned | 43 | 0 | 147 | `Core/ReviewQueue/QueueSnapshot.cs` |
| reader/owned | 40 | 2 | 319 | `Core/Desktop/PlanJson.cs` |
| reader/owned | 28 | 1 | 323 | `Core/Jobs/JobState.cs` |
| reader/owned | 24 | 0 | 417 | `Core/Desktop/PlanModels.cs` |
| reader/owned | 21 | 2 | 256 | `Core/Devenv/ObsidianPlugins.cs` |
| reader/owned | 17 | 0 | 1021 | `Core/Estate/Wrapup.cs` |
| reader/owned | 16 | 2 | 508 | `Core/Policy/NativeProjection.cs` |
| reader/owned | 16 | 0 | 769 | `Core/Estate/OrcaLayout.cs` |
| reader/owned | 14 | 2 | 201 | `Core/Desktop/PlanFiles.cs` |
| reader/owned | 13 | 3 | 800 | `Cli/Estate/EstateCommand.cs` |
| reader/owned | 13 | 1 | 275 | `Core/Estate/WrapupState.cs` |
| reader/owned | 13 | 1 | 290 | `Core/Jobs/RunHistory.cs` |
| reader/owned | 11 | 1 | 165 | `Core/Devenv/ClaudeBaseline.cs` |
| reader/owned | 11 | 0 | 190 | `Core/Devenv/SessionLedger.cs` |
| reader/owned | 11 | 0 | 97 | `Core/Devenv/OpencodeDefaults.cs` |
| reader/owned | 10 | 4 | 537 | `Cli/Workspace/WorkspaceCommand.cs` |
| reader/owned | 10 | 1 | 659 | `Core/Runtime/RuntimeManager.cs` |
| reader/owned | 10 | 0 | 265 | `Core/Skills/SkillVendors.cs` |
| reader/owned | 9 | 0 | 85 | `Core/Workspace/MachineConfig.cs` |
| reader/owned | 8 | 0 | 161 | `Core/Vault/ObsidianOrphans.cs` |
| reader/owned | 8 | 0 | 117 | `Core/Devenv/ToolCatalog.cs` |
| reader/owned | 8 | 0 | 117 | `Core/Devenv/TerminalSetup.cs` |
| reader/owned | 8 | 0 | 95 | `Core/Estate/DecisionFeed.cs` |
| reader/owned | 8 | 0 | 393 | `Web/Ops/Telemetry.cs` |
| reader/owned | 7 | 1 | 205 | `Core/Estate/CloneDiscovery.cs` |
| reader/owned | 7 | 0 | 766 | `Core/Estate/OrcaWorktrees.cs` |
| reader/owned | 6 | 2 | 173 | `Core/Config/ConfigJsonConverters.cs` |
| reader/owned | 6 | 1 | 314 | `Cli/Traces/TracesCommand.cs` |
| reader/owned | 6 | 1 | 128 | `Core/Workspace/EstateSync.cs` |
| reader/owned | 5 | 0 | 301 | `Core/Vault/KnowledgeServer.cs` |
| reader/owned | 5 | 1 | 204 | `Cli/Estate/VolumeCommand.cs` |
| reader/owned | 5 | 0 | 126 | `Core/Devenv/GitSetup.cs` |
| reader/owned | 5 | 1 | 58 | `Core/ReviewQueue/QueueJson.cs` |
| reader/owned | 5 | 0 | 102 | `Core/Web/EstateCatalogue.cs` |
| reader/owned | 4 | 0 | 152 | `Core/Workspace/ManagedProjects.cs` |
| reader/owned | 4 | 1 | 1213 | `Core/Estate/Shelve.cs` |
| reader/owned | 4 | 0 | 473 | `Core/Config/EstateConfigs.cs` |
| reader/owned | 4 | 0 | 362 | `Cli/Agent/AgentLauncher.cs` |
| reader/owned | 4 | 2 | 256 | `Core/Reconciliation/ReconciliationRunner.cs` |
| reader/owned | 4 | 0 | 122 | `Core/Orca/OrcaPolicy.cs` |
| reader/owned | 3 | 1 | 802 | `Core/Governance/RepoPolicies.cs` |
| reader/owned | 3 | 0 | 227 | `Core/Vault/Frontmatter.cs` |
| reader/owned | 3 | 0 | 446 | `Core/Desktop/Windows/OrcaUpdate.cs` |
| reader/owned | 3 | 2 | 500 | `Cli/Estate/ShelveCommand.cs` |
| reader/owned | 3 | 1 | 59 | `Core/Devenv/SettingsFile.cs` |
| writer/foreign | 2 | 0 | 216 | `Core/Governance/GovernedRepos.cs` |
| writer/foreign | 2 | 0 | 179 | `Core/Governance/BranchCleanup.cs` |
| writer/foreign | 2 | 0 | 331 | `Core/Orca/OrcaSync.cs` |
| writer/foreign | 2 | 2 | 148 | `Core/Chrome/MacOS/JxaScripts.cs` |
| writer/foreign | 2 | 0 | 16 | `Core/Packages/RegistryJson.cs` |
| writer/foreign | 1 | 0 | 421 | `Cli/Governance/GovernanceCommand.Sync.cs` |
| writer/foreign | 1 | 2 | 349 | `Cli/Governance/GovernanceCommand.Triage.cs` |
| writer/foreign | 1 | 1 | 316 | `Core/Governance/Conventions.cs` |
| writer/foreign | 1 | 1 | 295 | `Cli/Governance/GovernanceCommand.Files.cs` |
| writer/helper | 1 | 1 | 55 | `Core/Text/QuotedText.cs` |
| writer/owned | 2 | 0 | 189 | `Core/Config/ConfigLoader.cs` |
| writer/owned | 2 | 0 | 72 | `Core/Desktop/WindowPidFile.cs` |
| writer/owned | 2 | 1 | 433 | `Core/Estate/Decisions.cs` |
| writer/owned | 2 | 0 | 295 | `Core/Estate/ShelfStore.cs` |
| writer/owned | 2 | 3 | 286 | `Cli/Workspace/PackagesCommand.cs` |
| writer/owned | 2 | 2 | 223 | `Cli/Workspace/OrcaCommand.cs` |
| writer/owned | 2 | 0 | 179 | `Core/Governance/PolicyFiles.cs` |
| writer/owned | 2 | 0 | 106 | `Core/Jobs/JobsSite.cs` |
| writer/owned | 2 | 0 | 79 | `Core/Devenv/GhScopes.cs` |
| writer/owned | 1 | 0 | 368 | `Core/Skills/SkillCatalog.cs` |
| writer/owned | 1 | 0 | 167 | `Core/Policy/ProjectAudit.cs` |
| writer/owned | 1 | 1 | 468 | `Cli/Estate/WrapupCommand.cs` |
| writer/owned | 1 | 0 | 467 | `Core/Desktop/WindowReuse.cs` |
| writer/owned | 1 | 1 | 293 | `Cli/Estate/OrcaWorktreesCommand.cs` |
| writer/owned | 1 | 1 | 211 | `Cli/Estate/OrcaLayoutCommand.cs` |
| writer/owned | 1 | 1 | 91 | `Cli/Estate/PruneCachesCommand.cs` |
| writer/owned | 1 | 1 | 76 | `Cli/Estate/AppsCommand.cs` |
| writer/owned | 1 | 1 | 68 | `Cli/Estate/DownloadsCommand.cs` |
