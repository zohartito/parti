// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Sessions;

namespace Parti.Core.Grasshopper.Chat;

/// <summary>
/// Opens one <see cref="UndoScopeTurnEnvelope"/> per session entry call on top of an
/// <see cref="IUndoScopeFactory"/>. In Rhino the factory is <see cref="GrasshopperUndoScopeFactory"/>,
/// whose scopes also carry the document lease and the document identity.
/// </summary>
public sealed class UndoScopeTurnEnvelopeFactory : ITurnEnvelopeFactory
{
    private readonly IUndoScopeFactory _undoScopeFactory;

    /// <summary>Creates a factory over <paramref name="undoScopeFactory"/>.</summary>
    public UndoScopeTurnEnvelopeFactory(IUndoScopeFactory undoScopeFactory)
    {
        _undoScopeFactory = undoScopeFactory ?? throw new ArgumentNullException(nameof(undoScopeFactory));
    }

    /// <inheritdoc />
    public ITurnEnvelope Begin(string label) => new UndoScopeTurnEnvelope(_undoScopeFactory.Begin(label ?? string.Empty));
}

/// <summary>
/// The Grasshopper turn envelope: one undo record, the document lease, and the document
/// identity for one session loop.
/// <list type="bullet">
///   <item><see cref="AttachCapabilities"/> adds the lease to deep copies of the materialized,
///   approved calls only.</item>
///   <item><see cref="ScrubResult"/> removes the lease from a result's arguments and payload,
///   at any depth. The session hands it a deep copy, so the dispatcher's own objects are never
///   mutated.</item>
///   <item><see cref="Complete"/> abandons the undo record unless the loop kept it.</item>
/// </list>
/// </summary>
public sealed class UndoScopeTurnEnvelope : ITurnEnvelope
{
    private readonly IUndoScope _scope;
    private bool _disposed;

    /// <summary>Wraps <paramref name="scope"/>; the envelope owns and disposes it.</summary>
    public UndoScopeTurnEnvelope(IUndoScope scope)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
    }

    /// <summary>
    /// The pinned document's identity when the scope is a real Grasshopper scope; otherwise null.
    /// The session prefixes the plan binding with it.
    /// </summary>
    public string? BindingPrefix => _scope is GrasshopperUndoScope grasshopperScope ? grasshopperScope.DocumentId : null;

    /// <inheritdoc />
    public void BeforeReversibleDispatch(AIInteractionToolCall call) => _scope.RecordMutation(call);

    /// <inheritdoc />
    public IReadOnlyList<AIInteractionToolCall> AttachCapabilities(IReadOnlyList<AIInteractionToolCall> materialized)
    {
        if (materialized is null)
        {
            throw new ArgumentNullException(nameof(materialized));
        }

        if (_scope is not IGrasshopperDocumentLease lease || string.IsNullOrWhiteSpace(lease.DocumentLeaseToken))
        {
            return materialized;
        }

        return materialized.Select(call =>
        {
            var copy = new AIInteractionToolCall
            {
                Id = call.Id,
                Name = call.Name,
                Reasoning = call.Reasoning,
                Agent = call.Agent,
                TurnId = call.TurnId,
                Arguments = (JObject)(call.Arguments ?? new JObject()).DeepClone(),
            };
            copy.Arguments[GrasshopperDocumentCapability.LeaseArgument] = lease.DocumentLeaseToken;
            return copy;
        }).ToList();
    }

    /// <inheritdoc />
    public void ScrubResult(AIInteractionToolResult result)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        var token = (_scope as IGrasshopperDocumentLease)?.DocumentLeaseToken;
        result.Arguments = Scrub(result.Arguments, token);
        result.Result = Scrub(result.Result, token);
        result.Reasoning = Redact(result.Reasoning, token);
    }

    /// <inheritdoc />
    public void Complete(bool mutationAttempted)
    {
        if (mutationAttempted)
        {
            return;
        }

        try
        {
            _scope.Abandon();
        }
        catch
        {
            // Defensive: an abandon failure must not mask the loop's own outcome.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scope.Dispose();
    }

    /// <summary>
    /// Returns a copy of <paramref name="source"/> with every lease property removed, at any depth,
    /// and every occurrence of the live lease token redacted from string values and property names
    /// (for example inside an exception message that serialized the leased arguments).
    /// </summary>
    private static JObject Scrub(JObject? source, string? token)
    {
        var copy = (JObject)(source ?? new JObject()).DeepClone();
        foreach (var property in copy.Descendants().OfType<JProperty>()
                     .Where(p => p.Name == GrasshopperDocumentCapability.LeaseArgument).ToList())
        {
            property.Remove();
        }

        if (string.IsNullOrEmpty(token))
        {
            return copy;
        }

        foreach (var value in copy.Descendants().OfType<JValue>()
                     .Where(v => v.Type == JTokenType.String && ((string?)v)?.Contains(token, StringComparison.Ordinal) == true)
                     .ToList())
        {
            value.Value = Redact((string?)value, token);
        }

        foreach (var property in copy.Descendants().OfType<JProperty>()
                     .Where(p => p.Name.Contains(token, StringComparison.Ordinal)).Reverse().ToList())
        {
            // Deepest first: replacing a property re-parents (copies) its value.
            property.Replace(new JProperty(Redact(property.Name, token)!, property.Value));
        }

        return copy;
    }

    private static string? Redact(string? text, string? token) =>
        text is null || string.IsNullOrEmpty(token) ? text : text.Replace(token, "[redacted]", StringComparison.Ordinal);
}
