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
/// Auto-tidies the entire canvas by topological-sort-ish heuristics. Sources (no input wires)
/// get rank 0, their direct consumers rank 1, etc. Placement is column=rank, row=index-within-rank,
/// multiplied by spacing and offset from the root's current pivot, so the root does not move.
/// Orphans (no wires) are left in place. Best-effort — cycles are broken by BFS order.
/// The result carries <c>anchor</c>, <c>ranks</c> as an explicit <c>{instanceGuid: rank}</c> map,
/// and <c>countsPerRank</c>.
/// </summary>
public static class GhRelayout
{
    private const double DefaultSpacingX = 140.0;
    private const double DefaultSpacingY = 100.0;

    public static Task<JsonObject> ExecuteAsync(JsonObject parameters)
    {
        var spacingNode = parameters["spacing"] as JsonObject;
        double spacingX = ReadDouble(spacingNode, "x", DefaultSpacingX);
        double spacingY = ReadDouble(spacingNode, "y", DefaultSpacingY);
        var rootKey = parameters["root"]?.GetValue<string?>();
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
                var allObjects = doc.Objects.ToList();
                if (allObjects.Count == 0)
                {
                    tcs.SetResult(new JsonObject
                    {
                        ["status"] = "success",
                        ["moved"] = 0,
                        ["ranks"] = new JsonObject(),
                        ["countsPerRank"] = new JsonArray(),
                    });
                    return;
                }

                // Classify each object: has wires at all?
                var hasWires = new Dictionary<Guid, bool>();
                foreach (var obj in allObjects)
                {
                    hasWires[obj.InstanceGuid] = HasAnyWires(obj);
                }

                // Determine roots.
                var roots = new List<IGH_DocumentObject>();
                if (!string.IsNullOrEmpty(rootKey))
                {
                    var forced = ResolveTarget(doc, rootKey!);
                    if (forced is null)
                    {
                        tcs.SetResult(ErrorResult("Root identity drift detected; no components were moved."));
                        return;
                    }
                    roots.Add(forced);
                }

                if (roots.Count == 0)
                {
                    // Sources: wired objects that have no incoming wires.
                    foreach (var obj in allObjects)
                    {
                        if (!hasWires[obj.InstanceGuid]) continue;
                        if (!HasIncomingWires(obj))
                        {
                            roots.Add(obj);
                        }
                    }
                }

                // BFS from roots to assign ranks. Unreachable wired objects are appended at
                // the end (handles cycles and disconnected clusters).
                var rank = new Dictionary<Guid, int>();
                var queue = new Queue<IGH_DocumentObject>();
                foreach (var r in roots)
                {
                    if (!rank.ContainsKey(r.InstanceGuid))
                    {
                        rank[r.InstanceGuid] = 0;
                        queue.Enqueue(r);
                    }
                }

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    var currentRank = rank[current.InstanceGuid];
                    foreach (var downstream in GetDownstreamObjects(current, doc))
                    {
                        if (rank.ContainsKey(downstream.InstanceGuid)) continue;
                        rank[downstream.InstanceGuid] = currentRank + 1;
                        queue.Enqueue(downstream);
                    }
                }

                // Any wired object not yet ranked (cycles, disconnected subgraphs) — assign by
                // BFS order from whatever's left.
                foreach (var obj in allObjects)
                {
                    if (!hasWires[obj.InstanceGuid]) continue;
                    if (rank.ContainsKey(obj.InstanceGuid)) continue;

                    rank[obj.InstanceGuid] = 0;
                    queue.Enqueue(obj);
                    while (queue.Count > 0)
                    {
                        var current = queue.Dequeue();
                        var currentRank = rank[current.InstanceGuid];
                        foreach (var downstream in GetDownstreamObjects(current, doc))
                        {
                            if (rank.ContainsKey(downstream.InstanceGuid)) continue;
                            rank[downstream.InstanceGuid] = currentRank + 1;
                            queue.Enqueue(downstream);
                        }
                    }
                }

                // Anchor the cluster at the root's current pivot, not at canvas (0,0): the root stays
                // where the user put it and everything else is placed relative to it.
                var ranked = allObjects
                    .Where(o => rank.ContainsKey(o.InstanceGuid))
                    .Select(o => (o.InstanceGuid, rank[o.InstanceGuid]))
                    .ToList();
                var anchorObject = roots.FirstOrDefault()
                    ?? allObjects.FirstOrDefault(o => rank.ContainsKey(o.InstanceGuid));
                if (anchorObject is null)
                {
                    tcs.SetResult(new JsonObject
                    {
                        ["status"] = "success",
                        ["moved"] = 0,
                        ["ranks"] = new JsonObject(),
                        ["countsPerRank"] = new JsonArray(),
                    });
                    return;
                }
                if (anchorObject.Attributes is null)
                {
                    anchorObject.CreateAttributes();
                }
                var anchor = anchorObject.Attributes!.Pivot;
                var placement = ComputePlacement(ranked, anchorObject.InstanceGuid, anchor, spacingX, spacingY);

                int moved = 0;
                var ranks = new JsonObject();
                foreach (var obj in allObjects)
                {
                    if (!placement.Positions.TryGetValue(obj.InstanceGuid, out var pivot)) continue;
                    if (obj.Attributes is null)
                    {
                        obj.CreateAttributes();
                    }
                    if (obj.Attributes!.Pivot != pivot)
                    {
                        GhTurnUndo.BeforeMove(undo, obj);
                        obj.Attributes!.Pivot = pivot;
                        obj.Attributes!.ExpireLayout();
                        moved++;
                    }
                    ranks[obj.InstanceGuid.ToString("D")] = rank[obj.InstanceGuid];
                }
                var countsPerRank = new JsonArray();
                foreach (var count in placement.CountsPerRank)
                {
                    countsPerRank.Add(count);
                }

                doc.NewSolution(false);

                tcs.SetResult(new JsonObject
                {
                    ["status"] = "success",
                    ["moved"] = moved,
                    ["anchor"] = new JsonObject
                    {
                        ["instanceGuid"] = anchorObject.InstanceGuid.ToString("D"),
                        ["x"] = anchor.X,
                        ["y"] = anchor.Y,
                    },
                    ["ranks"] = ranks,
                    ["countsPerRank"] = countsPerRank,
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(ErrorResult(ex.Message));
            }
        });

        return tcs.Task;
    }

    /// <summary>Pivots per instance and the number of objects in each rank (rank order).</summary>
    internal sealed record Placement(IReadOnlyDictionary<Guid, PointF> Positions, IReadOnlyList<int> CountsPerRank);

    /// <summary>
    /// Places ranked objects in columns (column = rank, row = index within rank) offset from
    /// <paramref name="anchor"/>. The anchor object is moved to the front of its rank, so when it
    /// has rank 0 it keeps exactly its current pivot. Objects keep their input order otherwise.
    /// </summary>
    internal static Placement ComputePlacement(
        IReadOnlyList<(Guid Id, int Rank)> ranked,
        Guid anchorId,
        PointF anchor,
        double spacingX,
        double spacingY)
    {
        var byRank = new SortedDictionary<int, List<Guid>>();
        foreach (var (id, r) in ranked)
        {
            if (!byRank.TryGetValue(r, out var list))
            {
                list = new List<Guid>();
                byRank[r] = list;
            }
            if (id == anchorId)
            {
                list.Insert(0, id);
            }
            else
            {
                list.Add(id);
            }
        }

        var positions = new Dictionary<Guid, PointF>();
        var counts = new List<int>();
        foreach (var (r, list) in byRank)
        {
            for (int i = 0; i < list.Count; i++)
            {
                positions[list[i]] = new PointF(
                    (float)(anchor.X + r * spacingX),
                    (float)(anchor.Y + i * spacingY));
            }
            counts.Add(list.Count);
        }
        return new Placement(positions, counts);
    }

    private static bool HasAnyWires(IGH_DocumentObject obj)
    {
        if (obj is IGH_Component comp)
        {
            foreach (var p in comp.Params.Input)
            {
                if (p.Sources is not null && p.Sources.Count > 0) return true;
            }
            foreach (var p in comp.Params.Output)
            {
                if (p.Recipients is not null && p.Recipients.Count > 0) return true;
            }
            return false;
        }
        if (obj is IGH_Param param)
        {
            if (param.Sources is not null && param.Sources.Count > 0) return true;
            if (param.Recipients is not null && param.Recipients.Count > 0) return true;
            return false;
        }
        return false;
    }

    private static bool HasIncomingWires(IGH_DocumentObject obj)
    {
        if (obj is IGH_Component comp)
        {
            foreach (var p in comp.Params.Input)
            {
                if (p.Sources is not null && p.Sources.Count > 0) return true;
            }
            return false;
        }
        if (obj is IGH_Param param)
        {
            if (param.Sources is not null && param.Sources.Count > 0) return true;
            return false;
        }
        return false;
    }

    private static IEnumerable<IGH_DocumentObject> GetDownstreamObjects(IGH_DocumentObject obj, GH_Document doc)
    {
        var seen = new HashSet<Guid>();
        var outputs = new List<IGH_Param>();

        if (obj is IGH_Component comp)
        {
            outputs.AddRange(comp.Params.Output);
        }
        else if (obj is IGH_Param standalone)
        {
            outputs.Add(standalone);
        }

        foreach (var output in outputs)
        {
            if (output.Recipients is null) continue;
            foreach (var recipient in output.Recipients)
            {
                var ownerGuid = recipient.Attributes?.GetTopLevel?.DocObject?.InstanceGuid
                    ?? recipient.InstanceGuid;
                if (ownerGuid == obj.InstanceGuid) continue;
                if (!seen.Add(ownerGuid)) continue;
                var owner = doc.FindObject(ownerGuid, false);
                if (owner is not null)
                {
                    yield return owner;
                }
            }
        }
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
