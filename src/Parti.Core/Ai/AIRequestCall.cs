// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// Ported from SmartHopper (https://github.com/architects-toolkit/SmartHopper)
// Original Copyright (c) SmartHopper contributors, LGPL-3.0-or-later

using System;
using System.Collections.Generic;

namespace Parti.Core.Ai;

/// <summary>
/// A fully-specified single HTTP call to a provider. Populated by the session, mutated by the
/// provider's <see cref="IAIProvider.PreCall(AIRequestCall)"/>, and consumed by <see cref="IAIProvider.CreateRequestBody(AIRequestCall)"/>.
/// </summary>
public class AIRequestCall
{
    /// <summary>
    /// Model identifier (e.g. "claude-opus-4-7").
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// API key for the provider; set by the caller (never serialized into the body).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Absolute base URL of the provider (e.g. "https://api.anthropic.com/v1"). Concatenated with <see cref="Endpoint"/>.
    /// </summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>
    /// Endpoint path relative to <see cref="BaseUrl"/>, starting with '/' (e.g. "/messages").
    /// </summary>
    public string Endpoint { get; set; } = "/messages";

    /// <summary>
    /// HTTP method. Providers typically set this in <see cref="IAIProvider.PreCall(AIRequestCall)"/>.
    /// </summary>
    public string HttpMethod { get; set; } = "POST";

    /// <summary>
    /// Authentication scheme hint. Values used by the ported Anthropic provider: "x-api-key", "bearer".
    /// </summary>
    public string Authentication { get; set; } = "bearer";

    /// <summary>
    /// Content type for the POST body.
    /// </summary>
    public string ContentType { get; set; } = "application/json";

    /// <summary>
    /// Additional HTTP headers (case-insensitive keys). Providers add provider-specific headers
    /// (e.g. anthropic-version) here during <see cref="IAIProvider.PreCall(AIRequestCall)"/>.
    /// Reserved keys "Authorization" and "x-api-key" are applied from <see cref="ApiKey"/> by the executor.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maximum output tokens. Required by Anthropic's Messages API.
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Sampling temperature. Null (default) omits the field from the request body,
    /// which is required for claude-opus-4-7 (Anthropic deprecated temperature on that model).
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>
    /// Top-level system prompt. For Anthropic this populates the top-level "system" field, not a message.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Conversation history to send in this call. Includes user prompts, prior assistant replies,
    /// and tool call/result pairs.
    /// </summary>
    public List<IAIInteraction> Interactions { get; set; } = new();

    /// <summary>
    /// Tool definitions available for this call. Null or empty disables tool use.
    /// </summary>
    public List<AIToolDefinition> Tools { get; set; } = new();

    /// <summary>
    /// Anthropic tool_choice mode. "auto" (default), "any", "none", or a specific tool name.
    /// </summary>
    public string ToolChoice { get; set; } = "auto";

    /// <summary>
    /// Kind of request: generation (user-facing LLM call) vs. back-office (e.g. /models listing).
    /// </summary>
    public AIRequestKind RequestKind { get; set; } = AIRequestKind.Generation;

    /// <summary>
    /// Per-call HTTP timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Enables the Anthropic structured-outputs beta header. Disabled by default (v0.7 decision per PORT-PLAN).
    /// </summary>
    public bool EnableStructuredOutputsBeta { get; set; }

    /// <summary>
    /// Optional JSON schema that the response must conform to. When set and
    /// <see cref="EnableStructuredOutputsBeta"/> is true, enables structured outputs.
    /// </summary>
    public string? JsonOutputSchema { get; set; }
}
