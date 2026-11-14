// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// Ported from SmartHopper (https://github.com/architects-toolkit/SmartHopper)
// Original Copyright (c) SmartHopper contributors, LGPL-3.0-or-later

using System;
using System.Threading;

namespace Parti.Core.Sessions;

/// <summary>
/// Options controlling conversation session execution.
/// </summary>
public sealed class SessionOptions
{
    /// <summary>Maximum tool calls accepted from one provider response. Zero or less rejects all calls.</summary>
    public int MaxToolCallsPerResponse { get; set; } = 16;

    /// <summary>Maximum reversible (canvas-mutating) calls accepted from one provider response.</summary>
    public int MaxMutationCallsPerResponse { get; set; } = 8;

    /// <summary>Maximum tool calls dispatched during one user-initiated turn loop.</summary>
    public int MaxToolCallsPerTurn { get; set; } = 32;

    /// <summary>Maximum reversible calls dispatched during one user-initiated turn loop.</summary>
    public int MaxMutationCallsPerTurn { get; set; } = 16;

    /// <summary>Maximum UTF-8 JSON bytes in one model-supplied tool argument object.</summary>
    public int MaxToolArgumentBytes { get; set; } = 65_536;

    /// <summary>Maximum wall-clock time allowed for one serial tool batch.</summary>
    public TimeSpan MaxToolDispatchDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When true, the session will execute pending tool calls and allow providers to call tools.
    /// Tool passes are bounded by <see cref="MaxToolPasses"/> and do not consume <see cref="MaxTurns"/>.
    /// </summary>
    public bool ProcessTools { get; set; } = true;

    /// <summary>
    /// Maximum number of provider turns. Only provider calls increment this counter; tool passes do not.
    /// </summary>
    public int MaxTurns { get; set; } = 8;

    /// <summary>
    /// Maximum number of tool-processing passes per turn. Tool passes are consumed by tool execution
    /// and do not affect <see cref="MaxTurns"/>.
    /// </summary>
    public int MaxToolPasses { get; set; } = 4;

    /// <summary>
    /// Retained for source compatibility. Reviewed calls always dispatch serially so one approved
    /// document capability, call order, and target set cannot drift mid-batch.
    /// </summary>
    public bool AllowParallelTools { get; set; } = false;

    /// <summary>
    /// Model identifier passed to the provider on each call (e.g. "claude-opus-4-7").
    /// </summary>
    public string Model { get; set; } = "claude-opus-4-7";

    /// <summary>
    /// Maximum output tokens per provider call.
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Sampling temperature. Null (default) omits the field — required for claude-opus-4-7.
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>
    /// Optional top-level system prompt. Merged with any <see cref="Ai.AIAgent.System"/> interactions
    /// when building each provider request (SmartHopper behaviour: '\n---\n' separator).
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// API key forwarded to the provider on every call. Never serialized into the request body.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Additional cancellation token merged with the one passed to <c>RunAsync</c>.
    /// </summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>
    /// Bounded error-repair policy. When every tool in a tool-pass returns status=error
    /// and this happens for <see cref="ErrorRepairPolicy.MaxConsecutiveErrorPasses"/>
    /// consecutive passes, the session short-circuits instead of burning the rest of the
    /// MaxToolPasses budget on a stuck loop. Ported from SmartHopper's turn model.
    /// </summary>
    public ErrorRepairPolicy ErrorRepair { get; set; } = ErrorRepairPolicy.Default;

    /// <summary>
    /// When true, the session calls <c>CallStreamAsync</c> and forwards text and reasoning deltas
    /// to the observer; when false (default) it uses the non-streaming <c>CallAsync</c> path.
    /// </summary>
    public bool Stream { get; set; }

    /// <summary>
    /// When true, a batch containing a reversible call is gated on a complete three-pillar
    /// decomposition before plan review. Defaults to false so library callers keep today's behaviour.
    /// </summary>
    public bool RequireDecomposition { get; set; }

    /// <summary>
    /// How many times one entry call may automatically nudge the model after a reply with no
    /// visible text and no tool call (for example a reasoning model that spent the whole reply
    /// thinking). Each nudge is a labelled user-role note in history and costs one provider turn.
    /// When the reply is still empty after the nudges, the session ends with
    /// <see cref="SessionStatus.Error"/> instead of reporting a silent success. Zero disables the
    /// nudge (the empty reply fails immediately).
    /// </summary>
    public int EmptyResponseNudges { get; set; } = 1;

    /// <summary>
    /// Shallow copy of every option. Used by the composition root to start from caller-supplied
    /// overrides without mutating them.
    /// </summary>
    public SessionOptions Clone() => (SessionOptions)MemberwiseClone();
}
