// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Tools;

namespace Parti.Core.Sessions;

/// <summary>
/// Shared helpers for producing a <see cref="ToolCallPlan"/> from pending tool_use blocks. Used
/// by <see cref="ConversationSession"/> (non-streaming path) and the streaming chat controller so
/// the two callers surface identical previews for the same plan.
/// </summary>
public static class PlanReviewBuilder
{
    /// <summary>
    /// Build a human-readable <see cref="ToolCallPlan"/> from <paramref name="pendingCalls"/> and
    /// the optional assistant preamble text in <paramref name="assistantText"/>. Pass the
    /// reversibility map built with <see cref="BuildReversibilityLookup"/>.
    /// </summary>
    public static ToolCallPlan BuildPlan(
        IReadOnlyList<AIInteractionToolCall> pendingCalls,
        string? assistantText,
        IReadOnlyDictionary<string, bool> reversibilityByName) =>
        BuildPlan(pendingCalls, assistantText, reversibilityByName, catalog: null);

    /// <summary>
    /// As <see cref="BuildPlan(IReadOnlyList{AIInteractionToolCall}, string?, IReadOnlyDictionary{string, bool})"/>,
    /// and additionally resolves each <c>gh_add_component</c> call against the host
    /// <paramref name="catalog"/>. The resolved catalog entry (name, category/subcategory, library,
    /// obsolete flag, GUID) becomes <see cref="ToolCallSummary.ResolvedLabel"/> and the preview.
    /// The model-supplied <c>nickname</c> is never used as the component identity.
    /// </summary>
    public static ToolCallPlan BuildPlan(
        IReadOnlyList<AIInteractionToolCall> pendingCalls,
        string? assistantText,
        IReadOnlyDictionary<string, bool> reversibilityByName,
        IComponentCatalog? catalog)
    {
        if (pendingCalls is null)
        {
            throw new ArgumentNullException(nameof(pendingCalls));
        }
        if (reversibilityByName is null)
        {
            throw new ArgumentNullException(nameof(reversibilityByName));
        }

        var summaries = new List<ToolCallSummary>(pendingCalls.Count);
        foreach (var call in pendingCalls)
        {
            var name = call.Name ?? "(unknown)";
            bool isReversible = call.Name is string n
                && reversibilityByName.TryGetValue(n, out var rev) && rev;
            var effectiveArguments = EffectiveArguments(name, call.Arguments);
            var preview = SummarizeArguments(name, effectiveArguments);
            string? resolvedLabel = null;
            if (name == "gh_add_component")
            {
                resolvedLabel = ResolveComponentLabel(effectiveArguments, catalog, out var shortLabel);
                preview = Truncate(WithPosition(shortLabel, effectiveArguments), 80);
            }
            summaries.Add(new ToolCallSummary(
                name,
                isReversible,
                preview,
                effectiveArguments.ToString(Newtonsoft.Json.Formatting.None),
                resolvedLabel));
        }

        var trimmed = string.IsNullOrWhiteSpace(assistantText) ? null : assistantText!.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            trimmed = null;
        }

        return new ToolCallPlan(summaries, trimmed);
    }

    /// <summary>
    /// Copies model arguments into the exact object used for approval and dispatch, expanding the
    /// few historical mutation defaults. This deliberately makes defaults visible rather than
    /// letting handlers invent values after the user has approved a plan.
    /// </summary>
    public static JObject EffectiveArguments(string toolName, JObject? arguments)
    {
        var effective = arguments is null
            ? new JObject()
            : (JObject)arguments.DeepClone();

        switch (toolName)
        {
            case "gh_add_component":
                AddIfMissing(effective, "x", 0.0);
                AddIfMissing(effective, "y", 0.0);
                AddIfMissing(effective, "nickname", JValue.CreateNull());
                break;
            case "gh_connect":
                AddIfMissing(effective, "fromOutput", 0);
                AddIfMissing(effective, "toInput", 0);
                break;
            case "gh_set_input":
                AddIfMissing(effective, "input", 0);
                break;
            case "gh_group":
                AddIfMissing(effective, "nickname", JValue.CreateNull());
                AddIfMissing(effective, "colour", "#FFC107");
                break;
        }

        return effective;
    }

    private static void AddIfMissing(JObject args, string name, JToken value)
    {
        if (args[name] is null or { Type: JTokenType.Null })
        {
            args[name] = value;
        }
    }

    private static void AddIfMissing(JObject args, string name, string value) => AddIfMissing(args, name, new JValue(value));
    private static void AddIfMissing(JObject args, string name, int value) => AddIfMissing(args, name, new JValue(value));
    private static void AddIfMissing(JObject args, string name, double value) => AddIfMissing(args, name, new JValue(value));

    /// <summary>
    /// True when any call in <paramref name="pendingCalls"/> is marked reversible in the supplied
    /// lookup. Used to decide whether a batch deserves a plan review.
    /// </summary>
    public static bool HasAnyReversible(
        IReadOnlyList<AIInteractionToolCall> pendingCalls,
        IReadOnlyDictionary<string, bool> reversibilityByName)
    {
        if (pendingCalls is null || reversibilityByName is null)
        {
            return false;
        }

        foreach (var call in pendingCalls)
        {
            if (call.Name is string n && reversibilityByName.TryGetValue(n, out var rev) && rev)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Collapse assistant-text interactions from a provider return into a single preamble string,
    /// or <c>null</c> when no assistant text was emitted alongside the tool_use blocks.
    /// </summary>
    public static string? ExtractAssistantText(IEnumerable<IAIInteraction> interactions)
    {
        if (interactions is null)
        {
            return null;
        }
        var chunks = interactions
            .OfType<AIInteractionText>()
            .Where(t => t.Agent == AIAgent.Assistant && !string.IsNullOrWhiteSpace(t.Content))
            .Select(t => t.Content!)
            .ToList();
        if (chunks.Count == 0)
        {
            return null;
        }
        var joined = string.Join("\n", chunks).Trim();
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    /// <summary>
    /// Build a tool-name -> IsReversible map from <paramref name="definitions"/>. The built-in
    /// <c>ask_user</c> tool is included explicitly so review code never mistakes it for a
    /// reversible canvas mutation.
    /// </summary>
    public static Dictionary<string, bool> BuildReversibilityLookup(IEnumerable<AIToolDefinition> definitions)
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (definitions is not null)
        {
            foreach (var def in definitions)
            {
                map[def.Name] = def.IsReversible;
            }
        }
        map[AskUserTool.ToolName] = AskUserTool.Definition.IsReversible;
        return map;
    }

    /// <summary>
    /// Produce a short human-readable preview of the tool arguments. Handles the known Grasshopper
    /// tools ("gh_*") with tailored one-liners; falls back to a comma-separated key=value dump for
    /// anything else. This is only a secondary summary: consent UI must show RenderedArguments.
    /// </summary>
    public static string? SummarizeArguments(string toolName, JObject? arguments)
    {
        if (arguments is null || !arguments.HasValues)
        {
            return null;
        }

        string preview = toolName switch
        {
            "gh_add_component" => FormatAddComponent(arguments),
            "gh_connect" => FormatConnect(arguments),
            "gh_set_input" => FormatSetInput(arguments),
            "gh_delete" => FormatDelete(arguments),
            "gh_group" => FormatGroup(arguments),
            "gh_layout" => FormatLayout(arguments),
            "gh_relayout" => "tidy the whole canvas",
            "gh_describe" => FormatDescribe(arguments),
            _ => FormatGenericArguments(arguments),
        };

        return Truncate(preview, 80);
    }

    /// <summary>
    /// Canonical plan label for a catalog entry, e.g.
    /// <c>Number Slider [Params/Input · Grasshopper] 57da07bd-ecab-415d-9d86-af36d7073abc</c>,
    /// with <c> (obsolete)</c> after the bracket when the type is obsolete.
    /// </summary>
    public static string FormatComponentLabel(ComponentCatalogEntry entry)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }
        return $"{ShortComponentLabel(entry)} {entry.ComponentGuid:D}";
    }

    /// <summary>
    /// Canonical plan label for an existing canvas object, e.g.
    /// <c>Circle [Curve/Primitive] 0f3c…-instance-guid</c>. Built from the host's object (type name
    /// and category), never from its nickname, which the model or a file can set to anything.
    /// </summary>
    public static string FormatInstanceLabel(string? name, string? category, string? subCategory, Guid instance)
    {
        var typeName = string.IsNullOrWhiteSpace(name) ? "(unnamed object)" : name!.Trim();
        var place = string.IsNullOrWhiteSpace(category) && string.IsNullOrWhiteSpace(subCategory)
            ? string.Empty
            : $" [{category?.Trim()}/{subCategory?.Trim()}]";
        return $"{typeName}{place} {instance:D}";
    }

    /// <summary>
    /// Returns <paramref name="plan"/> with a <see cref="ToolCallSummary.ResolvedLabel"/> on every
    /// <c>gh_delete</c> call whose <c>target</c> is an instance GUID, so the approver sees what will
    /// be deleted rather than a bare GUID. <paramref name="describe"/> is the host's lookup on the
    /// pinned document (null when the object is not there). Display only: the rendered arguments
    /// that are approved and dispatched are unchanged.
    /// </summary>
    public static ToolCallPlan WithInstanceLabels(ToolCallPlan plan, Func<Guid, string?> describe)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }
        if (describe is null)
        {
            throw new ArgumentNullException(nameof(describe));
        }

        var changed = false;
        var calls = new List<ToolCallSummary>(plan.Calls.Count);
        foreach (var call in plan.Calls)
        {
            if (call.Name == "gh_delete"
                && string.IsNullOrWhiteSpace(call.ResolvedLabel)
                && TryReadTarget(call.RenderedArguments, out var instance))
            {
                string? label;
                try
                {
                    label = describe(instance);
                }
                catch
                {
                    label = null;
                }
                calls.Add(call with { ResolvedLabel = label ?? $"not found on the pinned canvas {instance:D}" });
                changed = true;
                continue;
            }
            calls.Add(call);
        }

        return changed ? plan with { Calls = calls } : plan;
    }

    private static bool TryReadTarget(string? renderedArguments, out Guid instance)
    {
        instance = Guid.Empty;
        if (string.IsNullOrWhiteSpace(renderedArguments))
        {
            return false;
        }
        try
        {
            return JObject.Parse(renderedArguments!)["target"] is JValue { Type: JTokenType.String } target
                && Guid.TryParse((string?)target, out instance);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return false;
        }
    }

    private static string ShortComponentLabel(ComponentCatalogEntry entry) =>
        $"{entry.Name} [{entry.Category}/{entry.SubCategory} · {entry.Library}]"
        + (entry.Obsolete ? " (obsolete)" : string.Empty);

    /// <summary>
    /// Resolves <c>componentGuid</c> through the catalog. Never reads <c>nickname</c>, <c>name</c>,
    /// or <c>type</c>: the identity shown to the human is the one the adapter executes. Returns the
    /// full label when the catalog resolves the GUID, otherwise null.
    /// </summary>
    private static string? ResolveComponentLabel(JObject args, IComponentCatalog? catalog, out string shortLabel)
    {
        var raw = args["componentGuid"] is JValue value ? value.ToString() : null;
        if (raw is null || !Guid.TryParse(raw, out var componentGuid))
        {
            shortLabel = "(invalid component GUID)";
            return null;
        }
        if (catalog is not null && catalog.TryGet(componentGuid, out var entry))
        {
            shortLabel = ShortComponentLabel(entry);
            return FormatComponentLabel(entry);
        }
        shortLabel = $"unresolved component {componentGuid:D}";
        return null;
    }

    private static string WithPosition(string label, JObject args)
    {
        var x = args["x"]?.ToString();
        var y = args["y"]?.ToString();
        return !string.IsNullOrEmpty(x) && !string.IsNullOrEmpty(y) ? $"{label} @ ({x}, {y})" : label;
    }

    private static string FormatAddComponent(JObject args)
    {
        // Without a catalog the identity is the exact GUID the adapter executes, never a nickname.
        ResolveComponentLabel(args, catalog: null, out var label);
        return WithPosition(label, args);
    }

    private static string FormatConnect(JObject args)
    {
        var from = args["from"]?.ToString() ?? "?";
        var to = args["to"]?.ToString() ?? "?";
        var fromOut = args["fromOutput"]?.ToString();
        var toIn = args["toInput"]?.ToString();
        var lhs = string.IsNullOrEmpty(fromOut) ? from : $"{from}.{fromOut}";
        var rhs = string.IsNullOrEmpty(toIn) ? to : $"{to}.{toIn}";
        return $"{lhs} -> {rhs}";
    }

    private static string FormatSetInput(JObject args)
    {
        var target = args["target"]?.ToString() ?? "?";
        var input = args["input"]?.ToString();
        var value = args["value"]?.ToString() ?? "(no value)";
        var min = args["min"]?.ToString();
        var max = args["max"]?.ToString();
        var bounds = min is null && max is null ? string.Empty : $" range [{min ?? "current"}..{max ?? "current"}]";
        return string.IsNullOrEmpty(input)
            ? $"{target} = {value}{bounds}"
            : $"{target}.{input} = {value}{bounds}";
    }

    private static string FormatDelete(JObject args)
    {
        var target = args["target"]?.ToString() ?? "?";
        return $"delete {target}";
    }

    private static string FormatGroup(JObject args)
    {
        var nickname = args["nickname"]?.ToString();
        int count = 0;
        if (args["targets"] is JArray arr)
        {
            count = arr.Count;
        }
        return string.IsNullOrEmpty(nickname)
            ? $"group {count} component(s)"
            : $"group {count} component(s) as '{nickname}'";
    }

    private static string FormatLayout(JObject args)
    {
        var mode = args["arrangement"]?.ToString() ?? "auto";
        int count = 0;
        if (args["targets"] is JArray arr)
        {
            count = arr.Count;
        }
        return $"layout {count} component(s) as {mode}";
    }

    private static string FormatDescribe(JObject args)
    {
        var target = args["target"]?.ToString() ?? "?";
        return $"describe {target}";
    }

    private static string FormatGenericArguments(JObject args)
    {
        var parts = new List<string>();
        foreach (var property in args.Properties())
        {
            var value = property.Value;
            string rendered = value.Type switch
            {
                JTokenType.String => value.ToString(),
                JTokenType.Integer => value.ToString(),
                JTokenType.Float => value.ToString(),
                JTokenType.Boolean => value.ToString(),
                JTokenType.Array => $"[{((JArray)value).Count} item(s)]",
                JTokenType.Object => "{...}",
                JTokenType.Null => "null",
                _ => value.ToString(),
            };
            parts.Add($"{property.Name}={rendered}");
        }
        return string.Join(", ", parts);
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
        {
            return text;
        }
        return text.Substring(0, Math.Max(0, max - 1)) + "\u2026";
    }
}
