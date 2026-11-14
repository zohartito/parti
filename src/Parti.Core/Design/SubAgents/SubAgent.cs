// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// SubAgent — v0.7.5 persona primitive.
// A sub-agent is a named, single-purpose LLM persona with its own system prompt. The main
// conversation delegates to sub-agents for focused tasks (decompose, pick substrate,
// clarify a missing pillar). In v0.7.5 sub-agents are delivered as data; orchestration
// (swapping them into the run loop) arrives in v0.8 with the parti-optimize persona.

using System;
using System.Collections.Generic;

namespace Parti.Core.Design.SubAgents;

/// <summary>
/// A single named sub-agent persona. Immutable.
/// </summary>
/// <param name="Name">Canonical name (e.g. "parti-formalize"). Stable across versions.</param>
/// <param name="Description">
/// One-sentence description — what the persona does, when to invoke it. Surfaced in
/// Layer 0 of the skill loader in v0.8.5.
/// </param>
/// <param name="SystemPrompt">
/// The persona's system prompt. Prepended to the shared Parti system prompt when the
/// persona is active.
/// </param>
/// <param name="PillarsCovered">
/// Which three-pillar slots this sub-agent helps resolve (e.g. Formalization, Feedback).
/// Empty for personas that do not advance the pillar gate (e.g. substrate selector).
/// </param>
public sealed record SubAgent(
    string Name,
    string Description,
    string SystemPrompt,
    IReadOnlyList<string> PillarsCovered);

/// <summary>
/// Read-only registry of the personas Parti ships with.
/// </summary>
public static class SubAgentRegistry
{
    /// <summary>
    /// Parti's v0.7.5 persona set:
    /// <list type="bullet">
    ///   <item>parti-formalize — resolves Inputs + Rules (Formalization pillar)</item>
    ///   <item>parti-abstract — resolves organizing Principle (Abstraction pillar)</item>
    ///   <item>parti-feedback — resolves Evaluations + FeedbackPlan (Feedback pillar)</item>
    ///   <item>parti-substrate — picks the right medium (pen/spreadsheet/GH/Houdini/code/ML)</item>
    ///   <item>parti-clarify — meta-persona for asking a single focused question</item>
    ///   <item>parti-optimize — authors a fitness function + solver choice for a run</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<SubAgent> All { get; } = new[]
    {
        Personas.Formalize,
        Personas.Abstract,
        Personas.Feedback,
        Personas.Substrate,
        Personas.Clarify,
        Personas.Optimize,
    };

    /// <summary>
    /// Look up a persona by name (case-insensitive). Throws when the name is unknown — the
    /// orchestrator should validate against <see cref="All"/> before dispatching.
    /// </summary>
    public static SubAgent Get(string name)
    {
        foreach (var agent in All)
        {
            if (string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return agent;
            }
        }
        throw new KeyNotFoundException($"Unknown sub-agent persona: {name}");
    }

    /// <summary>Try-get variant that returns null instead of throwing.</summary>
    public static SubAgent? TryGet(string name)
    {
        foreach (var agent in All)
        {
            if (string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return agent;
            }
        }
        return null;
    }

    /// <summary>
    /// Find all personas that cover the named pillar. Pillar names are
    /// "Formalization" / "Abstraction" / "Feedback". Returns empty for unknown pillars.
    /// </summary>
    public static IReadOnlyList<SubAgent> ForPillar(string pillar)
    {
        var results = new List<SubAgent>();
        foreach (var agent in All)
        {
            foreach (var p in agent.PillarsCovered)
            {
                if (string.Equals(p, pillar, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(agent);
                    break;
                }
            }
        }
        return results;
    }
}
