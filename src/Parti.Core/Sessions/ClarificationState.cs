// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

namespace Parti.Core.Sessions;

/// <summary>
/// Thread-safe holder for the clarification pause on a <see cref="ConversationSession"/>.
/// <para>
/// UIs read <see cref="IsPaused"/> / <see cref="Current"/> to render the paused questions;
/// <see cref="ConversationSession.ResumeAsync"/> drains the state when the user supplies answers.
/// </para>
/// </summary>
public sealed class ClarificationState
{
    private PausedState? _paused;
    private readonly object _lock = new();

    /// <summary>True when the session is currently paused awaiting clarification answers.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _paused is not null;
            }
        }
    }

    /// <summary>
    /// The current paused state, or null when the session is running. Safe to read at any time.
    /// </summary>
    public PausedState? Current
    {
        get
        {
            lock (_lock)
            {
                return _paused;
            }
        }
    }

    /// <summary>
    /// Set the paused state. Called by <see cref="ConversationSession"/> when the model emits
    /// an <c>ask_user</c> tool call.
    /// </summary>
    internal void Pause(PausedState s)
    {
        lock (_lock)
        {
            _paused = s;
        }
    }

    /// <summary>
    /// Atomically return the current paused state and clear it. Called on resume so only one
    /// caller can consume the state.
    /// </summary>
    internal PausedState? DrainAndResume()
    {
        lock (_lock)
        {
            var s = _paused;
            _paused = null;
            return s;
        }
    }
}
