# Port Plan — v0.4 (SmartHopper → Parti)

*Produced 2026-04-19 from structural research over `research/smarthopper-src/`.*

## Goal

First end-to-end LLM loop: text prompt → Anthropic Messages API → `tool_use` → dispatch one of Parti's 4 existing primitives → `tool_result` → final text. Target: 5 working days.

---

## 1. File / class inventory

Projects in `research/smarthopper-src/src/`:

| Project | Purpose | Scope for v0.4 |
|---------|---------|---|
| SmartHopper.Infrastructure | Core AICall, Sessions, Tools, Providers framework | **CRITICAL** — port `ConversationSession` + `AIRequestCall` / `AIReturn` / `AIInteraction` |
| SmartHopper.Providers.Anthropic | Anthropic provider implementation | **IN SCOPE** — port verbatim |
| SmartHopper.Core.Grasshopper | GH-specific tools (`gh_get`, `gh_put`, etc.) | DEFER — we have our own primitives |
| SmartHopper.Core | Plugin lifecycle (GH glue) | OUT OF SCOPE — UI, menus |
| SmartHopper.Components | Chat / chat-agent components | OUT OF SCOPE — v0.5 problem |
| SmartHopper.Menu | Menu system | OUT OF SCOPE |
| SmartHopper.Providers.{OpenAI, DeepSeek, ...} | Other providers | OUT OF SCOPE — Anthropic first |

Key insight: the real meat is in `SmartHopper.Infrastructure`. Provider logic is thin (Anthropic ≈ 1,100 lines). Tool dispatch is reflection-based and we will replace it with an explicit registry.

---

## 2. AnthropicProvider port map

Files:
- `SmartHopper.Providers.Anthropic/AnthropicProvider.cs` (~1,100 lines)
- `SmartHopper.Providers.Anthropic/AnthropicProviderModels.cs` (small, copy)
- `SmartHopper.Providers.Anthropic/AnthropicStreamingAdapter.cs` (streaming — **defer to v0.5**)

**Methods we must port:**

- `PreCall(AIRequestCall request)` — set `x-api-key`, `anthropic-version: 2023-06-01`, endpoint `/v1/messages`
- `Encode(IAIInteraction interaction)` — builds JSON message block (role + content[])
- `Decode(JObject response)` — parses Anthropic response; switch on `content[].type` for `text` / `tool_use` / `thinking` blocks
- `CreateRequestBody(AIRequestCall request)` — assembles final POST body: model, messages, max_tokens, system, tools, tool_choice
- `SortContentBlocks(...)` — orders blocks so **text comes before tool_use** (Anthropic rejects the reverse)

**Request shape:**
```json
{
  "model": "claude-opus-4-7",
  "max_tokens": 4096,
  "system": "...",
  "messages": [ { "role": "user", "content": [ { "type": "text", "text": "..." } ] } ],
  "tools": [ { "name": "gh_add_component", "description": "...", "input_schema": { ... } } ],
  "tool_choice": { "type": "auto" }
}
```

**Response shape:**
```json
{
  "content": [
    { "type": "text", "text": "..." },
    { "type": "tool_use", "id": "toolu_...", "name": "gh_add_component", "input": { ... } }
  ],
  "stop_reason": "tool_use"
}
```

**Critical wire-format note:** Anthropic's `tool_use.input` is a JSON **object**; OpenAI's `tool_use.arguments` is a JSON **string**. The decoder on line ~648 of SmartHopper's provider handles this: `Arguments = block["input"] as JObject ?? new JObject()`.

**Headers:**
- `x-api-key: <key>`
- `anthropic-version: 2023-06-01` (still current April 2026)
- `anthropic-beta: structured-outputs-2025-11-13` — **skip for v0.4** (mutually exclusive with `tools[]` per API cheatsheet research)

**Dependencies inherited:**
- Newtonsoft.Json (keep inside provider, convert to `JsonObject` at plugin boundary)
- System.Net.Http
- Base types from Infrastructure

**Verdict: PORT VERBATIM** (minus streaming adapter). Lean, self-contained, no UI deps.

---

## 3. ConversationSession port map

Files:
- `SmartHopper.Infrastructure/AICall/Sessions/ConversationSession.cs` (~1,100 lines, 3 partials)
- `ConversationSession.ContextManagement.cs` (context summarization — **out of scope v0.4**)
- `ConversationSession.SpecialTurns.cs` (greeting, summarization — **out of scope v0.4**)
- `SessionOptions.cs` (copy verbatim)

**Turn counting:**
- `MaxTurns = 8` — only provider calls increment `turns++` (main file ~line 323)
- `MaxToolPasses = 4` — `ProcessPendingToolsAsync()` loop at ~line 888: `while (toolPass < options.MaxToolPasses)`
- Tool execution does NOT count against turn budget; only completions do

**Tool dispatch mechanism (what we're changing):**
- SmartHopper uses reflection-based auto-discovery via `AIToolManager`
- Looks for `IAIToolProvider` implementations in the `SmartHopper.Core.Grasshopper.AITools` namespace
- Calls `GetTools()` on each; stores in static `Dictionary<string, AITool>` by name
- **Parti replaces this with explicit `ToolRegistry.Register(name, schema, handler)`** — opens v5 platform story (third-party tool packs), avoids namespace-coupling fragility

**Error propagation:**
- Tool throws → `AIToolCall.Exec()` catches (~lines 181-186) → returns `AIReturn` with error via `ret.CreateToolError(message, this)`
- `ProcessPendingToolsAsync()` receives error return, appends to `preparedYields`, continues
- Validation errors → caught in `AIToolManager.ExecuteTool()`, returned as `AIReturn` with error

**Verdict: PORT WITH EDITS.** Keep: main turn loop (lines 199-324 non-streaming), tool processing, session options. Remove: `GenerateGreetingAsync()`, `CheckAndSummarizeContextAsync()`, special-turn handling (~60-80 lines deleted).

**Estimated effort:** 2-3 hours (copy, prune, adapt).

---

## 4. Tools in SmartHopper

SmartHopper ships 31 tools in `SmartHopper.Core.Grasshopper/AITools/`. Most are irrelevant to v0.4 because **Parti already has better primitives**.

**Parti vs SmartHopper:**

| Capability | Parti (v0.3) | SmartHopper |
|---|---|---|
| Short handles | `SimpleId` (num1, radius1) | GhJSON with GUID suffixes |
| Canvas snapshot | `CanvasSnapshot` (compressed keys) | `gh_get` variants (4 of them) |
| Add component | `GhAddComponent` + `NameResolver` cascade | `gh_put` (with undo bug) |
| Selective read | `GhGetSelected` | `gh_get_selected` |
| Deep inspection | `GhDescribe` | `gh_get_by_guid` |

**Use for v0.4:** Parti's 4 existing tools. No SmartHopper tools needed.

**Copy as examples in v0.6+ only if useful:** `gh_move`, `gh_component_preview`, `_gh_parameter_modifier` (flatten/graft).

---

## 5. The 2-step undo bug (don't reproduce)

**Location:** `SmartHopper.Core.Grasshopper/AITools/gh_put.cs` lines ~235-265.

**Pattern:**
```csharp
// Record undo event for REMOVAL only
ghDoc.UndoUtil.RecordRemoveObjectEvent("[SH] Replace ...", existingComponents.Values);

// Add new components via GhJson.Put() — NOT wrapped in undo
// Result: one Ctrl+Z removes old; second Ctrl+Z does nothing → canvas left inconsistent
```

**Parti fix (planned v0.5):** wrap both removal AND addition in one `GH_UndoRecord` batch via `UndoUtil.CreateGenericObjectEvent()` or `StartGroup()` / `EndGroup()`. Single Ctrl+Z reverts whole AI turn.

---

## 6. Port order (5 working days to first green e2e)

**Day 1-2 — Anthropic provider scaffolding**
- Copy `AnthropicProvider.cs`, `AnthropicProviderModels.cs` into `src/Parti.Providers.Anthropic/`
- Copy base types: `AIRequestCall`, `AIReturn`, `AIInteraction`, `AIInteractionToolCall`, `AIToolCall`
- Implement `PreCall`, `Encode`, `Decode`, `CreateRequestBody`, `SortContentBlocks`
- Smoke test: POST to `/v1/messages`, get text response, decode cleanly

**Day 3 — ConversationSession**
- Copy `ConversationSession.cs` into `src/Parti.Core/Sessions/`
- Prune: remove SpecialTurns, ContextManagement references
- Keep: main turn loop, `MaxTurns` / `MaxToolPasses` enforcement, `ProcessPendingToolsAsync()`
- Copy `SessionOptions.cs` verbatim
- Wire to new `IProviderExecutor` interface in `Parti.Core`

**Day 4 — ToolRegistry + 4 tools wired**
- Create `ToolRegistry` with explicit `Register(name, schema, handler)` API
- Register `gh_add_component`, `gh_get_all`, `gh_get_selected`, `gh_describe`
- Each tool: name, description, JSON input schema, async handler returning `JsonObject`
- Test: session calls `gh_get_all` via registry → tool runs on UI thread → result becomes `tool_result` in next turn

**Day 5 — End-to-end CLI harness**
- Build `tools/parti-cli/Program.cs` — takes `-prompt "..."`, runs conversation, logs tool calls + final text
- Either connect to live Rhino via IPC or stub GH doc in unit test
- Test: `parti-cli "add a number slider and a panel, connect them"` → 3-4 tool calls → canvas updates → done

---

## 7. Flags & risks

| Item | Risk | Mitigation |
|------|-----------|-----------|
| 2-step undo bug | Medium | Wrap all modifications in single undo batch |
| Anthropic wire format (input vs arguments) | Low | Decoder handles it — keep that code path |
| Reflection tool discovery coupling | Medium | Replace with explicit `ToolRegistry` |
| Streaming deferral | Low | Non-streaming works for v0.4; streaming is a v0.5 UX problem |
| MaxTurns / MaxToolPasses enforcement | Low | Simple integer guards, port verbatim |

**Total lines to port:** ~2,500 (provider + session + base types)
**Total lines to integrate:** ~500 (tool wiring + CLI harness)
**Complexity:** Medium. Mostly copy + light refactoring, no algorithmic redesign.

---

## 8. Attribution

Every ported file keeps its `SPDX-License-Identifier: LGPL-3.0-or-later` header and adds a line:
```
// Ported from SmartHopper (https://github.com/architects-toolkit/SmartHopper)
// Original Copyright (c) SmartHopper contributors
// Adapted for Parti by Zohar Tito, 2026
```

See `ATTRIBUTION.md` (to be created in v0.4) for the running list of ported files.
