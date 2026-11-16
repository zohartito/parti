# Parti — Benchmark Methodology

*Last updated 2026-04-19. Companion to the auto-generated report at [EVAL-v0.1.md](EVAL-v0.1.md).*

This document is the reference for how Parti measures the claims it makes in the project README and docs. A reader who wants to reproduce a pass-rate number, audit a prompt-change regression, or extend the fixture suite should be able to do so from this document alone. Where something is not yet implemented, the document flags it as planned and names the target milestone.

Sections are ordered to match the order a careful reader would use: why the benchmark exists, what it measures, how a fixture is shaped, what predicates it supports, how a run is reproduced, how results are scored, how the suite grows, what the threats to validity are, what the surrounding landscape looks like, what is still open, and how to cite the work.

---

## 1. Motivation

Several chat-driven assistants can now edit a Grasshopper canvas, but as of April 2026 we found no public benchmark for them: no published set of inputs, prompts, tool surfaces, and scoring rules that would let a third party reproduce a pass-rate claim.

Architectural CAD AI lives or dies on trust. Architects pick tools for production work. A plugin that silently rewrites a canvas is a liability unless the practitioner can inspect what it was told to do and measure how often it does the right thing. That evidence has to be legible to somebody who is not the vendor. The community needs a benchmark it can extend, critique, and run against competing implementations — not a vendor's closed leaderboard.

Parti's bet is that a public benchmark is the right organizing artifact for this subfield. Every claim Parti makes about itself — that it handles a given mutation scenario, that it is safer than a baseline that dumps raw edits on the canvas, that Ollama-backed runs produce comparable outputs to Anthropic-backed runs — has to be grounded in a benchmark that a skeptical reviewer can re-run. This document specifies that benchmark.

The benchmark is designed with four properties in mind:

- **Reproducible without secrets.** The default lane runs scripted provider responses, so anybody with .NET 7 and the repository checked out can reproduce a pass-rate number with `dotnet test`. No API keys required.
- **Extensible.** Adding a fixture is adding a JSON file. No code changes. No schema migrations.
- **Versioned alongside prompts.** A prompt change is a PR that has to come with delta numbers. Regressions land with eyes on them.
- **Transparent about what it does not measure.** Scripted responses are not the same as live-model responses; simple fixtures are not the same as 300-component production canvases. The threats-to-validity section is explicit.

---

## 2. What we are measuring

The benchmark supports four claim families. They differ in whether the result is a statistical rate or a compliance check, and in how directly v0.7 tries to optimize against them.

**Correctness.** Given a user prompt `P` and an initial canvas state `S`, does Parti produce the canvas state the fixture declares as expected? This is the primary signal the pass rate summarizes. In v0.1 (12 hermetic fixtures), correctness is checked as a predicate set applied to the post-session state of a scripted provider run. In v0.7 (targeting 50 `.gh` fixtures), correctness extends to predicates applied against the after-state of a real Grasshopper document.

**Trust.** Are the system prompt and the tool definitions transparent and versioned? This is a compliance check, not a statistical claim. The benchmark asserts that (a) a canonical prompt file exists at `prompts/system_vX.Y.md`, (b) the tool registry's schema for each tool is committed to the repository, and (c) any change to either is gated by an eval-numbers diff per [PROMPTS.md](PROMPTS.md). Trust is binary per release: either the current shipped commit satisfies it or it does not. No percentage.

**Safety.** Does undo batching reliably collapse a multi-mutation AI turn into a single Ctrl+Z? This is binary per turn: the user presses undo once and the canvas reverts to its pre-turn state, or they do not and it does not. The benchmark measures this by instrumenting the GH_UndoRecord emission during mutation fixtures and asserting a single record per turn. v0.7 includes per-fixture safety checks on any fixture that produces a canvas mutation.

Safety is not a claim about absence of bugs — Parti does not claim to be "safe" in a general-purpose sense. It is a claim about a single concrete property: that the Grasshopper undo stack sees one entry per AI turn, not one per mutation. This matters because a multi-mutation turn that produces several undo entries forces the user into several `Ctrl+Z` presses to revert. A dispatcher that records one entry per mutation lets the user step into a half-reverted state; Parti's dispatcher wraps the whole turn in a `GH_UndoUtil.CreateGenericObjectEvent` batch so the state transition is atomic from the user's perspective. The benchmark instruments this directly rather than relying on behavioral observation.

**Performance.** Token usage, number of turns to completion, and wall-clock duration per fixture are captured and reported but are not a claim the v0.7 release optimizes against. Descriptive numbers show up in the auto-generated report and let a maintainer spot regression patterns (for example, a prompt change that triples the number of turns without changing the pass rate). Optimizing performance is scoped to v0.8+.

Correctness is the headline claim. Trust and safety appear as methodology arguments. Performance appears as supporting data in a table of aggregate numbers.

---

## 3. Fixture design

A fixture is the smallest reproducible unit of evaluation. Different fixture kinds exist because Parti needs to benchmark different things.

**Hermetic JSON fixture (v0.1 — shipped).** A single file at `tests/Parti.Eval.v01/fixtures/NN_slug.json` with the following fields:

- `id` — slug identifier matching the filename, e.g. `02_add_slider`.
- `description` — one-line human-readable summary. Surfaced in the auto-generated report.
- `userPrompt` — the verbatim prompt handed to `ConversationSession.RunAsync`.
- `sessionOptions` — optional overrides for `Model`, `MaxTurns`, `MaxToolPasses`, `SystemPrompt`.
- `providerScript` — ordered list of simulated provider responses. Entry `N` is returned for provider call `N`; the last entry repeats if the session keeps querying (used by `08_max_turns_exhausted` to drive the turn-limit path). Each entry carries a `stopReason` (`"end_turn"` or `"tool_use"`) and a list of content blocks (`"text"` or `"tool_call"`).
- `toolHandlers` — map from tool name to canned behaviour. A handler is one of: `result` (single canned `JObject` every call), `sequence` (ordered canned results; call `N` returns entry `N`; the last repeats), or `error` (synthetic `{ "success": false, "error": "..." }` payload). Tools referenced by the script but absent from the handler map return a synthetic `not registered` error so the `09_unknown_tool` fixture can drive recovery paths.
- `expected` — predicate block applied after the session finishes. Schema documented in §4.

The hermetic design is deliberate. A scripted provider makes the benchmark network-free, API-key-free, and deterministic. Any contributor with the repository and a .NET 7 SDK can reproduce the pass rate. CI does not need secrets. The cost is that a scripted provider does not exercise the actual Anthropic or Ollama models — it exercises Parti's handling of the shape of their responses. Whether that handling transfers to real model behavior is a construct-validity question answered by a second lane (§5).

**Worked example: `02_add_slider`.** The simplest non-trivial hermetic fixture illustrates the full shape. The `userPrompt` is a one-line request to add a number slider. The `providerScript` has two entries. The first entry has `stopReason: "tool_use"` and a single `tool_call` content block targeting `gh_add_component` with the arguments the scripted model would have produced. The second entry has `stopReason: "end_turn"` and a single `text` content block containing a one-sentence confirmation. The `toolHandlers` map has one entry under `gh_add_component` with a `result` payload describing the freshly-created component's `SimpleId`. The `expected` block asserts `finalStatus: "Finished"`, `toolCallCount: 1`, and a `textContains` substring that the confirmation must include. When the harness replays this fixture, `ConversationSession` drives the `FakeAIProvider` through both scripted steps, dispatches one call to the `FakeToolDispatcher`, receives the canned result, sends the result back as a `tool_result` message, and terminates on the `end_turn`. All three predicates evaluate true, the fixture passes, and the row in [EVAL-v0.1.md](EVAL-v0.1.md) shows `tool calls: 1`. This is the reference shape every new fixture author is expected to match.

**Worked example: `07_tool_error_then_retry`.** A more interesting fixture exercises error-recovery. The `providerScript` has four entries: a first tool-use that calls `gh_add_component` with arguments the tool handler rejects, an acknowledgement text block, a second tool-use with different arguments that succeed, and a final text block. The `toolHandlers` entry for `gh_add_component` uses a `sequence`: entry 0 is an error payload, entry 1 is a success payload. The `expected` block asserts `toolCallCount: 2` and a `textContains` substring specific to the recovery confirmation. The fixture passes when the session has dispatched exactly two tool calls and the final assistant text contains the expected substring — any failure along the way (for example, a regression that makes the session abort on the first error instead of retrying) flips the row to failing and shows up in the auto-generated report.

**`.gh` fixture (v0.2 — in progress per [GH-FIXTURE-SPEC.md](GH-FIXTURE-SPEC.md)).** A directory at `tests/Parti.Eval.v01/gh-fixtures/<NNN>-<slug>/` with three files: `before.gh` (starting canvas), `after.gh` (reference canvas the fixture asserts against), and `fixture.json` (prompt, tool expectations, canvas predicates, metadata). This is the unit of evaluation for live-canvas scenarios. A single fixture is a triple: a real Grasshopper definition in a known state, the Grasshopper definition the fixture considers a correct post-condition, and the JSON sidecar that names the prompt and the canvas assertions.

Real `.gh` files are committed to the repository as binary artifacts. Grasshopper's on-disk format is versioned by Rhino, so fixtures are pinned to Rhino 8. The `before.gh` / `after.gh` contract is deliberately permissive about *how* the canvas got to the after-state — the canvas assertions, not the byte-diff, define correctness. Two implementations that reach the same asserted facts should both pass.

**Live-model fixture (planned for v0.7.2).** Same JSON as a hermetic fixture, but a second test host runs it against a real provider endpoint (Anthropic Messages API or a local Ollama instance) instead of the scripted `FakeAIProvider`. Token usage is captured and rolled into the report. This lane requires API keys and is the lane behind live-model numbers. It does not yet exist. The gap is flagged in §10.

---

## 4. Predicate vocabulary

Predicates are how a fixture declares what counts as success. The vocabulary is split between the assertion block of a hermetic fixture (currently sparse) and the canvas-predicate DSL of a `.gh` fixture (richer).

### 4.1 Hermetic fixture predicates (v0.6)

The `expected` block in a hermetic fixture is the only thing that distinguishes a passing run from a failing one. Every field is optional; a missing field means "do not check". The v0.6 set:

| Predicate | Type | Meaning |
|---|---|---|
| `finalStopReason` | string | The last provider response's stop reason must match (`"end_turn"`, `"tool_use"`, etc.). |
| `finalStatus` | string | The conversation's terminal status. One of `"Finished"`, `"Paused"`, `"Error"`. |
| `minInteractions` | int | Lower bound on `session.History.Count` after the run. |
| `textContains` | string | Substring that must appear in at least one assistant text interaction. |
| `toolCallCount` | int | Exact count of tool calls dispatched across the whole session. |
| `providerCallCount` | int | Exact count of provider calls (equals the number of scripted steps consumed). |
| `errorContains` | string | Substring the terminal error message must contain. |

This set is sufficient to express the 12 shipped fixtures but is narrow. Known gaps, partially closed by v0.2 of the eval harness (see [EVAL-v0.2.md](EVAL-v0.2.md) for the shipped predicate spec) and otherwise tracked against v0.7.1 (alongside the v0.7 eval expansion):

- `toolCallsMinCount` / `toolCallsMaxCount` — per-tool bounded counts. **Shipped in v0.2.** Supports fixtures where any 2–4 tool calls of a named tool are acceptable, and forbid-semantics via `max=0`.
- `toolCallsInclude` / `toolCallsExclude` — per-tool set membership with no count constraint. **Shipped in v0.2.** Asserts `gh_connect` appeared in the call log without fixing its exact count.
- `finalTextContainsAny` / `finalTextContainsAll` — OR / AND logic across substrings, case-insensitive. **Shipped in v0.2.** Supports fixtures where several reasonable phrasings count as a correct confirmation, or where a stepwise confirmation must reference both "step 1" and "step 2".
- Predicate combinators (AND / OR / NOT) — expressible as a small tree rather than a flat set. Still pending. Lets one fixture express "either this text and this tool count, or that error substring".
- `textMatchesRegex` — regex instead of literal substring. Still pending. Useful for confirmation strings that include a resolved `SimpleId` the fixture author cannot predict.
- `toolCallOrder` — ordered sequence of tool names. Still pending; needed for fixtures that require `gh_describe` before `gh_delete`.
- Unknown-field validation at load time — an unknown predicate key in an `expected` block is silently ignored today. Still pending; a v0.3 validator should reject typos rather than produce silent passes.

The v0.6 set was chosen deliberately conservative: we shipped the minimum vocabulary the 12 fixtures needed, and we are extending it only as new fixtures force the extension. Predicate sprawl is an anti-pattern. Every predicate added is a predicate every future fixture author has to learn. The v0.2 additions above were driven by concrete v0.7 fixtures (`20_group_workflow`, `21_layout_grid`, `22_ask_user_clarify`) that the v0.1 vocabulary could not express cleanly.

### 4.2 `.gh` fixture canvas predicates (v0.7)

The `canvasAssertions` array in a `.gh` fixture is a list of predicate strings evaluated against `after.gh`. Per [GH-FIXTURE-SPEC.md](GH-FIXTURE-SPEC.md) the v0.7 supported forms are:

| Predicate | Shape | Asserts |
|---|---|---|
| `componentExists: <Type> named '<name>'` | existence | A component of the given type whose nickname or name matches. |
| `componentExists: <Type>` | existence | Any one component of the given type, irrespective of name. |
| `componentCount: <Type> == <n>` | exact count | Exactly `n` components of the given type exist. |
| `connectionExists: <SrcType> <srcName> → <DstType> <input>` | wiring | A wire from the named source's output into the named input port of a component of the destination type. |
| `inputValue: <Type> named '<name>' <input> == <value>` | state | Exact value on a named input of a component. |

The design goal is that a fixture author looks at `after.gh`, picks the minimum set of facts that define success for the prompt, and writes them down. Over-specifying — asserting layout, GUIDs, or cosmetic details the prompt did not ask for — is flagged as an anti-pattern in the spec.

`.gh` predicate vocabulary will also grow. Known backlog: negated forms (`componentAbsent`), topological forms (`connectionAbsent`, `sourceCount`), and parameter-tree forms (`outputAccess` on a data-tree-sensitive component). These land as fixtures demand them.

### 4.3 Tool-call bounds (`.gh` fixtures)

`.gh` fixtures also carry a `toolExpectations` array of `{ tool, minCalls, maxCalls }` entries. This bounds the number of calls per tool name without constraining the total. A fixture that demands exactly one `gh_connect` sets `{ "tool": "gh_connect", "minCalls": 1, "maxCalls": 1 }` and omits the rest. Tools the fixture does not care about are simply not listed. This is the `.gh` equivalent of the planned hermetic `toolCallCountByName`.

---

## 5. Experimental protocol

A reader reproducing Parti's numbers needs a protocol precise enough to rerun. The protocol below is what the v0.7 harness implements.

**Environment.** The primary lane is .NET 7 on Windows x64 under the xUnit test host. `dotnet --version` should report `7.0.x`. Rhino is not required for hermetic fixtures — the session talks to a `FakeAIProvider` and a `FakeToolDispatcher` — but is required for `.gh` fixtures (they load a Grasshopper definition and run it through Parti's canvas primitives). Mac support is scheduled for v2.0. Linux is not a target; the Windows-only restriction reflects Rhino 8's platform support, not Parti's.

**Reproducibility.** The headline command is:

```
dotnet test tests/Parti.Eval.v01/Parti.Eval.v01.csproj
```

A zero exit code means all fixtures passed; any non-zero exit is a failure. The test host emits the auto-generated [EVAL-v0.1.md](EVAL-v0.1.md) report as a side effect of the `Parti.Eval.V01.EvalReporter.WriteReport` xUnit fact. Anybody who runs the command sees the same report the CI job would produce, modulo floating-point durations.

**Isolation.** Every fixture runs in its own xUnit test case. `FixtureRunner` allocates a fresh `ConversationSession`, `FakeAIProvider`, and `FakeToolDispatcher` per fixture. There is no shared mutable state between fixtures. Tests can run in any order and in parallel.

**Determinism.** In the hermetic lane determinism is structural: the provider is scripted and tool handlers return canned payloads. Given the same fixture JSON and the same Parti commit, the session produces the same history every time. In the live-model lane determinism is best-effort. Parti sets `temperature=0` where the provider honors it. For `claude-opus-4-7` Anthropic has deprecated `temperature` in favor of its default greedy sampling, so we rely on the model's built-in determinism and note the known variance envelope (run-to-run tool-call ordering can diverge on ambiguous prompts; we do not claim bitwise reproducibility across live-model runs).

**Real-model lane (planned, v0.7.2).** A second CI job will run the same fixture JSON files against live providers — Claude Opus 4.7 over the Anthropic Messages API and Llama 3.1 over a local Ollama endpoint — and emit a second report with live token counts and turn counts alongside the hermetic pass-fail. API keys are injected via GitHub Actions secrets; the local Ollama run uses a containerized model fetched at CI start. This lane is the source of reported live-model numbers. It does not yet exist. Until it does, all published pass rates come from the hermetic lane and must be disclosed as such.

The live-model lane reuses the fixture JSON as-is but ignores the `providerScript` and `toolHandlers` fields — the real provider produces its own responses, and the real tool dispatcher executes the real tools (against either a stub Grasshopper document for pure text fixtures or a loaded `before.gh` for canvas fixtures). The `expected` block is evaluated the same way. A fixture that passes in the hermetic lane but fails in the live-model lane is diagnostic: either the scripted shape did not match what the real model produces, or the real model takes a different-but-still-reasonable path the fixture's predicates reject as wrong. Both are worth investigating. Reports publish the per-fixture delta, not just the aggregate.

Rate limits and cost budgeting matter for the live-model lane. The Anthropic path has a per-fixture cost bounded by `MaxTurns=8` and `MaxToolPasses=4` (both already enforced by the session), so a 50-fixture run has a predictable upper bound on tokens. The Ollama path runs on local compute and has no billing; its bottleneck is wall-clock latency on the CI runner. Both lanes will log per-fixture token counts (Anthropic returns them in the response envelope; Ollama is instrumented via its `/api/chat` response fields) and emit a summary at the end of the report.

**Run artifacts.** A successful run produces:

- An updated [EVAL-v0.1.md](EVAL-v0.1.md) table with one row per fixture, status, duration in milliseconds, and tool-call count.
- An xUnit `.trx` / JUnit-compatible XML file for CI ingestion.
- Per-fixture logs (captured by xUnit) showing the exact sequence of provider calls, tool dispatches, and tool results.

Neither the report nor the logs contain secrets. Both are safe to attach to a PR.

**Harness components.** The v0.1 harness consists of five small files under `tests/Parti.Eval.v01/`. `FixtureSchema.cs` is the in-memory representation of a fixture. `FixtureLoader.cs` deserializes a `fixtures/NN_slug.json` file into the schema types and applies defaults for omitted fields. `FixtureRunner.cs` materializes a `FakeAIProvider` from the `providerScript`, a `FakeToolDispatcher` from the `toolHandlers`, constructs a `ConversationSession`, drives `RunAsync`, and returns the result. `EvalFixtureTests.cs` is the xUnit `[Theory]` that enumerates the fixtures folder and invokes the runner once per fixture. `EvalReporter.cs` is a final `[Fact]` that collects the per-fixture results and overwrites [EVAL-v0.1.md](EVAL-v0.1.md) with the current table. The whole surface is roughly 400 lines of C#; a reviewer can read it cover-to-cover to verify there is no hidden scoring logic.

**Step-by-step reproduction.** To reproduce the numbers in [EVAL-v0.1.md](EVAL-v0.1.md):

1. Install the .NET 7 SDK. Confirm with `dotnet --version` returning a `7.0.x` string.
2. Clone the repository and check out the commit whose numbers you want to reproduce. The published live-result `gitSha` fields contain build labels rather than commit hashes, so those exact measured sources cannot be selected from this repository. Future publications should retain the source commit and per-run configuration, redacting hostnames separately.
3. Restore packages: `dotnet restore`.
4. Run the suite: `dotnet test tests/Parti.Eval.v01/Parti.Eval.v01.csproj`. The command returns exit code 0 when every fixture passes.
5. Open `docs/EVAL-v0.1.md`. The table is overwritten by the last run; confirm the row counts, statuses, and tool-call counts match the published table.

A reviewer who sees a mismatch should file it as a reproducibility issue. Tolerable differences are limited to per-fixture `duration ms` values (non-deterministic on a wall clock) and trailing whitespace; anything else is a methodology bug.

**Extending the harness.** To add a new predicate, the contributor edits `FixtureExpected` in `FixtureSchema.cs` to add the field, edits `FixtureRunner.cs` to evaluate the new predicate against the finished session, and adds at least one fixture under `fixtures/` that exercises it. A failing fixture without the predicate handler is a CI failure, which prevents predicates from being declared in a fixture but silently ignored by the runner.

---

## 6. Scoring methodology

**Per-fixture.** Each fixture resolves to a binary pass or fail. A fixture passes when every predicate in its `expected` block evaluates true after the session finishes. A predicate with a null value is not evaluated. A session that throws an unexpected exception fails with the exception message attached to the fixture row.

**Aggregate.** The headline metric is the percentage of fixtures passing: `passing / total * 100`, rounded to one decimal. The v0.1 report currently shows 11 of 12 passing; the one non-passing case is `08_max_turns_exhausted`, which is marked `error` by design because the fixture exercises the turn-limit abort path (the surrounding infrastructure work to classify a planned abort as a pass is tracked separately). The aggregate in EVAL-v0.1 is therefore reported as 11/12 with a note; it is not 91.7%.

**Category breakdown.** The v0.2 report will additionally break the aggregate down by fixture category (`add`, `edit`, `describe`, `connect`, `mixed`) and by difficulty (`easy`, `medium`, `hard`). These two breakdowns matter because readers want to know whether the pass rate is uniform across scenario types or whether it masks a poor result in a specific lane.

**Prompt-version sensitivity.** Per [PROMPTS.md](PROMPTS.md), any PR that touches `prompts/system_vX.Y.md` must include eval numbers before and after. The rules:

- Improvement or flat lands after code review.
- A drop of up to 5% lands if the PR author explains the tradeoff.
- A drop of 5% to 10% requires maintainer discussion in the PR. Usually held.
- A drop of more than 10% requires explicit maintainer sign-off with written rationale.

This gate is enforced by convention today (a maintainer reads the delta). The intent for v0.7.1 is to enforce it in CI: a GitHub Action that diffs the eval numbers from the PR branch against the base branch and posts the delta as a PR comment, blocking merge on sign-off for large drops.

**Reporting cadence.** The auto-generated [EVAL-v0.1.md](EVAL-v0.1.md) is regenerated on every test run. When the 50-fixture suite lands it will be renamed to `EVAL-v0.2.md` and the v0.1 file retained as a historical artifact. When a number is reported, it is reported together with the version it was measured on. If a later run differs, the report explains why: which prompt changed, which fixture was added, which bug was fixed.

**Worked example: prompt-delta reporting.** Suppose a PR adds a sentence to `prompts/system_v0.6.md` instructing the model to prefer a `gh_describe` call before any edit. The contributor runs `dotnet test` on the base branch, captures the 11/12 result, switches to the PR branch, re-runs, captures the new result, and attaches both to the PR body. If the PR branch shows 11/12 the delta is 0 and review is about the prompt text itself. If the PR branch shows 10/12 the delta is a drop of 8.3 percentage points, which crosses the 5% threshold into maintainer discussion. The PR body must name which fixture regressed and explain whether the regression is a reasonable tradeoff (for example, the new guidance adds a pre-read call that makes `05_list_selected` fail because the fixture expected exactly one tool call). A tradeoff may be accepted; a silent regression is rejected.

**Non-metric reporting.** In addition to the pass rate, every report emits three descriptive columns — duration in milliseconds, provider-call count, and tool-call count — per fixture. These are not aggregated into a claim. They exist so that a maintainer reviewing a regression can distinguish "the model took a different path but still passed" (tool-call count changed, pass rate flat) from "the model took the same path but broke a predicate" (tool-call count flat, pass rate dropped). Reports give the descriptive columns as summary statistics — mean, median, range — rather than as per-fixture claims.

---

## 7. Benchmark fixture library — growth plan

The fixture library grows deliberately. Every fixture added is a claim that the suite is now measuring something new, not redundantly re-measuring something it already covered.

**v0.1 — 12 hermetic fixtures (shipped 2026-04-19).** Text-only, multi-turn, token-accounting, and error-recovery cases. Breakdown:

| Fixture | What it exercises |
|---|---|
| `01_text_only` | Single-turn text reply, no tool use. |
| `02_add_slider` | Single tool call plus confirmation text. |
| `03_add_then_describe` | Two tool calls across three turns. |
| `04_list_all` | `gh_get_all` snapshot plus summarization. |
| `05_list_selected` | `gh_get_selected` followed by summary. |
| `06_add_two_then_describe_both` | Four tool-use turns with sequence-style tool handlers. |
| `07_tool_error_then_retry` | First call returns an error; model recovers. |
| `08_max_turns_exhausted` | Turn-limit abort path. |
| `09_unknown_tool` | Unknown tool name → synthetic not-registered error → recovery. |
| `10_long_conversation` | Four consecutive tool passes within the pass budget. |
| `11_add_connect` | v0.6 mutation tools: two `gh_add_component` calls, one `gh_connect`. |
| `12_set_and_delete` | v0.6 mutation tools: `gh_set_input` then `gh_delete`. |

The v0.6 additions (11, 12) cover the mutation-tool surface introduced in the v0.6 milestone. They use the hermetic lane because canvas-state assertions are not yet in the v0.1 harness.

**v0.2 target — 50 fixtures (v0.7).** Breakdown by category:

| Category | Count | Scope |
|---|---|---|
| Read-only | 10 | `gh_get_all`, `gh_get_selected`, `gh_describe` variants on canvases of varying size. |
| Single-mutation | 15 | One-call scenarios: slider edits, panel content, single-component adds. |
| Multi-mutation | 15 | Two- or three-call scenarios: add + connect + set combinations. |
| Failure / clarification | 10 | Ambiguous prompts that should trigger `ask_user`; malformed inputs; graceful refusals. |

The 50-fixture target is the planned reference eval set. It mixes hermetic JSON fixtures and `.gh` fixtures; the ratio is driven by which scenarios can be captured without a Grasshopper document.

**Beyond v0.7 — 100+ fixtures (v0.8 onward).** The long-term suite includes real `.gh` canvases contributed by the author and by early users (with consent). This is where the fixture library starts to include the realistic canvas sizes architectural practice produces — 100-plus components, nested clusters, data-tree-sensitive definitions — and where the pass rate becomes ecologically valid rather than merely internally valid.

Fixture additions follow the policy in [GH-FIXTURE-SPEC.md](GH-FIXTURE-SPEC.md) §"Anti-patterns": no ambiguous prompts, no over-specified `after.gh`, no fixtures that only one implementation happens to pass, no duplicates of existing coverage. The review bar is explicit.

**Fixture-authoring workflow (hermetic lane).** A contributor proposing a new hermetic fixture follows this sequence:

1. Pick a user scenario the existing suite does not cover. Grep the existing fixture descriptions in [EVAL-v0.1.md](EVAL-v0.1.md) before starting — duplicates are rejected.
2. Write the `userPrompt` in one line, the way a real user would type it. No pre-disambiguation.
3. Decide what a correct session looks like in terms of assistant turns and tool calls. Sketch the `providerScript` on paper first.
4. Create `tests/Parti.Eval.v01/fixtures/NN_slug.json` with the next free number. The number only orders rows in the report; gaps are allowed.
5. Fill the script and the tool handlers. Keep tool-handler payloads minimal — the fixture is not a place to specify canonical tool outputs.
6. Fill the `expected` block with the minimum set of predicates that uniquely define success. Over-specifying predicates locks the fixture to one implementation.
7. Run `dotnet test` locally. The fixture is picked up automatically by the `[Theory]` runner.
8. Iterate until the row shows `pass`. Open a PR including the new fixture, a description of what scenario it covers, and the before/after aggregate (which should move from N/12 to (N+1)/13 with the addition).

**Fixture-authoring workflow (`.gh` lane).** Adding a `.gh` fixture is more involved because the canvas artifacts have to round-trip through Rhino 8. The workflow mirrors the hermetic lane with these extra steps inserted: build `before.gh` by hand in Rhino 8 with only the components the prompt references, record the prompt, either drive Parti against the prompt and save the result or build `after.gh` manually to match what Parti should produce, and hand-write `canvasAssertions` listing only the facts the prompt implies. Both `.gh` files must open in Rhino 8 without "missing component" warnings; a fixture that depends on a third-party plugin Rhino does not know about is rejected at review.

**Recording tool-call sequences for scripted fixtures.** A recommended flow for authoring the `providerScript` of a hermetic fixture is to first run the same prompt through a live provider in a one-off script, capture the assistant's actual tool-call sequence, and transcribe that sequence into the fixture JSON. This gives the scripted provider a shape that matches what a real model would do, rather than a shape the fixture author guesses at. The transcription is not a replay — the fixture still owns the canonical response — but it reduces the construct-validity gap between hermetic and live-model lanes.

---

## 8. Validity threats

A benchmark that does not document its own threats to validity is marketing. This section is modeled on the standard CS / HCI construct-validity taxonomy.

**Construct validity.** The scripted-provider hermetic lane does not exercise a real LLM. It exercises Parti's handling of the shape of a provider response — its decoder, its content-block parser, its tool-dispatch wiring. A fixture that passes in the hermetic lane only guarantees that Parti handled the scripted shape correctly. It says nothing about whether a real Claude or Llama call, given the same prompt, would produce that shape. The mitigation is the planned v0.7.2 live-model lane, which runs the same fixtures against real providers and publishes the delta between hermetic pass rate and live-model pass rate. Until that lane exists, every published pass rate must disclose which lane produced it.

**External validity.** Fixtures are authored by Parti's maintainer. Author bias toward scenarios Parti happens to handle well is a real risk. Mitigations: (a) the fixture spec is public and the `.gh` format is simple, so community contributors can submit fixtures under the project's open-source license; (b) the failure / clarification category in the v0.2 plan deliberately targets scenarios Parti is *not* expected to handle cleanly, forcing the suite to measure graceful-degradation behavior; (c) reports list the fixtures that fail and what their failure modes suggest about Parti's weak spots.

**Internal validity.** The system prompt and the tool definitions are coupled. A change to the prompt can cause the model to call a different tool; a change to a tool's schema can cause the model to phrase its replies differently. Treating either as an independent variable is incorrect. Mitigation: both are versioned together and the eval gate in [PROMPTS.md](PROMPTS.md) requires the suite to be re-run on every prompt change. In practice the coupling means a prompt-delta report is always a prompt-and-tool-surface delta report; the PR description documents any tool-schema change alongside the prompt change.

**Ecological validity.** The fixtures are simple. A realistic Grasshopper definition has dozens to hundreds of components, clusters, data-tree operations, and plugin-sourced components whose behavior Parti has no model of. The v0.7 suite is a starter set, not a production proxy. Mitigations: (a) v0.8 adds large-scale fixtures (roughly 100+ components) drawn from representative architectural parametric patterns; (b) v0.9+ adds real user definitions contributed under explicit consent; (c) reports state the fixture size distribution and caution readers that results do not yet extrapolate to production canvases.

**Statistical validity.** With 12 fixtures, a single failing fixture moves the aggregate by 8.3 percentage points. That is too little resolution for confidence intervals or significance testing. The v0.2 target of 50 fixtures improves resolution to 2 percentage points per fixture; the v0.8 target of 100+ drops it to under 1 point. Reports give the raw numerator-denominator (`passing / total`) rather than a confidence interval, and will flag the small-sample limitation explicitly.

The 50-fixture target also supports category-level rates without collapsing to unstable numbers: 15 single-mutation fixtures, for instance, give one failure a 6.7-point impact on the single-mutation pass rate, which is coarse but legible. Category rates are what most readers will want to cite, because they distinguish "Parti handles X well and Y badly" from a single blended aggregate. Reports give both the overall rate and the per-category rates, with the per-category rates noted as the more informative number for any specific claim about scenario handling.

**Observer effects.** Parti's maintainer writes prompts, writes fixtures, and evaluates the suite. The same person is incentivized to see a green suite. Mitigations: (a) the fixture specs define correctness in terms a second reader can apply, (b) all fixtures and their predicates are committed and diff-reviewable, (c) community fixture contributions are invited specifically to break this feedback loop.

**Provider-shape validity.** The hermetic lane assumes the canned provider responses faithfully reflect the shape of real Anthropic and Ollama responses. If a provider changes its response schema (a new content block type, a different stop-reason string, a re-shuffled tool-call envelope), the hermetic lane would continue to pass while the live lane would fail. Mitigations: (a) the `FakeAIProvider` shape is derived from the provider's public documentation and mirrored byte-for-byte against sample captures; (b) the planned v0.7.2 live-model lane is the canary — if it diverges from hermetic results, the shape has drifted; (c) direct NuGet versions are declared in the individual `.csproj` files so an upstream shape change surfaces as a deliberate upgrade, not a silent drift.

**Canvas-state validity (`.gh` fixtures).** Assertions against `after.gh` assume the canvas predicates cover the user-visible facts the prompt cares about. A fixture author who writes predicates the prompt does not actually imply — for example, asserting a specific slider value when the prompt only said "add a slider" — encodes implementation details as correctness. The anti-pattern is flagged in [GH-FIXTURE-SPEC.md](GH-FIXTURE-SPEC.md) §"Anti-patterns" but enforcement is by review, not by tooling. A v0.8 backlog item is a lint step that flags `canvasAssertions` referencing facts not present in the `userPrompt`; this is a best-effort heuristic rather than a guarantee.

---

## 9. Comparison with other evaluations

Nobody else publishes a benchmark.

A survey of public materials across Grasshopper-adjacent AI plugins as of April 2026 found no published, reproducible fixture-based benchmark.

Parti's methodology is, to our knowledge, the first public benchmark for AI-in-Grasshopper. The framing is not "Parti outperforms baseline X by Y percent" — that claim requires baselines that do not exist in any comparable form — but "Parti defines and publishes a methodology that a future comparison across vendors could use".

If another project later publishes a fixture-based benchmark, Parti's fixtures and predicates are MIT-equivalent-licensed for reuse (final license to be confirmed at v1.0) so the two suites can be unioned and cross-run. A cross-vendor benchmark is a long-term aspiration, not a near-term deliverable.

**Related evaluation frameworks.** The design draws on prior art in LLM-as-agent evaluation without copying any single framework wholesale. `SWE-bench` established the pattern of grounding correctness in real-repository fixtures with predicate-style assertions. `MultiPL-E` and `HumanEval` established the pattern of hermetic scripted evaluation on a small fixture set. `AgentBench` and `ToolBench` established the pattern of evaluating tool-using agents on multi-turn trajectories rather than single-shot outputs. Parti's benchmark is closest in spirit to the trajectory-style evaluations: a fixture is a prompt plus an expected trajectory shape, not a prompt plus a single expected string. What is specific to Parti is that the trajectory runs through a Grasshopper-domain tool surface and terminates in a canvas state, not in generic code.

**What Parti does not claim.** The benchmark does not claim that Parti is better than any other Grasshopper AI tool, because there is no comparable suite to measure them on. Parti also does not claim its fixture library is exhaustive; 50 fixtures is a starter set and the v0.8 backlog explicitly targets growth toward production scale. The framing is methodological rather than competitive.

---

## 10. Open issues and v0.7.x backlog

The items below are known gaps in the benchmark as currently shipped. Each is tagged with its target milestone.

- **Predicate extension for hermetic fixtures (partially shipped in v0.2; remainder in v0.7.1).** `toolCallsMinCount`, `toolCallsMaxCount`, `toolCallsInclude`, `toolCallsExclude`, `finalTextContainsAny`, `finalTextContainsAll` landed in v0.2 (see [EVAL-v0.2.md](EVAL-v0.2.md)). Still outstanding: `textMatchesRegex`, `toolCallOrder`, `toolCallArgsContain`, combinator tree, unknown-field validation at load time. Required to express the 50-fixture v0.2 target without stretching the current flat-predicate set.
- **Live-model run lane (v0.7.2).** A second CI job that runs the same fixture JSON files against live Anthropic and Ollama endpoints, captures token counts and turn counts, and emits a comparison report. Required for live-model numbers.
- **`.gh` predicate extensions (v0.7.1).** `componentAbsent`, `connectionAbsent`, data-tree-sensitive forms (`outputAccess`), and parameter-tree assertions on canvases where tree structure matters.
- **Safety instrumentation in the harness (v0.7.1).** Per-fixture checks that assert exactly one `GH_UndoRecord` is emitted per assistant turn on `.gh` mutation fixtures.
- **CI eval-delta comment (v0.7.1).** GitHub Action that posts pass-rate before and after on prompt-changing PRs, enforcing the [PROMPTS.md](PROMPTS.md) gate automatically.
- **Multimodal fixtures (v4.0).** Screenshot-in, expected-edit-out. Out of scope for v0.7; requires vision-capable providers and a fixture format that carries images.
- **Per-user canvas benchmarks with consent (v0.9+).** Real architectural definitions contributed by early users. Needs a privacy / consent protocol this document does not yet specify.
- **Statistical reporting (v1.0).** Bootstrap confidence intervals or Wilson score intervals on the pass rate once the suite is large enough to support them (target: 100+ fixtures).
- **Fixture-authoring lint (v0.8).** A heuristic checker that flags `canvasAssertions` referring to facts not present in the `userPrompt`, implementing the anti-pattern check from [GH-FIXTURE-SPEC.md](GH-FIXTURE-SPEC.md) as tooling rather than reviewer discipline.
- **Cross-version fixture compatibility matrix (v1.0).** A matrix run that replays the current fixture set against a set of pinned prior Parti releases, to expose which regressions a given release introduced. Produces a per-fixture timeline alongside the current per-fixture row.
- **Longitudinal pass-rate plot (v1.0).** The auto-generated report will include a small per-fixture sparkline of pass/fail over the last N releases, making regressions visible at a glance.

Each backlog item above has a GitHub issue template planned at v0.9 so contributors can file and track them against the published methodology rather than against freeform text.

---

## 11. How to cite

For the software artifact, a researcher can cite:

```bibtex
@software{tito2026parti,
  author    = {Zohar Tito},
  title     = {Parti: An Open-Source AI Copilot for Grasshopper},
  year      = {2026},
  url       = {https://github.com/zohartito/parti},
  license   = {LGPL-3.0-or-later}
}
```

Citations of specific fixtures should include the fixture id and the Parti commit hash, since a fixture may be revised. Example: "fixture `07_tool_error_then_retry` at Parti commit `abc1234`".

---

## 12. Links

- [EVAL-v0.1.md](EVAL-v0.1.md) — auto-generated pass/fail table. Regenerated on every `dotnet test` run.
- [EVAL-v0.2.md](EVAL-v0.2.md) — live predicate specification. Hand-authored; source of truth for the v0.2 predicate vocabulary.
- [GH-FIXTURE-SPEC.md](GH-FIXTURE-SPEC.md) — on-disk schema for `.gh` fixtures, including the canvas-predicate DSL.
- [PROMPTS.md](PROMPTS.md) — governance for changes to the shipped system prompt; defines the eval gate.
- [prompts/parti-foundations/SKILL.md](../prompts/parti-foundations/SKILL.md) — current canonical Layer 0 prompt; the historical `system_v0.6.md` is absent from this public checkout.
- [tests/Parti.Eval.v01/fixtures/README.md](../tests/Parti.Eval.v01/fixtures/README.md) — on-disk schema for hermetic JSON fixtures.
- [tests/Parti.Eval.v01/FixtureSchema.cs](../tests/Parti.Eval.v01/FixtureSchema.cs) — in-memory representation of a fixture; source of truth for the predicate set.
