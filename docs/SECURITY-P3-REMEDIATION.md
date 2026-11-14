# P3 security remediation boundaries

This document records the three Low/P3 remediations applied after the document-capability,
approval-binding, fan-out, lock, and undo closures that came before them.

## Provider response budgets

`AnthropicProvider` treats provider output as a bounded transport, not as a consequence of
`max_tokens` or the request timeout. It rejects non-streaming and streaming success bodies above
1 MiB, preserves at most 16 KiB of an HTTP error diagnostic, and limits an SSE stream to 2,048
events, 65,536 characters per line, 524,288 accumulated text characters, and 262,144 characters
for one tool's arguments. Streaming tool-argument chunks now contain only the newly received
delta; the full JSON is materialized once when the content block closes.

The limits fail closed with an error result. Do not silently truncate a successful model response,
raise these limits without a measured compatibility need, or restore `ReadAsStringAsync` on any
Anthropic response path.

## Untrusted model/context data

The current prompts explicitly classify canvas labels, panels, component/plugin metadata, and
tool results as untrusted data. Instructions embedded in that data cannot change the system
rules. This is defense in depth: mutation authority remains deterministic in the reviewed,
document-pinned dispatcher and is not delegated to prompt compliance.

Do not treat this prompt boundary as permission to loosen consent, lock, document-identity, or
tool-batch checks. New provider paths and prompt resources must retain an equivalent untrusted-data
instruction before accepting canvas-derived free text.

## Interactive terminal output

`parti-cli` encodes C0 and C1 control characters before it prints provider-controlled assistant
text, tool-call arguments, or provider error text. Newlines remain readable; escape, carriage
return, tab, DEL, and C1 controls are rendered as `\\uXXXX` literals.

Do not write provider output directly to `Console.WriteLine`, `Console.Error.WriteLine`, or a
terminal logging sink. Route it through `TerminalTextSanitizer.Sanitize`. This safeguard covers
terminal display/clipboard manipulation; it is not an authorization mechanism and does not claim
to make arbitrary terminal escape languages safe in other UI sinks.

## Local and OpenAI-compatible provider budgets

`OllamaProvider` (native `/api/chat`, NDJSON) and `OpenAICompatibleProvider` (`/chat/completions`,
SSE; OpenAI, OpenRouter, Ollama `/v1`) read every response through
`src/Parti.Core/Ai/BoundedHttp.cs`: `BoundedReadStream` (byte budget, with a declared
Content-Length checked before reading), `BoundedLineReader` (per-line and physical-line budgets,
strict UTF-8), `ReadBodyAsync`, and `ReadErrorDiagnosticAsync`. The Anthropic numbers above do not
fit per-token NDJSON (Ollama emits one ~150-byte line per generated token), so these providers use
their own limits. Anthropic keeps its audited copy of the helpers and was not refactored.

| Limit | Value | Why |
|---|---|---|
| Non-streaming body | 1 MiB | as Anthropic |
| Error diagnostic | 16 KiB, then `[diagnostic truncated]` | the only material that is truncated, because it is never parsed |
| Output token clamp | `num_predict` / token-limit field clamped to [256, 16,384] | bounds the event count below |
| Stream events (NDJSON objects / SSE `data:` payloads) | 33,024 = 2 × 16,384 + 256 | reasoning and content tokens may each be an event, plus terminal/usage/error events |
| Physical lines | 66,048 (NDJSON), 132,096 (SSE) | counted separately from events; SSE adds a blank separator per event and comments |
| Stream bytes | 16 MiB | ≥ events × 480 bytes; a live qwen3.6:27b token line is ~150 bytes |
| Line length | 300,000 chars | ≥ one tool's argument limit plus envelope, because Ollama sends a whole tool call in one line |
| Content text / reasoning text | 524,288 chars each | |
| `reasoning_details` material | 524,288 chars, 256 entries | |
| One tool's arguments | 262,144 chars | |
| Tool calls per response | 64 (indices 0–63) | the session's `ToolBatchGuard` applies tighter per-response limits |

`NumPredictClampAndLineBudgetAreConsistent` asserts this arithmetic for both providers, and the
maximum-token and at-the-argument-limit stream tests prove the limits admit legitimate output.
Every limit fails closed with an error result; none truncates. `ReadAsStringAsync` is not used on
any of these provider paths. `AIRequestCall.TimeoutSeconds` bounds the wait for headers and each
idle gap between lines (a local model can generate for longer than the timeout while it keeps
streaming); external cancellation still aborts immediately.

**Atomic batch finalization.** Tool calls are buffered and released only after a normal
completion: Ollama `done:true` without `done_reason:"length"`; SSE `finish_reason` of `tool_calls`
or `stop` followed by `[DONE]` (the usage-only final chunk with empty `choices` is consumed). A
stream that ends early, a length or content-filter stop, a malformed line, an in-stream `error`
object, unparsable tool arguments, or a tool call whose id/name changes mid-stream is an error with
**no** tool calls, and no `ToolCallStart`/`ToolCallComplete` chunk is emitted for it. A text-only
length stop returns the text with stop reason `max_tokens`; a reasoning-only length stop is the
error "model used its whole output budget on reasoning; raise num_predict".

**Reasoning is continuation metadata, not content.** Ollama `thinking` and OpenAI-compatible
`reasoning` / `reasoning_content` / `reasoning_details` are streamed as `ReasoningDelta` for display
and stored on the assistant interaction's `Reasoning` property, never in `Content`, the plan, or a
tool result. The provider instance that produced it records it in a private weak table (keyed by
the interaction and by the reasoning string instance) and replays it — under its original field
name — only for assistant messages after the last user message (the current tool loop) sent
through that same instance. Reasoning in history that this instance did not produce (another
provider, a restored transcript, a hand-built interaction) is never sent.

**Endpoints and credentials.** `BaseUrl` must be absolute `http`/`https` with a host and no
user-info, query or fragment; options are snapshotted at construction. A request whose `BaseUrl`
differs from the configured endpoint is refused before any I/O. Ollama never sends
`Authorization` (`PreCall` sets `Authentication="none"`). The OpenAI-compatible bearer key goes
only to the configured host, is refused over plain `http` to a non-loopback host, and only the
allowlisted `HTTP-Referer` and `X-Title` extra headers are added (`request.Headers` is not
forwarded). Proofs: `ProviderEndpointValidationTests`, `OllamaStreamingTests.*Budget*`,
`OpenAICompatibleProviderTests.*Budget*`.
