// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;
using Rhino;

namespace Parti.Core.Grasshopper;

/// <summary>
/// Rhino-backed <see cref="IUndoScope"/> that batches every canvas mutation performed
/// during one assistant turn into a single <see cref="GH_UndoRecord"/>. Disposing the
/// scope emits that record onto the document's undo stack so one Ctrl+Z reverts the
/// whole turn.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> SmartHopper's <c>gh_put.cs</c> records a removal
/// event, then performs the addition outside any undo record. Two stack entries, one of
/// them empty — first Ctrl+Z restores the deleted nodes, second Ctrl+Z does nothing,
/// canvas left inconsistent. Parti rejects that pattern: every mutation touched during
/// a turn ends up in ONE record.</para>
///
/// <para><b>How the batching works.</b></para>
/// <list type="number">
///   <item><description>The factory hands the scope and the turn's document lease the same
///   <see cref="TurnUndoLog"/>. Every mutating tool handler resolves that log through the lease
///   and appends the matching Grasshopper undo action at the moment of the mutation
///   (<see cref="GhTurnUndo"/>): <see cref="GH_WireAction"/> before a source changes,
///   <see cref="GH_RemoveObjectAction"/> before a removal, <see cref="GH_GenericObjectAction"/>
///   before an in-place state edit, <see cref="GH_PivotAction"/> before a move, and
///   <see cref="GH_AddObjectAction"/> right after an addition. The log is chronological, and
///   Grasshopper undoes a record's actions in reverse, so each action is undone against the exact
///   state it captured.</description></item>
///   <item><description>On <see cref="Dispose"/> the log is closed and emitted as one record via
///   the injected <see cref="IGhUndoEmitter"/>. Only actions Parti's own handlers reported are in
///   it: the scope never scans the document, so an object the USER adds while a turn runs is never
///   put into (and never removed by undoing) the Parti record.</description></item>
///   <item><description><see cref="RecordMutation"/> is advisory: it just flags that
///   the document was touched so that a pristine no-op turn doesn't pollute the undo
///   stack.</description></item>
/// </list>
///
/// <para><b>Why not snapshot every object at Begin.</b> The previous design recorded a
/// <see cref="GH_GenericObjectAction"/> for every pre-existing object and nothing else. Live
/// (2026-09-27) that broke deletes: undoing a turn that deleted a wired component raised a modal
/// Grasshopper assertion inside <c>GH_GenericObjectAction.Internal_Undo</c> (the object was gone),
/// the component was not restored, an unrelated wire was lost, and no redo entry was left.</para>
///
/// <para><b>Threading.</b> Grasshopper objects and the undo stack are touched only on the Rhino
/// UI thread. <see cref="Dispose"/> usually runs on a chat-loop worker; it hops to the UI thread
/// through the injected <see cref="IUiThreadInvoker"/>, closes the log and records the event
/// there, and returns (releasing the lease and the factory) only after that work has run. The
/// worker waits for the UI thread, never the other way round: on the UI thread the work runs
/// inline. Closing the log on the UI thread also means a handler callback queued before the
/// close still lands in this record, and one queued after it cannot.</para>
///
/// <para><b>Reentrance.</b> Fail-fast. The factory rejects a second
/// <see cref="GrasshopperUndoScopeFactory.Begin"/> while an earlier scope is still open;
/// simultaneous assistant turns are not a real scenario in v0.5 (the chat controller
/// serialises turns) and nesting events would make undo behaviour opaque to the user.</para>
///
/// <para><b>API reference (verified against Grasshopper 8.35.26251.13002).</b></para>
/// <list type="bullet">
///   <item><description><c>GH_Document.UndoUtil.RecordEvent(GH_UndoRecord) : Guid</c></description></item>
///   <item><description><c>GH_UndoRecord(string name)</c> + <c>AddAction(IGH_UndoAction)</c></description></item>
///   <item><description><c>GH_AddObjectAction</c>, <c>GH_RemoveObjectAction</c>, <c>GH_WireAction</c>,
///   <c>GH_GenericObjectAction</c>, <c>GH_PivotAction</c>, <c>GH_PersistentDataAction&lt;T&gt;</c></description></item>
/// </list>
/// </remarks>
public sealed class GrasshopperUndoScope : IUndoScope, IGrasshopperDocumentLease
{
    private readonly IGhUndoEmitter _emitter;
    private readonly IUiThreadInvoker _ui;
    private readonly GH_Document? _doc;
    private readonly TurnUndoLog _log;
    private readonly Action? _onDisposed;
    private readonly string _documentLeaseToken;

    private bool _disposed;
    private volatile bool _abandoned;
    private volatile bool _dirty;

    /// <summary>Opens a scope over <paramref name="document"/>. Touches nothing in the document.</summary>
    /// <param name="label">Label shown in the undo stack (e.g. "add slider").</param>
    /// <param name="document">
    /// The pinned <see cref="GH_Document"/>, resolved by the factory on the UI thread. If
    /// <c>null</c>, the scope degrades to a no-op: <see cref="RecordMutation"/> and
    /// <see cref="Dispose"/> are accepted but the production emitter writes nothing. This keeps
    /// callers robust against Rhino closing.
    /// </param>
    /// <param name="emitter">The GH_UndoUtil wrapper, called on the UI thread. Production path
    /// injects <see cref="GhUndoEmitter"/>; tests inject a fake.</param>
    /// <param name="onDisposed">Optional callback fired exactly once when this scope finishes,
    /// pass or fail, after the record was emitted. Used by the factory to release the lease and
    /// its "scope in flight" flag.</param>
    /// <param name="documentLeaseToken">The token of the lease handlers resolve the document and
    /// <paramref name="log"/> through.</param>
    /// <param name="log">The turn's undo log, shared with the document lease. A private log is
    /// used when none is given.</param>
    /// <param name="ui">Marshals the emission onto the UI thread. Null runs it inline.</param>
    internal GrasshopperUndoScope(
        string label,
        GH_Document? document,
        IGhUndoEmitter emitter,
        Action? onDisposed = null,
        string? documentLeaseToken = null,
        TurnUndoLog? log = null,
        IUiThreadInvoker? ui = null)
    {
        Label = label ?? string.Empty;
        _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
        _ui = ui ?? InlineUiThreadInvoker.Instance;
        _doc = document;
        _onDisposed = onDisposed;
        _documentLeaseToken = documentLeaseToken ?? string.Empty;
        _log = log ?? new TurnUndoLog();
    }

    public string Label { get; }

    /// <summary>Opaque identity of the document pinned for this turn, when one was available.</summary>
    internal string? DocumentId => _doc is null ? null : GrasshopperDocumentCapability.DocumentId(_doc);
    string IGrasshopperDocumentLease.DocumentLeaseToken => _documentLeaseToken;

    public void RecordMutation(object document)
    {
        if (_disposed || _abandoned)
        {
            return;
        }

        // Advisory: flags that a reversible call was dispatched, so the turn is not treated as
        // pristine. The record itself holds only the actions the handlers logged.
        _dirty = true;
    }

    public void Abandon()
    {
        _abandoned = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            if (_abandoned)
            {
                // Caller aborted the turn. Per the IUndoScope contract Abandon means
                // "pretend this turn never happened" — nothing is written to the stack.
                _log.Close();
                return;
            }

            bool completed;
            try
            {
                completed = _ui.Invoke(EmitOnUiThread);
            }
            catch (Exception ex)
            {
                _log.Close();
                Log($"[Parti] undo event '{Label}' failed: {ex.Message}");
                return;
            }

            if (!completed)
            {
                // The UI thread did not answer in time. The queued emission still runs later,
                // ahead of any later turn's work in the UI queue; the lease is released now so a
                // hung UI thread cannot hold the factory forever.
                Log($"[Parti] undo event '{Label}' is still waiting for the Rhino UI thread.");
            }
        }
        finally
        {
            _onDisposed?.Invoke();
        }
    }

    /// <summary>Writes a diagnostic to the Rhino command line; never throws.</summary>
    internal static void Log(string message)
    {
        try
        {
            RhinoApp.WriteLine(message);
        }
        catch
        {
            // No Rhino command line (hermetic host, Rhino closing): a diagnostic must not fail Dispose.
        }
    }

    // UI thread only.
    private void EmitOnUiThread()
    {
        // Closing first means a late handler callback can never append to this turn.
        var actions = _log.Close();

        // A pristine turn — no advisory mutation flag and no recorded action — must not
        // pollute the undo stack.
        if (!_dirty && actions.Count == 0)
        {
            return;
        }

        // If _doc is null (Rhino closing) the production emitter fails soft and logs; it also
        // drops a record with no actions.
        _emitter.Emit(Label, _doc, actions);
    }
}
