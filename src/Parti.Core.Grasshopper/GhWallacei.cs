// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// gh_wallacei_setup — v0.9 solver binding. Mirrors GhGalapagos: delegate-injected executor,
// clamped params, pending_rhino default. Plugin-presence preflight via GhPluginScanner.
// Enforces FitnessFunction.RecommendSolver: Wallacei handles 4+ objectives or generative runs.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Tools;

namespace Parti.Core.Grasshopper;

/// <summary>
/// Delegate invoked by <see cref="GhWallaceiTool.HandlerFor"/> to wire a Wallacei solver on
/// the host Grasshopper canvas. Implementations live in the host — <see cref="Parti.Core"/>
/// stays UI-agnostic. When no executor is injected the default canned "pending_rhino"
/// response is emitted instead.
/// </summary>
/// <param name="args">Validated + clamped setup args (clauses + genomes + run params).</param>
public delegate JObject GhWallaceiSetupExecutor(JObject args);

/// <summary>
/// <c>gh_wallacei_setup</c> tool — v0.9 binding for the Wallacei generative / many-objective
/// evolutionary solver. Requires 4+ clauses OR a generative/constraint rule (override via
/// <c>forceWallacei</c>). Clamps run params, preflights plugin availability, and stages the
/// wiring. Default executor falls back to <c>pending_rhino</c> until the Rhino spike lands.
/// </summary>
public static class GhWallaceiTool
{
    /// <summary>Canonical tool name surfaced to providers.</summary>
    public const string ToolName = "gh_wallacei_setup";

    private const string PluginMatchName = "wallacei";
    private const string Food4RhinoUrl = "https://www.food4rhino.com/en/app/wallacei-x";
    internal const string DefaultAlgorithm = "NSGA2";

    // Run-parameter clamp ranges.
    internal const int MinGenerations = 1;
    internal const int MaxGenerations = 500;
    internal const int DefaultGenerations = 50;
    internal const int MinPopulation = 10;
    internal const int MaxPopulation = 500;
    internal const int DefaultPopulation = 100;
    internal const int DefaultEliteSize = 5;
    internal const double DefaultMutationProbability = 0.5;

    /// <summary>
    /// Tool definition for the AI provider. Reversible because the v0.9 implementation
    /// emits canvas mutations bracketed in the turn's undo record.
    /// </summary>
    public static AIToolDefinition Definition { get; } = new AIToolDefinition(
        Name: ToolName,
        Description: "Stage a Wallacei generative / many-objective evolutionary run on the "
            + "current Grasshopper canvas. Use this for 4+ competing objectives, or when the "
            + "design brief mentions generative rules or constraints (pass forceWallacei=true "
            + "to override the clause-count gate). For 1 objective use gh_galapagos_setup; "
            + "for 2-3 objectives use gh_octopus_setup. Parameters are clamped server-side "
            + "(generations 1..500, population 10..500). The component is wired but NOT "
            + "started; the user reverts it by undoing the turn (Ctrl+Z).",
        InputSchema: JObject.Parse(@"
{
  ""type"": ""object"",
  ""properties"": {
    ""clauses"": {
      ""type"": ""array"",
      ""description"": ""Fitness clauses. Each entry: { metric, direction: Maximize|Minimize, weight? }. Wallacei shines with 4+ clauses.""
    },
    ""genomes"": {
      ""type"": ""array"",
      ""minItems"": 1,
      ""description"": ""Sliders to tune. Each entry: { slider_name, min, max }.""
    },
    ""generations"": {
      ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 50,
      ""description"": ""Max generations. Clamped to [1..500].""
    },
    ""populationSize"": {
      ""type"": ""integer"", ""minimum"": 10, ""maximum"": 500, ""default"": 100,
      ""description"": ""Individuals per generation. Clamped to [10..500].""
    },
    ""eliteSize"": {
      ""type"": ""integer"", ""minimum"": 0, ""default"": 5,
      ""description"": ""Number of elite individuals preserved across generations.""
    },
    ""mutationProbability"": {
      ""type"": ""number"", ""minimum"": 0.0, ""maximum"": 1.0, ""default"": 0.5,
      ""description"": ""Per-gene mutation probability [0..1].""
    },
    ""algorithm"": {
      ""type"": ""string"", ""default"": ""NSGA2"",
      ""description"": ""Evolutionary algorithm variant (Wallacei defaults to NSGA2).""
    },
    ""forceWallacei"": {
      ""type"": ""boolean"", ""default"": false,
      ""description"": ""Bypass the 4+ clause requirement (e.g. when a rule is generative or constraint-driven).""
    }
  },
  ""required"": [""clauses"", ""genomes""]
}
"))
    {
        IsReversible = true,
    };

    /// <summary>
    /// Build a handler suitable for <see cref="ToolRegistry.Register"/>. Defaults:
    /// <list type="bullet">
    ///   <item>When <paramref name="scanner"/> is null, scans default filesystem roots.</item>
    ///   <item>When <paramref name="executor"/> is null, returns a <c>pending_rhino</c> stub
    ///         so callers can smoke-test the tool shape outside Rhino.</item>
    /// </list>
    /// </summary>
    public static Func<JObject, CancellationToken, Task<JObject>> HandlerFor(
        Func<IReadOnlyList<DetectedPlugin>>? scanner = null,
        GhWallaceiSetupExecutor? executor = null)
    {
        return (args, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            // 1. Gate on clause count OR force override (generative / constraint rules).
            var clauseError = ValidateClauseCount(args);
            if (clauseError is not null)
            {
                return Task.FromResult(clauseError);
            }

            // 2. Plugin preflight — fallback to Octopus when Wallacei is missing.
            var plugins = scanner is not null
                ? scanner()
                : GhPluginScanner.Scan(GhPluginScanner.DefaultRoots());

            var match = plugins?.FirstOrDefault(p =>
                p.Name.Contains(PluginMatchName, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                return Task.FromResult(new JObject
                {
                    ["status"] = "missing_plugin",
                    ["error"] = $"Wallacei is not installed. Install from Food4Rhino: {Food4RhinoUrl}",
                    ["fallback_suggestion"] = "Octopus",
                });
            }

            // 3. Clamp run params.
            var clamped = ClampSetupArgs(args);

            // 4. Executor path — host plugin supplies this when running inside Rhino.
            if (executor is not null)
            {
                try
                {
                    var result = executor(clamped) ?? new JObject();
                    return Task.FromResult(result);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return Task.FromResult(new JObject
                    {
                        ["status"] = "error",
                        ["error"] = ex.Message,
                    });
                }
            }

            // 5. Default: pending_rhino stub.
            var clauseCount = ((JArray)args!["clauses"]!).Count;
            var genomeCount = ((JArray)args!["genomes"]!).Count;
            return Task.FromResult(new JObject
            {
                ["status"] = "pending_rhino",
                ["component"] = "Wallacei",
                ["message"] = "Wallacei setup shape validated. Live canvas wiring activates "
                    + "when Parti.Core.Grasshopper is loaded inside Rhino.",
                ["detected_version"] = match.Version,
                ["validated"] = new JObject
                {
                    ["clause_count"] = clauseCount,
                    ["genome_count"] = genomeCount,
                    ["algorithm"] = clamped["algorithm"],
                },
                ["clamped_parameters"] = (JObject)clamped.DeepClone(),
            });
        };
    }

    /// <summary>
    /// Validate clause count for Wallacei. Requires 4+ clauses UNLESS
    /// <c>forceWallacei=true</c> (for generative / constraint-driven runs that don't
    /// express their goals as discrete objectives). Returns a ready-to-return error
    /// <see cref="JObject"/> on failure, or <c>null</c> on success.
    /// </summary>
    internal static JObject? ValidateClauseCount(JObject? args)
    {
        if (args is null || args["clauses"] is not JArray clauses)
        {
            return new JObject
            {
                ["status"] = "error",
                ["error"] = "clauses array is required",
            };
        }

        bool force = args["forceWallacei"]?.Value<bool?>() ?? false;
        if (clauses.Count < 4 && !force)
        {
            return new JObject
            {
                ["status"] = "error",
                ["error"] = $"Wallacei is intended for 4+ objectives, got {clauses.Count}.",
                ["hint"] = "Use Galapagos or Octopus. Pass forceWallacei=true for generative / constraint-driven rules.",
            };
        }
        return null;
    }

    /// <summary>
    /// Clamp setup args to sane ranges, applying defaults when keys are missing. Returns a
    /// fresh <see cref="JObject"/> carrying clauses + genomes + clamped run params + algorithm.
    /// </summary>
    internal static JObject ClampSetupArgs(JObject args)
    {
        int generations = args["generations"]?.Value<int?>() ?? DefaultGenerations;
        int population = args["populationSize"]?.Value<int?>() ?? DefaultPopulation;
        int elite = args["eliteSize"]?.Value<int?>() ?? DefaultEliteSize;
        double mutation = args["mutationProbability"]?.Value<double?>() ?? DefaultMutationProbability;
        string algorithm = args["algorithm"]?.Value<string>() ?? DefaultAlgorithm;
        if (string.IsNullOrWhiteSpace(algorithm)) algorithm = DefaultAlgorithm;

        if (generations < MinGenerations) generations = MinGenerations;
        if (generations > MaxGenerations) generations = MaxGenerations;
        if (population < MinPopulation) population = MinPopulation;
        if (population > MaxPopulation) population = MaxPopulation;
        if (elite < 0) elite = 0;
        if (elite > population) elite = population;
        if (mutation < 0.0) mutation = 0.0;
        if (mutation > 1.0) mutation = 1.0;

        return new JObject
        {
            ["clauses"] = args["clauses"]?.DeepClone() ?? new JArray(),
            ["genomes"] = args["genomes"]?.DeepClone() ?? new JArray(),
            ["generations"] = generations,
            ["populationSize"] = population,
            ["eliteSize"] = elite,
            ["mutationProbability"] = mutation,
            ["algorithm"] = algorithm,
        };
    }
}
