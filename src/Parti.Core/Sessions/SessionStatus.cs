// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

namespace Parti.Core.Sessions;

/// <summary>
/// Lifecycle state of a <see cref="ConversationSession"/>. Distinct from
/// <see cref="Parti.Core.Ai.AICallStatus"/>, which tracks a single provider call — this
/// tracks the session's overall state across turns (idle, running, paused for clarification, done).
/// </summary>
public enum SessionStatus
{
    /// <summary>No call in flight; either never started or last turn finished cleanly.</summary>
    Idle = 0,

    /// <summary>A turn loop is currently executing.</summary>
    Running,

    /// <summary>Model emitted an <c>ask_user</c> tool call; waiting for user answers via
    /// <see cref="ConversationSession.ResumeAsync"/>.</summary>
    Paused,

    /// <summary>The last turn ended with an error or cap hit.</summary>
    Error,
}
