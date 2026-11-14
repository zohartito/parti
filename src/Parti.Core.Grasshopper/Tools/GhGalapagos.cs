// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// gh_galapagos — v0.8 solver binding. Setup validates + stages; run falls back to manual Start until Rhino spike.

using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;

namespace Parti.Core.Grasshopper.Tools;

/// <summary>
/// Delegate invoked by <see cref="GhGalapagos.SetupHandlerFor"/> to wire a Galapagos solver
/// component on the host Grasshopper canvas. Receives the validated argument object and
/// returns a JSON result. Implementations live in the host — <see cref="Parti.Core"/> stays
/// UI-agnostic. When no executor is injected the default canned "pending_rhino" response is
/// emitted instead.
/// </summary>
/// <param name="args">Validated setup args (genomes + fitness block).</param>
public delegate JObject GhGalapagosSetupExecutor(JObject args);

/// <summary>
/// Delegate invoked by <see cref="GhGalapagos.RunHandlerFor"/> to kick off a Galapagos solver
/// run on the host Grasshopper canvas. Receives the clamped argument object. Fallback when no
/// executor is injected: emit a "manual_start_required" result telling the user to press Start
/// in the Galapagos Editor. The programmatic drive path ships in the v0.8 Rhino spike.
/// </summary>
/// <param name="args">Clamped run args (generations / population / timeout).</param>
public delegate JObject GhGalapagosRunExecutor(JObject args);

/// <summary>
/// <c>gh_galapagos_setup</c> + <c>gh_galapagos_run</c> tools — the v0.8 solver binding.
/// <para>
/// <c>Setup</c> wires a Galapagos component against user-chosen slider genomes and a fitness
/// metric. It is reversible because the canvas mutation it stages lives inside the assistant
/// turn's <c>GH_UndoRecord</c>, so the user's Ctrl+Z on the turn rolls it back.
/// </para>
/// <para>
/// <c>Run</c> launches the solver. It is NOT reversible: the setup it was based on can be
/// undone, but the solver's evaluation history is not a canvas undo candidate. Until the v0.8
/// Rhino spike lands, the default executor falls back to asking the user to press Start.
/// </para>
/// </summary>
public static class GhGalapagos
{
    /// <summary>Canonical tool name for the setup tool surfaced to providers.</summary>
    public const string SetupToolName = "gh_galapagos_setup";

    /// <summary>Canonical tool name for the run tool surfaced to providers.</summary>
    public const string RunToolName = "gh_galapagos_run";

    // Run-parameter clamp ranges (shared between schema defaults and the default executor).
    internal const int MinGenerations = 1;
    internal const int MaxGenerations = 500;
    internal const int DefaultGenerations = 50;
    internal const int MinPopulation = 4;
    internal const int MaxPopulation = 500;
    internal const int DefaultPopulation = 50;
    internal const int MinTimeoutSeconds = 10;
    internal const int MaxTimeoutSeconds = 3600;
    internal const int DefaultTimeoutSeconds = 300;

    /// <summary>
    /// Tool definition for <c>gh_galapagos_setup</c>. Reversible: wiring the solver component
    /// lives in the turn's undo record and is rolled back when the user undoes the turn.
    /// </summary>
    public static AIToolDefinition SetupDefinition { get; } = new AIToolDefinition(
        Name: SetupToolName,
        Description: "Stage a Galapagos evolutionary solver on the canvas against the supplied "
            + "slider genomes and fitness metric. Use this when the user wants to optimize a "
            + "parametric model — e.g. 'minimize structural weight by tuning these 4 sliders'. "
            + "The component is wired but NOT started; call gh_galapagos_run afterwards to "
            + "launch the solver. The mutation is bracketed by the turn's undo record and "
            + "the user reverts it by undoing the turn (Ctrl+Z).",
        InputSchema: JObject.Parse(@"
{
  ""type"": ""object"",
  ""properties"": {
    ""genomes"": {
      ""type"": ""array"",
      ""minItems"": 1,
      ""description"": ""Sliders to tune. Each entry names an existing slider by nickname and bounds its search range."",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""slider_name"": { ""type"": ""string"" },
          ""min"":         { ""type"": ""number"" },
          ""max"":         { ""type"": ""number"" }
        },
        ""required"": [""slider_name"", ""min"", ""max""]
      }
    },
    ""fitness"": {
      ""type"": ""object"",
      ""description"": ""Fitness metric and optimization direction."",
      ""properties"": {
        ""metric"":    { ""type"": ""string"", ""description"": ""Nickname of the numeric output component to optimize."" },
        ""direction"": { ""type"": ""string"", ""enum"": [""Maximize"", ""Minimize""] }
      },
      ""required"": [""metric"", ""direction""]
    }
  },
  ""required"": [""genomes"", ""fitness""]
}
"))
    {
        // Staging the component is a canvas mutation inside the turn's undo record.
        IsReversible = true,
    };

    /// <summary>
    /// Tool definition for <c>gh_galapagos_run</c>. Not reversible: solver evaluation history
    /// is not a canvas-undo candidate. The setup it was based on is undoable separately.
    /// </summary>
    public static AIToolDefinition RunDefinition { get; } = new AIToolDefinition(
        Name: RunToolName,
        Description: "Launch the Galapagos solver previously staged via gh_galapagos_setup. "
            + "Until the v0.8 Rhino spike ships, this falls back to asking the user to press "
            + "Start in the Galapagos Editor manually. Parameter values are clamped to sane "
            + "ranges server-side (generations 1..500, population 4..500, timeout 10..3600s).",
        InputSchema: JObject.Parse(@"
{
  ""type"": ""object"",
  ""properties"": {
    ""max_generations"": {
      ""type"": ""integer"",
      ""minimum"": 1,
      ""maximum"": 500,
      ""default"": 50,
      ""description"": ""Maximum number of generations before the solver halts. Clamped to [1..500].""
    },
    ""population_size"": {
      ""type"": ""integer"",
      ""minimum"": 4,
      ""maximum"": 500,
      ""default"": 50,
      ""description"": ""Individuals per generation. Clamped to [4..500].""
    },
    ""timeout_seconds"": {
      ""type"": ""integer"",
      ""minimum"": 10,
      ""maximum"": 3600,
      ""default"": 300,
      ""description"": ""Wall-clock budget in seconds. Clamped to [10..3600].""
    }
  }
}
"))
    {
        // Solver evaluation history is not a canvas undo candidate.
        IsReversible = false,
    };

    /// <summary>
    /// Build the <c>gh_galapagos_setup</c> handler. The optional <paramref name="executor"/>
    /// is supplied by the host plugin when running inside Rhino; when it is <c>null</c> the
    /// handler validates the args and returns a <c>pending_rhino</c> stub so callers can
    /// smoke-test the tool shape outside Rhino.
    /// </summary>
    public static Func<JObject, CancellationToken, Task<JObject>> SetupHandlerFor(
        GhGalapagosSetupExecutor? executor = null)
    {
        return (args, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            var validationError = ValidateSetupArgs(args);
            if (validationError is not null)
            {
                return Task.FromResult(new JObject
                {
                    ["status"] = "error",
                    ["error"] = validationError,
                });
            }

            if (executor is not null)
            {
                try
                {
                    var result = executor(args!) ?? new JObject();
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

            // Default executor: canned "pending_rhino" stub.
            var genomes = (JArray)args!["genomes"]!;
            var metric = args["fitness"]!["metric"]!.Value<string>();
            return Task.FromResult(new JObject
            {
                ["status"] = "pending_rhino",
                ["message"] = "Galapagos setup shape validated. Live canvas wiring activates when "
                    + "Parti.Core.Grasshopper is loaded inside Rhino.",
                ["validated"] = new JObject
                {
                    ["genome_count"] = genomes.Count,
                    ["fitness_metric"] = metric,
                },
            });
        };
    }

    /// <summary>
    /// Build the <c>gh_galapagos_run</c> handler. Like setup, the optional
    /// <paramref name="executor"/> is host-supplied and the default falls back to
    /// <c>manual_start_required</c>. Parameters are clamped BEFORE the executor is called so
    /// the host always sees sane values.
    /// </summary>
    public static Func<JObject, CancellationToken, Task<JObject>> RunHandlerFor(
        GhGalapagosRunExecutor? executor = null)
    {
        return (args, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            var clamped = ClampRunArgs(args);

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

            return Task.FromResult(new JObject
            {
                ["status"] = "manual_start_required",
                ["message"] = "Galapagos has been staged on the canvas. Press Start in the "
                    + "Galapagos Editor to begin optimization. Automatic programmatic drive "
                    + "ships in v0.8 spike week.",
                ["clamped_parameters"] = (JObject)clamped.DeepClone(),
            });
        };
    }

    /// <summary>
    /// Validate the <c>gh_galapagos_setup</c> args. Returns <c>null</c> on success, or a
    /// human-readable error string when the args are malformed.
    /// </summary>
    internal static string? ValidateSetupArgs(JObject? args)
    {
        if (args is null) return "args payload is required";

        // genomes: non-empty array, each entry has slider_name + min < max.
        if (args["genomes"] is not JArray genomes || genomes.Count == 0)
        {
            return "genomes must be a non-empty array";
        }
        for (int i = 0; i < genomes.Count; i++)
        {
            if (genomes[i] is not JObject g)
            {
                return $"genomes[{i}] must be an object";
            }
            var name = g["slider_name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                return $"genomes[{i}].slider_name is required";
            }
            var minTok = g["min"];
            var maxTok = g["max"];
            if (minTok is null || maxTok is null)
            {
                return $"genomes[{i}] requires both min and max";
            }
            double min, max;
            try
            {
                min = minTok.Value<double>();
                max = maxTok.Value<double>();
            }
            catch (Exception)
            {
                return $"genomes[{i}].min/max must be numeric";
            }
            if (min >= max)
            {
                return $"genomes[{i}] min ({min}) must be less than max ({max})";
            }
        }

        // fitness: metric non-empty, direction in {Maximize, Minimize}.
        if (args["fitness"] is not JObject fitness)
        {
            return "fitness object is required";
        }
        var metric = fitness["metric"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(metric))
        {
            return "fitness.metric is required";
        }
        var direction = fitness["direction"]?.Value<string>();
        if (direction != "Maximize" && direction != "Minimize")
        {
            return "fitness.direction must be 'Maximize' or 'Minimize'";
        }

        return null;
    }

    /// <summary>
    /// Clamp <c>gh_galapagos_run</c> args to sane ranges, applying defaults when keys are
    /// missing. Always returns a fresh <see cref="JObject"/> carrying the three clamped keys.
    /// </summary>
    internal static JObject ClampRunArgs(JObject? args)
    {
        int generations = args?["max_generations"]?.Value<int?>() ?? DefaultGenerations;
        int population = args?["population_size"]?.Value<int?>() ?? DefaultPopulation;
        int timeout = args?["timeout_seconds"]?.Value<int?>() ?? DefaultTimeoutSeconds;

        if (generations < MinGenerations) generations = MinGenerations;
        if (generations > MaxGenerations) generations = MaxGenerations;
        if (population < MinPopulation) population = MinPopulation;
        if (population > MaxPopulation) population = MaxPopulation;
        if (timeout < MinTimeoutSeconds) timeout = MinTimeoutSeconds;
        if (timeout > MaxTimeoutSeconds) timeout = MaxTimeoutSeconds;

        return new JObject
        {
            ["max_generations"] = generations,
            ["population_size"] = population,
            ["timeout_seconds"] = timeout,
        };
    }
}
