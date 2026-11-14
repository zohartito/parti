// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper.Kernel.Parameters;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Grasshopper;
using Parti.Core.Grasshopper.Tools;
using Parti.Core.Sessions;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// Adapter-level tests for <see cref="GrasshopperToolAdapters"/>. These exercise the
/// shape of each <see cref="AIToolDefinition"/> and the composition of
/// <see cref="GrasshopperToolAdapters.CreateDefault"/> — no live Rhino required, so the
/// mutation primitives themselves are not invoked here.
/// </summary>
public class GrasshopperToolAdaptersTests
{
    private static readonly HashSet<string> ExpectedMutationTools = new()
    {
        "gh_add_component",
        "gh_connect",
        "gh_set_input",
        "gh_delete",
        "gh_group",
        "gh_layout",
        "gh_relayout",
    };

    private static readonly HashSet<string> ExpectedReadTools = new()
    {
        "gh_get_all",
        "gh_get_selected",
        "gh_describe",
    };

    [Fact]
    public void CreateDefault_RegistersTenTools()
    {
        var registry = GrasshopperToolAdapters.CreateDefault();
        var defs = registry.GetDefinitions();

        Assert.Equal(10, defs.Count);
    }

    [Fact]
    public void CreateDefault_RegistersExpectedToolNames()
    {
        var registry = GrasshopperToolAdapters.CreateDefault();
        var names = registry.GetDefinitions().Select(d => d.Name).ToHashSet();

        var expected = new HashSet<string>(ExpectedMutationTools);
        expected.UnionWith(ExpectedReadTools);

        Assert.Equal(expected, names);
    }

    [Theory]
    [InlineData("gh_add_component")]
    [InlineData("gh_connect")]
    [InlineData("gh_set_input")]
    [InlineData("gh_delete")]
    [InlineData("gh_group")]
    [InlineData("gh_layout")]
    [InlineData("gh_relayout")]
    public void MutationTools_AreReversible(string toolName)
    {
        var registry = GrasshopperToolAdapters.CreateDefault();
        var def = registry.GetDefinitions().First(d => d.Name == toolName);

        Assert.True(def.IsReversible, $"Expected {toolName} to be reversible.");
    }

    [Theory]
    [InlineData("gh_get_all")]
    [InlineData("gh_get_selected")]
    [InlineData("gh_describe")]
    public void ReadTools_AreNotReversible(string toolName)
    {
        var registry = GrasshopperToolAdapters.CreateDefault();
        var def = registry.GetDefinitions().First(d => d.Name == toolName);

        Assert.False(def.IsReversible, $"Expected {toolName} NOT to be reversible.");
    }

    [Fact]
    public void EveryDefinition_HasNameDescriptionAndSchema()
    {
        var registry = GrasshopperToolAdapters.CreateDefault();

        foreach (var def in registry.GetDefinitions())
        {
            Assert.False(string.IsNullOrWhiteSpace(def.Name),
                $"Tool definition has empty name: {def}");
            Assert.False(string.IsNullOrWhiteSpace(def.Description),
                $"Tool '{def.Name}' has empty description");
            Assert.NotNull(def.InputSchema);
            Assert.Equal("object", def.InputSchema["type"]?.ToString());
        }
    }

    [Theory]
    [InlineData("gh_add_component", new[] { "componentGuid" })]
    [InlineData("gh_describe", new[] { "target" })]
    [InlineData("gh_connect", new[] { "from", "to" })]
    [InlineData("gh_set_input", new[] { "target", "value" })]
    [InlineData("gh_delete", new[] { "target" })]
    [InlineData("gh_group", new[] { "targets" })]
    [InlineData("gh_layout", new[] { "targets", "arrangement", "origin", "spacing" })]
    [InlineData("gh_relayout", new[] { "spacing", "root" })]
    public void RequiredFields_MatchExpectedKeys(string toolName, string[] expectedRequired)
    {
        var registry = GrasshopperToolAdapters.CreateDefault();
        var def = registry.GetDefinitions().First(d => d.Name == toolName);

        var required = def.InputSchema["required"] as JArray;
        Assert.NotNull(required);

        var actual = required!.Select(t => t.ToString()).ToHashSet();
        Assert.Equal(expectedRequired.ToHashSet(), actual);
    }

    [Theory]
    [InlineData("gh_get_all")]
    [InlineData("gh_get_selected")]
    public void ReadToolsWithoutRequired_HaveNoRequiredKey(string toolName)
    {
        var registry = GrasshopperToolAdapters.CreateDefault();
        var def = registry.GetDefinitions().First(d => d.Name == toolName);

        // These tools take no required parameters — absence of "required" is fine.
        Assert.Null(def.InputSchema["required"]);
    }

    [Theory]
    [InlineData("gh_group")]
    [InlineData("gh_layout")]
    [InlineData("gh_relayout")]
    public void OrganizationalTools_HaveNonEmptySchema(string toolName)
    {
        var registry = GrasshopperToolAdapters.CreateDefault();
        var def = registry.GetDefinitions().First(d => d.Name == toolName);

        Assert.NotNull(def.InputSchema);
        Assert.Equal("object", def.InputSchema["type"]?.ToString());
        var properties = def.InputSchema["properties"] as JObject;
        Assert.NotNull(properties);
        Assert.NotEmpty(properties!.Properties());
    }

    // ---- A1: the add adapter executes the reviewed componentGuid ----

    [Fact]
    public void AddAdapter_AlwaysExecutesComponentGuid()
    {
        const string reviewed = "57da07bd-ecab-415d-9d86-af36d7073abc";
        var args = new JObject
        {
            ["componentGuid"] = reviewed,
            ["type"] = "807b86e3-be8d-4970-92b5-f8cdcb45b06b",
            ["nickname"] = "Number Slider",
            ["x"] = 10.0,
            ["y"] = 20.0,
            ["__parti_document_lease"] = "lease-token",
        };

        var prepared = GrasshopperToolAdapters.PrepareAddArguments(args);

        Assert.Equal(reviewed, prepared["type"]!.GetValue<string>());
        Assert.Equal(10.0, prepared["x"]!.GetValue<double>());
        Assert.Equal(20.0, prepared["y"]!.GetValue<double>());
        Assert.Equal("lease-token", prepared["__parti_document_lease"]!.GetValue<string>());
        Assert.Null(prepared["nickname"]);
        Assert.Equal(new[] { "type", "x", "y", "__parti_document_lease" }, prepared.Select(p => p.Key));
    }

    [Fact]
    public void AddAdapter_WithoutComponentGuid_NeverExecutesAModelType()
    {
        var prepared = GrasshopperToolAdapters.PrepareAddArguments(new JObject { ["type"] = "807b86e3-be8d-4970-92b5-f8cdcb45b06b" });
        Assert.Null(prepared["type"]);
    }

    // ---- LR1: gh_set_input schema and slider range decision ----

    [Fact]
    public void SetInputSchema_DeclaresExplicitSliderBounds()
    {
        var def = GrasshopperToolAdapters.GhSetInputAdapter().Definition;
        var properties = (JObject)def.InputSchema["properties"]!;

        Assert.Equal("number", properties["min"]!["type"]!.ToString());
        Assert.Equal("number", properties["max"]!["type"]!.ToString());
        Assert.Contains("actually holds", def.Description);
        Assert.Contains("outside its current range", def.Description);
    }

    [Fact]
    public void SliderValueOutsideRange_WithoutBounds_IsAnErrorWithTheRange()
    {
        // The live baseline: 7.5 on a default 0..1 slider was reported as success while GH held 1.
        Assert.False(GhSetInput.TryPlanSliderAssignment(7.5, 0m, 1m, null, null, out _, out _, out var error));
        Assert.Contains("[0, 1]", error);
        Assert.Contains("'min'/'max'", error);
    }

    [Fact]
    public void SliderValueInsideRange_KeepsTheBounds()
    {
        Assert.True(GhSetInput.TryPlanSliderAssignment(0.25, 0m, 1m, null, null, out var min, out var max, out _));
        Assert.Equal(0m, min);
        Assert.Equal(1m, max);
    }

    [Theory]
    [InlineData(7.5, null, 10.0, 0.0, 10.0)]
    [InlineData(-3.0, -5.0, null, -5.0, 1.0)]
    [InlineData(50.0, 20.0, 100.0, 20.0, 100.0)]
    public void SliderValueOutsideRange_WithExplicitBounds_UsesTheNewBounds(
        double value, double? newMin, double? newMax, double expectedMin, double expectedMax)
    {
        Assert.True(GhSetInput.TryPlanSliderAssignment(value, 0m, 1m, newMin, newMax, out var min, out var max, out var error), error);
        Assert.Equal((decimal)expectedMin, min);
        Assert.Equal((decimal)expectedMax, max);
    }

    [Theory]
    [InlineData(5.0, 10.0, 20.0)]   // value below the new range
    [InlineData(5.0, 10.0, 10.0)]   // empty domain
    [InlineData(5.0, 8.0, 2.0)]     // inverted domain
    public void SliderBounds_ThatCannotHoldTheValue_AreRejected(double value, double newMin, double newMax)
    {
        Assert.False(GhSetInput.TryPlanSliderAssignment(value, 0m, 1m, newMin, newMax, out _, out _, out _));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e30)]
    public void SliderValue_NonFiniteOrHuge_IsRejected(double value)
    {
        Assert.False(GhSetInput.TryPlanSliderAssignment(value, 0m, 1m, null, 1e31, out _, out _, out _));
    }

    // ---- LR2: gh_relayout anchors at the root's current pivot ----

    [Fact]
    public void RelayoutPlacement_KeepsTheRootAtItsPivot()
    {
        var root = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var other0 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        var child = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
        var grandchild = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
        var anchor = new PointF(400f, -120f);

        // Document order lists another rank-0 object before the root.
        var placement = GhRelayout.ComputePlacement(
            new[] { (other0, 0), (child, 1), (root, 0), (grandchild, 2) }, root, anchor, 140, 100);

        Assert.Equal(anchor, placement.Positions[root]);
        Assert.Equal(new PointF(400f, -20f), placement.Positions[other0]);
        Assert.Equal(new PointF(540f, -120f), placement.Positions[child]);
        Assert.Equal(new PointF(680f, -120f), placement.Positions[grandchild]);
        Assert.Equal(new[] { 2, 1, 1 }, placement.CountsPerRank);
    }

    [Fact]
    public void RelayoutDescription_DocumentsAnchorAndRankMap()
    {
        var def = GrasshopperToolAdapters.GhRelayoutAdapter().Definition;
        Assert.Contains("anchored at the root's current position", def.Description);
        Assert.Contains("{instanceGuid: rank}", def.Description);
    }

    // ---- A2: gh_delete lock checks ----

    private const string LockedGuid = "aaaaaaaa-0000-0000-0000-000000000001";

    [Fact]
    public async Task DeleteAdapter_RefusesLockedTarget_BeforeTouchingTheDocument()
    {
        var locks = new ComponentLockRegistry();
        locks.Lock(LockedGuid);
        var handler = GrasshopperToolAdapters.GhDeleteAdapter(locks).Handler;

        var result = await handler(new JObject { ["target"] = LockedGuid.ToUpperInvariant() }, CancellationToken.None);

        Assert.Equal("locked", result["status"]!.ToString());
        Assert.False(result["success"]!.Value<bool>());
        Assert.Equal(LockedGuid, result["lockedComponent"]!.ToString());
    }

    [Fact]
    public async Task DeleteAdapter_UnlockedTarget_ReachesThePrimitive()
    {
        var locks = new ComponentLockRegistry();
        locks.Lock(LockedGuid);
        var handler = GrasshopperToolAdapters.GhDeleteAdapter(locks).Handler;

        // No lease in a unit test, so the primitive fails closed: proof the lock gate let it through.
        var result = await handler(new JObject { ["target"] = "bbbbbbbb-0000-0000-0000-000000000002" }, CancellationToken.None);
        Assert.Equal("error", result["status"]!.ToString());
        Assert.Contains("lease", result["message"]!.ToString());
    }

    [Fact]
    public void DeleteAdapter_RefusesWhenRecipientLocked()
    {
        var target = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        var locks = new ComponentLockRegistry();
        locks.Lock(LockedGuid);

        var downstream = GhDelete.FindLockConflict(target, new[]
        {
            (Guid.Parse("cccccccc-0000-0000-0000-000000000003"), "upstream"),
            (Guid.Parse(LockedGuid), "downstream"),
        }, locks);
        var upstream = GhDelete.FindLockConflict(target, new[] { (Guid.Parse(LockedGuid), "upstream") }, locks);
        var none = GhDelete.FindLockConflict(target, new[] { (Guid.Parse("cccccccc-0000-0000-0000-000000000003"), "downstream") }, locks);

        Assert.NotNull(downstream);
        Assert.Equal(Guid.Parse(LockedGuid), downstream!.LockedInstance);
        Assert.Equal("downstream", downstream.Relation);
        Assert.Equal("upstream", upstream!.Relation);
        Assert.Null(none);
    }

    [Fact]
    public void DeleteWireWalk_FindsNeighboursInBothDirections()
    {
        // Floating parameters wired a -> target -> b, built from real Grasshopper kernel types.
        var a = new Param_Number();
        var target = new Param_Number();
        var b = new Param_Number();
        a.NewInstanceGuid();
        target.NewInstanceGuid();
        b.NewInstanceGuid();
        target.AddSource(a);
        b.AddSource(target);

        var wired = GhDelete.WiredComponents(target).ToList();

        Assert.Contains((a.InstanceGuid, "upstream"), wired);
        Assert.Contains((b.InstanceGuid, "downstream"), wired);

        var locks = new ComponentLockRegistry();
        locks.Lock(a.InstanceGuid.ToString());
        Assert.Equal("upstream", GhDelete.FindLockConflict(target.InstanceGuid, wired, locks)!.Relation);
        locks.Clear();
        locks.Lock(b.InstanceGuid.ToString());
        Assert.Equal("downstream", GhDelete.FindLockConflict(target.InstanceGuid, wired, locks)!.Relation);
    }

    // ---- R13: targets resolve to top-level objects only ----

    [Fact]
    public void LockPolicy_ChildParamOfLockedComponent_IsRejected()
    {
        // A locked "component" (a floating param stands in for it) owning a nested input param.
        var owner = new Param_Number();
        owner.NewInstanceGuid();
        var child = new Param_Number();
        child.NewInstanceGuid();
        var locks = new ComponentLockRegistry();
        locks.Lock(owner.InstanceGuid.ToString());

        // The freeze-time policy only sees the child's literal GUID, which is not locked ...
        var policy = new Parti.Core.Sessions.Policies.ComponentLockPolicy(locks);
        var call = new AIInteractionToolCall
        {
            Id = "c1",
            Name = "gh_set_input",
            Arguments = new JObject { ["target"] = child.InstanceGuid.ToString(), ["value"] = 1 },
        };
        Assert.True(policy.TryValidate(new[] { call }, new Dictionary<string, bool> { ["gh_set_input"] = true }, out _));

        // ... so dispatch-time resolution refuses any nested GUID and names the owner instead.
        var resolved = GhTargets.Resolve(
            child.InstanceGuid.ToString(),
            id => id == owner.InstanceGuid ? owner : null,
            id => id == owner.InstanceGuid ? owner : id == child.InstanceGuid ? child : null,
            nested => nested == child ? owner.InstanceGuid : null,
            out var rejection);

        Assert.Null(resolved);
        Assert.NotNull(rejection);
        Assert.Equal("error", rejection!["status"]!.GetValue<string>());
        Assert.Equal(owner.InstanceGuid.ToString("D"), rejection["owner"]!.GetValue<string>());
        Assert.Contains("Nothing was changed", rejection["message"]!.GetValue<string>());

        // Naming the owner resolves it, and the dispatch lock check refuses it.
        var top = GhTargets.Resolve(owner.InstanceGuid.ToString(), id => id == owner.InstanceGuid ? owner : null, _ => owner, GhTargets.OwnerOf, out var none);
        Assert.Same(owner, top);
        Assert.Null(none);
        var locked = GhTargets.LockedResult(top!, owner.InstanceGuid.ToString(), "gh_set_input", locks);
        Assert.Equal("locked", locked!["status"]!.GetValue<string>());
        Assert.False(locked["success"]!.GetValue<bool>());
    }

    [Fact]
    public void TargetResolution_UnknownUnlockedAndOwnerless_Cases()
    {
        var obj = new Param_Number();
        obj.NewInstanceGuid();

        Assert.Null(GhTargets.Resolve(Guid.NewGuid().ToString(), _ => null, _ => null, GhTargets.OwnerOf, out var missing));
        Assert.Null(missing);
        Assert.Null(GhTargets.Resolve("slider1", _ => obj, _ => obj, GhTargets.OwnerOf, out var notGuid));
        Assert.Null(notGuid);
        // A nested GUID whose owner cannot be read is still refused (fail closed).
        Assert.Null(GhTargets.Resolve(obj.InstanceGuid.ToString(), _ => null, _ => obj, GhTargets.OwnerOf, out var ownerless));
        Assert.NotNull(ownerless);
        Assert.False(ownerless!.ContainsKey("owner"));
        Assert.Null(GhTargets.LockedResult(obj, obj.InstanceGuid.ToString(), "gh_connect", new ComponentLockRegistry()));
        Assert.Null(GhTargets.LockedResult(obj, obj.InstanceGuid.ToString(), "gh_connect", locks: null));
    }

    // ---- live catalog seam ----

    [Fact]
    public void GrasshopperCatalog_IsLazy_AndRebuildsWhenTheProxyCountChanges()
    {
        var probe = 1;
        var builds = 0;
        var uiCalls = 0;
        var entries = new List<ComponentCatalogEntry>
        {
            new(Guid.Parse("57da07bd-ecab-415d-9d86-af36d7073abc"), "Number Slider", "", "Params", "Input", "Grasshopper", false, "primary", "x"),
        };
        var catalog = new GrasshopperComponentCatalog(
            () => probe,
            () =>
            {
                builds++;
                return (entries.ToList(), new[] { new LibraryEntry("Grasshopper", "1", "McNeel", true) });
            },
            action => { uiCalls++; action(); });

        Assert.Equal(0, builds); // nothing read at construction

        Assert.Single(catalog.All);
        Assert.True(catalog.TryGet(Guid.Parse("57da07bd-ecab-415d-9d86-af36d7073abc"), out _));
        Assert.Single(catalog.Libraries());
        Assert.Equal(1, builds);
        Assert.Equal(3, uiCalls); // every access is marshalled to the UI thread

        entries.Add(new(Guid.Parse("807b86e3-be8d-4970-92b5-f8cdcb45b06b"), "Circle", "Cir", "Curve", "Primitive", "Curve Components", false, "secondary", "y"));
        probe = 2;
        Assert.Equal(2, catalog.All.Count);
        Assert.Equal(2, builds);
    }

    [Fact]
    public void GrasshopperCatalog_CleansPluginStrings()
    {
        var catalog = new GrasshopperComponentCatalog(
            () => 1,
            () => (new[] { new ComponentCatalogEntry(Guid.NewGuid(), "Bad\u0000Name‮", "", "C", "S", "L", false, "primary", "") },
                   Array.Empty<LibraryEntry>()),
            action => action());

        Assert.Equal("Bad Name", Assert.Single(catalog.All).Name);
    }
}
