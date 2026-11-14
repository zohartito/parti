# Attribution

Parti is LGPL-3.0-or-later. Third-party code ported into Parti keeps its original license and is acknowledged here.

---

## SmartHopper (LGPL-3.0-or-later)

Repo: https://github.com/architects-toolkit/SmartHopper

Files ported or adapted into Parti v0.4:

| Parti file | SmartHopper source |
|---|---|
| `src/Parti.Core/Ai/IAIProvider.cs` | `SmartHopper.Infrastructure/AICall/Core/` (lean subset) |
| `src/Parti.Core/Ai/AIRequestCall.cs` | `SmartHopper.Infrastructure/AICall/Core/Requests/AIRequestCall.cs` |
| `src/Parti.Core/Ai/AIReturn.cs` | `SmartHopper.Infrastructure/AICall/Core/Returns/AIReturn.cs` |
| `src/Parti.Core/Ai/AIInteractionBase.cs` + concrete interactions | `SmartHopper.Infrastructure/AICall/Core/Interactions/*.cs` |
| `src/Parti.Core/Ai/AIAgent.cs`, `AICallStatus.cs`, `AIRequestKind.cs` | `SmartHopper.Infrastructure/AICall/Core/Base/` |
| `src/Parti.Providers.Anthropic/AnthropicProvider.cs` | `SmartHopper.Providers.Anthropic/AnthropicProvider.cs` (732 lines, non-streaming path + v0.5 SSE `CallStreamAsync` adapted from the original `AnthropicStreamingAdapter`; beta header still disabled per PORT-PLAN-v0.4) |
| `src/Parti.Core/Ai/AIStreamChunk.cs` | Parti-native shape informed by SmartHopper's `AIReturn`-delta streaming model in `SmartHopper.Infrastructure/Streaming/IStreamingAdapter.cs` (different API: Parti emits discrete chunk kinds instead of cumulative `AIReturn` snapshots) |
| `src/Parti.Core/Sessions/ConversationSession.cs` | `SmartHopper.Infrastructure/AICall/Sessions/ConversationSession.cs` (pruned — SpecialTurns + ContextManagement not ported) |
| `src/Parti.Core/Sessions/SessionOptions.cs` | `SmartHopper.Infrastructure/AICall/Sessions/SessionOptions.cs` (extended with Parti session params) |
| `src/Parti.Core/Sessions/IConversationObserver.cs` | `SmartHopper.Infrastructure/AICall/Sessions/IConversationObserver.cs` |

Each ported file carries SPDX headers plus a port-attribution line. SmartHopper's tool-dispatch pattern (`AIToolManager` reflection scan) was studied but deliberately not ported — Parti uses explicit `ToolRegistry.Register(...)` for better extensibility.

---

## Patterns observed (not code-ported)

Some designs follow general patterns seen in other Grasshopper and Rhino AI tools. No code was copied; each was implemented independently:

- Compressed JSON canvas schema with short-handle ids: `src/Parti.Core.Grasshopper/CanvasSnapshot.cs`, `src/Parti.Core/SimpleId.cs`.
- Selection-scoped canvas read: `src/Parti.Core.Grasshopper/GhGetSelected.cs`.
- In-process MCP server inside Rhino 8 (planned).

