// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Undo;
using Rhino;

namespace Parti.Core.Grasshopper;

/// <summary>
/// Testable seam around the one Grasshopper API call that can only run inside a live
/// Rhino process: <c>GH_Document.UndoUtil.RecordEvent(GH_UndoRecord)</c>. Splitting this
/// out lets the hermetic unit tests assert that the scope collected the right mutations
/// and would have emitted exactly one event, without the tests ever touching Rhino.
/// </summary>
/// <remarks>
/// Kept <c>internal</c> on purpose: this is an implementation detail of
/// <see cref="GrasshopperUndoScope"/>, not part of the public contract. External callers
/// interact with <see cref="IUndoScope"/> / <see cref="IUndoScopeFactory"/> only.
/// </remarks>
internal interface IGhUndoEmitter
{
    /// <summary>
    /// Emits a single Grasshopper undo event containing every pending action so that one
    /// Ctrl+Z reverts the whole batch. Returns <c>true</c> when an event was actually
    /// written to the undo stack; returns <c>false</c> when the emitter fails soft
    /// (e.g. GH_Document was null because Rhino is closing). Called on the UI thread.
    /// </summary>
    /// <param name="label">Human-readable turn label shown in the undo stack.</param>
    /// <param name="document">
    /// The live <see cref="GH_Document"/>. Boxed as <see cref="object"/> so unit tests
    /// never need to materialise one — the production emitter downcasts.
    /// </param>
    /// <param name="actions">
    /// Pre-built <see cref="IGH_UndoAction"/> instances, one per mutated/added document
    /// object. Boxed as <see cref="object"/> so test doubles can construct the list
    /// with plain markers instead of touching Grasshopper types.
    /// </param>
    bool Emit(string label, object? document, IReadOnlyList<object> actions);
}

/// <summary>
/// Production implementation of <see cref="IGhUndoEmitter"/>. Wraps the actions in a
/// single <see cref="GH_UndoRecord"/> and pushes it onto the document's undo stack via
/// <see cref="GH_Document.UndoUtil"/>.RecordEvent. It must be called on Rhino's UI thread
/// because <see cref="GH_Document"/> mutations (including undo stack edits) must happen there;
/// <see cref="GrasshopperUndoScope"/> marshals the call through its <see cref="IUiThreadInvoker"/>.
/// </summary>
/// <remarks>
/// <para>API verified against Grasshopper 8.30.26103.11001 via metadata reflection:</para>
/// <list type="bullet">
///   <item><description><c>GH_Document</c>.<c>UndoUtil</c> is a nested public instance
///   class (<c>GH_Document.GH_UndoUtil</c>) — not a static class, despite how older
///   docs describe it.</description></item>
///   <item><description><c>GH_UndoUtil.RecordEvent(GH_UndoRecord)</c> returns
///   <see cref="Guid"/> and appends exactly one record onto the stack.</description></item>
///   <item><description><c>GH_UndoRecord(string name)</c> ctor + <c>AddAction</c> lets
///   us batch an arbitrary number of pre-built <see cref="IGH_UndoAction"/>s into that
///   single record — which is what turns "n mutations" into "one Ctrl+Z".</description></item>
/// </list>
/// <para>This is exactly the pattern SmartHopper's <c>gh_put.cs</c> missed: SmartHopper
/// called <c>RecordRemoveObjectEvent</c> for the removal and then performed the addition
/// outside any undo record, producing two stack entries (one of them empty). Wrapping
/// every action in a single <see cref="GH_UndoRecord"/> closes that gap.</para>
/// </remarks>
internal sealed class GhUndoEmitter : IGhUndoEmitter
{
    public bool Emit(string label, object? document, IReadOnlyList<object> actions)
    {
        if (document is not GH_Document doc)
        {
            // Rhino closing or document torn down between Begin and Dispose.
            // Fail soft: skip the event rather than throwing mid-Dispose.
            RhinoApp.WriteLine($"[Parti] skipped undo event '{label}': GH_Document unavailable.");
            return false;
        }

        if (actions.Count == 0)
        {
            // Nothing recorded. Avoid polluting the undo stack with a no-op entry.
            return false;
        }

        var record = new GH_UndoRecord(string.IsNullOrEmpty(label) ? "Parti turn" : label);
        foreach (var action in actions)
        {
            if (action is IGH_UndoAction typed)
            {
                record.AddAction(typed);
            }
        }

        if (record.ActionCount == 0)
        {
            return false;
        }

        // The scope calls this on the UI thread (GH_Document mutations, including undo stack
        // edits, must happen there), so the event is on the stack when this returns.
        try
        {
            doc.UndoUtil.RecordEvent(record);
            return true;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[Parti] failed to record undo event '{label}': {ex.Message}");
            return false;
        }
    }
}
