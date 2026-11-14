// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Grasshopper.Tools;
using Xunit;

namespace Parti.Core.Tests.Tools;

public class GhGalapagosToolTests
{
    // ---- Definition shape -------------------------------------------------

    [Fact]
    public void SetupToolName_IsGhGalapagosSetup()
    {
        Assert.Equal("gh_galapagos_setup", GhGalapagos.SetupToolName);
        Assert.Equal("gh_galapagos_setup", GhGalapagos.SetupDefinition.Name);
    }

    [Fact]
    public void RunToolName_IsGhGalapagosRun()
    {
        Assert.Equal("gh_galapagos_run", GhGalapagos.RunToolName);
        Assert.Equal("gh_galapagos_run", GhGalapagos.RunDefinition.Name);
    }

    [Fact]
    public void Setup_IsReversible_True()
    {
        Assert.True(GhGalapagos.SetupDefinition.IsReversible);
    }

    [Fact]
    public void Run_IsReversible_False()
    {
        Assert.False(GhGalapagos.RunDefinition.IsReversible);
    }

    // ---- Setup default executor ------------------------------------------

    [Fact]
    public async Task Setup_Default_ReturnsPendingRhino_WithValidArgs()
    {
        var handler = GhGalapagos.SetupHandlerFor();
        var args = BuildValidSetupArgs();

        var result = await handler(args, CancellationToken.None);

        Assert.Equal("pending_rhino", result["status"]!.Value<string>());
        Assert.NotNull(result["message"]);
        Assert.Equal(2, result["validated"]!["genome_count"]!.Value<int>());
        Assert.Equal("volume", result["validated"]!["fitness_metric"]!.Value<string>());
    }

    [Fact]
    public async Task Setup_Default_ReturnsError_WhenGenomesEmpty()
    {
        var handler = GhGalapagos.SetupHandlerFor();
        var args = new JObject
        {
            ["genomes"] = new JArray(),
            ["fitness"] = new JObject
            {
                ["metric"] = "volume",
                ["direction"] = "Maximize",
            },
        };

        var result = await handler(args, CancellationToken.None);

        Assert.Equal("error", result["status"]!.Value<string>());
        Assert.Contains("genomes", result["error"]!.Value<string>());
    }

    [Fact]
    public async Task Setup_Default_ReturnsError_WhenMinExceedsMax()
    {
        var handler = GhGalapagos.SetupHandlerFor();
        var args = new JObject
        {
            ["genomes"] = new JArray
            {
                new JObject
                {
                    ["slider_name"] = "width",
                    ["min"] = 10.0,
                    ["max"] = 5.0,
                },
            },
            ["fitness"] = new JObject
            {
                ["metric"] = "volume",
                ["direction"] = "Maximize",
            },
        };

        var result = await handler(args, CancellationToken.None);

        Assert.Equal("error", result["status"]!.Value<string>());
        Assert.Contains("min", result["error"]!.Value<string>());
    }

    [Fact]
    public async Task Setup_Default_ReturnsError_WhenDirectionInvalid()
    {
        var handler = GhGalapagos.SetupHandlerFor();
        var args = new JObject
        {
            ["genomes"] = new JArray
            {
                new JObject
                {
                    ["slider_name"] = "width",
                    ["min"] = 1.0,
                    ["max"] = 10.0,
                },
            },
            ["fitness"] = new JObject
            {
                ["metric"] = "volume",
                ["direction"] = "Sideways",
            },
        };

        var result = await handler(args, CancellationToken.None);

        Assert.Equal("error", result["status"]!.Value<string>());
        Assert.Contains("direction", result["error"]!.Value<string>());
    }

    [Fact]
    public async Task Setup_Default_ReturnsError_WhenFitnessMetricMissing()
    {
        var handler = GhGalapagos.SetupHandlerFor();
        var args = new JObject
        {
            ["genomes"] = new JArray
            {
                new JObject
                {
                    ["slider_name"] = "width",
                    ["min"] = 1.0,
                    ["max"] = 10.0,
                },
            },
            ["fitness"] = new JObject
            {
                ["direction"] = "Maximize",
            },
        };

        var result = await handler(args, CancellationToken.None);

        Assert.Equal("error", result["status"]!.Value<string>());
        Assert.Contains("metric", result["error"]!.Value<string>());
    }

    // ---- Run default executor --------------------------------------------

    [Fact]
    public async Task Run_Default_ReturnsManualStartRequired()
    {
        var handler = GhGalapagos.RunHandlerFor();
        var result = await handler(new JObject(), CancellationToken.None);

        Assert.Equal("manual_start_required", result["status"]!.Value<string>());
        Assert.NotNull(result["message"]);
        Assert.Equal(50, result["clamped_parameters"]!["max_generations"]!.Value<int>());
        Assert.Equal(50, result["clamped_parameters"]!["population_size"]!.Value<int>());
        Assert.Equal(300, result["clamped_parameters"]!["timeout_seconds"]!.Value<int>());
    }

    [Fact]
    public async Task Run_Default_ClampsMaxGenerationsTo500()
    {
        var handler = GhGalapagos.RunHandlerFor();
        var args = new JObject { ["max_generations"] = 999_999 };

        var result = await handler(args, CancellationToken.None);

        Assert.Equal(500, result["clamped_parameters"]!["max_generations"]!.Value<int>());
    }

    [Fact]
    public async Task Run_Default_ClampsPopulationTo4_Minimum()
    {
        var handler = GhGalapagos.RunHandlerFor();
        var args = new JObject { ["population_size"] = 1 };

        var result = await handler(args, CancellationToken.None);

        Assert.Equal(4, result["clamped_parameters"]!["population_size"]!.Value<int>());
    }

    [Fact]
    public async Task Run_Default_ClampsTimeoutToUpperBound()
    {
        var handler = GhGalapagos.RunHandlerFor();
        var args = new JObject { ["timeout_seconds"] = 10_000 };

        var result = await handler(args, CancellationToken.None);

        Assert.Equal(3600, result["clamped_parameters"]!["timeout_seconds"]!.Value<int>());
    }

    // ---- Injected-executor path ------------------------------------------

    [Fact]
    public async Task Setup_InjectedExecutor_IsCalledWithArgs()
    {
        JObject? observed = null;
        GhGalapagosSetupExecutor exec = args =>
        {
            observed = args;
            return new JObject
            {
                ["status"] = "ok",
                ["wired"] = true,
            };
        };

        var handler = GhGalapagos.SetupHandlerFor(exec);
        var result = await handler(BuildValidSetupArgs(), CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal("volume", observed!["fitness"]!["metric"]!.Value<string>());
        Assert.Equal("ok", result["status"]!.Value<string>());
        Assert.True(result["wired"]!.Value<bool>());
    }

    [Fact]
    public async Task Run_InjectedExecutor_IsCalledWithClampedArgs()
    {
        JObject? observed = null;
        GhGalapagosRunExecutor exec = args =>
        {
            observed = args;
            return new JObject
            {
                ["status"] = "ok",
                ["generations_completed"] = 500,
            };
        };

        var handler = GhGalapagos.RunHandlerFor(exec);
        var args = new JObject
        {
            ["max_generations"] = 10_000, // clamp → 500
            ["population_size"] = 2,      // clamp → 4
            ["timeout_seconds"] = 5,      // clamp → 10
        };

        var result = await handler(args, CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal(500, observed!["max_generations"]!.Value<int>());
        Assert.Equal(4, observed["population_size"]!.Value<int>());
        Assert.Equal(10, observed["timeout_seconds"]!.Value<int>());
        Assert.Equal("ok", result["status"]!.Value<string>());
    }

    [Fact]
    public async Task Setup_InjectedExecutor_NotCalled_WhenValidationFails()
    {
        bool called = false;
        GhGalapagosSetupExecutor exec = args =>
        {
            called = true;
            return new JObject();
        };

        var handler = GhGalapagos.SetupHandlerFor(exec);
        var args = new JObject
        {
            ["genomes"] = new JArray(),
            ["fitness"] = new JObject
            {
                ["metric"] = "volume",
                ["direction"] = "Maximize",
            },
        };

        var result = await handler(args, CancellationToken.None);

        Assert.False(called);
        Assert.Equal("error", result["status"]!.Value<string>());
    }

    // ---- helpers ----------------------------------------------------------

    private static JObject BuildValidSetupArgs() => new JObject
    {
        ["genomes"] = new JArray
        {
            new JObject
            {
                ["slider_name"] = "width",
                ["min"] = 1.0,
                ["max"] = 10.0,
            },
            new JObject
            {
                ["slider_name"] = "height",
                ["min"] = 2.0,
                ["max"] = 20.0,
            },
        },
        ["fitness"] = new JObject
        {
            ["metric"] = "volume",
            ["direction"] = "Maximize",
        },
    };
}
