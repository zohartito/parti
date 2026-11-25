# Parti Eval v0.2 — Predicate Specification

*Live spec. Supersedes the v0.1 predicate surface. Auto-generated pass/fail table lives at [EVAL-v0.1.md](EVAL-v0.1.md); methodology discussion lives at [BENCHMARK-METHODOLOGY.md](BENCHMARK-METHODOLOGY.md).*

---

## 1. Purpose

This document is the reference for the **hermetic fixture predicate vocabulary** consumed by `tests/Parti.Eval.v01/`. It describes the original predicate families and the current loading rules. The complete closed set, including product-loop and hermetic-canvas predicates, is listed in [fixtures/README.md](../tests/Parti.Eval.v01/fixtures/README.md#predicates-closed-set) and `FixtureExpected`. Examples below are `expected` fragments to merge into a complete fixture.

Fixture loader, runner, and evaluator source of truth:

- [`FixtureSchema.cs`](../tests/Parti.Eval.v01/FixtureSchema.cs) — in-memory representation of a fixture (every predicate here is a property on `FixtureExpected`).
- [`PredicateEvaluator.cs`](../tests/Parti.Eval.v01/PredicateEvaluator.cs) — predicate implementation; collects every violation into a structured failure list.
- [`EvalFixtureTests.cs`](../tests/Parti.Eval.v01/EvalFixtureTests.cs) — xUnit `[Theory]` that discovers fixtures and asserts on the evaluator's output.

A predicate added to this document must also be implemented in `PredicateEvaluator.Evaluate` and exercised in `PredicateEvaluatorTests`. The loader rejects unknown keys anywhere in the fixture, duplicate keys, and case-variant property names. Misspelled predicates fail loading; they do not silently pass.

---

## 2. Predicate vocabulary

`expected.terminalStatus` is required. Other fields are optional; null / missing means "do not check". Empty predicate operands are rejected. All string comparisons are `Ordinal` unless flagged as **case-insensitive**.

### 2.1 Session-shape predicates (v0.1, unchanged)

| Field | JSON type | Asserts |
|---|---|---|
| `terminalStatus` | string | **Required.** Controller status: `Idle`, `Paused`, or `Error`. Live briefs may use alternatives such as `Idle\|Paused`; scripted fixtures require one status. |
| `finalStopReason` | string | The last provider response's `StopReason` equals this string. Typical: `"end_turn"`, `"tool_use"`. |
| `finalStatus` | string | The `AIReturn.Status` of the final return equals this string. One of `"Finished"`, `"Error"`. |
| `minInteractions` | int | `session.History.Count` is at least this value. |
| `toolCallCount` | int | Exact count of tool calls that reached the product tool dispatcher. Does not include session-intercepted tools such as `ask_user`. |
| `providerCallCount` | int | Exact count of provider calls made (equal to number of scripted steps consumed). |
| `errorContains` | string | Substring that must appear in the final return's `ErrorMessage`. |
| `textContains` | string | Substring that must appear (case-sensitive) in at least one assistant `AIInteractionText.Content`. |

Example (`02_add_slider.json`):

```jsonc
{
  "expected": {
    "terminalStatus": "Idle",
    "finalStopReason": "end_turn",
    "finalStatus": "Finished",
    "toolCallCount": 1,
    "textContains": "Number Slider"
  }
}
```

### 2.2 Tool-call set predicates (v0.2)

| Field | JSON type | Asserts |
|---|---|---|
| `toolCallsInclude` | array of string | Every name listed must appear at least once in the session's tool-call history. Order not asserted. |
| `toolCallsExclude` | array of string | None of the listed names may appear in the session's tool-call history. |
| `toolCallsMinCount` | object `{tool: int}` | Per-tool lower bound. A tool absent from history counts as 0. Use `toolCallsMaxCount` with 0 to forbid a tool — `toolCallsMinCount: {"gh_delete": 0}` is always true and therefore useless. |
| `toolCallsMaxCount` | object `{tool: int}` | Per-tool upper bound. Value `0` means the tool must not be called. |

Counting rule: tool-call counts are sourced from `session.History` (not from the dispatcher), so session-intercepted tools such as `ask_user` are included. Rejected model calls still count in history. `ask_user` must be the sole tool call in its response; sibling calls cause rejection of the whole batch without dispatch or a pause. `parti_record_decomposition` likewise must be the sole tool call.

Example (`20_group_workflow.json`):

```jsonc
{
  "expected": {
    "terminalStatus": "Idle",
    "toolCallsMinCount": { "gh_add_component": 2, "gh_group": 1 },
    "toolCallsMaxCount": { "gh_delete": 0 }
  }
}
```

Example (`21_layout_grid.json`):

```jsonc
{
  "expected": {
    "terminalStatus": "Idle",
    "toolCallsMinCount": { "gh_add_component": 4, "gh_layout": 1 },
    "toolCallsMaxCount": { "gh_delete": 0 }
  }
}
```

### 2.3 Final-text predicates (v0.2)

| Field | JSON type | Asserts |
|---|---|---|
| `finalTextContains` | string | Substring that must appear in at least one assistant text block. **Case-insensitive.** |
| `finalTextContainsAny` | array of string | At least one of these substrings must appear in an assistant text block. **Case-insensitive.** |
| `finalTextContainsAll` | array of string | Every listed substring must appear across the assistant text blocks (in any block, not necessarily the same one). **Case-insensitive.** |

Counting rule: all three predicates scan the flat list of assistant text blocks (`AIInteractionText` entries where `Agent == Assistant`) that accumulated across every turn. This matches `textContains` semantics but adds case-insensitivity and set operators. If a fixture specifically needs last-turn-only matching, use `textContains` on a string known to appear only in the last turn; a dedicated last-turn predicate is on the v0.3 backlog.

Example (`22_ask_user_clarify.json`):

```jsonc
{
  "expected": {
    "terminalStatus": "Paused",
    "finalTextContainsAny": ["which", "clarify", "specify"]
  }
}
```

Example (`21_layout_grid.json`, using `contains_all`):

```jsonc
{
  "expected": {
    "terminalStatus": "Idle",
    "finalTextContainsAll": ["step 1", "step 2"]
  }
}
```

---

## 3. Failure reporting

`PredicateEvaluator.Evaluate` returns a `List<PredicateFailure>`. Each failure carries `PredicateName`, `Expected`, and `Actual`. The xUnit runner formats them as:

```
Fixture '22_ask_user_clarify' failed 2 predicate(s):
  - [toolCallsMinCount] expected: tool 'ask_user' called >= 1 time(s); actual: tool 'ask_user' called 0 time(s)
  - [finalTextContainsAny] expected: at least one of ['which', 'clarify', 'specify'] (case-insensitive); actual: ...
```

Every violation is reported in a single message so CI does not have to iterate on re-runs to unearth secondary failures. This is the behavioural difference from v0.1, where `Assert.Equal` / `Assert.Contains` stopped at the first violation.

---

## 4. Shipped fixtures by predicate

| Predicate | Fixture examples |
|---|---|
| `finalStopReason` | `01_text_only`, `02_add_slider`, `11_add_connect` (all existing fixtures) |
| `finalStatus` | all existing fixtures |
| `minInteractions` | `01`–`12` |
| `toolCallCount` | `02`–`12` |
| `providerCallCount` | `01`–`12` |
| `textContains` | `02`–`12` |
| `errorContains` | `08_max_turns_exhausted` |
| `toolCallsInclude` | covered by `PredicateEvaluatorTests` (in-memory) |
| `toolCallsExclude` | covered by `PredicateEvaluatorTests` (in-memory) |
| `toolCallsMinCount` | `20_group_workflow`, `21_layout_grid`, `22_ask_user_clarify` |
| `toolCallsMaxCount` | `20_group_workflow`, `21_layout_grid`, `22_ask_user_clarify` |
| `finalTextContains` | covered by `PredicateEvaluatorTests` (in-memory) |
| `finalTextContainsAny` | `20_group_workflow`, `22_ask_user_clarify` |
| `finalTextContainsAll` | `21_layout_grid` |

---

## 5. v0.3 predicate backlog

Predicates that came up while shipping v0.2 but were intentionally deferred:

- **`finalTextContainsNone: [...]`** — OR of excluded strings; useful for refusing to hallucinate obsolete tool names. Can be emulated today by hand-inverting assertions but deserves first-class support.
- **`finalTextMatchesRegex: "..."`** — needed when a confirmation string carries a resolved `SimpleId` the fixture author cannot predict. Flagged in [BENCHMARK-METHODOLOGY.md](BENCHMARK-METHODOLOGY.md) §4.1.
- **`toolCallOrder: [...]`** — ordered sequence of tool names. Today a fixture cannot express "must call `gh_describe` before `gh_delete`".
- **`toolCallArgsContain: {tool: {arg: value}}`** — argument-level assertion. v0.2 checks names and counts but treats `Arguments` as opaque.
- **Predicate combinators (AND / OR / NOT)** — tree-shaped predicates so a fixture can say "either this tool count with that text, or this error substring". The flat-field model scales to about a dozen predicates before it becomes easier to express invariants as a tree than as a conjunction of flat fields.
- **Last-turn-only text matching** — `lastAssistantTextContains(Any|All)` targeted at only the final assistant text block, not the entire conversation. `finalText*` predicates currently scan all assistant text.

Each of these unblocks specific fixture classes in the v0.7 suite plan (per [BENCHMARK-METHODOLOGY.md §7](BENCHMARK-METHODOLOGY.md)). They are tracked in the roadmap under v0.7.1 / v0.8.

---

## 6. Compatibility

The current loader preserves the older predicate fields but is stricter than the original v0.1 format. Older fixtures must add `expected.terminalStatus`, remove unknown keys, and use registered product tool names. Session tools must be sole calls; mutating scripts need a complete decomposition before writing. Follow the current [fixture schema](../tests/Parti.Eval.v01/fixtures/README.md) when migrating examples.

---

## 7. Links

- [BENCHMARK-METHODOLOGY.md](BENCHMARK-METHODOLOGY.md) — peer-review reference for the overall benchmark; §4.1 discusses the v0.1 → v0.2 predicate evolution.
- [EVAL-v0.1.md](EVAL-v0.1.md) — auto-generated pass/fail table. Regenerated on every `dotnet test` run.
- [tests/Parti.Eval.v01/fixtures/README.md](../tests/Parti.Eval.v01/fixtures/README.md) — on-disk fixture schema (JSON shape).
- [tests/Parti.Eval.v01/FixtureSchema.cs](../tests/Parti.Eval.v01/FixtureSchema.cs) — source of truth for the predicate set.
- [tests/Parti.Eval.v01/PredicateEvaluator.cs](../tests/Parti.Eval.v01/PredicateEvaluator.cs) — implementation.
- [tests/Parti.Eval.v01/PredicateEvaluatorTests.cs](../tests/Parti.Eval.v01/PredicateEvaluatorTests.cs) — pass/fail tests per predicate.
