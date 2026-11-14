// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Tools;

namespace Parti.Integration.Tests;

/// <summary>
/// Hermetic <see cref="IToolDispatcher"/> for integration tests. Returns canned results from a
/// user-supplied dispatch function; tracks every call for assertions.
/// </summary>
public sealed class FakeToolDispatcher : IToolDispatcher
{
    private readonly IReadOnlyList<AIToolDefinition> _definitions;
    private readonly Func<string, JObject, JObject> _dispatch;

    /// <summary>Every tool call observed by <see cref="DispatchAsync"/>, in call order.</summary>
    public List<AIInteractionToolCall> Calls { get; } = new();

    /// <summary>Number of successful dispatches.</summary>
    public int DispatchCount { get; private set; }

    /// <summary>
    /// Constructs a fake dispatcher that surfaces <paramref name="definitions"/> to the provider and
    /// resolves each call through <paramref name="dispatch"/> (keyed by tool name + arguments).
    /// </summary>
    public FakeToolDispatcher(
        IReadOnlyList<AIToolDefinition> definitions,
        Func<string, JObject, JObject> dispatch)
    {
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }

    /// <inheritdoc />
    public IReadOnlyList<AIToolDefinition> GetDefinitions() => _definitions;

    /// <inheritdoc />
    public bool HasTool(string name)
    {
        foreach (var def in _definitions)
        {
            if (def.Name == name)
            {
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc />
    public Task<AIInteractionToolResult> DispatchAsync(AIInteractionToolCall call, CancellationToken ct = default)
    {
        if (call is null)
        {
            throw new ArgumentNullException(nameof(call));
        }

        Calls.Add(call);
        DispatchCount++;

        var payload = _dispatch(call.Name ?? string.Empty, call.Arguments ?? new JObject()) ?? new JObject();
        var result = new AIInteractionToolResult
        {
            Id = call.Id,
            Name = call.Name,
            Arguments = call.Arguments ?? new JObject(),
            Agent = AIAgent.ToolResult,
            Result = payload,
        };
        return Task.FromResult(result);
    }
}
