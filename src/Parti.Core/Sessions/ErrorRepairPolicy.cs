// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// ErrorRepairPolicy — v0.7.5 primitive, inspired by SmartHopper's turn model.
// When a tool call fails (status="error", success=false, or a host rejection), its result is
// fed back to the model so the model can attempt a repair. This policy governs how many
// CONSECUTIVE error-only passes to allow before giving up, independent of the overall
// MaxToolPasses cap.

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;

namespace Parti.Core.Sessions;

/// <summary>
/// Config for bounded error-repair. The cap is defined exactly: the session stops when the
/// <c>N</c>th consecutive all-error tool pass completes, where <c>N</c> is
/// <see cref="MaxConsecutiveErrorPasses"/>. Values below 1 behave as 1 (the first all-error
/// pass ends the loop).
/// </summary>
/// <param name="MaxConsecutiveErrorPasses">
/// Number of consecutive all-error tool passes after which the session stops attempting
/// repair. Set high to defer to MaxToolPasses.
/// </param>
public sealed record ErrorRepairPolicy(int MaxConsecutiveErrorPasses = 4)
{
    /// <summary>
    /// The library default: three repair attempts after a failed pass, so the session stops
    /// when the 4th consecutive all-error pass completes. This is the library's historical
    /// behaviour, expressed under the exact cap definition. The product composition uses 5.
    /// </summary>
    public static ErrorRepairPolicy Default { get; } = new(MaxConsecutiveErrorPasses: 4);

    /// <summary>
    /// Classifier: a tool result is an error when its Result payload has, at the top level,
    /// <c>status == "error"</c>, <c>success == false</c>, or <c>rejected == true</c>. Host
    /// rejections (batch guard, policies, the three-pillar gate) therefore count toward the
    /// repair cap, so a model that keeps retrying a rejected write is stopped.
    /// </summary>
    public static bool IsErrorResult(AIInteractionToolResult result)
    {
        if (result?.Result is not JObject obj) return false;

        if (obj["status"] is JValue { Type: JTokenType.String } status &&
            string.Equals((string?)status, "error", System.StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (obj["success"] is JValue { Type: JTokenType.Boolean } success && !success.Value<bool>())
        {
            return true;
        }

        return obj["rejected"] is JValue { Type: JTokenType.Boolean } rejected && rejected.Value<bool>();
    }

    /// <summary>
    /// Evaluate whether a tool-pass that produced <paramref name="results"/> should count
    /// as "all errors." Empty input counts as a non-error pass so the repair budget is
    /// not drained by empty passes.
    /// </summary>
    public static bool IsAllErrors(IReadOnlyList<AIInteractionToolResult> results)
    {
        if (results is null || results.Count == 0) return false;
        return results.All(IsErrorResult);
    }
}

/// <summary>
/// Mutable per-turn counter that an <see cref="ConversationSession"/> run loop calls after
/// each tool pass. Not thread-safe — owned by a single run loop at a time.
/// </summary>
public sealed class ErrorRepairTracker
{
    private readonly ErrorRepairPolicy _policy;
    private int _consecutiveErrorPasses;

    public ErrorRepairTracker(ErrorRepairPolicy? policy = null)
    {
        _policy = policy ?? ErrorRepairPolicy.Default;
    }

    /// <summary>Consecutive error-only passes observed since last non-error pass.</summary>
    public int ConsecutiveErrorPasses => _consecutiveErrorPasses;

    /// <summary>
    /// Update the counter based on the latest tool-pass results. Returns true when the
    /// caller should STOP attempting further repairs: this pass was all errors and it is the
    /// <c>N</c>th consecutive one (<c>N = max(1, MaxConsecutiveErrorPasses)</c>).
    /// </summary>
    public bool RecordPassAndShouldStop(IReadOnlyList<AIInteractionToolResult> results)
    {
        if (!ErrorRepairPolicy.IsAllErrors(results))
        {
            _consecutiveErrorPasses = 0;
            return false;
        }

        _consecutiveErrorPasses++;
        return _consecutiveErrorPasses >= System.Math.Max(1, _policy.MaxConsecutiveErrorPasses);
    }

    /// <summary>Reset the counter — call at the start of a fresh turn.</summary>
    public void Reset() => _consecutiveErrorPasses = 0;
}
