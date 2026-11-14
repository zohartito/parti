// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// Ported from SmartHopper (https://github.com/architects-toolkit/SmartHopper)
// Original Copyright (c) SmartHopper contributors, LGPL-3.0-or-later

namespace Parti.Core.Ai;

/// <summary>
/// Plain-text message (user prompt, assistant reply, system instruction, or context note).
/// </summary>
public class AIInteractionText : AIInteractionBase
{
    /// <summary>
    /// The text content.
    /// </summary>
    public string? Content { get; set; }

    /// <summary>
    /// Optional reasoning from providers that expose it (Ollama <c>thinking</c>, OpenAI-compatible
    /// <c>reasoning</c>). This is bounded continuation metadata, never visible content: the UI shows it
    /// only through streamed reasoning deltas, and it is never part of a plan. The provider instance
    /// that produced it replays it on a tool-loop continuation; it is never sent to a different provider.
    /// </summary>
    public string? Reasoning { get; set; }

    public override string ToString()
    {
        var result = string.Empty;
        if (!string.IsNullOrEmpty(Reasoning))
        {
            result += $"<think>{Reasoning}</think>";
        }

        if (!string.IsNullOrEmpty(Content))
        {
            result += Content;
        }

        return result;
    }
}
