// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// Ported from SmartHopper (https://github.com/architects-toolkit/SmartHopper)
// Original Copyright (c) SmartHopper contributors, LGPL-3.0-or-later

namespace Parti.Core.Ai;

/// <summary>
/// Classification of an incremental <see cref="AIStreamChunk"/> emitted by
/// <see cref="IAIProvider.CallStreamAsync(AIRequestCall, System.Threading.CancellationToken)"/>.
/// </summary>
public enum AIStreamChunkKind
{
    /// <summary>
    /// Incremental text fragment. <see cref="AIStreamChunk.Text"/> carries the delta text only.
    /// </summary>
    TextDelta = 0,

    /// <summary>
    /// A new tool_use block has started. <see cref="AIStreamChunk.ToolName"/> and
    /// <see cref="AIStreamChunk.ToolUseId"/> are populated; tool arguments have not streamed yet.
    /// </summary>
    ToolCallStart,

    /// <summary>
    /// A fragment of a tool_use block's JSON arguments. <see cref="AIStreamChunk.ToolArgsPartial"/>
    /// contains only the newly received fragment; consumers that render partial JSON may append it
    /// locally. The provider keeps the cumulative value private until block completion.
    /// </summary>
    ToolArgDelta,

    /// <summary>
    /// The tool_use block has closed. <see cref="AIStreamChunk.ToolArgsPartial"/> holds the full
    /// (cumulative) JSON string for convenience.
    /// </summary>
    ToolCallComplete,

    /// <summary>
    /// Terminal event. <see cref="AIStreamChunk.Final"/> is populated with a cumulative
    /// <see cref="AIReturn"/>. Errors are also delivered as a Done chunk with
    /// <see cref="AIReturn.Status"/> set to <see cref="AICallStatus.Error"/>.
    /// </summary>
    Done,

    /// <summary>
    /// Incremental model reasoning ("thinking") fragment. <see cref="AIStreamChunk.Text"/> carries
    /// the delta text only. Reasoning is surfaced for display and is never folded into visible
    /// assistant content. Appended after <see cref="Done"/> so existing ordinal values are unchanged.
    /// </summary>
    ReasoningDelta,
}

/// <summary>
/// One unit of output in a streaming provider response. A stream yields zero or more
/// non-terminal chunks (text or tool fragments) followed by exactly one
/// <see cref="AIStreamChunkKind.Done"/> chunk carrying the cumulative <see cref="AIReturn"/>.
/// </summary>
/// <param name="Kind">Classification of this chunk.</param>
/// <param name="Text">Incremental text fragment (only populated for
/// <see cref="AIStreamChunkKind.TextDelta"/> and <see cref="AIStreamChunkKind.ReasoningDelta"/>).</param>
/// <param name="ToolName">Tool name (populated for all tool events).</param>
/// <param name="ToolUseId">Provider-assigned tool_use id, e.g. "toolu_..." (populated for all tool events).</param>
/// <param name="ToolArgsPartial">For <see cref="AIStreamChunkKind.ToolArgDelta"/>, the newly
/// received tool-arguments fragment. For <see cref="AIStreamChunkKind.ToolCallComplete"/>, the
/// complete JSON string before parsing. Keeping only fragments on delta chunks prevents quadratic
/// copying when a provider splits one arguments object into many events.</param>
/// <param name="Final">Cumulative final state. Non-null only when
/// <see cref="Kind"/> is <see cref="AIStreamChunkKind.Done"/>.</param>
/// <param name="StopReason">Provider stop_reason (end_turn / tool_use / max_tokens / stop_sequence).
/// Populated only on <see cref="AIStreamChunkKind.Done"/>.</param>
public sealed record AIStreamChunk(
    AIStreamChunkKind Kind,
    string? Text = null,
    string? ToolName = null,
    string? ToolUseId = null,
    string? ToolArgsPartial = null,
    AIReturn? Final = null,
    string? StopReason = null);
