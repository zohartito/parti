// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Tools;

namespace Parti.Core.Sessions.Policies;

/// <summary>
/// Deterministic lock enforcement at freeze time. Rejects the whole frozen response when:
/// <list type="bullet">
/// <item>any of <c>gh_set_input.target</c>, <c>gh_delete.target</c>, <c>gh_connect.from</c>,
/// <c>gh_connect.to</c>, <c>gh_group.targets[]</c>, or <c>gh_layout.targets[]</c> names a locked
/// instance GUID (compared after <c>Guid.Parse(...).ToString("D")</c> normalization);</item>
/// <item>it contains <c>gh_relayout</c> while any lock exists (relayout moves the whole canvas);</item>
/// <item>it mixes <c>gh_component_lock</c> with a reversible call.</item>
/// </list>
/// The <c>gh_delete</c> adapter repeats the check on the leased document at dispatch, including the
/// components wired to the target in either direction. This policy compares literal GUIDs only; the
/// <c>gh_set_input</c>, <c>gh_connect</c> and <c>gh_delete</c> handlers refuse, at dispatch on the
/// leased document, any GUID that names a parameter nested inside a component, so a locked
/// component cannot be reached through one of its own parameters.
/// </summary>
public sealed class ComponentLockPolicy : IToolCallPolicy
{
    private readonly ComponentLockRegistry _locks;

    /// <summary>Creates a policy over the registry shared with the lock tool handler.</summary>
    public ComponentLockPolicy(ComponentLockRegistry locks) =>
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));

    /// <summary>The lock registry this policy consults.</summary>
    public ComponentLockRegistry Locks => _locks;

    /// <summary>Single-GUID argument fields that name an existing component, per tool.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> TargetFields { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["gh_set_input"] = new[] { "target" },
            ["gh_delete"] = new[] { "target" },
            ["gh_connect"] = new[] { "from", "to" },
        };

    /// <summary>GUID-array argument fields that name existing components, per tool.</summary>
    public static IReadOnlyDictionary<string, string> TargetArrayFields { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gh_group"] = "targets",
            ["gh_layout"] = "targets",
        };

    /// <inheritdoc />
    public bool TryValidate(
        IReadOnlyList<AIInteractionToolCall> frozen,
        IReadOnlyDictionary<string, bool> reversibilityByName,
        out string error)
    {
        error = string.Empty;
        if (frozen is null || frozen.Count == 0)
        {
            return true;
        }

        var hasLockCall = frozen.Any(c => c.Name == GhComponentLockTool.ToolName);
        var hasReversible = frozen.Any(c =>
            c.Name is string name && reversibilityByName is not null &&
            reversibilityByName.TryGetValue(name, out var reversible) && reversible);
        if (hasLockCall && hasReversible)
        {
            error = "Tool response rejected: gh_component_lock must not be combined with a canvas edit in the same response.";
            return false;
        }

        if (_locks.Count == 0)
        {
            return true;
        }

        foreach (var call in frozen)
        {
            var name = call.Name ?? string.Empty;
            if (name == "gh_relayout")
            {
                error = "Tool response rejected: gh_relayout moves the whole canvas and is not allowed while any component is locked. Use gh_layout on unlocked components instead.";
                return false;
            }
            foreach (var target in TargetsOf(name, call.Arguments))
            {
                if (_locks.IsLocked(target))
                {
                    ComponentLockRegistry.TryNormalize(target, out var id);
                    error = $"Tool response rejected: component {id} is locked by the user; '{name}' may not touch it. Ask the user to unlock it first.";
                    return false;
                }
            }
        }
        return true;
    }

    private static IEnumerable<string> TargetsOf(string name, JObject? arguments)
    {
        if (arguments is null)
        {
            yield break;
        }
        if (TargetFields.TryGetValue(name, out var fields))
        {
            foreach (var field in fields)
            {
                if (arguments[field] is JValue value && value.Type == JTokenType.String)
                {
                    yield return (string)value!;
                }
            }
        }
        if (TargetArrayFields.TryGetValue(name, out var arrayField) && arguments[arrayField] is JArray array)
        {
            foreach (var item in array)
            {
                if (item is JValue value && value.Type == JTokenType.String)
                {
                    yield return (string)value!;
                }
            }
        }
    }
}
