// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// gh_component_lock — v0.7.5 tool, inspired by SmartHopper.
// Lets the model (and by extension the user via natural-language request) mark components
// as locked so mutation tools will refuse to touch them, and list the locks. Backed by the
// ComponentLockRegistry shared with ComponentLockPolicy. The model can never unlock: unlocking
// is a human-only action in the chat UI.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Sessions;

namespace Parti.Core.Tools;

/// <summary>
/// <c>gh_component_lock</c> tool: lock or list trusted components by instance GUID.
/// </summary>
public static class GhComponentLockTool
{
    /// <summary>Canonical tool name surfaced to providers.</summary>
    public const string ToolName = "gh_component_lock";

    /// <summary>Most targets accepted by one lock call.</summary>
    public const int MaxTargets = 32;

    /// <summary>
    /// Tool definition for the AI provider. Non-reversible because it changes session state, not
    /// canvas geometry. There is deliberately no <c>unlock</c> action.
    /// </summary>
    public static AIToolDefinition Definition { get; } = new AIToolDefinition(
        Name: ToolName,
        Description: "Lock trusted components, or list the locked ones. The host refuses any later edit, "
            + "connect, group, layout, or delete that names a locked instance GUID, and any canvas-wide relayout "
            + "while a lock exists. Use lock when the user says a part of the canvas must not be touched. "
            + "Only the user can unlock, from the chat window. Must not be combined with a canvas edit in the same response.",
        InputSchema: JObject.Parse(@"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""required"": [""action""],
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""lock"", ""list""] },
    ""targets"": {
      ""type"": ""array"",
      ""minItems"": 1,
      ""maxItems"": 32,
      ""items"": { ""type"": ""string"" },
      ""description"": ""Instance GUIDs; required for lock""
    }
  }
}"))
    {
        IsReversible = false,
    };

    /// <summary>
    /// Handler bound to the <paramref name="registry"/> shared with the lock policy. Results are
    /// <c>{"status":"ok","action":"lock|list","locked":[guid…],"changed":[guid…]}</c>. A lock call
    /// is all-or-nothing: if any target is not an instance GUID, nothing is locked.
    /// </summary>
    public static Func<JObject, CancellationToken, Task<JObject>> HandlerFor(ComponentLockRegistry registry)
    {
        if (registry is null) throw new ArgumentNullException(nameof(registry));

        return (args, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            var action = args?["action"]?.Type == JTokenType.String ? (string?)args["action"] : null;
            switch (action)
            {
                case null or "":
                    return Task.FromResult(Error("Missing required field 'action'."));

                case "list":
                    return Task.FromResult(Result("list", registry, Array.Empty<string>()));

                case "lock":
                {
                    if (args?["targets"] is not JArray targets || targets.Count == 0)
                    {
                        return Task.FromResult(Error("Action 'lock' requires a non-empty 'targets' array of instance GUIDs."));
                    }
                    if (targets.Count > MaxTargets)
                    {
                        return Task.FromResult(Error($"Action 'lock' accepts at most {MaxTargets} targets."));
                    }

                    var normalized = new List<string>(targets.Count);
                    foreach (var token in targets)
                    {
                        var raw = token.Type == JTokenType.String ? (string?)token : null;
                        if (!ComponentLockRegistry.TryNormalize(raw, out var id))
                        {
                            return Task.FromResult(Error("Every lock target must be a component instance GUID; nothing was locked."));
                        }
                        normalized.Add(id);
                    }

                    var changed = normalized.Distinct(StringComparer.Ordinal).Where(registry.Lock).ToList();
                    return Task.FromResult(Result("lock", registry, changed));
                }

                default:
                    return Task.FromResult(Error("Unknown action. Expected: lock | list. Only the user can unlock components."));
            }
        };
    }

    private static JObject Result(string action, ComponentLockRegistry registry, IReadOnlyList<string> changed) => new()
    {
        ["status"] = "ok",
        ["action"] = action,
        ["locked"] = new JArray(registry.ListLocked()),
        ["changed"] = new JArray(changed),
    };

    private static JObject Error(string message) => new()
    {
        ["status"] = "error",
        ["message"] = message,
    };
}
