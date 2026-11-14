// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.
// Phenotype record — v0.8 optimization layer. A Phenotype is one concrete design instance
// produced by the solver: its rank in the population, the metric scores it achieved, the
// genome values that produced it, and optionally a viewport screenshot for review.

using System.Collections.Generic;

namespace Parti.Core.Design;

/// <summary>
/// A single design instance evaluated by the solver. One row in the optimization population.
/// </summary>
/// <param name="Rank">
/// Rank of this phenotype within its population (0 = best). For multi-objective runs this
/// is the Pareto rank; for single-objective runs it is the score-ordered position.
/// </param>
/// <param name="MetricValues">
/// Metric name to observed score, one entry per <see cref="FitnessClause"/>.
/// </param>
/// <param name="GenomeValues">
/// Slider name to sampled value, one entry per <see cref="Genome"/>.
/// </param>
/// <param name="ViewportScreenshotPath">
/// Optional absolute path to a captured Rhino viewport image of this instance. Null when
/// no screenshot has been taken (headless evaluation or pre-capture state).
/// </param>
public sealed record Phenotype(
    int Rank,
    IReadOnlyDictionary<string, double> MetricValues,
    IReadOnlyDictionary<string, double> GenomeValues,
    string? ViewportScreenshotPath = null);
