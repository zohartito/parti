// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Design;

namespace Parti.Core.Sessions;

/// <summary>
/// Deterministic three-pillar gate for tool batches. When
/// <see cref="SessionOptions.RequireDecomposition"/> is set, <see cref="ConversationSession"/>
/// runs this after the batch is frozen and the <see cref="IToolCallPolicy"/> checks pass, and
/// before plan review. A batch that contains any reversible call is rejected unless the
/// recorded decomposition passes <see cref="PillarGate.Evaluate"/>. Read-only calls,
/// <c>gh_component_lock</c>, and the session tools are never gated because they are not
/// reversible.
/// </summary>
public static class DecompositionGate
{
    /// <summary>Value of the <c>gate</c> field on a rejected tool result.</summary>
    public const string GateName = "three_pillar";

    /// <summary>
    /// Evaluates the gate for one frozen batch.
    /// </summary>
    /// <param name="frozen">The frozen calls of the response.</param>
    /// <param name="reversibilityByName">Tool name to reversibility.</param>
    /// <param name="current">The session's recorded decomposition.</param>
    /// <param name="result">The pillar-gate result when the batch is gated; otherwise null.</param>
    /// <returns><c>true</c> when the batch may proceed to review.</returns>
    public static bool Allows(
        IReadOnlyList<AIInteractionToolCall> frozen,
        IReadOnlyDictionary<string, bool> reversibilityByName,
        Decomposition current,
        out PillarGateResult? result)
    {
        if (frozen is null) throw new ArgumentNullException(nameof(frozen));
        if (reversibilityByName is null) throw new ArgumentNullException(nameof(reversibilityByName));
        if (current is null) throw new ArgumentNullException(nameof(current));

        result = null;
        if (!PlanReviewBuilder.HasAnyReversible(frozen, reversibilityByName))
        {
            return true;
        }

        var evaluation = PillarGate.Evaluate(current);
        if (evaluation.Outcome == PillarGateOutcome.Pass)
        {
            return true;
        }

        result = evaluation;
        return false;
    }

    /// <summary>
    /// Builds the tool_result payload for a gated call:
    /// <c>{"success":false,"rejected":true,"gate":"three_pillar","message":…,"missingPillars":[…],"suggestedQuestions":[…]}</c>.
    /// </summary>
    public static JObject RejectionPayload(PillarGateResult result)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));

        return new JObject
        {
            ["success"] = false,
            ["rejected"] = true,
            ["gate"] = GateName,
            ["message"] = "Canvas write rejected by the three-pillar gate: record the decomposition with "
                + "parti_record_decomposition (as the only call in its response) or ask the user with ask_user "
                + "before writing. Missing: " + string.Join(", ", result.MissingPillars) + ". No call in this response was dispatched.",
            ["missingPillars"] = new JArray(result.MissingPillars.ToArray()),
            ["suggestedQuestions"] = new JArray(result.SuggestedQuestions.Select(q => new JObject
            {
                ["id"] = q.Id,
                ["prompt"] = q.Prompt,
            })),
        };
    }
}
