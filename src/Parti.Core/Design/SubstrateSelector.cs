// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// SubstrateSelector — v0.7.5 primitive.
// The @abhinavbwj note [DXda58ngaQF] argues that Grasshopper is not always the right tool.
// Parti should be able to say "this is a spreadsheet problem" or "this is pen-and-paper"
// and route the user accordingly instead of dragging every problem onto the canvas.

using System.Collections.Generic;

namespace Parti.Core.Design;

/// <summary>
/// Substrate — the medium best suited to work a design problem.
/// Ordered along the formalization spectrum: the lower the ordinal, the cheaper to iterate;
/// the higher, the more expressive but costlier to author.
/// </summary>
public enum Substrate
{
    /// <summary>Pen-and-paper sketches. Right when the problem is still being felt out.</summary>
    PenAndPaper = 0,
    /// <summary>Spreadsheet. Right when the problem is numeric and tabular but not geometric.</summary>
    Spreadsheet = 1,
    /// <summary>Python script. Right for numeric / algorithmic work that doesn't need live geometry.</summary>
    Python = 2,
    /// <summary>Grasshopper. Right for parametric geometry with a visible dataflow.</summary>
    Grasshopper = 3,
    /// <summary>Houdini. Right for very large simulations or VFX-grade geometry.</summary>
    Houdini = 4,
    /// <summary>Hand-written compiled code (C#, C++). Right for performance-critical custom solvers.</summary>
    CustomCode = 5,
    /// <summary>ML / learned models. Right for problems where the rules are implicit in data.</summary>
    MachineLearning = 6,
}

/// <summary>
/// Result of a substrate recommendation: the picked substrate, a runner-up, and the
/// rationale for both so Parti can explain its choice to the user.
/// </summary>
/// <param name="Recommended">The recommended substrate.</param>
/// <param name="Runnerup">
/// Optional second choice — offered when the decision is close, null when the top pick is
/// unambiguous.
/// </param>
/// <param name="Rationale">Short human-readable explanation, one sentence.</param>
/// <param name="SignalsMatched">
/// Diagnostic list of which signals drove the recommendation. Useful for evals and for
/// explaining "why not Grasshopper?" to users who expected the default.
/// </param>
public sealed record SubstrateRecommendation(
    Substrate Recommended,
    Substrate? Runnerup,
    string Rationale,
    IReadOnlyList<string> SignalsMatched);

/// <summary>
/// Heuristic picker that maps a <see cref="Decomposition"/> + user prompt to a
/// <see cref="Substrate"/>. Pure function — no I/O, no state.
/// <para>
/// Not every design problem is a Grasshopper problem. This primitive lets Parti push back
/// with "try a spreadsheet first" when the signals don't justify the canvas. See
/// @abhinavbwj note [DXda58ngaQF] and docs/ROADMAP.md v0.7.5 section.
/// </para>
/// </summary>
public static class SubstrateSelector
{
    /// <summary>
    /// Pick a substrate for the given decomposition + free-form prompt text. Both inputs
    /// are consulted: the decomposition provides structured signals (has inputs? has
    /// geometric principle?) and the prompt provides surface-level hints (the word
    /// "spreadsheet", presence of dataset references).
    /// </summary>
    public static SubstrateRecommendation Select(Decomposition decomposition, string promptText)
    {
        promptText ??= string.Empty;
        var lower = promptText.ToLowerInvariant();
        var signals = new List<string>();

        // --- Strong directional signals ------------------------------------
        // Explicit user ask for a substrate wins outright.
        if (ContainsAny(lower, "spreadsheet", "excel", "csv", "tabular"))
        {
            signals.Add("explicit: spreadsheet keyword in prompt");
            return new SubstrateRecommendation(
                Substrate.Spreadsheet,
                Runnerup: Substrate.Python,
                Rationale: "The prompt mentions tabular / spreadsheet data directly.",
                SignalsMatched: signals);
        }

        if (ContainsAny(lower, "machine learning", "neural", "ml model", "train a model", "dataset"))
        {
            signals.Add("explicit: ML keyword in prompt");
            return new SubstrateRecommendation(
                Substrate.MachineLearning,
                Runnerup: Substrate.Python,
                Rationale: "The prompt describes a learned-model problem.",
                SignalsMatched: signals);
        }

        if (ContainsAny(lower, "sketch", "draw by hand", "napkin", "back of envelope"))
        {
            signals.Add("explicit: pen-and-paper keyword in prompt");
            return new SubstrateRecommendation(
                Substrate.PenAndPaper,
                Runnerup: null,
                Rationale: "The prompt describes a sketching exercise, not a parametric model.",
                SignalsMatched: signals);
        }

        if (ContainsAny(lower, "simulation", "fluid", "particle", "vfx", "smoke", "cloth sim"))
        {
            signals.Add("explicit: simulation/VFX keyword in prompt");
            return new SubstrateRecommendation(
                Substrate.Houdini,
                Runnerup: Substrate.CustomCode,
                Rationale: "Large-scale simulation is better served by Houdini than Grasshopper.",
                SignalsMatched: signals);
        }

        // --- Structured signals from the decomposition ---------------------
        // If the decomposition has no geometric principle and all inputs are numeric,
        // a spreadsheet or Python script is probably enough.
        bool hasGeometricPrinciple =
            !string.IsNullOrWhiteSpace(decomposition.Principle) &&
            MentionsGeometry(decomposition.Principle!);
        bool hasGeometricPromptHint = MentionsGeometry(lower);

        if (!hasGeometricPrinciple && !hasGeometricPromptHint)
        {
            if (decomposition.Inputs.Count > 0)
            {
                signals.Add("no geometric principle + numeric inputs");
                return new SubstrateRecommendation(
                    Substrate.Spreadsheet,
                    Runnerup: Substrate.Python,
                    Rationale: "The problem is numeric; a spreadsheet is cheaper than Grasshopper.",
                    SignalsMatched: signals);
            }

            // Nothing concrete at all yet — push back to pen-and-paper.
            if (!decomposition.HasFormalization)
            {
                signals.Add("no formalization yet");
                return new SubstrateRecommendation(
                    Substrate.PenAndPaper,
                    Runnerup: Substrate.Grasshopper,
                    Rationale: "The problem is still being formed; sketch it on paper first.",
                    SignalsMatched: signals);
            }
        }

        // --- Default: Grasshopper --------------------------------------------
        signals.Add("default: geometric parametric problem");
        return new SubstrateRecommendation(
            Substrate.Grasshopper,
            Runnerup: null,
            Rationale: "Parametric geometry problem with varying inputs — Grasshopper is the fit.",
            SignalsMatched: signals);
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var n in needles)
        {
            if (haystack.Contains(n)) return true;
        }
        return false;
    }

    private static readonly string[] GeometryKeywords =
    {
        "facade", "panel", "roof", "wall", "curve", "surface", "mesh", "brep", "tower",
        "column", "beam", "truss", "tile", "louvre", "louver", "rainscreen", "girt",
        "bracket", "tessellat", "geometry", "geometric", "form", "shape", "volume",
        "mass", "massing", "plan", "section", "elevation", "envelope", "pavilion",
    };

    private static bool MentionsGeometry(string text)
    {
        var lower = text.ToLowerInvariant();
        foreach (var kw in GeometryKeywords)
        {
            if (lower.Contains(kw)) return true;
        }
        return false;
    }
}
