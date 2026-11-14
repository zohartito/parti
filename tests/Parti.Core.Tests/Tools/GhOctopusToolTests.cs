// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Grasshopper;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Core.Tests.Tools;

public class GhOctopusToolTests
{
    // ---- Definition shape -------------------------------------------------

    [Fact]
    public void ToolName_IsCanonical()
    {
        Assert.Equal("gh_octopus_setup", GhOctopusTool.ToolName);
        Assert.Equal("gh_octopus_setup", GhOctopusTool.Definition.Name);
    }

    [Fact]
    public void Definition_IsReversible()
    {
        Assert.True(GhOctopusTool.Definition.IsReversible);
    }

    // ---- Plugin-missing fallback -----------------------------------------

    [Fact]
    public async Task Handler_ReturnsMissingPlugin_WhenScannerEmpty()
    {
        var handler = GhOctopusTool.HandlerFor(() => new List<DetectedPlugin>());
        var result = await handler(BuildValidArgs(2), CancellationToken.None);

        Assert.Equal("missing_plugin", result["status"]!.Value<string>());
        var err = result["error"]!.Value<string>()!;
        Assert.Contains("Octopus", err);
        Assert.Contains("food4rhino.com", err);
        Assert.Equal("Galapagos", result["fallback_suggestion"]!.Value<string>());
    }

    [Fact]
    public async Task Handler_ReturnsMissingPlugin_WhenOnlyUnrelatedPluginsInstalled()
    {
        var handler = GhOctopusTool.HandlerFor(() => new List<DetectedPlugin>
        {
            new("Kangaroo2", "gha", @"C:\fake\Kangaroo2.gha", null),
            new("ladybug", "yak", @"C:\fake\ladybug", "1.8.0"),
        });
        var result = await handler(BuildValidArgs(2), CancellationToken.None);

        Assert.Equal("missing_plugin", result["status"]!.Value<string>());
        Assert.Contains("Octopus", result["error"]!.Value<string>()!);
    }

    // ---- Clause-count gating ---------------------------------------------

    [Fact]
    public async Task Handler_Rejects_SingleObjective_WithGalapagosHint()
    {
        var handler = GhOctopusTool.HandlerFor(OctopusInstalled());
        var result = await handler(BuildValidArgs(1), CancellationToken.None);

        Assert.Equal("error", result["status"]!.Value<string>());
        Assert.Contains("Galapagos", result["hint"]!.Value<string>()!);
    }

    [Fact]
    public async Task Handler_Rejects_FourPlusObjectives_WithWallaceiHint()
    {
        var handler = GhOctopusTool.HandlerFor(OctopusInstalled());
        var result = await handler(BuildValidArgs(4), CancellationToken.None);

        Assert.Equal("error", result["status"]!.Value<string>());
        Assert.Contains("Wallacei", result["hint"]!.Value<string>()!);
    }

    // ---- Pending-rhino default -------------------------------------------

    [Fact]
    public async Task Handler_ReturnsPendingRhino_WithValidArgs()
    {
        var handler = GhOctopusTool.HandlerFor(OctopusInstalled());
        var result = await handler(BuildValidArgs(3), CancellationToken.None);

        Assert.Equal("pending_rhino", result["status"]!.Value<string>());
        Assert.Equal("Octopus", result["component"]!.Value<string>());
        Assert.NotNull(result["message"]);
        Assert.Equal("0.4.0", result["detected_version"]!.Value<string>());
        Assert.Equal(3, result["validated"]!["clause_count"]!.Value<int>());
        Assert.Equal(1, result["validated"]!["genome_count"]!.Value<int>());
    }

    // ---- Clamping --------------------------------------------------------

    [Fact]
    public async Task Handler_ClampsGenerations_ToUpperBound()
    {
        var handler = GhOctopusTool.HandlerFor(OctopusInstalled());
        var args = BuildValidArgs(2);
        args["generations"] = 99_999;

        var result = await handler(args, CancellationToken.None);

        Assert.Equal(500, result["clamped_parameters"]!["generations"]!.Value<int>());
    }

    [Fact]
    public async Task Handler_ClampsPopulation_ToLowerBound()
    {
        var handler = GhOctopusTool.HandlerFor(OctopusInstalled());
        var args = BuildValidArgs(2);
        args["populationSize"] = 1;

        var result = await handler(args, CancellationToken.None);

        Assert.Equal(10, result["clamped_parameters"]!["populationSize"]!.Value<int>());
    }

    [Fact]
    public async Task Handler_AppliesDefaults_WhenParamsOmitted()
    {
        var handler = GhOctopusTool.HandlerFor(OctopusInstalled());
        var result = await handler(BuildValidArgs(2), CancellationToken.None);

        var clamped = (JObject)result["clamped_parameters"]!;
        Assert.Equal(50, clamped["generations"]!.Value<int>());
        Assert.Equal(100, clamped["populationSize"]!.Value<int>());
        Assert.Equal(5, clamped["eliteSize"]!.Value<int>());
        Assert.Equal(0.5, clamped["mutationProbability"]!.Value<double>());
    }

    // ---- Executor delegate -----------------------------------------------

    [Fact]
    public async Task Handler_InjectedExecutor_IsCalledWithClampedArgs()
    {
        JObject? observed = null;
        GhOctopusSetupExecutor exec = args =>
        {
            observed = args;
            return new JObject { ["status"] = "ok", ["pareto_size"] = 42 };
        };

        var handler = GhOctopusTool.HandlerFor(OctopusInstalled(), exec);
        var args = BuildValidArgs(2);
        args["generations"] = 99_999;
        args["populationSize"] = 1;

        var result = await handler(args, CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal(500, observed!["generations"]!.Value<int>());
        Assert.Equal(10, observed["populationSize"]!.Value<int>());
        Assert.Equal("ok", result["status"]!.Value<string>());
        Assert.Equal(42, result["pareto_size"]!.Value<int>());
    }

    [Fact]
    public async Task Handler_InjectedExecutor_NotCalled_WhenClauseCountInvalid()
    {
        bool called = false;
        GhOctopusSetupExecutor exec = _ =>
        {
            called = true;
            return new JObject();
        };

        var handler = GhOctopusTool.HandlerFor(OctopusInstalled(), exec);
        var result = await handler(BuildValidArgs(1), CancellationToken.None);

        Assert.False(called);
        Assert.Equal("error", result["status"]!.Value<string>());
    }

    [Fact]
    public async Task Handler_InjectedExecutor_NotCalled_WhenPluginMissing()
    {
        bool called = false;
        GhOctopusSetupExecutor exec = _ =>
        {
            called = true;
            return new JObject();
        };

        var handler = GhOctopusTool.HandlerFor(() => new List<DetectedPlugin>(), exec);
        var result = await handler(BuildValidArgs(2), CancellationToken.None);

        Assert.False(called);
        Assert.Equal("missing_plugin", result["status"]!.Value<string>());
    }

    // ---- helpers ---------------------------------------------------------

    private static Func<IReadOnlyList<DetectedPlugin>> OctopusInstalled() =>
        () => new List<DetectedPlugin>
        {
            new("Octopus", "gha", @"C:\fake\Octopus.gha", "0.4.0"),
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
