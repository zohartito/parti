// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Grasshopper.Tools;
using Parti.Core.Sessions;
using Parti.Core.Sessions.Policies;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// <see cref="ProductToolSet"/> registers exactly the product tools (D3): the ten Grasshopper
/// primitives plus <c>gh_find_component</c>, <c>gh_plugin_check</c>, and <c>gh_component_lock</c>,
/// with the lock tool, the delete adapter, and the policies sharing one registry and one catalog.
/// </summary>
public class ProductToolSetTests
{
    private static readonly string[] GhPrimitives =
    {
        "gh_add_component", "gh_get_all", "gh_get_selected", "gh_describe", "gh_connect",
        "gh_set_input", "gh_delete", "gh_group", "gh_layout", "gh_relayout",
    };

    private static ProductToolContext Context(ComponentLockRegistry? locks = null) =>
        new(JsonComponentCatalog.Load("tests/shared/rhino8-gh-catalog.json"), locks ?? new ComponentLockRegistry());

    [Fact]
    public void ProductToolSet_RegistersExactly()
    {
        var names = ProductToolSet.Create(Context()).GetDefinitions().Select(d => d.Name).ToArray();

        Assert.Equal(GhPrimitives.Concat(new[] { "gh_find_component", "gh_plugin_check", "gh_component_lock" }), names);
    }

    [Fact]
    public void ProductToolSet_NoSolverOrUndoTools()
    {
        var names = ProductToolSet.Definitions(Context()).Select(d => d.Name).ToArray();

        Assert.DoesNotContain(names, n => n.Contains("undo") || n.Contains("galapagos") || n.Contains("octopus") || n.Contains("wallacei"));
        Assert.DoesNotContain(AskUserTool.ToolName, names);
    }

    [Fact]
    public void Definitions_KeepGhPrimitiveSchemasAndReversibility()
    {
        var expected = GrasshopperToolAdapters.CreateDefault().GetDefinitions();
        var actual = ProductToolSet.Definitions(Context());

        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Name, actual[i].Name);
            Assert.Equal(expected[i].IsReversible, actual[i].IsReversible);
            Assert.Equal(expected[i].InputSchema.ToString(), actual[i].InputSchema.ToString());
        }
        Assert.All(actual.Skip(expected.Count), d => Assert.False(d.IsReversible));
    }

    [Fact]
    public async Task LockTool_DeleteAdapter_AndPolicy_ShareOneRegistry()
    {
        var locks = new ComponentLockRegistry();
        var context = Context(locks);
        var registry = ProductToolSet.Create(context);
        const string target = "aaaaaaaa-0000-0000-0000-000000000001";

        var lockResult = await registry.DispatchAsync(new AIInteractionToolCall
        {
            Id = "t1",
            Name = GhComponentLockTool.ToolName,
            Arguments = new JObject { ["action"] = "lock", ["targets"] = new JArray(target) },
        }, CancellationToken.None);
        Assert.True(locks.IsLocked(target), lockResult.Result.ToString());

        var deleteResult = await registry.DispatchAsync(new AIInteractionToolCall
        {
            Id = "t2",
            Name = "gh_delete",
            Arguments = new JObject { ["target"] = target },
        }, CancellationToken.None);
        Assert.Equal("locked", deleteResult.Result["status"]!.ToString());

        var policy = Assert.IsType<ComponentLockPolicy>(ProductToolSet.Policies(context)[0]);
        Assert.Same(locks, policy.Locks);
        Assert.Same(context.Catalog, Assert.IsType<ComponentCatalogPolicy>(ProductToolSet.Policies(context)[1]).Catalog);
    }

    [Fact]
    public async Task FindTool_UsesTheContextCatalog()
    {
        var registry = ProductToolSet.Create(Context());

        var result = await registry.DispatchAsync(new AIInteractionToolCall
        {
            Id = "t1",
            Name = GhFindComponentTool.ToolName,
            Arguments = new JObject { ["query"] = "Number Slider" },
        }, CancellationToken.None);

        Assert.Equal("57da07bd-ecab-415d-9d86-af36d7073abc", result.Result["candidates"]![0]!["componentGuid"]!.ToString());
    }

    [Fact]
    public void DeclaredArguments_CoverEveryProductToolAndSessionTools()
    {
        var map = ProductToolSet.DeclaredArguments(Context(), new[] { AskUserTool.Definition });

        Assert.Equal(GhPrimitives.Length + 4, map.Count);
        Assert.Contains(AskUserTool.ToolName, map.Keys);
        Assert.Equal(new[] { "action", "targets" }, map[GhComponentLockTool.ToolName].OrderBy(s => s, System.StringComparer.Ordinal));
        Assert.DoesNotContain("type", map["gh_add_component"]);
        Assert.DoesNotContain("nickname", map["gh_add_component"]);
    }
}
