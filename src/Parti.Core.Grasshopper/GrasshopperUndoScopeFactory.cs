// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Threading;
using Grasshopper;
using Grasshopper.Kernel;

namespace Parti.Core.Grasshopper;

/// <summary>
/// Hands out <see cref="GrasshopperUndoScope"/> instances, one per assistant turn.
/// Resolves the active <see cref="GH_Document"/> lazily at <see cref="Begin"/> time so
/// the factory can be constructed once per chat controller — even before a canvas is
/// open — and still see the right document when each turn fires.
/// </summary>
/// <remarks>
/// <para><b>Document resolution.</b> Callers may supply their own
/// <see cref="Func{GH_Document}"/> (useful for tests and for chat sessions pinned to a
/// specific document). If none is supplied, the factory falls back to
/// <see cref="Instances"/>.<c>ActiveCanvas.Document</c> once, at scope creation, read on the Rhino
/// UI thread (the calling worker waits; on the UI thread it runs inline). The factory
/// seals that result into a per-turn capability; individual tool handlers never resolve the
/// mutable active canvas themselves.</para>
///
/// <para><b>Fail-soft on null.</b> If the resolver returns <c>null</c> (Rhino closing,
/// no canvas active) the factory still hands out a scope — it just produces a degenerate
/// one that swallows <see cref="IUndoScope.RecordMutation"/> and emits nothing on
/// dispose. The chat controller never has to care about Rhino lifecycle edge cases.</para>
///
/// <para><b>Fail-fast on reentrance.</b> Only one live scope at a time. If a second
/// <see cref="Begin"/> is called while an earlier scope is still undisposed, an
/// <see cref="InvalidOperationException"/> is thrown. Chat controllers serialise turns,
/// so nesting is neither needed nor desirable — two turns compressing into one undo
/// record would hide the boundary from the user.</para>
/// </remarks>
public sealed class GrasshopperUndoScopeFactory : IUndoScopeFactory
{
    private readonly Func<GH_Document?> _documentResolver;
    private readonly IGhUndoEmitter _emitter;
    private readonly IUiThreadInvoker _ui;

    /// <summary>
    /// 1 while a scope is open, 0 otherwise. Used with
    /// <see cref="Interlocked.CompareExchange(ref int, int, int)"/> so reentrance
    /// detection is thread-safe even if two threads race to <see cref="Begin"/>.
    /// </summary>
    private int _scopeInFlight;

    /// <summary>
    /// Primary constructor used inside Rhino. Resolves the active document lazily from
    /// <see cref="Instances.ActiveCanvas"/>.
    /// </summary>
    public GrasshopperUndoScopeFactory()
        : this(() => Instances.ActiveCanvas?.Document, new GhUndoEmitter(), new RhinoUiThreadInvoker())
    {
    }

    /// <summary>
    /// Constructor used in tests — lets the caller pin the document (or return <c>null</c>),
    /// swap the emitter for a fake, and supply the UI-thread marshaller.
    /// </summary>
    /// <param name="documentResolver">Called once per <see cref="Begin"/>, on the UI thread, to
    /// fetch the current document. May return <c>null</c>; the resulting scope will be a no-op.</param>
    /// <param name="emitter">The GH_UndoUtil wrapper, called on the UI thread. Production passes
    /// <see cref="GhUndoEmitter"/>; tests pass a recording fake.</param>
    /// <param name="ui">Runs document resolution and the record emission on the UI thread. Null
    /// runs them inline (hermetic tests).</param>
    internal GrasshopperUndoScopeFactory(
        Func<GH_Document?> documentResolver,
        IGhUndoEmitter emitter,
        IUiThreadInvoker? ui = null)
    {
        _documentResolver = documentResolver ?? throw new ArgumentNullException(nameof(documentResolver));
        _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
        _ui = ui ?? InlineUiThreadInvoker.Instance;
    }

    public IUndoScope Begin(string label)
    {
        if (Interlocked.CompareExchange(ref _scopeInFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "GrasshopperUndoScopeFactory.Begin was called while an earlier scope is still open. " +
                "Chat turns must be serialised — nested undo records would hide the turn boundary " +
                "from the user. Dispose the previous scope first.");
        }

        GH_Document? doc = null;
        try
        {
            // The active canvas is UI state: read it on the UI thread.
            GH_Document? resolved = null;
            if (_ui.Invoke(() => resolved = _documentResolver()))
            {
                doc = resolved;
            }
            else
            {
                GrasshopperUndoScope.Log("[Parti] undo scope factory: the Rhino UI thread did not answer; no document is pinned.");
            }
        }
        catch (Exception ex)
        {
            // Resolver threw (Rhino tearing down, etc). Treat as "no document".
            GrasshopperUndoScope.Log($"[Parti] undo scope factory: document resolver failed: {ex.Message}");
            doc = null;
        }

        // Even when doc is null we still return a live scope — it's the caller's
        // contract that scopes are disposable once and only once. The scope itself
        // will be a no-op in that case.
        // One chronological undo log per turn, shared by the lease (handlers append to it) and
        // the scope (which emits it as one record).
        var log = new TurnUndoLog();
        var capability = GrasshopperDocumentCapability.Acquire(doc, log);
        return new GrasshopperUndoScope(
            label: label ?? string.Empty,
            document: doc,
            emitter: _emitter,
            onDisposed: () =>
            {
                capability.Dispose();
                Interlocked.Exchange(ref _scopeInFlight, 0);
            },
            documentLeaseToken: capability.Token,
            log: log,
            ui: _ui);
    }
}
