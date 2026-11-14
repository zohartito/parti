// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// Ported from SmartHopper (https://github.com/architects-toolkit/SmartHopper)
// Original Copyright (c) SmartHopper contributors, LGPL-3.0-or-later

using System;

namespace Parti.Core.Ai;

/// <summary>
/// A single message or event in a conversation (text, tool call, tool result).
/// </summary>
public interface IAIInteraction
{
    /// <summary>
    /// Per-turn stable identifier. All interactions that belong to the same logical assistant
    /// turn share the same TurnId.
    /// </summary>
    string? TurnId { get; set; }

    /// <summary>
    /// UTC timestamp of the interaction.
    /// </summary>
    DateTime Time { get; set; }

    /// <summary>
    /// Who produced this interaction.
    /// </summary>
    AIAgent Agent { get; set; }
}
