// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using Grasshopper;
using Parti.Core.Sessions;

namespace Parti.Core.Grasshopper.Chat;

/// <summary>
/// Plan-dialog labels for existing canvas objects (see <see cref="PlanReviewBuilder.WithInstanceLabels"/>).
/// </summary>
public static class CanvasObjectLabels
{
    /// <summary>
    /// The canonical label of <paramref name="instance"/> on the document the plan is bound to, or
    /// null when the active canvas is not that document or the object is not on it. UI thread only.
    /// </summary>
    /// <param name="bindingId">The plan's binding id, which starts with the pinned document's identity.</param>
    /// <param name="instance">The instance GUID the call targets.</param>
    public static string? Describe(string? bindingId, Guid instance)
    {
        try
        {
            var document = Instances.ActiveCanvas?.Document;
            if (document is null || !IsBoundTo(bindingId, GrasshopperDocumentCapability.DocumentId(document)))
            {
                return null;
            }

            var obj = document.FindObject(instance, true);
            return obj is null ? null : PlanReviewBuilder.FormatInstanceLabel(obj.Name, obj.Category, obj.SubCategory, instance);
        }
        catch
        {
            // The canvas may be closing; the dialog falls back to the bare GUID.
            return null;
        }
    }

    /// <summary>True when <paramref name="bindingId"/> was issued for the document <paramref name="documentId"/>.</summary>
    internal static bool IsBoundTo(string? bindingId, string documentId) =>
        !string.IsNullOrEmpty(bindingId)
        && !string.IsNullOrEmpty(documentId)
        && bindingId!.StartsWith(documentId + ":", StringComparison.Ordinal);
}
