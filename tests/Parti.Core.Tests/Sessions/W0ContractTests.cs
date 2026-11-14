// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Sessions;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Core.Tests.Sessions;

/// <summary>
/// W0 product-wiring contracts that carry logic: the pass-through envelope, the empty catalog,
/// the shared seen-id ledger on <see cref="ToolBatchGuard"/>, and the no-behaviour-change defaults.
/// </summary>
public class W0ContractTests
{
    private static readonly IReadOnlyDictionary<string, bool> ReadOnly = new Dictionary<string, bool>
    {
        ["gh_get_all"] = false,
    };

    private static AIInteractionToolCall Call(string id) => new()
    {
        Id = id,
        Name = "gh_get_all",
        Arguments = new JObject(),
    };

    [Fact]
    public void NullTurnEnvelope_IsPassThrough()
    {
        using var envelope = NullTurnEnvelopeFactory.Instance.Begin("Parti turn");
        var calls = new[] { Call("toolu_1") };
        var result = new AIInteractionToolResult { Id = "toolu_1", Name = "gh_get_all", Result = new JObject { ["status"] = "ok" } };

        envelope.BeforeReversibleDispatch(calls[0]);
        Assert.Null(envelope.BindingPrefix);
        Assert.Same(calls, envelope.AttachCapabilities(calls));
        envelope.ScrubResult(result);
        Assert.Equal("{\"status\":\"ok\"}", result.Result.ToString(Newtonsoft.Json.Formatting.None));
        envelope.Complete(mutationAttempted: true);
    }

    [Fact]
    public void EmptyComponentCatalog_HasNothing()
    {
        var catalog = EmptyComponentCatalog.Instance;
        Assert.Empty(catalog.All);
        Assert.Empty(catalog.Libraries());
        Assert.False(catalog.TryGet(Guid.Parse("57da07bd-ecab-415d-9d86-af36d7073abc"), out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void ToolBatchGuard_SharedSeenSet_RejectsReplayAcrossGuards()
    {
        var options = new SessionOptions();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Assert.True(new ToolBatchGuard(options, seen).TryFreeze(new[] { Call("toolu_1") }, ReadOnly, "turn-1", out _, out _, out _));
        Assert.Contains("toolu_1", seen);

        Assert.False(new ToolBatchGuard(options, seen).TryFreeze(new[] { Call("toolu_1") }, ReadOnly, "turn-2", out _, out _, out var error));
        Assert.Contains("replayed", error);
    }

    [Fact]
    public void ToolBatchGuard_DefaultCtor_KeepsPerGuardLedger()
    {
        var options = new SessionOptions();
        Assert.True(new ToolBatchGuard(options).TryFreeze(new[] { Call("toolu_1") }, ReadOnly, "turn-1", out _, out _, out _));
        Assert.True(new ToolBatchGuard(options).TryFreeze(new[] { Call("toolu_1") }, ReadOnly, "turn-2", out _, out _, out _));
    }

    [Fact]
    public void Defaults_KeepExistingBehaviour()
    {
        var options = new SessionOptions();
        Assert.False(options.Stream);
        Assert.False(options.RequireDecomposition);

        // Appended after Done so existing ordinals are stable.
        Assert.Equal(4, (int)AIStreamChunkKind.Done);
        Assert.Equal(5, (int)AIStreamChunkKind.ReasoningDelta);

        var plan = new ToolCallPlan(Array.Empty<ToolCallSummary>(), null);
        Assert.Null(plan.Decomposition);
        Assert.Equal(0, plan.DecompositionRevision);
        Assert.Null(new ToolCallSummary("gh_get_all", false, null).ResolvedLabel);
    }
}
