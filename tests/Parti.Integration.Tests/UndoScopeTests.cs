// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Parti.Core.Grasshopper;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// Hermetic tests for <see cref="GrasshopperUndoScope"/> and
/// <see cref="GrasshopperUndoScopeFactory"/>. Exercises every branch of the scope
/// lifecycle (Begin / RecordMutation / Abandon / Dispose) against a recording fake
/// <see cref="IGhUndoEmitter"/> so no live Rhino process is required.
/// </summary>
/// <remarks>
/// Rhino's <c>GH_Document</c> type-initialises against the native <c>rhcommon_c</c>
/// DLL and therefore can't be constructed outside the Rhino process. The tests work
/// around that by passing <c>null</c> as the document — the scope still advances
/// through its lifecycle and invokes the injected <see cref="IGhUndoEmitter"/>, which
/// is what matters for correctness. The real <c>GH_UndoUtil.RecordEvent</c> call that
/// the production emitter wraps is exercised only in live Rhino.
/// </remarks>
public class UndoScopeTests
{
    [Fact]
    public void DocumentLeases_ExpireIndependentlyWhenDisposedOutOfOrder()
    {
        var first = GrasshopperDocumentCapability.Acquire(document: null);
        var second = GrasshopperDocumentCapability.Acquire(document: null);
        var firstToken = first.Token;
        var secondToken = second.Token;

        Assert.True(GrasshopperDocumentCapability.IsLive(firstToken));
        Assert.True(GrasshopperDocumentCapability.IsLive(secondToken));

        first.Dispose();
        Assert.False(GrasshopperDocumentCapability.IsLive(firstToken));
        Assert.True(GrasshopperDocumentCapability.IsLive(secondToken));

        second.Dispose();
        Assert.False(GrasshopperDocumentCapability.IsLive(secondToken));
    }

    [Fact]
    public void NoOpScope_DoesNothing_OnDispose()
    {
        using var scope = new NoOpUndoScope("anything");
        scope.RecordMutation(document: "doc-a");
        scope.RecordMutation(document: "doc-b");
        scope.Abandon();

        // Act — the dispose runs via the using block.
        // Assert — the only observable promise NoOpUndoScope makes is "doesn't throw".
        // We don't touch any canvas, we don't emit any event. Label is preserved.
        Assert.Equal("anything", scope.Label);
    }

    [Fact]
    public void Begin_ReturnsScopeWithLabel()
    {
        var emitter = new FakeGhUndoEmitter();
        var factory = new GrasshopperUndoScopeFactory(
            documentResolver: () => null,
            emitter: emitter);

        using var scope = factory.Begin("add-slider");

        Assert.Equal("add-slider", scope.Label);
        Assert.IsType<GrasshopperUndoScope>(scope);
    }

    [Fact]
    public void RecordMutation_BeforeDispose_CollectsDocument()
    {
        var emitter = new FakeGhUndoEmitter();
        var factory = new GrasshopperUndoScopeFactory(
            documentResolver: () => null,
            emitter: emitter);

        using (var scope = factory.Begin("record-one"))
        {
            // Simulate what a tool handler does: tell the scope that the canvas was
            // mutated. RecordMutation flips the scope's dirty flag so the emitter is
            // invoked on Dispose.
            scope.RecordMutation(document: new object());
        }

        Assert.Single(emitter.Calls);
        var call = emitter.Calls[0];
        Assert.Equal("record-one", call.Label);
        // Document is null here because we're hermetic — the important invariant is
        // that exactly one emit happened for the turn, carrying whatever doc handle
        // the scope saw at Begin.
        Assert.Null(call.Document);
    }

    [Fact]
    public void Abandon_ThenDispose_EmitsNoEvent()
    {
        var emitter = new FakeGhUndoEmitter();
        var factory = new GrasshopperUndoScopeFactory(
            documentResolver: () => null,
            emitter: emitter);

        var scope = factory.Begin("abandoned-turn");
        scope.RecordMutation(document: new object());
        scope.Abandon();
        scope.Dispose();

        Assert.Empty(emitter.Calls);
        // Double-Dispose must be safe and must not retroactively emit anything.
        scope.Dispose();
        Assert.Empty(emitter.Calls);
    }

    [Fact]
    public void MultipleRecordMutations_SameDocument_StillOneEvent()
    {
        var emitter = new FakeGhUndoEmitter();
        var factory = new GrasshopperUndoScopeFactory(
            documentResolver: () => null,
            emitter: emitter);

        using (var scope = factory.Begin("batch"))
        {
            var doc = new object();
            scope.RecordMutation(doc);
            scope.RecordMutation(doc);
            scope.RecordMutation(doc);
            scope.RecordMutation(doc);
        }

        // This is the SmartHopper fix: every in-turn mutation coalesces into exactly
        // one undo event so a single Ctrl+Z reverts the whole turn.
        Assert.Single(emitter.Calls);
        Assert.Equal("batch", emitter.Calls[0].Label);
    }

    [Fact]
    public void Begin_WhileScopeOpen_FailsFast()
    {
        var emitter = new FakeGhUndoEmitter();
        var factory = new GrasshopperUndoScopeFactory(
            documentResolver: () => null,
            emitter: emitter);

        using var first = factory.Begin("first");
        var ex = Assert.Throws<InvalidOperationException>(() => factory.Begin("second"));
        Assert.Contains("still open", ex.Message, StringComparison.OrdinalIgnoreCase);

        // After Dispose the factory must accept a new turn — reentrance is guarded by
        // a counter, not a one-shot flag.
        first.Dispose();
        using var third = factory.Begin("third");
        Assert.Equal("third", third.Label);
    }

    [Fact]
    public void HandlerActions_RoutedThroughTheLease_AreEmittedInCaptureOrder()
    {
        // F1: tools resolve the turn's undo log through the document lease and append each
        // action at the moment of the mutation. One record, chronological, so Grasshopper's
        // reverse replay undoes every action against the state it captured.
        var emitter = new FakeGhUndoEmitter();
        var factory = new GrasshopperUndoScopeFactory(documentResolver: () => null, emitter: emitter);

        using (var scope = factory.Begin("turn"))
        {
            var args = LeaseArgs(scope);
            var log = GrasshopperDocumentCapability.ResolveUndoLog(args);
            Assert.NotNull(log);
            scope.RecordMutation(new object());
            log!.Add("add point");
            log.Add("wire circle.C");
            log.Add("edit slider");
            log.Add("remove unit z");
        }

        var call = Assert.Single(emitter.Calls);
        Assert.Equal(new object[] { "add point", "wire circle.C", "edit slider", "remove unit z" }, call.Actions);
    }

    [Fact]
    public void RecordedActions_WithoutAdvisoryFlag_StillEmitOneRecord()
    {
        var emitter = new FakeGhUndoEmitter();
        var factory = new GrasshopperUndoScopeFactory(documentResolver: () => null, emitter: emitter);

        using (var scope = factory.Begin("turn"))
        {
            GrasshopperDocumentCapability.ResolveUndoLog(LeaseArgs(scope))!.Add("wire");
        }

        Assert.Equal(new object[] { "wire" }, Assert.Single(emitter.Calls).Actions);
    }

    [Fact]
    public void PristineTurn_EmitsNothing()
    {
        var emitter = new FakeGhUndoEmitter();
        var factory = new GrasshopperUndoScopeFactory(documentResolver: () => null, emitter: emitter);

        using (factory.Begin("read-only turn"))
        {
        }

        Assert.Empty(emitter.Calls);
    }

    [Fact]
    public void AbandonedTurn_DropsRecordedActions()
    {
        var emitter = new FakeGhUndoEmitter();
        var factory = new GrasshopperUndoScopeFactory(documentResolver: () => null, emitter: emitter);

        var scope = factory.Begin("abandoned");
        GrasshopperDocumentCapability.ResolveUndoLog(LeaseArgs(scope))!.Add("wire");
        scope.Abandon();
        scope.Dispose();

        Assert.Empty(emitter.Calls);
    }

    [Fact]
    public void AfterDispose_TheLeaseNoLongerResolvesALog_AndLateAppendsAreIgnored()
    {
        var emitter = new FakeGhUndoEmitter();
        var factory = new GrasshopperUndoScopeFactory(documentResolver: () => null, emitter: emitter);

        var scope = factory.Begin("turn");
        var args = LeaseArgs(scope);
        var log = GrasshopperDocumentCapability.ResolveUndoLog(args)!;
        log.Add("first");
        scope.Dispose();

        Assert.Null(GrasshopperDocumentCapability.ResolveUndoLog(args));
        log.Add("late"); // e.g. a handler callback that ran after the turn ended
        Assert.Equal(new object[] { "first" }, Assert.Single(emitter.Calls).Actions);
        Assert.Equal(1, log.Count);
    }

    [Fact]
    public void EachTurn_GetsItsOwnLog()
    {
        var emitter = new FakeGhUndoEmitter();
        var factory = new GrasshopperUndoScopeFactory(documentResolver: () => null, emitter: emitter);

        using (var first = factory.Begin("first"))
        {
            GrasshopperDocumentCapability.ResolveUndoLog(LeaseArgs(first))!.Add("a");
        }
        using (var second = factory.Begin("second"))
        {
            GrasshopperDocumentCapability.ResolveUndoLog(LeaseArgs(second))!.Add("b");
        }

        Assert.Equal(2, emitter.Calls.Count);
        Assert.Equal(new object[] { "a" }, emitter.Calls[0].Actions);
        Assert.Equal(new object[] { "b" }, emitter.Calls[1].Actions);
    }

    [Fact]
    public void LeaseWithoutLog_ResolvesNoLog()
    {
        using var lease = GrasshopperDocumentCapability.Acquire(document: null);
        var args = new System.Text.Json.Nodes.JsonObject { [GrasshopperDocumentCapability.LeaseArgument] = lease.Token };
        Assert.Null(GrasshopperDocumentCapability.ResolveUndoLog(args));
        Assert.Null(GrasshopperDocumentCapability.ResolveUndoLog(new System.Text.Json.Nodes.JsonObject()));
    }

    [Fact]
    public async Task DisposeOnAWorker_ResolvesAndEmitsOnTheUiThread_ThenReleasesTheLease()
    {
        // C1: document resolution and the record emission run on the UI thread; the worker's
        // Dispose returns only after the record is on the stack, and only then is the lease freed.
        using var ui = new DedicatedUiThread();
        var resolverThread = -1;
        string? token = null;
        var emitter = new FakeGhUndoEmitter
        {
            OnEmit = () => Assert.True(GrasshopperDocumentCapability.IsLive(token!), "the lease must outlive the emission"),
        };
        var factory = new GrasshopperUndoScopeFactory(
            documentResolver: () => { resolverThread = Environment.CurrentManagedThreadId; return null; },
            emitter: emitter,
            ui: ui);

        await Task.Run(() =>
        {
            var scope = factory.Begin("turn");
            token = ((IGrasshopperDocumentLease)scope).DocumentLeaseToken;
            ui.Invoke(() => GrasshopperDocumentCapability.ResolveUndoLog(LeaseArgs(scope))!.Add("add circle"));
            scope.Dispose();

            // Synchronous completion: the record exists when Dispose returns.
            Assert.Single(emitter.Calls);
        });

        Assert.Equal(ui.ThreadId, resolverThread);
        var call = Assert.Single(emitter.Calls);
        Assert.Equal(ui.ThreadId, call.ThreadId);
        Assert.Equal(new object[] { "add circle" }, call.Actions);
        Assert.False(GrasshopperDocumentCapability.IsLive(token!));
        using var next = factory.Begin("next turn");
    }

    [Fact]
    public async Task DisposeOnTheUiThread_RunsInline_WithoutWaitingOnItself()
    {
        using var ui = new DedicatedUiThread();
        var emitter = new FakeGhUndoEmitter();
        var factory = new GrasshopperUndoScopeFactory(documentResolver: () => null, emitter: emitter, ui: ui);

        var run = Task.Run(() => ui.Invoke(() =>
        {
            var scope = factory.Begin("ui turn");
            GrasshopperDocumentCapability.ResolveUndoLog(LeaseArgs(scope))!.Add("wire");
            scope.Dispose();
        }));

        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.True(await run);
        Assert.Equal(ui.ThreadId, Assert.Single(emitter.Calls).ThreadId);
    }

    [Fact]
    public async Task HandlerAppend_QueuedOnTheUiThreadBeforeDispose_LandsInTheRecord()
    {
        // The log is closed on the UI thread, so a handler callback that was already queued there
        // when the loop gave up waiting still lands in this turn's record, in order.
        using var ui = new DedicatedUiThread();
        var emitter = new FakeGhUndoEmitter();
        var factory = new GrasshopperUndoScopeFactory(documentResolver: () => null, emitter: emitter, ui: ui);

        await Task.Run(() =>
        {
            var scope = factory.Begin("turn");
            var log = GrasshopperDocumentCapability.ResolveUndoLog(LeaseArgs(scope))!;
            using var gate = new ManualResetEventSlim(false);
            ui.Post(() => gate.Wait());
            ui.Post(() => log.Add("slow handler"));
            gate.Set();
            scope.Dispose();
        });

        Assert.Equal(new object[] { "slow handler" }, Assert.Single(emitter.Calls).Actions);
    }

    [Fact]
    public void Dispose_WhenTheUiThreadDoesNotAnswer_StillReleasesTheLeaseAndFactory()
    {
        var emitter = new FakeGhUndoEmitter();
        var factory = new GrasshopperUndoScopeFactory(documentResolver: () => null, emitter: emitter, ui: new UnresponsiveUi());

        var scope = factory.Begin("turn");
        var token = ((IGrasshopperDocumentLease)scope).DocumentLeaseToken;
        scope.RecordMutation(new object());
        scope.Dispose();

        Assert.Empty(emitter.Calls);
        Assert.False(GrasshopperDocumentCapability.IsLive(token));
        using var next = factory.Begin("next turn");
    }

    [Fact]
    public void KeptReadOnlyTurn_RecordsNothing_EvenWithADocumentChangeItDidNotMake()
    {
        // C2: a read-only turn's envelope is kept (Complete(true) on a natural stop), but the
        // scope records only what Parti's handlers logged. It never diffs the document, so an
        // object the user adds meanwhile is not put into a Parti record (nor removed by its undo).
        var emitter = new FakeGhUndoEmitter();
        var factory = new GrasshopperUndoScopeFactory(documentResolver: () => null, emitter: emitter);
        var envelope = new Parti.Core.Grasshopper.Chat.UndoScopeTurnEnvelopeFactory(factory).Begin("read-only turn");

        envelope.Complete(mutationAttempted: true);
        envelope.Dispose();

        Assert.Empty(emitter.Calls);
    }

    [Fact]
    public void MutatingTurn_RecordsOnlyTheAdditionsItsHandlersReported()
    {
        var emitter = new FakeGhUndoEmitter();
        var factory = new GrasshopperUndoScopeFactory(documentResolver: () => null, emitter: emitter);
        var userAddition = Guid.NewGuid();
        var partiAddition = Guid.NewGuid();

        var scope = factory.Begin("turn");
        var log = GrasshopperDocumentCapability.ResolveUndoLog(LeaseArgs(scope))!;
        scope.RecordMutation(new object());
        log.Add("add slider", partiAddition);
        scope.Dispose();

        Assert.True(log.IsRecordedAddition(partiAddition));
        Assert.False(log.IsRecordedAddition(userAddition));
        Assert.Equal(new object[] { "add slider" }, Assert.Single(emitter.Calls).Actions);
    }

    private static System.Text.Json.Nodes.JsonObject LeaseArgs(IUndoScope scope) => new()
    {
        [GrasshopperDocumentCapability.LeaseArgument] = ((IGrasshopperDocumentLease)scope).DocumentLeaseToken,
    };

    /// <summary>
    /// Records every <see cref="IGhUndoEmitter.Emit"/> call so tests can assert
    /// count, label, document identity and calling thread without a live Rhino.
    /// </summary>
    private sealed class FakeGhUndoEmitter : IGhUndoEmitter
    {
        public List<EmitCall> Calls { get; } = new();

        public Action? OnEmit { get; init; }

        public bool Emit(string label, object? document, IReadOnlyList<object> actions)
        {
            OnEmit?.Invoke();
            Calls.Add(new EmitCall(label, document, actions, Environment.CurrentManagedThreadId));
            return true;
        }
    }

    private sealed record EmitCall(string Label, object? Document, IReadOnlyList<object> Actions, int ThreadId);

    /// <summary>A single dedicated thread standing in for Rhino's UI thread.</summary>
    private sealed class DedicatedUiThread : IUiThreadInvoker, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public DedicatedUiThread()
        {
            _thread = new Thread(() =>
            {
                foreach (var action in _queue.GetConsumingEnumerable())
                {
                    action();
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public void Post(Action action) => _queue.Add(action);

        public bool Invoke(Action action)
        {
            if (Environment.CurrentManagedThreadId == ThreadId)
            {
                action();
                return true;
            }

            using var done = new ManualResetEventSlim(false);
            Exception? failure = null;
            _queue.Add(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
                finally { done.Set(); }
            });
            if (!done.Wait(TimeSpan.FromSeconds(10)))
            {
                return false;
            }
            if (failure is not null)
            {
                throw new InvalidOperationException(failure.Message, failure);
            }
            return true;
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    /// <summary>A UI thread that never runs anything (a hung or closing Rhino).</summary>
    private sealed class UnresponsiveUi : IUiThreadInvoker
    {
        public bool Invoke(Action action) => false;
    }
}
