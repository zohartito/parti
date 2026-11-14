// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Parti.Core.Sessions;
using Rhino;

namespace Parti.Core.Grasshopper;

/// <summary>
/// Removes a single component from the pinned Grasshopper document by stable instance GUID.
/// When a lock registry is supplied, the delete is refused if the target is locked or has any
/// wire to or from a locked component, since removing it would cut a wire of a locked component.
/// The check and the removal run together on the UI thread against the leased document.
/// </summary>
public static class GhDelete
{
    public static Task<JsonObject> ExecuteAsync(JsonObject parameters) => ExecuteAsync(parameters, locks: null);

    /// <summary>
    /// Deletes the target unless it, or any component wired to it in either direction, is locked
    /// in <paramref name="locks"/>.
    /// </summary>
    public static Task<JsonObject> ExecuteAsync(JsonObject parameters, ComponentLockRegistry? locks)
    {
        var targetKey = parameters["target"]?.GetValue<string>() ?? "";
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
                if (string.IsNullOrEmpty(targetKey))
                {
                    tcs.SetResult(ErrorResult("'target' is required."));
                    return;
                }

                var obj = GhTargets.Resolve(doc, targetKey, out var nestedRejection);
                if (nestedRejection is not null)
                {
                    tcs.SetResult(nestedRejection);
                    return;
                }

                if (obj is null)
                {
                    tcs.SetResult(new JsonObject
                    {
                        ["status"] = "not_found",
                        ["target"] = targetKey,
                    });
                    return;
                }

                if (locks is not null &&
                    FindLockConflict(obj.InstanceGuid, WiredComponents(obj), locks) is { } conflict)
                {
                    tcs.SetResult(LockedResult(targetKey, conflict));
                    return;
                }

                var componentName = obj.Name ?? "";

                // update=false: caller triggers solution afterward (mirrors GhAddComponent).
                // Captured before the removal: undo restores the object with its upstream and
                // downstream wires.
                GhTurnUndo.BeforeRemove(undo, obj);
                doc.RemoveObject(obj, false);
                doc.NewSolution(false);

                tcs.SetResult(new JsonObject
                {
                    ["status"] = "success",
                    ["target"] = targetKey,
                    ["componentName"] = componentName,
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(ErrorResult(ex.Message));
            }
        });

        return tcs.Task;
    }

    /// <summary>A lock that blocks a delete: the locked instance and how it relates to the target.</summary>
    internal sealed record LockConflict(Guid LockedInstance, string Relation);

    /// <summary>
    /// Returns the first lock that blocks deleting <paramref name="target"/>: the target itself,
    /// then its wired neighbours in the order given. Null when nothing blocks the delete.
    /// </summary>
    internal static LockConflict? FindLockConflict(
        Guid target,
        IEnumerable<(Guid Owner, string Direction)> wiredComponents,
        ComponentLockRegistry locks)
    {
        if (locks.IsLocked(target))
        {
            return new LockConflict(target, "target");
        }
        foreach (var (owner, direction) in wiredComponents)
        {
            if (owner != target && locks.IsLocked(owner))
            {
                return new LockConflict(owner, direction);
            }
        }
        return null;
    }

    /// <summary>
    /// Every top-level component wired to <paramref name="obj"/>: the owners of all
    /// <c>Sources</c> (direction <c>upstream</c>) and <c>Recipients</c> (direction <c>downstream</c>)
    /// of every parameter the object owns, or of the object itself when it is a floating parameter.
    /// </summary>
    internal static IEnumerable<(Guid Owner, string Direction)> WiredComponents(IGH_DocumentObject obj)
    {
        var parameters = new List<IGH_Param>();
        if (obj is IGH_Component component)
        {
            parameters.AddRange(component.Params.Input);
            parameters.AddRange(component.Params.Output);
        }
        else if (obj is IGH_Param standalone)
        {
            parameters.Add(standalone);
        }

        foreach (var param in parameters)
        {
            foreach (var source in param.Sources ?? Enumerable.Empty<IGH_Param>())
            {
                yield return (OwnerOf(source), "upstream");
            }
            foreach (var recipient in param.Recipients ?? Enumerable.Empty<IGH_Param>())
            {
                yield return (OwnerOf(recipient), "downstream");
            }
        }
    }

    private static Guid OwnerOf(IGH_Param param) =>
        param.Attributes?.GetTopLevel?.DocObject?.InstanceGuid ?? param.InstanceGuid;

    private static JsonObject LockedResult(string target, LockConflict conflict) => new()
    {
        ["status"] = "locked",
        ["success"] = false,
        ["target"] = target,
        ["lockedComponent"] = conflict.LockedInstance.ToString("D"),
        ["relation"] = conflict.Relation,
        ["message"] = conflict.Relation == "target"
            ? "The target component is locked by the user; nothing was deleted."
            : $"The target is wired to locked component {conflict.LockedInstance:D} ({conflict.Relation}); deleting it would cut that wire. Nothing was deleted.",
    };

    private static JsonObject ErrorResult(string message) => new()
    {
        ["status"] = "error",
        ["message"] = message,
    };
}
