// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// ComponentLockRegistry — v0.7.5 primitive, inspired by SmartHopper's component locking.
// Users can "lock" trusted components so that the AI cannot edit, reconnect, or delete
// them. The registry is the source of truth; the lock policy consults it at freeze time and
// the delete adapter consults it again at dispatch.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Parti.Core.Sessions;

/// <summary>
/// Thread-safe set of locked component <b>instance GUIDs</b>, i.e. components that are off-limits
/// to AI mutations. Every id is normalized with <c>Guid.Parse(...).ToString("D")</c>, so any GUID
/// spelling (braces, upper case, no hyphens) names the same component; strings that are not GUIDs
/// (SimpleIds, names) are never stored and are never reported as locked.
/// <para>
/// Distinct from Rhino's layer lock: this is Parti-internal. A locked component can still be
/// edited manually by the user; only AI edits are refused. Locks record user intent, so they are
/// kept across a conversation reset and live as long as the owning controller.
/// </para>
/// </summary>
public sealed class ComponentLockRegistry
{
    private readonly HashSet<string> _locked = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>
    /// Normalizes a component instance GUID to its canonical <c>"D"</c> form, or returns false when
    /// <paramref name="id"/> is not a GUID.
    /// </summary>
    public static bool TryNormalize(string? id, out string normalized)
    {
        if (!string.IsNullOrWhiteSpace(id) && Guid.TryParse(id, out var guid))
        {
            normalized = guid.ToString("D");
            return true;
        }
        normalized = string.Empty;
        return false;
    }

    /// <summary>True when the given instance GUID is currently locked.</summary>
    public bool IsLocked(string instanceGuid)
    {
        if (!TryNormalize(instanceGuid, out var id)) return false;
        lock (_lock)
        {
            return _locked.Contains(id);
        }
    }

    /// <summary>True when the given instance GUID is currently locked.</summary>
    public bool IsLocked(Guid instanceGuid) => IsLocked(instanceGuid.ToString("D"));

    /// <summary>
    /// Lock the given instance GUID. Idempotent. Returns true if the component was newly locked,
    /// false if it was already locked or <paramref name="instanceGuid"/> is not a GUID.
    /// </summary>
    public bool Lock(string instanceGuid)
    {
        if (!TryNormalize(instanceGuid, out var id)) return false;
        lock (_lock)
        {
            return _locked.Add(id);
        }
    }

    /// <summary>
    /// Unlock the given instance GUID. Idempotent. Returns true if the component was locked
    /// and has now been unlocked. Human-only: no model-facing tool calls this.
    /// </summary>
    public bool Unlock(string instanceGuid)
    {
        if (!TryNormalize(instanceGuid, out var id)) return false;
        lock (_lock)
        {
            return _locked.Remove(id);
        }
    }

    /// <summary>Unlock the given instance GUID. Human-only; see <see cref="Unlock(string)"/>.</summary>
    public bool Unlock(Guid instanceGuid) => Unlock(instanceGuid.ToString("D"));

    /// <summary>Snapshot of all currently locked instance GUIDs, sorted ordinally.</summary>
    public IReadOnlyList<string> ListLocked()
    {
        lock (_lock)
        {
            return _locked.OrderBy(s => s, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Count of currently locked components.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _locked.Count;
            }
        }
    }

    /// <summary>Clear all locks. Intended for an explicit user "unlock all".</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _locked.Clear();
        }
    }
}
