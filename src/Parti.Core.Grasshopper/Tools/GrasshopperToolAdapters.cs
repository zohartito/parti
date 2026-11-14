// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Sessions;
using Parti.Core.Tools;

namespace Parti.Core.Grasshopper.Tools;

/// <summary>
/// Adapters that wrap Parti's ten Grasshopper primitives as <see cref="ToolRegistry"/>
/// entries. Each adapter pairs an <see cref="AIToolDefinition"/> (JSON schema the model sees)
/// with a handler that bridges Newtonsoft ↔ System.Text.Json and invokes the primitive.
///
/// Mutation tools (<c>gh_add_component</c>, <c>gh_connect</c>, <c>gh_set_input</c>,
/// <c>gh_delete</c>) and organizational tools (<c>gh_group</c>, <c>gh_layout</c>,
/// <c>gh_relayout</c>) are flagged <see cref="AIToolDefinition.IsReversible"/> so the
/// session wraps a whole assistant turn in one undo scope — a single Ctrl+Z reverts
/// everything. Read-only tools (<c>gh_get_all</c>, <c>gh_get_selected</c>,
/// <c>gh_describe</c>) leave the flag at its default of false.
/// </summary>
public static class GrasshopperToolAdapters
{
    /// <summary>Adapter for <see cref="GhAddComponent"/>.</summary>
    public static (AIToolDefinition Definition, Func<JObject, CancellationToken, Task<JObject>> Handler) GhAddComponentAdapter()
    {
        var def = new AIToolDefinition(
            Name: "gh_add_component",
            Description: "Add a Grasshopper component to the pinned document by its exact component GUID. Coordinates place it at (x, y). Reversible via Ctrl+Z.",
            InputSchema: JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""componentGuid"": { ""type"": ""string"", ""description"": ""Exact Grasshopper component GUID from a prior snapshot"" },
    ""x"": { ""type"": ""number"" },
    ""y"": { ""type"": ""number"" }
  },
  ""required"": [""componentGuid""]
}"))
        { IsReversible = true };

        Func<JObject, CancellationToken, Task<JObject>> handler = async (args, ct) =>
        {
            var stjResult = await GhAddComponent.ExecuteAsync(PrepareAddArguments(args)).ConfigureAwait(false);
            return JsonBridge.ToNewtonsoft(stjResult);
        };

        return (def, handler);
    }

    /// <summary>
    /// Builds the primitive's arguments from the frozen, approved call. The primitive executes
    /// <c>type</c>, which is always taken from the reviewed <c>componentGuid</c> and overwrites
    /// anything else; only the declared fields (<c>x</c>, <c>y</c>) and the host-attached document
    /// lease are carried over. Undeclared model fields such as <c>nickname</c> or <c>type</c> never
    /// reach the primitive.
    /// </summary>
    internal static JsonObject PrepareAddArguments(JObject args)
    {
        var source = JsonBridge.ToSystemText(args ?? new JObject());
        var prepared = new JsonObject();
        if (source["componentGuid"] is JsonNode componentGuid)
        {
            prepared["type"] = JsonNode.Parse(componentGuid.ToJsonString());
        }
        foreach (var key in new[] { "x", "y", GrasshopperDocumentCapability.LeaseArgument })
        {
            if (source[key] is JsonNode node)
            {
                prepared[key] = JsonNode.Parse(node.ToJsonString());
            }
        }
        return prepared;
    }

    /// <summary>Adapter for <see cref="GhGetAll"/>.</summary>
    public static (AIToolDefinition Definition, Func<JObject, CancellationToken, Task<JObject>> Handler) GhGetAllAdapter()
    {
        var def = new AIToolDefinition(
            Name: "gh_get_all",
            Description: "Return a compact snapshot of every component on the active Grasshopper canvas (document-scoped instance GUID handles, names, positions, wires, per-param data counts, and each node's runtime errors/warnings).",
            InputSchema: JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {}
}"));

        Func<JObject, CancellationToken, Task<JObject>> handler = async (args, ct) =>
        {
            var stjArgs = JsonBridge.ToSystemText(args);
            var stjResult = await GhGetAll.ExecuteAsync(stjArgs).ConfigureAwait(false);
            return JsonBridge.ToNewtonsoft(stjResult);
        };

        return (def, handler);
    }

    /// <summary>Adapter for <see cref="GhGetSelected"/>.</summary>
    public static (AIToolDefinition Definition, Func<JObject, CancellationToken, Task<JObject>> Handler) GhGetSelectedAdapter()
    {
        var def = new AIToolDefinition(
            Name: "gh_get_selected",
            Description: "Return a compact snapshot of only the currently selected components on the active Grasshopper canvas (same fields as gh_get_all, including runtime errors/warnings).",
            InputSchema: JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {}
}"));

        Func<JObject, CancellationToken, Task<JObject>> handler = async (args, ct) =>
        {
            var stjArgs = JsonBridge.ToSystemText(args);
            var stjResult = await GhGetSelected.ExecuteAsync(stjArgs).ConfigureAwait(false);
            return JsonBridge.ToNewtonsoft(stjResult);
        };

        return (def, handler);
    }

    /// <summary>Adapter for <see cref="GhDescribe"/>.</summary>
    public static (AIToolDefinition Definition, Func<JObject, CancellationToken, Task<JObject>> Handler) GhDescribeAdapter()
    {
        var def = new AIToolDefinition(
            Name: "gh_describe",
            Description: "Return a detailed description of a single component on the active Grasshopper canvas, identified by its stable instance GUID.",
            InputSchema: JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""target"": { ""type"": ""string"", ""description"": ""Stable instance GUID of the target component"" }
  },
  ""required"": [""target""]
}"));

        Func<JObject, CancellationToken, Task<JObject>> handler = async (args, ct) =>
        {
            var stjArgs = JsonBridge.ToSystemText(args);
            var stjResult = await GhDescribe.ExecuteAsync(stjArgs).ConfigureAwait(false);
            return JsonBridge.ToNewtonsoft(stjResult);
        };

        return (def, handler);
    }

    /// <summary>Adapter for <see cref="GhConnect"/> without lock enforcement at dispatch.</summary>
    public static (AIToolDefinition Definition, Func<JObject, CancellationToken, Task<JObject>> Handler) GhConnectAdapter() =>
        GhConnectAdapter(locks: null);

    /// <summary>
    /// Adapter for <see cref="GhConnect"/>. With <paramref name="locks"/>, the wire is refused at
    /// dispatch on the leased document when either end belongs to a locked component.
    /// </summary>
    public static (AIToolDefinition Definition, Func<JObject, CancellationToken, Task<JObject>> Handler) GhConnectAdapter(
        ComponentLockRegistry? locks)
    {
        var def = new AIToolDefinition(
            Name: "gh_connect",
            Description: "Wire an output parameter of one component into an input parameter of another on the active Grasshopper canvas. Parameters may be selected by zero-based index or by name. Reversible via Ctrl+Z.",
            InputSchema: JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""from"": { ""type"": ""string"", ""description"": ""Stable instance GUID of the source component"" },
    ""fromOutput"": { ""description"": ""Output parameter index (integer) or name (string). Defaults to 0."" },
    ""to"": { ""type"": ""string"", ""description"": ""Stable instance GUID of the target component"" },
    ""toInput"": { ""description"": ""Input parameter index (integer) or name (string). Defaults to 0."" }
  },
  ""required"": [""from"", ""to""]
}"))
        { IsReversible = true };

        Func<JObject, CancellationToken, Task<JObject>> handler = async (args, ct) =>
        {
            var stjArgs = JsonBridge.ToSystemText(args);
            var stjResult = await GhConnect.ExecuteAsync(stjArgs, locks).ConfigureAwait(false);
            return JsonBridge.ToNewtonsoft(stjResult);
        };

        return (def, handler);
    }

    /// <summary>Adapter for <see cref="GhSetInput"/> without lock enforcement at dispatch.</summary>
    public static (AIToolDefinition Definition, Func<JObject, CancellationToken, Task<JObject>> Handler) GhSetInputAdapter() =>
        GhSetInputAdapter(locks: null);

    /// <summary>
    /// Adapter for <see cref="GhSetInput"/>. With <paramref name="locks"/>, the edit is refused at
    /// dispatch on the leased document when the target belongs to a locked component.
    /// </summary>
    public static (AIToolDefinition Definition, Func<JObject, CancellationToken, Task<JObject>> Handler) GhSetInputAdapter(
        ComponentLockRegistry? locks)
    {
        var def = new AIToolDefinition(
            Name: "gh_set_input",
            Description: "Set the persistent value of a parameter on the active canvas. Supports number sliders, panels, and Param_Number / Param_Integer / Param_String. "
                + "The result reports the value Grasshopper actually holds after the change ('value'; sliders round to their precision). "
                + "A number slider rejects a value outside its current range (the error includes the range) unless you pass explicit new bounds 'min'/'max'. Reversible via Ctrl+Z.",
            InputSchema: JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""target"": { ""type"": ""string"", ""description"": ""Stable instance GUID of the target component"" },
    ""input"": { ""description"": ""Input parameter index (integer) or name (string). Defaults to 0. Ignored for sliders and panels."" },
    ""value"": { ""description"": ""New persistent value. Numbers for sliders/Param_Number/Param_Integer, strings for panels/Param_String."" },
    ""min"": { ""type"": ""number"", ""description"": ""Number sliders only: explicit new lower bound. Needed when value is below the current range."" },
    ""max"": { ""type"": ""number"", ""description"": ""Number sliders only: explicit new upper bound. Needed when value is above the current range."" }
  },
  ""required"": [""target"", ""value""]
}"))
        { IsReversible = true };

        Func<JObject, CancellationToken, Task<JObject>> handler = async (args, ct) =>
        {
            var stjArgs = JsonBridge.ToSystemText(args);
            var stjResult = await GhSetInput.ExecuteAsync(stjArgs, locks).ConfigureAwait(false);
            return JsonBridge.ToNewtonsoft(stjResult);
        };

        return (def, handler);
    }

    /// <summary>Adapter for <see cref="GhDelete"/> without lock enforcement at dispatch.</summary>
    public static (AIToolDefinition Definition, Func<JObject, CancellationToken, Task<JObject>> Handler) GhDeleteAdapter() =>
        GhDeleteAdapter(locks: null);

    /// <summary>
    /// Adapter for <see cref="GhDelete"/>. With <paramref name="locks"/>, the delete is refused at
    /// dispatch when the target is locked (checked before touching the document) or, on the leased
    /// document on the UI thread, when the target is wired to a locked component in either direction.
    /// </summary>
    public static (AIToolDefinition Definition, Func<JObject, CancellationToken, Task<JObject>> Handler) GhDeleteAdapter(
        ComponentLockRegistry? locks)
    {
        var def = new AIToolDefinition(
            Name: "gh_delete",
            Description: "Remove a component from the active Grasshopper canvas, identified by its stable instance GUID. Refused when the component, or any component wired to it, is locked. Reversible via Ctrl+Z.",
            InputSchema: JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""target"": { ""type"": ""string"", ""description"": ""Stable instance GUID of the component to delete"" }
  },
  ""required"": [""target""]
}"))
        { IsReversible = true };

        Func<JObject, CancellationToken, Task<JObject>> handler = async (args, ct) =>
        {
            var target = args?["target"] is JValue { Type: JTokenType.String } value ? (string?)value : null;
            if (locks is not null && target is not null && locks.IsLocked(target))
            {
                return new JObject
                {
                    ["status"] = "locked",
                    ["success"] = false,
                    ["target"] = target,
                    ["lockedComponent"] = Guid.Parse(target).ToString("D"),
                    ["relation"] = "target",
                    ["message"] = "The target component is locked by the user; nothing was deleted.",
                };
            }
            var stjArgs = JsonBridge.ToSystemText(args ?? new JObject());
            var stjResult = await GhDelete.ExecuteAsync(stjArgs, locks).ConfigureAwait(false);
            return JsonBridge.ToNewtonsoft(stjResult);
        };

        return (def, handler);
    }

    /// <summary>Adapter for <see cref="GhGroup"/>.</summary>
    public static (AIToolDefinition Definition, Func<JObject, CancellationToken, Task<JObject>> Handler) GhGroupAdapter()
    {
        var def = new AIToolDefinition(
            Name: "gh_group",
            Description: "Wrap a set of components on the active Grasshopper canvas in a visual group (colored background, optional label). Reversible via Ctrl+Z.",
            InputSchema: JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""targets"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""string"" },
      ""description"": ""Stable instance GUIDs of the components to group""
    },
    ""nickname"": { ""type"": ""string"", ""description"": ""Optional label displayed on the group"" },
    ""colour"": { ""type"": ""string"", ""description"": ""Optional hex colour (e.g. '#FFC107') for the group background"" }
  },
  ""required"": [""targets""]
}"))
        { IsReversible = true };

        Func<JObject, CancellationToken, Task<JObject>> handler = async (args, ct) =>
        {
            var stjArgs = JsonBridge.ToSystemText(args);
            var stjResult = await GhGroup.ExecuteAsync(stjArgs).ConfigureAwait(false);
            return JsonBridge.ToNewtonsoft(stjResult);
        };

        return (def, handler);
    }

    /// <summary>Adapter for <see cref="GhLayout"/>.</summary>
    public static (AIToolDefinition Definition, Func<JObject, CancellationToken, Task<JObject>> Handler) GhLayoutAdapter()
    {
        var def = new AIToolDefinition(
            Name: "gh_layout",
            Description: "Auto-position a set of components on the active Grasshopper canvas as a row, column, or grid. Reversible via Ctrl+Z.",
            InputSchema: JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""targets"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""string"" },
      ""description"": ""Stable instance GUIDs of the components to arrange""
    },
    ""arrangement"": {
      ""type"": ""string"",
      ""enum"": [""row"", ""column"", ""grid""],
      ""description"": ""Layout shape. Must be explicit in a reviewed mutation.""
    },
    ""origin"": {
      ""type"": ""object"",
      ""properties"": {
        ""x"": { ""type"": ""number"" },
        ""y"": { ""type"": ""number"" }
      },
      ""description"": ""Explicit top-left anchor for the reviewed mutation.""
    },
    ""spacing"": {
      ""type"": ""object"",
      ""properties"": {
        ""x"": { ""type"": ""number"" },
        ""y"": { ""type"": ""number"" }
      },
      ""description"": ""Explicit gap between components.""
    }
  },
  ""required"": [""targets"", ""arrangement"", ""origin"", ""spacing""]
}"))
        { IsReversible = true };

        Func<JObject, CancellationToken, Task<JObject>> handler = async (args, ct) =>
        {
            var stjArgs = JsonBridge.ToSystemText(args);
            var stjResult = await GhLayout.ExecuteAsync(stjArgs).ConfigureAwait(false);
            return JsonBridge.ToNewtonsoft(stjResult);
        };

        return (def, handler);
    }

    /// <summary>Adapter for <see cref="GhRelayout"/>.</summary>
    public static (AIToolDefinition Definition, Func<JObject, CancellationToken, Task<JObject>> Handler) GhRelayoutAdapter()
    {
        var def = new AIToolDefinition(
            Name: "gh_relayout",
            Description: "Auto-tidy the entire active Grasshopper canvas. Places components in columns by dependency rank (the root at column 0, its consumers at column 1, etc.), anchored at the root's current position so the root does not move. Orphans are left in place. Returns 'ranks' as {instanceGuid: rank} and 'countsPerRank'. Not allowed while any component is locked. Reversible via Ctrl+Z.",
            InputSchema: JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""spacing"": {
      ""type"": ""object"",
      ""properties"": {
        ""x"": { ""type"": ""number"" },
        ""y"": { ""type"": ""number"" }
      },
      ""description"": ""Explicit gap between columns/rows.""
    },
    ""root"": { ""type"": ""string"", ""description"": ""Stable instance GUID to use as the rank-0 anchor; required for a reviewed mutation."" }
  },
  ""required"": [""spacing"", ""root""]
}"))
        { IsReversible = true };

        Func<JObject, CancellationToken, Task<JObject>> handler = async (args, ct) =>
        {
            var stjArgs = JsonBridge.ToSystemText(args);
            var stjResult = await GhRelayout.ExecuteAsync(stjArgs).ConfigureAwait(false);
            return JsonBridge.ToNewtonsoft(stjResult);
        };

        return (def, handler);
    }

    /// <summary>Build a <see cref="ToolRegistry"/> pre-registered with all ten Grasshopper adapters.</summary>
    public static ToolRegistry CreateDefault()
    {
        var registry = new ToolRegistry();

        var add = GhAddComponentAdapter();
        registry.Register(add.Definition, add.Handler);

        var getAll = GhGetAllAdapter();
        registry.Register(getAll.Definition, getAll.Handler);

        var getSelected = GhGetSelectedAdapter();
        registry.Register(getSelected.Definition, getSelected.Handler);

        var describe = GhDescribeAdapter();
        registry.Register(describe.Definition, describe.Handler);

        var connect = GhConnectAdapter();
        registry.Register(connect.Definition, connect.Handler);

        var setInput = GhSetInputAdapter();
        registry.Register(setInput.Definition, setInput.Handler);

        var delete = GhDeleteAdapter();
        registry.Register(delete.Definition, delete.Handler);

        var group = GhGroupAdapter();
        registry.Register(group.Definition, group.Handler);

        var layout = GhLayoutAdapter();
        registry.Register(layout.Definition, layout.Handler);

        var relayout = GhRelayoutAdapter();
        registry.Register(relayout.Definition, relayout.Handler);

        return registry;
    }
}
