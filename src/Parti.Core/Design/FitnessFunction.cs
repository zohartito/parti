// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.
// FitnessFunction record — v0.8 optimization layer. A FitnessFunction is the structured
// answer to "how do we score a design instance?" and pairs clauses (Feedback pillar) with
// genomes (Formalization pillar) and a chosen solver (Galapagos / Octopus / Wallacei).

using System.Collections.Generic;

namespace Parti.Core.Design;

/// <summary>
/// Supported Grasshopper optimization solvers. Galapagos ships with Rhino; Octopus and
/// Wallacei are third-party multi-objective plugins. Selection is a recommendation from
/// clause count plus user override / availability.
/// </summary>
public enum Solver
{
    Galapagos,
    Octopus,
    Wallacei
}

/// <summary>
/// A single variable gene wired into the optimization — corresponds to a Grasshopper
/// Number Slider the solver is allowed to move within a bounded domain.
/// </summary>
/// <param name="SliderName">
/// Name of the target Number Slider (or Gene Pool) in the Grasshopper document.
/// </param>
/// <param name="Min">Inclusive lower bound of the slider's search domain.</param>
/// <param name="Max">Inclusive upper bound of the slider's search domain.</param>
/// <param name="Step">
/// Optional discrete step size. Null means the solver treats the domain as continuous.
/// </param>
public sealed record Genome(string SliderName, double Min, double Max, double? Step = null);

/// <summary>
/// A single scoring clause of a fitness function. One clause maps to one objective in the
/// solver. Reuses <see cref="EvalDirection"/> from <see cref="Decomposition"/> so the
/// vocabulary stays aligned between pillars.
/// </summary>
/// <param name="Metric">
/// Name of the metric being scored, e.g. "total glare hours" or "structural mass".
/// </param>
/// <param name="Direction">Maximize / Minimize / Keep.</param>
/// <param name="Target">
/// Optional numeric target. For Keep, this is the value to hold. For Maximize / Minimize,
/// this is an optional floor / ceiling.
/// </param>
/// <param name="Weight">
/// Relative importance of this clause when combined with others. Defaults to 1.0.
/// </param>
public sealed record FitnessClause(string Metric, EvalDirection Direction, double? Target = null, double Weight = 1.0);

/// <summary>
/// A complete fitness function — the full specification handed to the chosen solver.
/// Pairs scoring clauses with the genomes they're allowed to move, and records which
/// solver was actually chosen (which may differ from the purely count-based recommendation
/// when the user overrides or a plugin is unavailable).
/// </summary>
/// <param name="Clauses">Scoring clauses; each becomes one solver objective.</param>
/// <param name="Genomes">Variable genes the solver is allowed to move.</param>
/// <param name="ChosenSolver">The solver actually selected for this optimization run.</param>
public sealed record FitnessFunction(
    IReadOnlyList<FitnessClause> Clauses,
    IReadOnlyList<Genome> Genomes,
    Solver ChosenSolver)
{
    /// <summary>
    /// Empty fitness function — no clauses, no genomes, default Galapagos. Used as the
    /// initial state before the optimization sub-agent has processed the decomposition.
    /// </summary>
    public static FitnessFunction Empty { get; } = new(
        System.Array.Empty<FitnessClause>(),
        System.Array.Empty<Genome>(),
        Solver.Galapagos);

    /// <summary>
    /// True when exactly one scoring clause is present — a single-objective optimization.
    /// </summary>
    public bool IsSingleObjective => Clauses.Count == 1;

    /// <summary>
    /// True when two or more scoring clauses are present — a multi-objective optimization.
    /// </summary>
    public bool IsMultiObjective => Clauses.Count >= 2;

    /// <summary>
    /// Solver recommended purely by clause count (Galapagos=1, Octopus=2-3, Wallacei=4+).
    /// Actual <see cref="ChosenSolver"/> may differ (user override or plugin availability).
    /// </summary>
    /// <param name="clauseCount">Number of scoring clauses in the fitness function.</param>
    public static Solver RecommendSolver(int clauseCount) =>
        clauseCount <= 1 ? Solver.Galapagos : clauseCount <= 3 ? Solver.Octopus : Solver.Wallacei;
}
