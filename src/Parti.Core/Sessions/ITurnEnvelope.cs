// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using Parti.Core.Ai;

namespace Parti.Core.Sessions;

/// <summary>
/// Per-loop host envelope opened once per <c>RunAsync</c> / <c>ContinueAsync</c> / <c>ResumeAsync</c>
/// call, before the first provider call. It bridges host-only concerns (an undo scope, a document
/// lease, a binding prefix carrying the document identity) into the Core session without Core
/// depending on any Grasshopper type.
/// </summary>
public interface ITurnEnvelope : IDisposable
{
    /// <summary>
    /// Prefix prepended to the plan binding as <c>"{BindingPrefix}:{bindingId}"</c>; null when the
    /// host has no document identity to bind.
    /// </summary>
    string? BindingPrefix { get; }

    /// <summary>
    /// Called immediately before a reversible call is dispatched. Marks that a mutation was attempted.
    /// </summary>
    void BeforeReversibleDispatch(AIInteractionToolCall call);

    /// <summary>
    /// Attaches host capabilities (e.g. a document lease) to the materialized, approved copies
    /// returned by the batch guard. Must never mutate calls that reach history or the plan.
    /// </summary>
    IReadOnlyList<AIInteractionToolCall> AttachCapabilities(IReadOnlyList<AIInteractionToolCall> materialized);

    /// <summary>
    /// Removes host capabilities from a (deep-copied) tool result before it enters history or
    /// reaches the observer or memory harvest.
    /// </summary>
    void ScrubResult(AIInteractionToolResult result);

    /// <summary>
    /// Ends the envelope: kept when <paramref name="mutationAttempted"/> is true, abandoned otherwise.
    /// </summary>
    void Complete(bool mutationAttempted);
}

/// <summary>Opens one <see cref="ITurnEnvelope"/> per session loop call.</summary>
public interface ITurnEnvelopeFactory
{
    /// <summary>Opens a new envelope with a human-readable label (e.g. the undo record name).</summary>
    ITurnEnvelope Begin(string label);
}

/// <summary>
/// Pass-through factory for hosts with no undo scope or document lease (Core library,
/// <c>parti-cli</c>, tests). Its envelopes have no binding prefix, attach nothing, scrub nothing.
/// </summary>
public sealed class NullTurnEnvelopeFactory : ITurnEnvelopeFactory
{
    /// <summary>Shared instance; the factory is stateless.</summary>
    public static NullTurnEnvelopeFactory Instance { get; } = new();

    /// <inheritdoc />
    public ITurnEnvelope Begin(string label) => NullTurnEnvelope.Instance;

    private sealed class NullTurnEnvelope : ITurnEnvelope
    {
        internal static NullTurnEnvelope Instance { get; } = new();

        public string? BindingPrefix => null;

        public void BeforeReversibleDispatch(AIInteractionToolCall call)
        {
        }

        public IReadOnlyList<AIInteractionToolCall> AttachCapabilities(IReadOnlyList<AIInteractionToolCall> materialized) =>
            materialized ?? throw new ArgumentNullException(nameof(materialized));

        public void ScrubResult(AIInteractionToolResult result)
        {
        }

        public void Complete(bool mutationAttempted)
        {
        }

        public void Dispose()
        {
        }
    }
}
