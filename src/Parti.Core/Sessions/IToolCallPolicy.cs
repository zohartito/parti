// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;
using Parti.Core.Ai;

namespace Parti.Core.Sessions;

/// <summary>
/// Deterministic check the session runs on a frozen provider response, after the batch guard's
/// <c>TryFreeze</c> and before the decomposition gate and plan review. A policy that returns
/// <c>false</c> rejects the whole response; nothing in it is dispatched.
/// </summary>
public interface IToolCallPolicy
{
    /// <summary>
    /// Validates the frozen calls of one provider response.
    /// </summary>
    /// <param name="frozen">Deep-copied calls produced by the batch guard.</param>
    /// <param name="reversibilityByName">Tool name to reversible (canvas-mutating) flag.</param>
    /// <param name="error">Human- and model-readable reason when the response is rejected.</param>
    /// <returns><c>true</c> when the response may proceed.</returns>
    bool TryValidate(
        IReadOnlyList<AIInteractionToolCall> frozen,
        IReadOnlyDictionary<string, bool> reversibilityByName,
        out string error);
}
