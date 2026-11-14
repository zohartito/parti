// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using Parti.Core.Design;

namespace Parti.Core.Sessions;

/// <summary>
/// Thread-safe holder for the active <see cref="Decomposition"/> on a
/// <see cref="ConversationSession"/>. The decomposition accumulates across turns as the
/// decomposer / clarifier sub-agents refine their understanding of the design problem.
/// <para>
/// The three-pillar gate consults this state before allowing generation. When
/// <see cref="Current"/> is incomplete, the gate either (a) asks the clarifier sub-agent
/// to fill the missing pillars, or (b) triggers an <c>ask_user</c> pause via
/// <see cref="ClarificationState"/>.
/// </para>
/// </summary>
public sealed class DecompositionState
{
    private Decomposition _current = Decomposition.Empty;
    private int _revision;
    private readonly object _lock = new();

    /// <summary>
    /// Current decomposition snapshot. Always non-null — starts as <see cref="Decomposition.Empty"/>
    /// and is replaced atomically as sub-agents refine it.
    /// </summary>
    public Decomposition Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Number of times a decomposition has been recorded since construction or the last
    /// <see cref="Reset"/>. Zero means nothing has been recorded.
    /// </summary>
    public int Revision
    {
        get
        {
            lock (_lock)
            {
                return _revision;
            }
        }
    }

    /// <summary>
    /// True when the current decomposition passes <see cref="PillarGate.Evaluate"/> (all three
    /// pillars carry content) and the session is allowed to proceed to generation.
    /// </summary>
    public bool IsComplete
    {
        get
        {
            lock (_lock)
            {
                return PillarGate.Evaluate(_current).Outcome == PillarGateOutcome.Pass;
            }
        }
    }

    /// <summary>
    /// Returns the current decomposition and its revision as one consistent pair.
    /// </summary>
    public (Decomposition Decomposition, int Revision) Snapshot()
    {
        lock (_lock)
        {
            return (_current, _revision);
        }
    }

    /// <summary>
    /// Replace the current decomposition and bump <see cref="Revision"/>. Called by the session
    /// when the model records a decomposition with <c>parti_record_decomposition</c>.
    /// </summary>
    /// <returns>The new revision.</returns>
    internal int Update(Decomposition decomposition)
    {
        if (decomposition is null)
        {
            throw new System.ArgumentNullException(nameof(decomposition));
        }

        lock (_lock)
        {
            _current = decomposition;
            return ++_revision;
        }
    }

    /// <summary>
    /// Reset to <see cref="Decomposition.Empty"/>. Called at the start of a fresh session
    /// or when the user starts a clearly unrelated task.
    /// </summary>
    internal void Reset()
    {
        lock (_lock)
        {
            _current = Decomposition.Empty;
            _revision = 0;
        }
    }
}
