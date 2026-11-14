// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Grasshopper.Kernel;
using System.Text.Json.Nodes;

namespace Parti.Core.Grasshopper;

/// <summary>
/// Explicit, opaque leases for Grasshopper documents. Tool handlers must receive a live lease
/// token in their arguments; they never fall back to the mutable active canvas. Leases are keyed
/// independently, so disposing one out of order cannot resurrect another document capability.
/// </summary>
internal static class GrasshopperDocumentCapability
{
    internal const string LeaseArgument = "__parti_document_lease";

    private static readonly ConcurrentDictionary<string, LeaseRecord> Leases = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<GH_Document, Identity> Identities = new();

    private sealed class Identity { internal string Value { get; } = Guid.NewGuid().ToString("N"); }
    private sealed record LeaseRecord(GH_Document? Document, TurnUndoLog? Undo);

    internal static DocumentLease Acquire(GH_Document? document, TurnUndoLog? undo = null)
    {
        var token = Guid.NewGuid().ToString("N");
        Leases.TryAdd(token, new LeaseRecord(document, undo));
        return new DocumentLease(token);
    }

    internal static GH_Document? ResolveRequired(JsonObject parameters)
    {
        var token = parameters[LeaseArgument]?.GetValue<string?>();
        return token is not null && Leases.TryGetValue(token, out var lease) ? lease.Document : null;
    }

    /// <summary>
    /// The undo log of the turn that owns the lease in <paramref name="parameters"/>, or null when
    /// the lease is missing, expired, or was acquired without one. Mutating handlers append their
    /// undo actions here (see <see cref="GhTurnUndo"/>).
    /// </summary>
    internal static TurnUndoLog? ResolveUndoLog(JsonObject parameters)
    {
        var token = parameters[LeaseArgument]?.GetValue<string?>();
        return token is not null && Leases.TryGetValue(token, out var lease) ? lease.Undo : null;
    }

    internal static bool IsLive(string token) => Leases.ContainsKey(token);

    internal static string DocumentId(GH_Document document) => Identities.GetValue(document, _ => new Identity()).Value;

    internal sealed class DocumentLease : IDisposable
    {
        private string? _token;
        internal DocumentLease(string token) => _token = token;
        internal string Token => _token ?? string.Empty;
        public void Dispose()
        {
            var token = System.Threading.Interlocked.Exchange(ref _token, null);
            if (token is not null)
            {
                Leases.TryRemove(token, out _);
            }
        }
    }
}

/// <summary>Implemented by undo scopes that own an explicit Grasshopper document lease.</summary>
public interface IGrasshopperDocumentLease
{
    string DocumentLeaseToken { get; }
}
