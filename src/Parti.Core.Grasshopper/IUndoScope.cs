// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;

namespace Parti.Core.Grasshopper;

/// <summary>
/// A disposable scope that batches every canvas mutation performed during an assistant turn
/// into a single Grasshopper undo event. Disposing the scope finalizes the batch so one
/// Ctrl+Z reverts the whole turn.
///
/// Lives behind an interface so:
///   (1) the chat controller can wire it without a hard Rhino dependency, and
///   (2) unit tests can substitute <see cref="NoOpUndoScope"/>.
///
/// The real GH_UndoUtil-backed implementation lives in <c>GrasshopperUndoScope</c> and
/// requires a live Rhino host.
/// </summary>
public interface IUndoScope : IDisposable
{
    /// <summary>
    /// Human-readable label for this turn, shown to the user in the undo stack
    /// (e.g. "add slider" or "connect A to B").
    /// </summary>
    string Label { get; }

    /// <summary>
    /// Notifies the scope that <paramref name="document"/> has been mutated and needs
    /// to be included in the batched undo event. Called by tool handlers after each
    /// canvas edit. Implementations may coalesce multiple invocations into one event.
    /// </summary>
    void RecordMutation(object document);

    /// <summary>
    /// Cancels the batch without recording anything. Useful when a turn errors before
    /// any mutation landed and we don't want to pollute the undo stack with a no-op.
    /// </summary>
    void Abandon();
}

/// <summary>
/// Degenerate scope used by hermetic tests and by the chat controller when running
/// outside Rhino. Swallows <see cref="RecordMutation"/>/<see cref="Abandon"/>; does nothing
/// on <see cref="IDisposable.Dispose"/>.
/// </summary>
public sealed class NoOpUndoScope : IUndoScope
{
    public NoOpUndoScope(string label = "")
    {
        Label = label ?? string.Empty;
    }

    public string Label { get; }

    public void RecordMutation(object document) { }

    public void Abandon() { }

    public void Dispose() { }
}

/// <summary>
/// Factory contract. Created once per chat controller and produces one scope per
/// assistant turn. Kept separate from <see cref="IUndoScope"/> so the factory can hold
/// host state (e.g. the Rhino UI dispatcher) without leaking it into per-turn scopes.
/// </summary>
public interface IUndoScopeFactory
{
    /// <summary>
    /// Opens a new scope for a turn with the given human-readable label. Callers
    /// must Dispose the returned scope exactly once.
    /// </summary>
    IUndoScope Begin(string label);
}

/// <summary>
/// Factory that hands out <see cref="NoOpUndoScope"/> instances. Used by tests and by
/// the chat controller running outside Rhino.
/// </summary>
public sealed class NoOpUndoScopeFactory : IUndoScopeFactory
{
    public IUndoScope Begin(string label) => new NoOpUndoScope(label);
}
