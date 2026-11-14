// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Text.Json.Nodes;
using Grasshopper.Kernel;
using Parti.Core.Sessions;

namespace Parti.Core.Grasshopper;

/// <summary>
/// Resolves the <c>target</c>/<c>from</c>/<c>to</c> GUID of a canvas edit to a TOP-LEVEL object on
/// the leased document. Locks and the lock policy are keyed by top-level instance GUIDs, so the
/// GUID of a parameter inside a component (which <c>FindObject(id, false)</c> would also find) is
/// refused: otherwise an input of a locked component could be edited by naming the input's own
/// GUID. Snapshots only ever show top-level GUIDs, so no legitimate call is affected.
/// </summary>
internal static class GhTargets
{
    /// <summary>Resolution against the leased document. Call on the UI thread.</summary>
    internal static IGH_DocumentObject? Resolve(GH_Document doc, string target, out JsonObject? rejection) =>
        Resolve(target, id => doc.FindObject(id, true), id => doc.FindObject(id, false), OwnerOf, out rejection);

    /// <summary>The instance GUID of the top-level object that owns <paramref name="nested"/>, if known.</summary>
    internal static Guid? OwnerOf(IGH_DocumentObject nested) => nested.Attributes?.GetTopLevel?.DocObject?.InstanceGuid;

    /// <summary>
    /// Returns the top-level object named by <paramref name="target"/>, or null. When the GUID names
    /// a nested object, <paramref name="rejection"/> is an error result naming its owner when known;
    /// otherwise it is null and the caller reports "not found".
    /// </summary>
    internal static IGH_DocumentObject? Resolve(
        string target,
        Func<Guid, IGH_DocumentObject?> findTopLevel,
        Func<Guid, IGH_DocumentObject?> findAny,
        Func<IGH_DocumentObject, Guid?> ownerOf,
        out JsonObject? rejection)
    {
        rejection = null;
        if (!Guid.TryParse(target, out var id))
        {
            return null;
        }

        if (findTopLevel(id) is { } topLevel)
        {
            return topLevel;
        }

        if (findAny(id) is not { } nested)
        {
            return null;
        }

        var owner = ownerOf(nested);
        var ownerText = owner is { } ownerGuid && ownerGuid != nested.InstanceGuid ? ownerGuid.ToString("D") : null;
        rejection = new JsonObject
        {
            ["status"] = "error",
            ["target"] = target,
            ["message"] = ownerText is null
                ? $"'{target}' is a parameter inside a component, not a top-level object. Use the component's instance GUID with an input/output selector. Nothing was changed."
                : $"'{target}' is a parameter inside component {ownerText}. Use target {ownerText} with an input/output selector. Nothing was changed.",
        };
        if (ownerText is not null)
        {
            rejection["owner"] = ownerText;
        }

        return null;
    }

    /// <summary>
    /// The "locked" result for a write that names a locked top-level component, or null when
    /// <paramref name="obj"/> is not locked (or no registry is supplied).
    /// </summary>
    internal static JsonObject? LockedResult(IGH_DocumentObject obj, string target, string tool, ComponentLockRegistry? locks)
    {
        if (locks is null || !locks.IsLocked(obj.InstanceGuid))
        {
            return null;
        }

        return new JsonObject
        {
            ["status"] = "locked",
            ["success"] = false,
            ["target"] = target,
            ["lockedComponent"] = obj.InstanceGuid.ToString("D"),
            ["message"] = $"Component {obj.InstanceGuid:D} is locked by the user; '{tool}' may not touch it. Nothing was changed.",
        };
    }
}
