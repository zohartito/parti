# Parti eval fixtures

Each `*.json` directly in this folder is one gated scenario. `FixtureRunner` runs it through the
**product loop**: `PartiComposition.CreateController` builds the same `ChatController` the chat window
uses (skill-bundle prompt, streaming, three-pillar gate, lock and catalog policies, strict declared
arguments, error-repair cap 5). Only the edges are doubles: a scripted streaming provider, a
`RecordingChatView`, a recording reviewer, a lease-carrying undo factory under the product's
`UndoScopeTurnEnvelopeFactory`, the Rhino 8.35 catalog snapshot (`tests/shared/rhino8-gh-catalog.json`),
and `EvalToolSet` in place of the Grasshopper primitives. No network, no Rhino.

- `live/` holds the live-model briefs run by `LiveModelRunner` (see `docs/EVAL-LIVE.md`). Not gated.
- `deferred/` holds the solver fixtures (26–32) for tools that are not registered this wave. Not run.

## Loading is strict

- Every object is deserialized with `MissingMemberHandling.Error`: an unknown key anywhere (a
  mistyped predicate, a stale field) fails the load.
- `id` must equal the file name, and `expected.terminalStatus` is required.
- Every tool name in `providerScript`, `toolHandlers`, and the `toolCalls*` predicates must be a
  product tool (`ProductToolSet` plus `ask_user` and `parti_record_decomposition`).
  `09_unknown_tool` opts out with `"expectUnknownTool": true`.
- Canvas state (`canvasSeed`, `canvas*` predicates) needs `"canvas": "hermetic"`.

## Pass rule (A14)

A fixture passes when every expectation holds, **including its expected terminal status**, and the
run's invariants hold: no document lease outside dispatch copies (history, view, plans, provider
requests), and one tool_result per tool_use unless paused. `25_error_repair_cap` expects `Error` and
passes when the loop errors. `docs/EVAL-v0.1.md` shows the runtime status in its own column and has
no timings, so it is byte-stable.

## Schema

```jsonc
{
  "id": "NN_slug",                       // equals the file name
  "description": "one line, shown in the report",
  "userPrompt": "sent with ChatController.SendAsync",
  "canvas": "canned | hermetic",         // default canned
  "canvasSeed": {                        // hermetic only
    "components": [{ "instanceGuid": "...", "componentGuid": "...", "nickname": "radius",
                     "x": 0, "y": 0, "value": 5, "min": 1, "max": 10, "selected": false }],
    "wires": [{ "from": "<instance>", "fromOutput": "0", "to": "<instance>", "toInput": "Radius" }],
    "locked": ["<instance>"]             // locked by the user (UI) before the prompt
  },
  "reviewer": "approve | reject",        // default approve; records every plan
  "clarificationAnswers": [{ "id": "q1", "answer": "..." }],  // applied with AnswerAsync on pause
  "expectUnknownTool": false,
  "sessionOptions": { "maxTurns": 3, "maxToolPasses": 10 },   // product budgets when absent
  "providerScript": [                    // entry N answers provider call N; the last one replays
    { "stopReason": "tool_use", "interactions": [
        { "kind": "text", "text": "streamed as a text delta" },
        { "kind": "reasoning", "text": "streamed as a reasoning delta" },
        { "kind": "tool_call", "toolId": "toolu_x", "toolName": "gh_add_component",
          "input": { "componentGuid": "57da07bd-ecab-415d-9d86-af36d7073abc", "x": 0, "y": 0 } } ] }
  ],
  "toolHandlers": {                      // Grasshopper primitives only; overrides the canvas
    "gh_add_component": { "result": {} },   // or "sequence": [...] or "error": "..."
  },
  "expected": { /* predicates below */ }
}
```

`gh_find_component`, `gh_plugin_check` and `gh_component_lock` always run their real handlers
(over the catalog snapshot and the controller's own lock registry). A canned fixture that calls a
primitive without a handler gets an error result. Every primitive call must carry a live document
lease, as in Rhino.

The hermetic canvas assigns instance GUIDs `c0000000-0000-4000-8000-000000000001`, `…002`, … in add
order, so a later scripted call can use the GUID an earlier result returned. It tracks components,
positions, values (sliders start at `0.0 < 0.5 < 1.0` and reject out-of-range values unless
`min`/`max` are given), wires and groups; it does not model parameters or solve.

## Predicates (closed set)

| predicate | meaning |
|-----------|---------|
| `terminalStatus` | **required**. Controller status after the run: `Idle`, `Paused` or `Error` (live briefs may list `Idle\|Paused`) |
| `finalStopReason`, `finalStatus`, `errorContains` | final return's stop reason, `AICallStatus`, error substring |
| `minInteractions`, `providerCallCount`, `toolCallCount` | history size, provider calls, calls that reached the dispatcher |
| `textContains`, `finalTextContains`, `finalTextContainsAny`, `finalTextContainsAll` | assistant text (first is case-sensitive) |
| `toolCallsInclude`, `toolCallsExclude`, `toolCallsMinCount`, `toolCallsMaxCount` | the model's tool calls, rejected ones included |
| `planReviewCount`, `planLabelsContain` | plans the reviewer saw; catalog-resolved labels |
| `decompositionComplete`, `decompositionInputsInclude`, `evaluationsInclude` | recorded three-pillar decomposition |
| `canvasHasComponent` (`componentGuid` or `anyOf` equally correct types, `min`, `max`), `canvasWireCount`, `canvasHasWire` (by type or instance GUID; `fromAnyOf`/`toAnyOf` for equally correct endpoints), `canvasUnchanged` | hermetic canvas state |
| `gateRejections`, `lockRejections`, `catalogRejections`, `rejectionMessagesContain` | host rejections in tool results |
| `lockedComponents`, `pausedWith` | controller lock list; question ids shown by `ask_user` |

## Adding a fixture

Drop `NN_slug.json` here; the `[Theory]` discovers it. Mutating fixtures must record a complete
decomposition first (`parti_record_decomposition` as the sole call), use exact component GUIDs from
the catalog, and target instance GUIDs taken from earlier results.
