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
using Parti.Core;
using Rhino;

namespace Parti.Core.Grasshopper;

/// <summary>
/// Auto-positions stable instance-GUID targets on the pinned document into a tidy row, column, or grid.
/// </summary>
public static class GhLayout
{
    private const double DefaultSpacingX = 120.0;
    private const double DefaultSpacingY = 80.0;

    public static Task<JsonObject> ExecuteAsync(JsonObject parameters)
    {
        var targets = ReadTargets(parameters["targets"]);
        var arrangement = (parameters["arrangement"]?.GetValue<string?>() ?? "row").ToLowerInvariant();
        var originNode = parameters["origin"] as JsonObject;
        var spacingNode = parameters["spacing"] as JsonObject;

        double spacingX = ReadDouble(spacingNode, "x", DefaultSpacingX);
        double spacingY = ReadDouble(spacingNode, "y", DefaultSpacingY);
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

                if (arrangement != "row" && arrangement != "column" && arrangement != "grid")
                {
                    tcs.SetResult(ErrorResult(
                        $"arrangement must be one of 'row', 'column', 'grid' (got '{arrangement}')"));
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

                if (resolved.Count == 0)
                {
                    tcs.SetResult(ErrorResult("No targets resolved."));
                    return;
                }
                if (missing.Count > 0)
                {
                    tcs.SetResult(ErrorResult("Target identity drift detected; no components were moved."));
                    return;
                }

                double originX, originY;
                if (originNode is not null)
                {
                    originX = ReadDouble(originNode, "x", 0.0);
                    originY = ReadDouble(originNode, "y", 0.0);
                }
                else
                {
                    var pivots = resolved
                        .Where(o => o.Attributes is not null)
                        .Select(o => o.Attributes!.Pivot)
                        .ToList();
                    if (pivots.Count > 0)
                    {
                        originX = pivots.Average(p => (double)p.X);
                        originY = pivots.Average(p => (double)p.Y);
                    }
                    else
                    {
                        originX = 0.0;
                        originY = 0.0;
                    }
                }

                int cols = arrangement == "grid"
                    ? Math.Max(1, (int)Math.Ceiling(Math.Sqrt(resolved.Count)))
                    : 1;

                for (int i = 0; i < resolved.Count; i++)
                {
                    double x, y;
                    switch (arrangement)
                    {
                        case "row":
                            x = originX + i * spacingX;
                            y = originY;
                            break;
                        case "column":
                            x = originX;
                            y = originY + i * spacingY;
                            break;
                        default: // grid
                            x = originX + (i % cols) * spacingX;
                            y = originY + (i / cols) * spacingY;
                            break;
                    }

                    var obj = resolved[i];
                    if (obj.Attributes is null)
                    {
                        obj.CreateAttributes();
                    }
                    GhTurnUndo.BeforeMove(undo, obj);
                    obj.Attributes!.Pivot = new PointF((float)x, (float)y);
                    obj.Attributes!.ExpireLayout();
                }

                doc.NewSolution(false);

                var result = new JsonObject
                {
                    ["status"] = "success",
                    ["moved"] = resolved.Count,
                    ["arrangement"] = arrangement,
                };

                if (missing.Count > 0)
                {
                    var missingArr = new JsonArray();
                    foreach (var m in missing)
                    {
                        missingArr.Add(m);
                    }
                    result["missing"] = missingArr;
                }

                tcs.SetResult(result);
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

    private static double ReadDouble(JsonObject? obj, string key, double fallback)
    {
        if (obj is null) return fallback;
        if (obj[key] is JsonValue value)
        {
            if (value.TryGetValue<double>(out var d)) return d;
            if (value.TryGetValue<int>(out var i)) return i;
        }
        return fallback;
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
