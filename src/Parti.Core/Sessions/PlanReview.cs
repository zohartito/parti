// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Parti.Core.Design;

namespace Parti.Core.Sessions;

/// <summary>
/// Single entry in a <see cref="ToolCallPlan"/>. Lightweight preview of an upcoming tool call so
/// the user can decide whether to approve the batch before the session dispatches it.
/// <para>
/// <see cref="ArgumentPreview"/> is a compact human-readable summary. <see cref="RenderedArguments"/>
/// carries the complete canonical JSON that is shown to the approver and then dispatched.
/// <see cref="ResolvedLabel"/> is the host-catalog-resolved identity of the target (e.g. the
/// component's catalog name), never the model's nickname; null when nothing was resolved.
/// </para>
/// </summary>
public sealed record ToolCallSummary(
    string Name,
    bool IsReversible,
    string? ArgumentPreview,
    string? RenderedArguments = null,
    string? ResolvedLabel = null);

/// <summary>
/// Plan surfaced to a <see cref="PlanReviewer"/> immediately before a batch of tool calls runs.
/// Contains one <see cref="ToolCallSummary"/> per pending call plus any assistant text that
/// accompanied the tool_use blocks (the model often narrates what it is about to do).
/// <see cref="Decomposition"/> and <see cref="DecompositionRevision"/> carry the recorded
/// three-pillar decomposition the batch was approved against; null / 0 when none was recorded.
/// </summary>
public sealed record ToolCallPlan(
    IReadOnlyList<ToolCallSummary> Calls,
    string? AssistantText,
    string? BindingId = null,
    Decomposition? Decomposition = null,
    int DecompositionRevision = 0);

/// <summary>
/// Reviewer callback invoked by <see cref="ConversationSession"/> before it dispatches a batch of
/// reversible tool calls. Return <c>true</c> to approve and continue with normal dispatch; return
/// <c>false</c> to cancel — the session emits synthetic "User cancelled plan before execution."
/// tool_results for every pending call so the model sees a clean signal, then ends the turn.
/// </summary>
public delegate Task<bool> PlanReviewer(ToolCallPlan plan, CancellationToken ct);
