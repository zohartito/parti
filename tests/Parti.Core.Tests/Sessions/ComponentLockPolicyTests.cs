// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Grasshopper.Tools;
using Parti.Core.Sessions;
using Parti.Core.Sessions.Policies;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Core.Tests.Sessions;

/// <summary>
/// S12: <see cref="ComponentLockPolicy"/> rejects, at freeze time, every D3 field that names a
/// locked instance, any relayout while a lock exists, and a lock call mixed with a reversible call.
/// </summary>
public class ComponentLockPolicyTests
{
    private const string Locked = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Free = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string Other = "cccccccc-0000-0000-0000-000000000003";

    private static readonly IReadOnlyDictionary<string, bool> Reversibility = PlanReviewBuilder.BuildReversibilityLookup(
        ProductToolSet.Definitions(new ProductToolContext(EmptyComponentCatalog.Instance, new ComponentLockRegistry())));

    private static ComponentLockPolicy PolicyWithLock(string? id = Locked)
    {
        var registry = new ComponentLockRegistry();
        if (id is not null)
        {
            registry.Lock(id);
        }
        return new ComponentLockPolicy(registry);
    }

    private static AIInteractionToolCall Call(string name, JObject args, string id = "t1") =>
        new() { Id = id, Name = name, Arguments = args };

    private static JObject Spacing() => new() { ["x"] = 10, ["y"] = 10 };

    public static IEnumerable<object[]> LockedFieldCases()
    {
        // One case per field in D3, each spelled differently to prove GUID normalization.
        yield return new object[] { "gh_set_input.target", Call("gh_set_input", new JObject { ["target"] = Locked, ["value"] = 1 }) };
        yield return new object[] { "gh_delete.target", Call("gh_delete", new JObject { ["target"] = Locked.ToUpperInvariant() }) };
        yield return new object[] { "gh_connect.from", Call("gh_connect", new JObject { ["from"] = "{" + Locked + "}", ["to"] = Free }) };
        yield return new object[] { "gh_connect.to", Call("gh_connect", new JObject { ["from"] = Free, ["to"] = Locked.Replace("-", "") }) };
        yield return new object[] { "gh_group.targets[]", Call("gh_group", new JObject { ["targets"] = new JArray(Free, Locked) }) };
        yield return new object[] { "gh_layout.targets[]", Call("gh_layout", new JObject
        {
            ["targets"] = new JArray(Locked, Free),
            ["arrangement"] = "row",
            ["origin"] = new JObject { ["x"] = 0, ["y"] = 0 },
            ["spacing"] = Spacing(),
        }) };
    }

    [Theory]
    [MemberData(nameof(LockedFieldCases))]
    public void LockedTarget_InEveryField_IsRejected(string field, AIInteractionToolCall call)
    {
        Assert.False(PolicyWithLock().TryValidate(new[] { call }, Reversibility, out var error), field);
        Assert.Contains(Locked, error);
        Assert.Contains("locked by the user", error);
    }

    [Fact]
    public void LockedTarget_AnywhereInTheResponse_RejectsTheWholeResponse()
    {
        var calls = new[]
        {
            Call("gh_set_input", new JObject { ["target"] = Free, ["value"] = 1 }, "t1"),
            Call("gh_delete", new JObject { ["target"] = Locked }, "t2"),
        };
        Assert.False(PolicyWithLock().TryValidate(calls, Reversibility, out _));
    }

    [Fact]
    public void UnlockedTargets_Pass()
    {
        var calls = new[]
        {
            Call("gh_set_input", new JObject { ["target"] = Free, ["value"] = 1 }, "t1"),
            Call("gh_connect", new JObject { ["from"] = Free, ["to"] = Other }, "t2"),
            Call("gh_group", new JObject { ["targets"] = new JArray(Free, Other) }, "t3"),
        };
        Assert.True(PolicyWithLock().TryValidate(calls, Reversibility, out var error), error);
    }

    [Fact]
    public void ReadOnlyCalls_NamingALockedComponent_Pass()
    {
        var calls = new[]
        {
            Call("gh_describe", new JObject { ["target"] = Locked }, "t1"),
            Call("gh_get_all", new JObject(), "t2"),
        };
        Assert.True(PolicyWithLock().TryValidate(calls, Reversibility, out var error), error);
    }

    [Fact]
    public void Relayout_WithAnyLock_IsRejected()
    {
        var call = Call("gh_relayout", new JObject { ["root"] = Free, ["spacing"] = Spacing() });

        Assert.False(PolicyWithLock().TryValidate(new[] { call }, Reversibility, out var error));
        Assert.Contains("gh_relayout", error);
    }

    [Fact]
    public void Relayout_WithoutLocks_Passes()
    {
        var call = Call("gh_relayout", new JObject { ["root"] = Free, ["spacing"] = Spacing() });
        Assert.True(PolicyWithLock(id: null).TryValidate(new[] { call }, Reversibility, out _));
    }

    [Fact]
    public void LockCall_MixedWithReversibleCall_IsRejected_EvenWithoutLocks()
    {
        var calls = new[]
        {
            Call(GhComponentLockTool.ToolName, new JObject { ["action"] = "lock", ["targets"] = new JArray(Free) }, "t1"),
            Call("gh_set_input", new JObject { ["target"] = Other, ["value"] = 2 }, "t2"),
        };

        Assert.False(PolicyWithLock(id: null).TryValidate(calls, Reversibility, out var error));
        Assert.Contains("gh_component_lock", error);
    }

    [Fact]
    public void LockCall_MixedWithReadOnlyCall_Passes()
    {
        var calls = new[]
        {
            Call("gh_get_all", new JObject(), "t1"),
            Call(GhComponentLockTool.ToolName, new JObject { ["action"] = "lock", ["targets"] = new JArray(Free) }, "t2"),
        };
        Assert.True(PolicyWithLock().TryValidate(calls, Reversibility, out var error), error);
    }

    [Fact]
    public void Policy_ReadsTheSharedRegistryLive()
    {
        var registry = new ComponentLockRegistry();
        var policy = new ComponentLockPolicy(registry);
        var call = Call("gh_delete", new JObject { ["target"] = Free });

        Assert.True(policy.TryValidate(new[] { call }, Reversibility, out _));
        registry.Lock(Free);
        Assert.False(policy.TryValidate(new[] { call }, Reversibility, out _));
        Assert.Same(registry, policy.Locks);
    }
}
