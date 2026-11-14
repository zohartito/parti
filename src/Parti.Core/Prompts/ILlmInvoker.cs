// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// ILlmInvoker — v0.9 minimal seam over "send a prompt to an LLM, get a string back".
// Kept deliberately tiny: this is the dependency boundary, not a full LLM client. Real
// implementations (Anthropic, OpenAI, local) live elsewhere and adapt into this shape.

using System.Threading;
using System.Threading.Tasks;

namespace Parti.Core.Prompts;

/// <summary>
/// One-shot LLM invocation seam. A single method: send a system prompt plus a user
/// prompt, await a raw string completion. No streaming, no tool-use — routing decisions
/// are small enough not to need either.
/// </summary>
public interface ILlmInvoker
{
    /// <summary>
    /// Sends <paramref name="systemPrompt"/> and <paramref name="userPrompt"/> to the
    /// underlying model and returns the raw completion string.
    /// </summary>
    Task<string> InvokeAsync(string systemPrompt, string userPrompt, CancellationToken ct);
}
