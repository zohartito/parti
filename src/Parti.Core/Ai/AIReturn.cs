// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// Ported from SmartHopper (https://github.com/architects-toolkit/SmartHopper)
// Original Copyright (c) SmartHopper contributors, LGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace Parti.Core.Ai;

/// <summary>
/// Result of a single provider call: decoded interactions, stop reason, token usage, and status.
/// </summary>
public class AIReturn
{
    /// <summary>
    /// Decoded content blocks from the provider response (text, tool_use, etc.).
    /// </summary>
    public List<IAIInteraction> Interactions { get; set; } = new();

    /// <summary>
    /// Current status of the call (Processing / CallingTools / Finished / Error).
    /// </summary>
    public AICallStatus Status { get; set; } = AICallStatus.Idle;

    /// <summary>
    /// Provider stop_reason ("end_turn", "tool_use", "max_tokens", "stop_sequence").
    /// </summary>
    public string? StopReason { get; set; }

    /// <summary>
    /// Input tokens as reported by the provider.
    /// </summary>
    public int InputTokens { get; set; }

    /// <summary>
    /// Output tokens as reported by the provider.
    /// </summary>
    public int OutputTokens { get; set; }

    /// <summary>
    /// The request that produced this return (for diagnostics).
    /// </summary>
    public AIRequestCall? Request { get; set; }

    /// <summary>
    /// Error message when <see cref="Status"/> is Error, otherwise null.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// True when the call completed without error.
    /// </summary>
    public bool Success => Status != AICallStatus.Error && string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// Convenience: returns true if the assistant signalled a tool call in this return.
    /// </summary>
    public bool HasToolCalls => Interactions.OfType<AIInteractionToolCall>().Any(i => i is not AIInteractionToolResult);

    /// <summary>
    /// Populates this return as an error result.
    /// </summary>
    public void CreateError(string message, AIRequestCall? request = null)
    {
        Status = AICallStatus.Error;
        ErrorMessage = message;
        if (request != null)
        {
            Request = request;
        }
    }
}
