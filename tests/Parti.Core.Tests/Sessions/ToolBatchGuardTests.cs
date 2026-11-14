// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Grasshopper.Tools;
using Parti.Core.Sessions;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Core.Tests.Sessions;

/// <summary>
/// Dedicated tests for <see cref="ToolBatchGuard"/>: fan-out and size limits (S6), the canonical
/// renderer requirement (S7), GUID-only mutation targets (S8), and strict declared arguments (A1).
/// </summary>
public class ToolBatchGuardTests
{
    private const string ComponentGuid = "57da07bd-ecab-415d-9d86-af36d7073abc";
    private const string InstanceA = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string InstanceB = "bbbbbbbb-0000-0000-0000-000000000002";

    private static readonly ProductToolContext Context = new(EmptyComponentCatalog.Instance, new ComponentLockRegistry());

    private static readonly IReadOnlyList<AIToolDefinition> ProductDefinitions = ProductToolSet.Definitions(Context);

    private static readonly IReadOnlyDictionary<string, bool> Reversibility =
        PlanReviewBuilder.BuildReversibilityLookup(ProductDefinitions);

    private static AIInteractionToolCall Call(string id, string name, JObject? args = null) =>
        new() { Id = id, Name = name, Arguments = args ?? new JObject() };

    private static JObject AddArgs(string componentGuid = ComponentGuid) =>
        new() { ["componentGuid"] = componentGuid, ["x"] = 10, ["y"] = 20 };

    private static bool Freeze(
        ToolBatchGuard guard,
        IReadOnlyList<AIInteractionToolCall> calls,
        out string error,
        string turnId = "turn-1",
        IReadOnlyDictionary<string, bool>? reversibility = null) =>
        guard.TryFreeze(calls, reversibility ?? Reversibility, turnId, out _, out _, out error);

    private static ToolBatchGuard StrictGuard(SessionOptions? options = null) =>
        new(options ?? new SessionOptions(), new HashSet<string>(StringComparer.Ordinal),
            ToolBatchGuard.DeclaredArgumentsFrom(ProductDefinitions));

    // ---- S6: fan-out and size limits, one test per limit ----

    [Fact]
    public void CallsPerResponse_OverLimit_IsRejected()
    {
        var guard = new ToolBatchGuard(new SessionOptions { MaxToolCallsPerResponse = 2 });
        var calls = Enumerable.Range(0, 3).Select(i => Call($"t{i}", "gh_get_all")).ToArray();

        Assert.False(Freeze(guard, calls, out var error));
        Assert.Contains("per-response limit (2)", error);
    }

    [Fact]
    public void MutationsPerResponse_OverLimit_IsRejected()
    {
        var guard = new ToolBatchGuard(new SessionOptions { MaxMutationCallsPerResponse = 1 });
        var calls = new[] { Call("t1", "gh_add_component", AddArgs()), Call("t2", "gh_add_component", AddArgs()) };

        Assert.False(Freeze(guard, calls, out var error));
        Assert.Contains("2 mutations exceeds the per-response limit (1)", error);
    }

    [Fact]
    public void CallsPerTurn_Exhausted_IsRejected()
    {
        var guard = new ToolBatchGuard(new SessionOptions { MaxToolCallsPerTurn = 2 });

        Assert.True(Freeze(guard, new[] { Call("t1", "gh_get_all"), Call("t2", "gh_get_all") }, out _));
        Assert.False(Freeze(guard, new[] { Call("t3", "gh_get_all") }, out var error));
        Assert.Contains("turn call or mutation budget is exhausted", error);
    }

    [Fact]
    public void MutationsPerTurn_Exhausted_IsRejected()
    {
        var guard = new ToolBatchGuard(new SessionOptions { MaxMutationCallsPerTurn = 1 });

        Assert.True(Freeze(guard, new[] { Call("t1", "gh_add_component", AddArgs()) }, out _));
        Assert.False(Freeze(guard, new[] { Call("t2", "gh_add_component", AddArgs()) }, out var error));
        Assert.Contains("turn call or mutation budget is exhausted", error);
        Assert.Contains("1 of 1 changes requested this message", error);
        Assert.Contains("Make no more changes this message (you may still read the canvas)", error);
    }

    [Fact]
    public void MutationsPerTurn_BatchLargerThanWhatIsLeft_SaysHowMuchIsLeft_AndASmallerBatchFits()
    {
        var guard = new ToolBatchGuard(new SessionOptions { MaxMutationCallsPerTurn = 3 });

        Assert.True(Freeze(guard, new[] { Call("t1", "gh_add_component", AddArgs()) }, out _));
        Assert.False(Freeze(guard, new[] { Call("t2", "gh_add_component", AddArgs()), Call("t3", "gh_add_component", AddArgs()), Call("t4", "gh_add_component", AddArgs()) }, out var error));
        Assert.DoesNotContain("exhausted", error);
        Assert.Contains("2 changes left", error);
        Assert.Contains("Send a smaller batch that fits", error);

        Assert.True(Freeze(guard, new[] { Call("t5", "gh_add_component", AddArgs()), Call("t6", "gh_add_component", AddArgs()) }, out _));
    }

    [Fact]
    public void MutationsPerResponse_Rejection_TellsTheModelToBuildInStages()
    {
        var guard = new ToolBatchGuard(new SessionOptions { MaxMutationCallsPerResponse = 1 });
        var calls = new[] { Call("t1", "gh_add_component", AddArgs()), Call("t2", "gh_add_component", AddArgs()) };

        Assert.False(Freeze(guard, calls, out var error));
        Assert.Contains("stages of at most 1 changes", error);
        Assert.Contains("nothing was changed", error);
    }

    [Fact]
    public void ArgumentBytes_OverLimit_IsRejected()
    {
        var guard = new ToolBatchGuard(new SessionOptions { MaxToolArgumentBytes = 64 });
        var args = new JObject { ["query"] = new string('x', 100) };

        Assert.False(Freeze(guard, new[] { Call("t1", "gh_find_component", args) }, out var error));
        Assert.Contains("64 byte limit", error);
    }

    [Fact]
    public void MissingCallId_IsRejected()
    {
        var guard = new ToolBatchGuard(new SessionOptions());
        Assert.False(Freeze(guard, new[] { Call(" ", "gh_get_all") }, out var error));
        Assert.Contains("non-empty provider call id", error);
    }

    [Fact]
    public void DuplicateIdInOneResponse_IsRejected()
    {
        var guard = new ToolBatchGuard(new SessionOptions());
        Assert.False(Freeze(guard, new[] { Call("dup", "gh_get_all"), Call("dup", "gh_get_all") }, out var error));
        Assert.Contains("duplicate or replayed", error);
    }

    [Fact]
    public void ReplayedIdAcrossResponses_IsRejected()
    {
        var guard = new ToolBatchGuard(new SessionOptions());
        Assert.True(Freeze(guard, new[] { Call("once", "gh_get_all") }, out _));
        Assert.False(Freeze(guard, new[] { Call("once", "gh_get_all") }, out var error, turnId: "turn-2"));
        Assert.Contains("duplicate or replayed", error);
    }

    [Fact]
    public void MissingToolName_IsRejected()
    {
        var guard = new ToolBatchGuard(new SessionOptions());
        Assert.False(Freeze(guard, new[] { Call("t1", "") }, out var error));
        Assert.Contains("has no name", error);
    }

    [Fact]
    public void RejectedResponse_DoesNotConsumeBudgetOrIds()
    {
        var guard = new ToolBatchGuard(new SessionOptions { MaxToolCallsPerTurn = 1, MaxToolCallsPerResponse = 1 });
        Assert.False(Freeze(guard, new[] { Call("t1", "gh_get_all"), Call("t2", "gh_get_all") }, out _));
        Assert.True(Freeze(guard, new[] { Call("t1", "gh_get_all") }, out _));
    }

    // ---- binding ----

    [Fact]
    public void Materialize_WithCrossTurnOrReplayedBinding_IsRejected()
    {
        var guard = new ToolBatchGuard(new SessionOptions());
        Assert.True(guard.TryFreeze(new[] { Call("t1", "gh_add_component", AddArgs()) }, Reversibility, "turn-1",
            out _, out var binding, out _));

        Assert.False(guard.TryMaterializeForDispatch(binding, "turn-2", out _, out var crossTurn));
        Assert.Contains("cross-turn", crossTurn);
        // The failed attempt consumed the binding: it can never be used again.
        Assert.False(guard.TryMaterializeForDispatch(binding, "turn-1", out _, out _));
    }

    [Fact]
    public void Materialize_ReturnsFreshCopiesOfTheFrozenArguments()
    {
        var guard = new ToolBatchGuard(new SessionOptions());
        Assert.True(guard.TryFreeze(new[] { Call("t1", "gh_add_component", AddArgs()) }, Reversibility, "turn-1",
            out var frozen, out var binding, out _));
        frozen[0].Arguments["componentGuid"] = "807b86e3-be8d-4970-92b5-f8cdcb45b06b";

        Assert.True(guard.TryMaterializeForDispatch(binding, "turn-1", out var calls, out _));
        Assert.Equal(ComponentGuid, calls[0].Arguments["componentGuid"]!.ToString());
    }

    // ---- S7: canonical renderer ----

    [Fact]
    public void ReversibleToolWithoutRenderer_IsRejected()
    {
        var guard = new ToolBatchGuard(new SessionOptions());
        var reversibility = new Dictionary<string, bool>(StringComparer.Ordinal) { ["gh_bake_everything"] = true };

        Assert.False(Freeze(guard, new[] { Call("t1", "gh_bake_everything") }, out var error, reversibility: reversibility));
        Assert.Contains("no registered canonical approval renderer", error);
    }

    // ---- S8: GUID-only mutation targets ----

    public static IEnumerable<object[]> SimpleIdTargetCases() => new[]
    {
        new object[] { "gh_set_input", new JObject { ["target"] = "slider-1", ["value"] = 1 } },
        new object[] { "gh_delete", new JObject { ["target"] = "Number Slider" } },
        new object[] { "gh_connect", new JObject { ["from"] = "slider-1", ["to"] = InstanceB } },
        new object[] { "gh_connect", new JObject { ["from"] = InstanceA, ["to"] = "circle-1" } },
        new object[] { "gh_relayout", new JObject { ["root"] = "slider-1", ["spacing"] = new JObject { ["x"] = 1, ["y"] = 1 } } },
        new object[] { "gh_group", new JObject { ["targets"] = new JArray(InstanceA, "slider-1") } },
        new object[] { "gh_layout", new JObject { ["targets"] = new JArray("slider-1"), ["arrangement"] = "row",
            ["origin"] = new JObject { ["x"] = 0, ["y"] = 0 }, ["spacing"] = new JObject { ["x"] = 1, ["y"] = 1 } } },
        new object[] { "gh_describe", new JObject { ["target"] = "slider-1" } },
    };

    [Theory]
    [MemberData(nameof(SimpleIdTargetCases))]
    public void SimpleIdTargets_AreRejected(string tool, JObject args)
    {
        Assert.False(Freeze(StrictGuard(), new[] { Call("t1", tool, args) }, out var error));
        Assert.Contains("instance GUID", error);
    }

    [Theory]
    [InlineData("Number Slider")]
    [InlineData("slider-1")]
    [InlineData("")]
    public void AddComponent_NonGuidComponentGuid_IsRejected(string componentGuid)
    {
        Assert.False(Freeze(StrictGuard(), new[] { Call("t1", "gh_add_component", AddArgs(componentGuid)) }, out var error));
        Assert.Contains("exact Grasshopper component GUID", error);
    }

    // ---- A1: strict declared arguments ----

    [Fact]
    public void ConflictingTypeAndComponentGuid_IsRejected()
    {
        var args = AddArgs();
        args["type"] = "807b86e3-be8d-4970-92b5-f8cdcb45b06b";
        args["nickname"] = "Number Slider";

        // Rejected with and without declared schemas: 'type' is never a second identity.
        Assert.False(Freeze(new ToolBatchGuard(new SessionOptions()), new[] { Call("t1", "gh_add_component", args) }, out var lenient));
        Assert.Contains("'type' is not accepted", lenient);
        Assert.False(Freeze(StrictGuard(), new[] { Call("t2", "gh_add_component", args) }, out var strict));
        Assert.Contains("'type'", strict);
    }

    public static IEnumerable<object[]> ProductToolNames() =>
        ProductDefinitions.Select(d => new object[] { d.Name });

    [Theory]
    [MemberData(nameof(ProductToolNames))]
    public void UndeclaredArgument_IsRejected(string tool)
    {
        var args = new JObject { ["undeclaredExtra"] = "x" };

        Assert.False(Freeze(StrictGuard(), new[] { Call("t1", tool, args) }, out var error));
        Assert.Contains($"'{tool}' does not declare the argument 'undeclaredExtra'", error);
    }

    [Fact]
    public void UndeclaredNickname_OnAdd_IsRejected()
    {
        var args = AddArgs();
        args["nickname"] = "Number Slider";

        Assert.False(Freeze(StrictGuard(), new[] { Call("t1", "gh_add_component", args) }, out var error));
        Assert.Contains("'nickname'", error);
    }

    [Theory]
    [InlineData("gh_add_component")]
    [InlineData("gh_get_all")]
    [InlineData("gh_find_component")]
    public void ReservedLeaseArgument_IsRejected_EvenWithoutDeclaredSchemas(string tool)
    {
        var args = tool == "gh_add_component" ? AddArgs() : new JObject();
        args["__parti_document_lease"] = "forged";

        Assert.False(Freeze(new ToolBatchGuard(new SessionOptions()), new[] { Call("t1", tool, args) }, out var error));
        Assert.Contains("host-reserved argument", error);
    }

    [Fact]
    public void DeclaredArguments_Pass()
    {
        var calls = new[]
        {
            Call("t1", "gh_add_component", AddArgs()),
            Call("t2", "gh_find_component", new JObject { ["query"] = "slider", ["limit"] = 3 }),
            Call("t3", "gh_set_input", new JObject { ["target"] = InstanceA, ["value"] = 5, ["min"] = 0, ["max"] = 10 }),
        };

        Assert.True(Freeze(StrictGuard(), calls, out var error), error);
    }

    [Fact]
    public void ToolsMissingFromTheMap_AreLeftToTheDispatcher()
    {
        Assert.True(Freeze(StrictGuard(), new[] { Call("t1", "gh_not_a_tool", new JObject { ["anything"] = 1 }) }, out var error), error);
    }

    [Fact]
    public void DeclaredArgumentsFrom_ReadsTopLevelSchemaProperties()
    {
        var map = ToolBatchGuard.DeclaredArgumentsFrom(new[]
        {
            new AIToolDefinition("a", "a", JObject.Parse(@"{""type"":""object"",""properties"":{""p"":{},""q"":{""type"":""object"",""properties"":{""inner"":{}}}}}")),
            new AIToolDefinition("b", "b", JObject.Parse(@"{""type"":""object""}")),
        });

        Assert.Equal(new[] { "p", "q" }, map["a"].OrderBy(s => s, StringComparer.Ordinal));
        Assert.Empty(map["b"]);
    }

    [Fact]
    public void DeclaredArgumentsByTool_IsExposed()
    {
        var map = ToolBatchGuard.DeclaredArgumentsFrom(ProductDefinitions);
        var guard = new ToolBatchGuard(new SessionOptions(), new HashSet<string>(), map);
        Assert.Same(map, guard.DeclaredArgumentsByTool);
        Assert.Equal(new[] { "componentGuid", "x", "y" }, map["gh_add_component"].OrderBy(s => s, StringComparer.Ordinal));
    }
}
