// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// Ported from SmartHopper (https://github.com/architects-toolkit/SmartHopper)
// Original Copyright (c) SmartHopper contributors, LGPL-3.0-or-later

using System;
using System.Collections.Generic;
using Parti.Core.Ai;
using Parti.Core.Design;

namespace Parti.Core.Sessions;

/// <summary>
/// Observer of conversation session lifecycle events. Parti v0.4 exposes this as the only
/// observability surface for the session — no logging framework, no metrics system.
/// Implementations should be fast and non-throwing; the session does not trap observer exceptions.
/// </summary>
public interface IConversationObserver
{
    /// <summary>
    /// Fired once when <c>RunAsync</c> begins, before the first provider call.
    /// </summary>
    /// <param name="request">The first provider request that will be sent.</param>
    void OnStart(AIRequestCall request);

    /// <summary>
    /// Fired at the start of each assistant turn, after a fresh TurnId has been allocated.
    /// </summary>
    /// <param name="turnId">The TurnId shared by every interaction produced in this turn.</param>
    /// <param name="turnNumber">One-based turn counter (1..<see cref="SessionOptions.MaxTurns"/>).</param>
    void OnTurnStart(string turnId, int turnNumber);

    /// <summary>
    /// Fired once per interaction after it is persisted to session history. Covers user text,
    /// assistant text, tool calls, and tool results.
    /// </summary>
    /// <param name="interaction">The persisted interaction.</param>
    void OnInteractionCompleted(IAIInteraction interaction);

    /// <summary>
    /// Fired immediately before a tool is dispatched. Useful for UI indicators ("running gh_get_all...").
    /// </summary>
    /// <param name="toolCall">The pending tool call interaction.</param>
    void OnToolCall(AIInteractionToolCall toolCall);

    /// <summary>
    /// Fired immediately after a tool produces a result (success or error).
    /// </summary>
    /// <param name="toolResult">The persisted tool result interaction.</param>
    void OnToolResult(AIInteractionToolResult toolResult);

    /// <summary>
    /// Fired once when the session reaches a stable end state (end_turn, error, or cap hit).
    /// </summary>
    /// <param name="finalResult">The final return surfaced from <c>RunAsync</c>.</param>
    void OnFinal(AIReturn finalResult);

    /// <summary>
    /// Fired when the session surfaces an error (provider failure, cancellation, unexpected exception).
    /// Called before <see cref="OnFinal(AIReturn)"/> in error scenarios.
    /// </summary>
    /// <param name="error">The exception that caused the error.</param>
    void OnError(Exception error);

    /// <summary>
    /// v0.9: fired once per <c>RunAsync</c> / <c>ContinueAsync</c> / <c>ResumeAsync</c> call,
    /// immediately after the <see cref="Parti.Core.Prompts.SessionPromptBuilder"/> routes the
    /// user turn to its Layer 1 sub-skills, before <see cref="OnStart(AIRequestCall)"/>.
    /// Fired only when a builder is wired on the session; sessions using the static
    /// <see cref="SessionOptions.SystemPrompt"/> path do not trigger this event.
    /// </summary>
    /// <param name="turnId">The TurnId of the first turn in this call.</param>
    /// <param name="activatedSubSkills">
    /// Sub-skill names (e.g. <c>parti-ghcanvas</c>) whose Layer 1 bodies were appended to the
    /// system prompt for this call.
    /// </param>
    void OnSkillsActivated(string turnId, IReadOnlyList<string> activatedSubSkills)
    {
        // Default no-op so existing observer implementations do not need to change.
    }

    /// <summary>
    /// Fired for each incremental assistant text fragment when the session streams
    /// (<see cref="SessionOptions.Stream"/>). The persisted interaction still arrives through
    /// <see cref="OnInteractionCompleted(IAIInteraction)"/>.
    /// </summary>
    /// <param name="turnId">The TurnId of the streaming turn.</param>
    /// <param name="delta">The newly received text fragment only.</param>
    void OnTextDelta(string turnId, string delta)
    {
        // Default no-op so existing observer implementations do not need to change.
    }

    /// <summary>
    /// Fired for each incremental reasoning ("thinking") fragment when the session streams.
    /// Reasoning is display-only and never becomes history content.
    /// </summary>
    /// <param name="turnId">The TurnId of the streaming turn.</param>
    /// <param name="delta">The newly received reasoning fragment only.</param>
    void OnReasoningDelta(string turnId, string delta)
    {
        // Default no-op so existing observer implementations do not need to change.
    }

    /// <summary>
    /// Fired when the session pauses on an <c>ask_user</c> call and is waiting for answers.
    /// </summary>
    /// <param name="state">The paused state, including the pending questions.</param>
    void OnPaused(PausedState state)
    {
        // Default no-op so existing observer implementations do not need to change.
    }

    /// <summary>
    /// Fired after <c>parti_record_decomposition</c> successfully updates the session's decomposition.
    /// </summary>
    /// <param name="decomposition">The newly recorded decomposition.</param>
    /// <param name="revision">Monotonic revision number of the recorded decomposition.</param>
    void OnDecompositionRecorded(Decomposition decomposition, int revision)
    {
        // Default no-op so existing observer implementations do not need to change.
    }
}
