// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// Ported from SmartHopper (https://github.com/architects-toolkit/SmartHopper)
// Original Copyright (c) SmartHopper contributors, LGPL-3.0-or-later

using System;

namespace Parti.Core.Ai;

/// <summary>
/// Base class for all interactions. Centralizes TurnId, Time, and Agent so concrete interactions
/// only carry their payload.
/// </summary>
public abstract class AIInteractionBase : IAIInteraction
{
    /// <inheritdoc />
    public virtual string? TurnId { get; set; }

    /// <inheritdoc />
    public virtual DateTime Time { get; set; } = DateTime.UtcNow;

    /// <inheritdoc />
    public virtual AIAgent Agent { get; set; }
}
