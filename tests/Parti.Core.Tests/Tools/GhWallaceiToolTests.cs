// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Grasshopper;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Core.Tests.Tools;

public class GhWallaceiToolTests
{
    // ---- Definition shape -------------------------------------------------

    [Fact]
    public void ToolName_IsCanonical()
    {
        Assert.Equal("gh_wallacei_setup", GhWallaceiTool.ToolName);
        Assert.Equal("gh_wallacei_setup", GhWallaceiTool.Definition.Name);
    }

    [Fact]
    public void Definition_IsReversible()
    {
        Assert.True(GhWallaceiTool.Definition.IsReversible);
    }

    // ---- Plugin-missing fallback -----------------------------------------

    [Fact]
    public async Task Handler_ReturnsMissingPlugin_WhenScannerEmpty()
    {
        var handler = GhWallaceiTool.HandlerFor(() => new List<DetectedPlugin>());
        var result = await handler(BuildValidArgs(4), CancellationToken.None);

        Assert.Equal("missing_plugin", result["status"]!.Value<string>());
        var err = result["error"]!.Value<string>()!;
        Assert.Contains("Wallacei", err);
        Assert.Contains("food4rhino.com", err);
        Assert.Equal("Octopus", result["fallback_suggestion"]!.Value<string>());
    }

    [Fact]
    public async Task Handler_ReturnsMissingPlugin_WhenOnlyUnrelatedPluginsInstalled()
    {
        var handler = GhWallaceiTool.HandlerFor(() => new List<DetectedPlugin>
        {
            new("Kangaroo2", "gha", @"C:\fake\Kangaroo2.gha", null),
            new("Octopus", "gha", @"C:\fake\Octopus.gha", "0.4.0"),
        });
        var result = await handler(BuildValidArgs(4), CancellationToken.None);

        Assert.Equal("missing_plugin", result["status"]!.Value<string>());
        Assert.Contains("Wallacei", result["error"]!.Value<string>()!);
    }

    // ---- Clause-count gating ---------------------------------------------

    [Fact]
    public async Task Handler_Rejects_FewerThanFourClauses_WithoutForce()
    {
        var handler = GhWallaceiTool.HandlerFor(WallaceiInstalled());
        var result = await handler(BuildValidArgs(3), CancellationToken.None);

        Assert.Equal("error", result["status"]!.Value<string>());
        var hint = result["hint"]!.Value<string>()!;
        Assert.Contains("Galapagos", hint);
        Assert.Contains("Octopus", hint);
    }

    [Fact]
    public async Task Handler_Accepts_FewerClauses_WhenForceWallaceiTrue()
    {
        var handler = GhWallaceiTool.HandlerFor(WallaceiInstalled());
        var args = BuildValidArgs(2);
        args["forceWallacei"] = true;

        var result = await handler(args, CancellationToken.None);

        Assert.Equal("pending_rhino", result["status"]!.Value<string>());
        Assert.Equal(2, result["validated"]!["clause_count"]!.Value<int>());
    }

    // ---- Pending-rhino default -------------------------------------------

    [Fact]
    public async Task Handler_ReturnsPendingRhino_WithFourClauses()
    {
        var handler = GhWallaceiTool.HandlerFor(WallaceiInstalled());
        var result = await handler(BuildValidArgs(4), CancellationToken.None);

        Assert.Equal("pending_rhino", result["status"]!.Value<string>());
        Assert.Equal("Wallacei", result["component"]!.Value<string>());
        Assert.NotNull(result["message"]);
        Assert.Equal("2.7.0", result["detected_version"]!.Value<string>());
        Assert.Equal(4, result["validated"]!["clause_count"]!.Value<int>());
        Assert.Equal("NSGA2", result["validated"]!["algorithm"]!.Value<string>());
    }

    // ---- Clamping --------------------------------------------------------

    [Fact]
    public async Task Handler_ClampsGenerations_ToUpperBound()
    {
        var handler = GhWallaceiTool.HandlerFor(WallaceiInstalled());
        var args = BuildValidArgs(4);
        args["generations"] = 99_999;

        var result = await handler(args, CancellationToken.None);

        Assert.Equal(500, result["clamped_parameters"]!["generations"]!.Value<int>());
    }

    [Fact]
    public async Task Handler_ClampsMutationProbability_ToUnitInterval()
    {
        var handler = GhWallaceiTool.HandlerFor(WallaceiInstalled());
        var args = BuildValidArgs(4);
        args["mutationProbability"] = 5.0;

        var result = await handler(args, CancellationToken.None);

        Assert.Equal(1.0, result["clamped_parameters"]!["mutationProbability"]!.Value<double>());
    }

    [Fact]
    public async Task Handler_DefaultAlgorithm_IsNSGA2()
    {
        var handler = GhWallaceiTool.HandlerFor(WallaceiInstalled());
        var result = await handler(BuildValidArgs(4), CancellationToken.None);

        Assert.Equal("NSGA2", result["clamped_parameters"]!["algorithm"]!.Value<string>());
    }

    [Fact]
    public async Task Handler_CustomAlgorithm_IsPreserved()
    {
        var handler = GhWallaceiTool.HandlerFor(WallaceiInstalled());
        var args = BuildValidArgs(4);
        args["algorithm"] = "NSGA3";

        var result = await handler(args, CancellationToken.None);

        Assert.Equal("NSGA3", result["clamped_parameters"]!["algorithm"]!.Value<string>());
    }

    // ---- Executor delegate -----------------------------------------------

    [Fact]
    public async Task Handler_InjectedExecutor_IsCalledWithClampedArgs()
    {
        JObject? observed = null;
        GhWallaceiSetupExecutor exec = args =>
        {
            observed = args;
            return new JObject { ["status"] = "ok", ["generations_completed"] = 12 };
        };

        var handler = GhWallaceiTool.HandlerFor(WallaceiInstalled(), exec);
        var args = BuildValidArgs(4);
        args["generations"] = 99_999;

        var result = await handler(args, CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal(500, observed!["generations"]!.Value<int>());
        Assert.Equal("ok", result["status"]!.Value<string>());
    }

    [Fact]
    public async Task Handler_InjectedExecutor_NotCalled_WhenClauseCountInvalid()
    {
        bool called = false;
        GhWallaceiSetupExecutor exec = _ =>
        {
            called = true;
            return new JObject();
        };

        var handler = GhWallaceiTool.HandlerFor(WallaceiInstalled(), exec);
        var result = await handler(BuildValidArgs(2), CancellationToken.None);

        Assert.False(called);
        Assert.Equal("error", result["status"]!.Value<string>());
    }

    // ---- helpers ---------------------------------------------------------

    private static Func<IReadOnlyList<DetectedPlugin>> WallaceiInstalled() =>
        () => new List<DetectedPlugin>
        {
            new("Wallacei X", "gha", @"C:\fake\Wallacei X.gha", "2.7.0"),
        };

    private static JObject BuildValidArgs(int clauseCount)
    {
        var clauses = new JArray();
        for (int i = 0; i < clauseCount; i++)
        {
            clauses.Add(new JObject
            {
                ["metric"] = $"metric_{i}",
                ["direction"] = i % 2 == 0 ? "Maximize" : "Minimize",
            });
        }
        return new JObject
        {
            ["clauses"] = clauses,
            ["genomes"] = new JArray
            {
                new JObject
                {
                    ["slider_name"] = "width",
                    ["min"] = 1.0,
                    ["max"] = 10.0,
                },
            },
        };
    }
}
