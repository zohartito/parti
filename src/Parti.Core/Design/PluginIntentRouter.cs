// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// PluginIntentRouter — v0.7.5 primitive.
// Given a user prompt, identifies which third-party GH plugins (if any) it is asking for,
// and reports whether they are actually installed. Pairs with GhPluginCheck — router
// provides the intent, GhPluginCheck the ground truth. Responds to note [DU0qLF1iDVu].

using System;
using System.Collections.Generic;
using System.Linq;
using Parti.Core.Tools;

namespace Parti.Core.Design;

/// <summary>
/// One plugin the user's prompt implied they want to use.
/// </summary>
/// <param name="PluginName">Canonical plugin name (lowercased).</param>
/// <param name="Confidence">0.0..1.0 — 1.0 when the user named the plugin directly.</param>
/// <param name="MatchedTerms">Which words in the prompt triggered the match.</param>
/// <param name="IsInstalled">
/// True when <see cref="GhPluginScanner.Scan"/> sees the plugin on disk; false when it's
/// implied but missing, null when no plugin list was supplied to the router.
/// </param>
public sealed record PluginIntent(
    string PluginName,
    double Confidence,
    IReadOnlyList<string> MatchedTerms,
    bool? IsInstalled);

/// <summary>
/// Routes user prompts to the plugins they imply. Pure / stateless.
/// <para>
/// Two use cases:
/// </para>
/// <list type="bullet">
///   <item>"Use Kangaroo2 to simulate tension" — explicit plugin name, confidence 1.0</item>
///   <item>"Do a daylight study" — implicit Ladybug/Honeybee, confidence 0.6</item>
/// </list>
/// </summary>
public static class PluginIntentRouter
{
    // Keyword → canonical plugin name, confidence.
    private static readonly Dictionary<string, (string Canonical, double Confidence)> Keywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Direct plugin names (high confidence).
            ["kangaroo"] = ("kangaroo", 1.0),
            ["kangaroo2"] = ("kangaroo", 1.0),
            ["ladybug"] = ("ladybug", 1.0),
            ["honeybee"] = ("honeybee", 1.0),
            ["butterfly"] = ("butterfly", 1.0),
            ["dragonfly"] = ("dragonfly", 1.0),
            ["lunchbox"] = ("lunchbox", 1.0),
            ["anemone"] = ("anemone", 1.0),
            ["weaverbird"] = ("weaverbird", 1.0),
            ["pufferfish"] = ("pufferfish", 1.0),
            ["dendro"] = ("dendro", 1.0),
            ["elefront"] = ("elefront", 1.0),
            ["elk"] = ("elk", 1.0),
            ["galapagos"] = ("galapagos", 1.0),
            ["octopus"] = ("octopus", 1.0),
            ["wallacei"] = ("wallacei", 1.0),

            // Implicit references by capability (medium confidence).
            ["daylight"] = ("ladybug", 0.6),
            ["sun hours"] = ("ladybug", 0.7),
            ["solar radiation"] = ("ladybug", 0.7),
            ["energy model"] = ("honeybee", 0.7),
            ["cfd"] = ("butterfly", 0.6),
            ["urban heat"] = ("dragonfly", 0.6),
            ["tension"] = ("kangaroo", 0.7),
            ["form finding"] = ("kangaroo", 0.8),
            ["catenary"] = ("kangaroo", 0.8),
            ["relaxation"] = ("kangaroo", 0.7),
            ["physics"] = ("kangaroo", 0.6),
            ["voxel"] = ("dendro", 0.7),
            ["sdf"] = ("dendro", 0.7),
            ["loop"] = ("anemone", 0.5), // weak — the word is common
            ["iterative"] = ("anemone", 0.5),
            ["subdivision"] = ("weaverbird", 0.7),
            ["catmull"] = ("weaverbird", 0.8),
            ["catmull-clark"] = ("weaverbird", 0.9),
            ["fillet"] = ("pufferfish", 0.5),
            ["blend"] = ("pufferfish", 0.5),
            ["openstreetmap"] = ("elk", 0.8),
            ["osm"] = ("elk", 0.8),
            ["block instance"] = ("elefront", 0.7),
            ["bake"] = ("elefront", 0.5),
            ["genetic"] = ("galapagos", 0.6),
            ["multi-objective"] = ("octopus", 0.7),
            ["pareto"] = ("octopus", 0.7),
            ["nsga"] = ("wallacei", 0.8),
            ["evolutionary"] = ("wallacei", 0.6),
        };

    /// <summary>
    /// Parse the prompt and return all plugins implied by it, ranked by confidence.
    /// <paramref name="installedPlugins"/> is optional — when supplied, each result is
    /// annotated with <see cref="PluginIntent.IsInstalled"/>.
    /// </summary>
    public static IReadOnlyList<PluginIntent> Route(
        string promptText,
        IReadOnlyList<DetectedPlugin>? installedPlugins = null)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return Array.Empty<PluginIntent>();
        var lower = promptText.ToLowerInvariant();

        // Aggregate matches per canonical plugin — a plugin may be matched by several terms.
        var matchesByCanonical = new Dictionary<string, (double MaxConfidence, List<string> Terms)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (keyword, (canonical, confidence)) in Keywords)
        {
            if (lower.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                if (!matchesByCanonical.TryGetValue(canonical, out var existing))
                {
                    existing = (0.0, new List<string>());
                }
                existing.Terms.Add(keyword);
                if (confidence > existing.MaxConfidence) existing.MaxConfidence = confidence;
                matchesByCanonical[canonical] = existing;
            }
        }

        var installedSet = installedPlugins is null
            ? null
            : new HashSet<string>(
                installedPlugins.Select(p => p.Name.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

        return matchesByCanonical
            .Select(kv => new PluginIntent(
                PluginName: kv.Key,
                Confidence: kv.Value.MaxConfidence,
                MatchedTerms: kv.Value.Terms,
                IsInstalled: installedSet?.Contains(kv.Key)))
            .OrderByDescending(p => p.Confidence)
            .ThenBy(p => p.PluginName)
            .ToList();
    }

    /// <summary>
    /// Convenience: filter a Route result to plugins the prompt wanted but the user does
    /// NOT have installed. These are the cases Parti should flag before proposing a
    /// workflow.
    /// </summary>
    public static IReadOnlyList<PluginIntent> MissingPlugins(IReadOnlyList<PluginIntent> intents)
        => intents.Where(i => i.IsInstalled == false).ToList();
}
