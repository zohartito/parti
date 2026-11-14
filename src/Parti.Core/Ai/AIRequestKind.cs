// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// Ported from SmartHopper (https://github.com/architects-toolkit/SmartHopper)
// Original Copyright (c) SmartHopper contributors, LGPL-3.0-or-later

namespace Parti.Core.Ai;

/// <summary>
/// Kind of provider request. Generation covers user-facing LLM calls; Backoffice covers
/// provider metadata endpoints like /models.
/// </summary>
public enum AIRequestKind
{
    Generation = 0,
    Backoffice,
}
