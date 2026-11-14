# Parti — Architecture

Public summary of how the shipped product is wired (2026-09-27 product-wiring wave). The design record is
[design/2026-09-27-product-wiring.md](design/2026-09-27-product-wiring.md) plus
[design/2026-09-27-review-amendments.md](design/2026-09-27-review-amendments.md), which overrides it where they differ.

## Layers

- `Parti.Core` — provider contract (`IAIProvider`, streaming chunks), the single turn loop (`ConversationSession`),
  tool registry, `ToolBatchGuard` (strict arguments, fan-out limits, replayed-id rejection, plan binding), policies
  (component locks, component catalog, decomposition gate), the decomposition record and `PillarGate`, the skill-prompt
  loader (`SessionPromptBuilder` + `KeywordSkillRouter`), bounded HTTP helpers.
- `Parti.Core.Grasshopper` — Grasshopper tools and adapters, the live component catalog, the per-turn undo log and
  undo scope, document-lease capability, `ChatController` (a thin adapter over the session for the UI), and
  `PartiComposition` (the one place the product is assembled).
- `Parti.Components` — the `.gha`: the Parti Chat component, `ChatForm` (Eto.Forms), settings, question panel, plan
  dialog. Embeds `prompts/parti-foundations/**` as resources.
- `Parti.Providers.Anthropic`, `Parti.Providers.Ollama`, `Parti.Providers.OpenAI` (`OpenAICompatibleProvider`: OpenAI,
  OpenRouter, any `/v1` server).
- `Parti.Providers.Cli` — `SubscriptionCliProvider` runs the user's signed-in `claude` or `codex` CLI as the model, one
  process per provider call: the real executable (resolved from Settings, `PATH`, then install folders; never a shell)
  with its own tools, MCP servers, settings and plugins disabled, in a fresh temp folder. The system prompt and tool
  list go in a file, the transcript on stdin with per-call nonce role markers (canvas data only inside tool-result
  blocks), and the reply comes back as `{"text", "tool_calls":[{"id","name","arguments_json"}]}` enforced by the CLI's
  schema flag. Output is byte-capped and time-limited (process-tree kill); an unreadable reply is retried once. The
  tool calls then take the same path as any provider's: `ToolBatchGuard`, policies, gate, plan review.
- `Parti.Mcp` — MCP companion over stdio with a stub dispatcher (no live canvas).

## One turn

1. The user message goes to `ChatController.SendAsync` → `ConversationSession` (streaming on).
2. The prompt is Layer 0 of the skill bundle plus any keyword-routed Layer 1 skill; the tool list is `ProductToolSet`
   (10 Grasshopper tools + `gh_find_component`, `gh_plugin_check`, `gh_component_lock`) plus the session tools
   `ask_user` and `parti_record_decomposition`.
3. Each provider response is frozen by `ToolBatchGuard` (strict declared arguments, limits, binding id), then the
   policies run (locks, catalog), then the decomposition gate (no reversible call without a complete decomposition),
   then the plan reviewer (the Approve dialog) for any batch with a reversible call.
4. Approved calls are materialized with the document lease and dispatched one at a time; each tool records its own
   undo action at the moment it mutates; results are scrubbed of the lease before they enter history.
5. The turn's undo actions become one Grasshopper undo record, so one Ctrl+Z reverts the turn.

## Where the guarantees live

| Guarantee | Enforced in |
|---|---|
| Writes only to the pinned document | `GrasshopperDocumentCapability` lease, attached after approval, scrubbed from history |
| You approve what executes | `ToolBatchGuard` binding + `PlanReviewBuilder` (catalog-resolved labels) + adapters that execute only the frozen arguments |
| Decomposition before drawing | `DecompositionGate` + `PillarGate` (code, not prompt) |
| Locked components stay untouched | `ComponentLockPolicy` at freeze + lock checks on the leased document at dispatch |
| Bounded model output | `BoundedHttp` / provider budgets (the CLI provider: a byte cap on stdout and the reply file, and a per-call time limit); abnormal termination returns no tool calls |
| One undo per turn | `TurnUndoLog` + `GrasshopperUndoScope` |

Evidence: live runs in Rhino 8.35 on macOS (see `docs/EVAL-LIVE.md`) and the four test suites.
