// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;

namespace Parti.Core.Sessions;

/// <summary>
/// Freezes, budgets, and identifies a provider tool-use array before anything reaches a
/// dispatcher. A frozen call is a deep copy: approval and execution therefore cannot observe
/// different argument objects if a provider, observer, or UI mutates the original return later.
/// </summary>
public sealed class ToolBatchGuard
{
    private readonly SessionOptions _options;
    private readonly ISet<string> _seenToolUseIds;
    private readonly Dictionary<string, PreparedBatch> _prepared = new(StringComparer.Ordinal);
    private int _callsUsed;
    private int _mutationsUsed;

    public ToolBatchGuard(SessionOptions options)
        : this(options, new HashSet<string>(StringComparer.Ordinal))
    {
    }

    /// <summary>
    /// Creates a guard that records accepted provider tool-use ids in <paramref name="seenToolUseIds"/>.
    /// Passing a set that outlives this guard (e.g. one per session, cleared by Reset) makes replay
    /// detection span user turns. The caller owns the set; it should use ordinal comparison.
    /// </summary>
    /// <param name="options">Budgets and limits.</param>
    /// <param name="seenToolUseIds">Replay ledger shared with the caller.</param>
    /// <param name="declaredArgumentsByTool">
    /// Optional tool name to declared schema property names (see <see cref="DeclaredArgumentsFrom"/>).
    /// When supplied, a call to a listed tool that carries any argument not declared in that tool's
    /// schema <c>properties</c> is rejected. Tools absent from the map are left to the dispatcher,
    /// which reports unknown tools itself.
    /// </param>
    public ToolBatchGuard(
        SessionOptions options,
        ISet<string> seenToolUseIds,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? declaredArgumentsByTool = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _seenToolUseIds = seenToolUseIds ?? throw new ArgumentNullException(nameof(seenToolUseIds));
        DeclaredArgumentsByTool = declaredArgumentsByTool;
    }

    /// <summary>
    /// Declared schema property names per tool, when supplied. Enforced by <see cref="TryFreeze"/>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>>? DeclaredArgumentsByTool { get; }

    /// <summary>
    /// Argument-name prefix reserved for host capabilities (e.g. the Grasshopper document lease)
    /// that the host attaches after approval. A provider can never supply such an argument.
    /// </summary>
    public const string ReservedArgumentPrefix = "__parti_";

    /// <summary>
    /// Builds the <c>declaredArgumentsByTool</c> map from tool definitions: each tool name maps to
    /// the property names of its input schema's top-level <c>properties</c> object (empty when the
    /// schema declares none). A later definition with the same name replaces an earlier one.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> DeclaredArgumentsFrom(
        IEnumerable<AIToolDefinition> definitions)
    {
        if (definitions is null)
        {
            throw new ArgumentNullException(nameof(definitions));
        }
        var map = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (definition.InputSchema?["properties"] is JObject properties)
            {
                foreach (var property in properties.Properties())
                {
                    names.Add(property.Name);
                }
            }
            map[definition.Name] = names;
        }
        return map;
    }

    public bool TryFreeze(
        IReadOnlyList<AIInteractionToolCall> source,
        IReadOnlyDictionary<string, bool> reversibilityByName,
        string turnId,
        out IReadOnlyList<AIInteractionToolCall> frozen,
        out string bindingId,
        out string error)
    {
        frozen = Array.Empty<AIInteractionToolCall>();
        bindingId = string.Empty;
        error = string.Empty;

        // Each rejection says how to recover, so the model splits the work instead of retrying it.
        if (source.Count > _options.MaxToolCallsPerResponse)
        {
            error = $"Tool response rejected: {source.Count} calls exceeds the per-response limit ({_options.MaxToolCallsPerResponse}); " +
                    $"nothing was run. Send at most {_options.MaxToolCallsPerResponse} calls per response and put the rest in the next response.";
            return false;
        }

        var mutationCount = source.Count(c => IsMutation(c, reversibilityByName));
        if (mutationCount > _options.MaxMutationCallsPerResponse)
        {
            error = $"Tool response rejected: {mutationCount} mutations exceeds the per-response limit ({_options.MaxMutationCallsPerResponse}); " +
                    $"nothing was changed. Build in stages of at most {_options.MaxMutationCallsPerResponse} changes: " +
                    "add the components in one response, wire them in the next, then set values.";
            return false;
        }
        if (_callsUsed + source.Count > _options.MaxToolCallsPerTurn ||
            _mutationsUsed + mutationCount > _options.MaxMutationCallsPerTurn)
        {
            // Counts are what this turn has requested (admitted batches), not what reached the canvas.
            var callsLeft = Math.Max(0, _options.MaxToolCallsPerTurn - _callsUsed);
            var changesLeft = Math.Max(0, _options.MaxMutationCallsPerTurn - _mutationsUsed);
            if (callsLeft == 0 || (mutationCount > 0 && changesLeft == 0))
            {
                error = "Tool response rejected: the turn call or mutation budget is exhausted " +
                        $"({_callsUsed} of {_options.MaxToolCallsPerTurn} calls and {_mutationsUsed} of {_options.MaxMutationCallsPerTurn} changes requested this message); " +
                        "nothing was run. Make no more changes this message" + (callsLeft > 0 ? " (you may still read the canvas)" : "") +
                        ". Tell the user what is built and what is left, and ask them to send a message to continue; " +
                        "the budget resets with their next message.";
            }
            else
            {
                error = $"Tool response rejected: this response has {source.Count} calls and {mutationCount} changes, but this message has only " +
                        $"{callsLeft} calls and {changesLeft} changes left; nothing was run. Send a smaller batch that fits, " +
                        "or stop and tell the user what is left; the budget resets with their next message.";
            }
            return false;
        }

        var copies = new List<AIInteractionToolCall>(source.Count);
        var responseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in source)
        {
            if (string.IsNullOrWhiteSpace(call.Id))
            {
                error = "Tool response rejected: every tool call must have a non-empty provider call id.";
                return false;
            }
            if (!responseIds.Add(call.Id!) || _seenToolUseIds.Contains(call.Id!))
            {
                error = "Tool response rejected: duplicate or replayed provider tool-call id.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(call.Name))
            {
                error = "Tool response rejected: a tool call has no name.";
                return false;
            }

            if (!ValidateArgumentNames(call.Name!, call.Arguments, out error))
            {
                return false;
            }
            if (call.Name == "gh_describe" && !HasStableGuid(call.Arguments, "target", out error))
            {
                return false;
            }
            if (IsMutation(call, reversibilityByName) && !ValidateMutationArguments(call.Name!, call.Arguments, out error))
            {
                return false;
            }

            var effective = PlanReviewBuilder.EffectiveArguments(call.Name!, call.Arguments);
            var json = effective.ToString(Formatting.None);
            if (Encoding.UTF8.GetByteCount(json) > _options.MaxToolArgumentBytes)
            {
                error = $"Tool response rejected: arguments for '{call.Name}' exceed the { _options.MaxToolArgumentBytes } byte limit.";
                return false;
            }
            copies.Add(new AIInteractionToolCall
            {
                Id = call.Id,
                Name = call.Name,
                Arguments = effective,
                Reasoning = call.Reasoning,
                Agent = call.Agent,
                TurnId = turnId,
            });
        }

        // The binding covers the exact turn, ordered ids/names, and canonical JSON args. It is
        // not an authorization secret; it is an audit/replay guard carried into the approval view.
        var canonical = turnId + "\n" + string.Join("\n", copies.Select(c =>
            c.Id + "\u001f" + c.Name + "\u001f" + c.Arguments.ToString(Formatting.None)));
        bindingId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        foreach (var id in responseIds) _seenToolUseIds.Add(id);
        _callsUsed += copies.Count;
        _mutationsUsed += mutationCount;
        _prepared[bindingId] = new PreparedBatch(turnId, copies.Select(ImmutableToolCall.From).ToArray());
        frozen = copies;
        return true;
    }

    /// <summary>
    /// Recomputes the plan binding from immutable canonical JSON immediately before dispatch and
    /// materializes fresh mutable call objects only for the dispatcher. A preview/observer cannot
    /// alter these objects after the user approves.
    /// </summary>
    public bool TryMaterializeForDispatch(
        string bindingId,
        string turnId,
        out IReadOnlyList<AIInteractionToolCall> calls,
        out string error)
    {
        calls = Array.Empty<AIInteractionToolCall>();
        error = string.Empty;
        if (!_prepared.Remove(bindingId, out var batch) || !string.Equals(batch.TurnId, turnId, StringComparison.Ordinal))
        {
            error = "Tool dispatch rejected: missing, replayed, or cross-turn approval binding.";
            return false;
        }
        var canonical = turnId + "\n" + string.Join("\n", batch.Calls.Select(c => c.Id + "\u001f" + c.Name + "\u001f" + c.ArgumentsJson));
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(bindingId)))
        {
            error = "Tool dispatch rejected: approval binding verification failed.";
            return false;
        }
        calls = batch.Calls.Select(c => c.Materialize()).ToArray();
        return true;
    }

    /// <summary>
    /// Strict argument names. Host-reserved names are never accepted from a provider and, when the
    /// caller supplied declared schemas, every argument must be declared by that tool's schema.
    /// This keeps the reviewed arguments equal to the executed ones: an undeclared argument (for
    /// example a <c>type</c> next to <c>componentGuid</c>) could otherwise steer a handler without
    /// being part of the identity the human approved.
    /// </summary>
    private bool ValidateArgumentNames(string name, JObject? arguments, out string error)
    {
        error = string.Empty;
        if (arguments is null)
        {
            return true;
        }
        foreach (var property in arguments.Properties())
        {
            if (property.Name.StartsWith(ReservedArgumentPrefix, StringComparison.Ordinal))
            {
                error = $"Tool response rejected: '{name}' carries the host-reserved argument '{property.Name}'.";
                return false;
            }
        }
        // gh_add_component executes exactly the reviewed componentGuid. A second identity field is
        // never accepted, whether or not declared schemas were supplied.
        if (name == "gh_add_component" && arguments.Property("type", StringComparison.Ordinal) is not null)
        {
            error = "Tool response rejected: 'gh_add_component' identifies the component only by 'componentGuid'; 'type' is not accepted.";
            return false;
        }
        if (DeclaredArgumentsByTool is not null && DeclaredArgumentsByTool.TryGetValue(name, out var declared))
        {
            foreach (var property in arguments.Properties())
            {
                if (!declared.Contains(property.Name))
                {
                    error = $"Tool response rejected: '{name}' does not declare the argument '{property.Name}'.";
                    return false;
                }
            }
        }
        return true;
    }

    private static bool IsMutation(AIInteractionToolCall call, IReadOnlyDictionary<string, bool> map) =>
        call.Name is string name && map.TryGetValue(name, out var reversible) && reversible;

    private static bool ValidateMutationArguments(string name, JObject arguments, out string error)
    {
        error = string.Empty;
        if (name is not ("gh_add_component" or "gh_connect" or "gh_set_input" or "gh_delete" or "gh_group" or "gh_layout" or "gh_relayout"))
        {
            error = $"Tool response rejected: reversible tool '{name}' has no registered canonical approval renderer.";
            return false;
        }
        // Never resolve ordinal display handles at the mutation boundary. The only accepted
        // existing-component handles are instance GUIDs, which are stable for a document.
        if (name == "gh_add_component" &&
            (arguments["componentGuid"] is not JValue componentGuid || !Guid.TryParse(componentGuid.ToString(), out _)))
        {
            error = "Tool response rejected: 'gh_add_component.componentGuid' must be an exact Grasshopper component GUID.";
            return false;
        }
        foreach (var field in TargetFields(name))
        {
            if (arguments[field] is not JValue value || !Guid.TryParse(value.ToString(), out _))
            {
                error = $"Tool response rejected: '{name}.{field}' must be a stable component instance GUID.";
                return false;
            }
        }
        if (name is "gh_group" or "gh_layout")
        {
            if (arguments["targets"] is not JArray targets || targets.Count == 0 ||
                targets.Any(t => t is not JValue || !Guid.TryParse(t.ToString(), out _)))
            {
                error = $"Tool response rejected: '{name}.targets' must be a non-empty array of stable component instance GUIDs.";
                return false;
            }
        }
        if (name == "gh_group" && arguments["colour"] is JValue colour &&
            !IsHexColour(colour.ToString()))
        {
            error = "Tool response rejected: 'gh_group.colour' must be an explicit #RRGGBB value.";
            return false;
        }
        if (name == "gh_layout" && arguments["arrangement"] is not JValue)
        {
            error = "Tool response rejected: 'gh_layout.arrangement' must be explicit; post-approval layout defaults are not allowed.";
            return false;
        }
        if (name == "gh_layout" && string.IsNullOrWhiteSpace(arguments["arrangement"]!.ToString()))
        {
            error = "Tool response rejected: 'gh_layout.arrangement' must be explicit; post-approval layout defaults are not allowed.";
            return false;
        }
        if (name == "gh_layout" && !HasPoint(arguments, "origin") ||
            name == "gh_layout" && !HasPoint(arguments, "spacing") ||
            name == "gh_relayout" && !HasPoint(arguments, "spacing"))
        {
            error = $"Tool response rejected: '{name}' must provide explicit origin/spacing values; post-approval layout defaults are not allowed.";
            return false;
        }
        return true;
    }

    private static IEnumerable<string> TargetFields(string name) => name switch
    {
        "gh_connect" => new[] { "from", "to" },
        "gh_set_input" or "gh_delete" => new[] { "target" },
        "gh_relayout" => new[] { "root" },
        _ => Array.Empty<string>(),
    };

    private static bool HasStableGuid(JObject arguments, string field, out string error)
    {
        if (arguments[field] is JValue value && Guid.TryParse(value.ToString(), out _))
        {
            error = string.Empty;
            return true;
        }
        error = $"Tool response rejected: 'gh_describe.{field}' must be a stable component instance GUID.";
        return false;
    }

    private static bool HasPoint(JObject args, string name) => args[name] is JObject point
        && point["x"]?.Type is JTokenType.Integer or JTokenType.Float
        && point["y"]?.Type is JTokenType.Integer or JTokenType.Float;

    private static bool IsHexColour(string value) => value.Length == 7 && value[0] == '#'
        && value.Skip(1).All(c => char.IsAsciiHexDigit(c));

    private sealed record PreparedBatch(string TurnId, IReadOnlyList<ImmutableToolCall> Calls);

    private sealed record ImmutableToolCall(string Id, string Name, string ArgumentsJson, string? Reasoning)
    {
        internal static ImmutableToolCall From(AIInteractionToolCall call) => new(
            call.Id!, call.Name!, call.Arguments.ToString(Formatting.None), call.Reasoning);

        internal AIInteractionToolCall Materialize() => new()
        {
            Id = Id,
            Name = Name,
            Arguments = JObject.Parse(ArgumentsJson),
            Reasoning = Reasoning,
            Agent = AIAgent.ToolCall,
        };
    }
}
