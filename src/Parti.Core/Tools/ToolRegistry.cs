// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;

namespace Parti.Core.Tools;

/// <summary>
/// Explicit tool registry. Implements <see cref="IToolDispatcher"/> without reflection —
/// callers register each tool by name with a definition and an async handler.
/// Opens the v5 platform story for third-party tool packs.
/// </summary>
public sealed class ToolRegistry : IToolDispatcher
{
    private readonly Dictionary<string, Entry> _tools = new(StringComparer.Ordinal);

    /// <summary>
    /// Register a tool by name with its definition and handler. Replaces any existing
    /// registration with the same name.
    /// </summary>
    public void Register(AIToolDefinition definition, Func<JObject, CancellationToken, Task<JObject>> handler)
    {
        if (definition is null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        _tools[definition.Name] = new Entry(definition, handler);
    }

    /// <inheritdoc />
    public IReadOnlyList<AIToolDefinition> GetDefinitions()
    {
        var list = new List<AIToolDefinition>(_tools.Count);
        foreach (var entry in _tools.Values)
        {
            list.Add(entry.Definition);
        }
        return list;
    }

    /// <inheritdoc />
    public bool HasTool(string name) => !string.IsNullOrEmpty(name) && _tools.ContainsKey(name);

    /// <inheritdoc />
    public async Task<AIInteractionToolResult> DispatchAsync(AIInteractionToolCall call, CancellationToken ct = default)
    {
        if (call is null)
        {
            throw new ArgumentNullException(nameof(call));
        }
        ct.ThrowIfCancellationRequested();

        var result = new AIInteractionToolResult
        {
            Id = call.Id,
            Name = call.Name,
            Arguments = call.Arguments ?? new JObject(),
        };

        if (string.IsNullOrEmpty(call.Name) || !_tools.TryGetValue(call.Name!, out var entry))
        {
            result.Result = new JObject
            {
                ["status"] = "error",
                ["message"] = $"Unknown tool: {call.Name}",
            };
            return result;
        }

        try
        {
            var args = call.Arguments ?? new JObject();
            var handlerResult = await entry.Handler(args, ct).ConfigureAwait(false);
            result.Result = handlerResult ?? new JObject();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Result = new JObject
            {
                ["status"] = "error",
                ["message"] = ex.Message,
            };
        }

        return result;
    }

    private readonly record struct Entry(AIToolDefinition Definition, Func<JObject, CancellationToken, Task<JObject>> Handler);
}
