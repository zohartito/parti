// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Parti.Core.Ai;

namespace Parti.Core.Tools;

/// <summary>
/// Shared contract between ConversationSession (which calls tools) and ToolRegistry
/// (which owns the handlers). Explicit — no reflection — opens the v5 third-party
/// tool-pack story.
/// </summary>
public interface IToolDispatcher
{
    /// <summary>
    /// Returns the definitions (name, description, input schema) that the provider
    /// surfaces to the model in the "tools" array of each request.
    /// </summary>
    IReadOnlyList<AIToolDefinition> GetDefinitions();

    /// <summary>
    /// Execute a single tool call and return the result interaction, which the session
    /// will append to the conversation history as a tool_result block on the next turn.
    /// </summary>
    Task<AIInteractionToolResult> DispatchAsync(AIInteractionToolCall call, CancellationToken ct = default);

    /// <summary>True if the dispatcher has a handler registered for the given tool name.</summary>
    bool HasTool(string name);
}
