// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// Ported from SmartHopper (https://github.com/architects-toolkit/SmartHopper)
// Original Copyright (c) SmartHopper contributors, LGPL-3.0-or-later

namespace Parti.Core.Ai;

/// <summary>
/// Logical sender of an <see cref="IAIInteraction"/>. Providers map these to wire-level roles
/// (e.g. Anthropic: User/Assistant, with ToolResult encoded as a User message).
/// </summary>
public enum AIAgent
{
    Unknown = 0,
    System,
    Context,
    User,
    Assistant,
    ToolCall,
    ToolResult,
}
