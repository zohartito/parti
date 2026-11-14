// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;

namespace Parti.Core.Grasshopper;

/// <summary>
/// The ordered list of undo actions one Parti turn produced. Tool handlers append an action at
/// the moment of each mutation (captured before the change, or right after an addition), so the
/// list is chronological. Grasshopper undoes a record's actions in reverse order, which replays
/// the turn backwards exactly: every action is undone against the document state it was
/// captured from.
/// </summary>
/// <remarks>
/// Actions are boxed as <see cref="object"/> so hermetic tests can use plain markers; the
/// production emitter keeps only <c>IGH_UndoAction</c> instances. Appends come from the Rhino UI
/// thread; <see cref="Close"/> runs there too when the record is emitted (or on the session thread
/// for an abandoned turn), and tests read it from anywhere, so every member takes the lock. After
/// <see cref="Close"/> further appends are ignored: a late tool callback cannot slip an action
/// into a record that was already emitted.
/// </remarks>
internal sealed class TurnUndoLog
{
    private readonly object _gate = new();
    private readonly List<object> _actions = new();
    private readonly HashSet<Guid> _recordedAdditions = new();
    private bool _closed;

    /// <summary>Appends <paramref name="action"/> unless the log is closed.</summary>
    /// <param name="action">The undo action, captured at the moment of the mutation.</param>
    /// <param name="addedInstance">For an addition, the new object's instance GUID. Only additions
    /// reported this way are Parti's; the scope never adds objects it did not create.</param>
    internal void Add(object action, Guid? addedInstance = null)
    {
        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }
            _actions.Add(action);
            if (addedInstance is Guid id)
            {
                _recordedAdditions.Add(id);
            }
        }
    }

    /// <summary>True when an addition of <paramref name="instance"/> was already recorded.</summary>
    internal bool IsRecordedAddition(Guid instance)
    {
        lock (_gate)
        {
            return _recordedAdditions.Contains(instance);
        }
    }

    /// <summary>Number of actions recorded so far.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _actions.Count;
            }
        }
    }

    /// <summary>Closes the log and returns its actions in the order they were recorded.</summary>
    internal List<object> Close()
    {
        lock (_gate)
        {
            _closed = true;
            return new List<object>(_actions);
        }
    }
}
