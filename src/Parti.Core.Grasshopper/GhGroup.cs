// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Parti.Core;
using Rhino;

namespace Parti.Core.Grasshopper;

/// <summary>
/// Wraps stable instance-GUID targets in a Grasshopper group (visual lasso + colored background).
/// </summary>
public static class GhGroup
{
    private static readonly Color DefaultColour = Color.FromArgb(255, 0xFF, 0xC1, 0x07);

    public static Task<JsonObject> ExecuteAsync(JsonObject parameters)
    {
        var targets = ReadTargets(parameters["targets"]);
        var nickname = parameters["nickname"]?.GetValue<string?>();
        var colourHex = parameters["colour"]?.GetValue<string?>();
        var doc = GrasshopperDocumentCapability.ResolveRequired(parameters);
        if (doc is null)
        {
            return Task.FromResult(ErrorResult("Missing or expired pinned Grasshopper document lease."));
        }
        var undo = GrasshopperDocumentCapability.ResolveUndoLog(parameters);

        var tcs = new TaskCompletionSource<JsonObject>();

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                if (targets.Count == 0)
                {
                    tcs.SetResult(ErrorResult("targets is required and must be non-empty"));
                    return;
                }

                var resolved = new List<IGH_DocumentObject>();
                var missing = new List<string>();
                foreach (var target in targets)
                {
                    var obj = ResolveTarget(doc, target);
                    if (obj is null)
                    {
                        missing.Add(target);
                    }
                    else
                    {
                        resolved.Add(obj);
                    }
                }

                // An approval names an exact set. Do not create a smaller group after a target
                // disappears between preview and dispatch; that would execute a different plan.
                if (missing.Count > 0)
                {
                    tcs.SetResult(ErrorResult("Target identity drift detected; no group was created."));
                    return;
                }

                var group = new GH_Group();
                group.CreateAttributes();

                if (!string.IsNullOrEmpty(nickname))
                {
                    group.NickName = nickname!;
                }

                if (!string.IsNullOrEmpty(colourHex) && TryParseHexColour(colourHex!, out var parsed))
                {
                    group.Colour = parsed;
                }
                else
                {
                    group.Colour = DefaultColour;
                }

                doc.AddObject(group, false);

                foreach (var obj in resolved)
                {
                    group.AddObject(obj.InstanceGuid);
                }

                // Recorded once membership is set so redo restores the populated group.
                GhTurnUndo.Added(undo, group);
                group.ExpireCaches();
                doc.NewSolution(false);

                if (missing.Count > 0)
                {
                    var missingArr = new JsonArray();
                    foreach (var m in missing)
                    {
                        missingArr.Add(m);
                    }

                    tcs.SetResult(new JsonObject
                    {
                        ["status"] = "partial",
                        ["message"] = $"Grouped {resolved.Count} of {targets.Count} targets; {missing.Count} not found.",
                        ["missing"] = missingArr,
                        ["added"] = resolved.Count,
                        ["groupGuid"] = group.InstanceGuid.ToString(),
                        ["nickname"] = group.NickName ?? "",
                    });
                    return;
                }

                tcs.SetResult(new JsonObject
                {
                    ["status"] = "success",
                    ["groupGuid"] = group.InstanceGuid.ToString(),
                    ["targetCount"] = resolved.Count,
                    ["nickname"] = group.NickName ?? "",
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(ErrorResult(ex.Message));
            }
        });

        return tcs.Task;
    }

    private static List<string> ReadTargets(JsonNode? node)
    {
        var list = new List<string>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                {
                    list.Add(s);
                }
            }
        }
        return list;
    }

    private static bool TryParseHexColour(string hex, out Color colour)
    {
        colour = Color.Empty;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var trimmed = hex.Trim();
        if (trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(1);
        }

        if (trimmed.Length != 6 && trimmed.Length != 8)
        {
            return false;
        }

        try
        {
            if (trimmed.Length == 6)
            {
                var r = Convert.ToInt32(trimmed.Substring(0, 2), 16);
                var g = Convert.ToInt32(trimmed.Substring(2, 2), 16);
                var b = Convert.ToInt32(trimmed.Substring(4, 2), 16);
                colour = Color.FromArgb(255, r, g, b);
                return true;
            }
            else
            {
                var a = Convert.ToInt32(trimmed.Substring(0, 2), 16);
                var r = Convert.ToInt32(trimmed.Substring(2, 2), 16);
                var g = Convert.ToInt32(trimmed.Substring(4, 2), 16);
                var b = Convert.ToInt32(trimmed.Substring(6, 2), 16);
                colour = Color.FromArgb(a, r, g, b);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    // Top-level objects only: a GUID naming a parameter inside a component is not a target (lock checks
    // and plan labels work on components, so a nested GUID must not slip past them).
    private static IGH_DocumentObject? ResolveTarget(GH_Document doc, string target)
    {
        return Guid.TryParse(target, out var instanceGuid) ? doc.FindObject(instanceGuid, true) : null;
    }

    private static JsonObject ErrorResult(string message) => new()
    {
        ["status"] = "error",
        ["message"] = message,
    };
}
