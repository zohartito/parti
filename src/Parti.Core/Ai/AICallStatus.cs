// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// Ported from SmartHopper (https://github.com/architects-toolkit/SmartHopper)
// Original Copyright (c) SmartHopper contributors, LGPL-3.0-or-later

namespace Parti.Core.Ai;

/// <summary>
/// Lifecycle status of an AI request call.
/// </summary>
public enum AICallStatus
{
    Idle = 0,
    Pending,
    Processing,
    Streaming,
    CallingTools,
    Finished,
    Error,
}
