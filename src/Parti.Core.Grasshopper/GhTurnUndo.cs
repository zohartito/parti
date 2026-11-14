// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using Grasshopper.Kernel;
using Grasshopper.Kernel.Undo.Actions;

namespace Parti.Core.Grasshopper;

/// <summary>
/// Builds the Grasshopper undo action for each kind of canvas mutation and appends it to the
/// turn's <see cref="TurnUndoLog"/>. Handlers call these on the UI thread, immediately BEFORE the
/// mutation (so the action captures the pre-change state), except <see cref="Added"/>, which runs
/// right after the object is in the document. A null log (no turn scope) makes every call a no-op.
/// </summary>
/// <remarks>
/// Verified live on Rhino 8.35 / Grasshopper 8.35.26251.13002: a record holding
/// [add, wire, generic(slider), remove] in capture order undoes to the exact pre-turn canvas —
/// including the deleted component's upstream AND downstream wires, which
/// <see cref="GH_RemoveObjectAction"/> restores itself — and redoes to the exact post-turn canvas.
/// Never snapshot an object the turn later deletes with <see cref="GH_GenericObjectAction"/>
/// alone: its undo raises a modal Grasshopper assertion when the object is gone.
/// </remarks>
internal static class GhTurnUndo
{
    /// <summary>After <c>doc.AddObject</c>: undo removes the object.</summary>
    internal static void Added(TurnUndoLog? log, IGH_DocumentObject obj) =>
        log?.Add(new GH_AddObjectAction(obj), obj.InstanceGuid);

    /// <summary>Before changing <paramref name="param"/>'s sources: undo restores them.</summary>
    internal static void BeforeWire(TurnUndoLog? log, IGH_Param param) =>
        log?.Add(new GH_WireAction(param));

    /// <summary>Before <c>doc.RemoveObject</c>: undo restores the object and its wires.</summary>
    internal static void BeforeRemove(TurnUndoLog? log, IGH_DocumentObject obj) =>
        log?.Add(new GH_RemoveObjectAction(obj));

    /// <summary>Before an in-place state edit (slider range/value, panel text): undo restores the archived state.</summary>
    internal static void BeforeEdit(TurnUndoLog? log, IGH_DocumentObject obj) =>
        log?.Add(new GH_GenericObjectAction(obj));

    /// <summary>Before replacing a parameter's persistent data: undo restores it.</summary>
    internal static void BeforePersistentData<T>(TurnUndoLog? log, GH_PersistentParam<T> param)
        where T : class, global::Grasshopper.Kernel.Types.IGH_Goo =>
        log?.Add(new GH_PersistentDataAction<T>(param));

    /// <summary>Before moving an object: undo restores its pivot.</summary>
    internal static void BeforeMove(TurnUndoLog? log, IGH_DocumentObject obj) =>
        log?.Add(new GH_PivotAction(obj));
}
