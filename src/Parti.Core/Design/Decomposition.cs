// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.
// Decomposition record — v0.7.5 three-pillar gate (Formalization / Abstraction / Feedback).
// A Decomposition is the LLM's structured answer to "what is this design problem?" before
// any Grasshopper component is placed. If the three pillars can't be articulated, Parti
// asks a clarifying question instead of drawing.

using System.Collections.Generic;

namespace Parti.Core.Design;

/// <summary>
/// Direction of an evaluation clause — whether we want the metric to go up, down, or hold
/// steady. Matches the fitness-function vocabulary used in v0.8.
/// </summary>
public enum EvalDirection
{
    Maximize,
    Minimize,
    Keep
}

/// <summary>
/// How an evaluation's <see cref="Evaluation.Target"/> bounds the metric. Serialized to the model
/// as <c>"&lt;="</c>, <c>"&gt;="</c> and <c>"=="</c>.
/// </summary>
public enum EvalComparator
{
    /// <summary><c>&lt;=</c>: the metric must be at most the target (an upper bound, e.g. "area under 5 m2").</summary>
    AtMost,
    /// <summary><c>&gt;=</c>: the metric must be at least the target (a lower bound).</summary>
    AtLeast,
    /// <summary><c>==</c>: the metric must equal the target, within a practical tolerance.</summary>
    Equal,
}

/// <summary>
/// Where a decomposition item came from: stated by the user, or assumed by the model. Plan
/// review flags assumed items so the human can confirm them before approving a write.
/// </summary>
public enum PillarSource
{
    /// <summary>The user said this (default).</summary>
    UserStated,
    /// <summary>The model inferred this; shown flagged in plan review.</summary>
    Assumed,
}

/// <summary>
/// A named, domain-bounded input to the design problem. Corresponds to a Number Slider,
/// Gene Pool, or other parameter source in Grasshopper.
/// </summary>
/// <param name="Name">Human-readable input name, e.g. "panel depth" or "sun angle".</param>
/// <param name="Domain">
/// Free-form domain description. May be numeric ("0.1..0.4"), enum ("square|hex|triangle"),
/// or qualitative ("any positive real"). Parsed downstream by SubstrateSelector / Galapagos.
/// </param>
/// <param name="Unit">Optional unit label for numeric inputs (e.g. "m", "degrees", "mm").</param>
/// <param name="Source">Whether the user stated this input or the model assumed it.</param>
public sealed record Input(string Name, string Domain, string? Unit, PillarSource Source = PillarSource.UserStated);

/// <summary>
/// A rule the design must obey — the Formalization pillar. Rules are prose statements the
/// LLM commits to before drawing; they become constraints, not goals.
/// </summary>
/// <param name="Statement">
/// Single declarative sentence, e.g. "every panel must be anchored to a girt".
/// </param>
/// <param name="Source">Whether the user stated this rule or the model assumed it.</param>
public sealed record Rule(string Statement, PillarSource Source = PillarSource.UserStated);

/// <summary>
/// An evaluation clause — the Feedback pillar. Describes how to score a design instance.
/// In v0.8 these feed directly into FitnessFunction clauses wired to Galapagos / Octopus /
/// Wallacei.
/// </summary>
/// <param name="Metric">Name of the metric being evaluated, e.g. "total glare hours".</param>
/// <param name="Direction">Maximize / Minimize / Keep.</param>
/// <param name="Target">
/// Optional numeric target. Without a <paramref name="Comparator"/>: for Keep, the value the
/// metric should land on or near; for Maximize, the good-enough level the metric should reach
/// (at least the target); for Minimize, the good-enough level the metric should get down to
/// (at most the target). With a comparator, the target is a hard bound instead.
/// </param>
/// <param name="Source">Whether the user stated this evaluation or the model assumed it.</param>
/// <param name="Comparator">
/// Optional; valid only with a <paramref name="Target"/>. Turns the target into a hard bound the
/// design must satisfy: <see cref="EvalComparator.AtMost"/> (<c>&lt;=</c>),
/// <see cref="EvalComparator.AtLeast"/> (<c>&gt;=</c>) or <see cref="EvalComparator.Equal"/>
/// (<c>==</c>). "Keep the area under 5 m2" is Keep + <c>&lt;=</c> + 5.
/// </param>
public sealed record Evaluation(
    string Metric,
    EvalDirection Direction,
    double? Target,
    PillarSource Source = PillarSource.UserStated,
    EvalComparator? Comparator = null)
{
    /// <summary>The wire symbol of <paramref name="comparator"/>: <c>&lt;=</c>, <c>&gt;=</c> or <c>==</c>.</summary>
    public static string Symbol(EvalComparator comparator) => comparator switch
    {
        EvalComparator.AtMost => "<=",
        EvalComparator.AtLeast => ">=",
        _ => "==",
    };

    /// <summary>
    /// One-line human description for plan review, e.g. <c>keep section area &lt;= 5</c> or
    /// <c>minimize cost (target 100)</c>.
    /// </summary>
    public string Describe()
    {
        var text = Direction.ToString().ToLowerInvariant() + " " + Metric;
        if (Target is not { } target)
        {
            return text;
        }
        var value = target.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        return Comparator is { } comparator
            ? $"{text} {Symbol(comparator)} {value}"
            : $"{text} (target {value})";
    }
}

/// <summary>
/// A complete decomposition of a design problem into Parti's three pillars:
/// <list type="bullet">
///   <item><b>Formalization</b> — Inputs + Rules (what varies, what is fixed)</item>
///   <item><b>Abstraction</b> — Principle (the organizing idea behind the geometry)</item>
///   <item><b>Feedback</b> — Evaluations + FeedbackPlan (how we know it's working)</item>
/// </list>
/// A Decomposition is the precondition for generation. If <see cref="Principle"/> or
/// <see cref="FeedbackPlan"/> is null AND <see cref="Evaluations"/> is empty, the
/// three-pillar gate fails and Parti pauses with a clarifying question instead of drawing.
/// </summary>
/// <param name="Inputs">Formalization: the varying parameters.</param>
/// <param name="Rules">Formalization: the fixed constraints.</param>
/// <param name="Evaluations">Feedback: how to score an instance.</param>
/// <param name="Principle">
/// Abstraction: one-sentence organizing idea, e.g. "modular rainscreen with staggered
/// courses". Null when the LLM hasn't yet committed to a principle.
/// </param>
/// <param name="FeedbackPlan">
/// Feedback: how evaluations will actually be observed, e.g. "Ladybug sun hours" or
/// "manual review against reference photo". Null when unspecified.
/// </param>
public sealed record Decomposition(
    IReadOnlyList<Input> Inputs,
    IReadOnlyList<Rule> Rules,
    IReadOnlyList<Evaluation> Evaluations,
    string? Principle,
    string? FeedbackPlan)
{
    /// <summary>
    /// Empty decomposition — no pillars resolved. Used as the initial state before the
    /// sub-agent has processed the user prompt.
    /// </summary>
    public static Decomposition Empty { get; } = new(
        Inputs: System.Array.Empty<Input>(),
        Rules: System.Array.Empty<Rule>(),
        Evaluations: System.Array.Empty<Evaluation>(),
        Principle: null,
        FeedbackPlan: null);

    /// <summary>
    /// Formalization pillar satisfied: at least one Input OR Rule is present.
    /// </summary>
    public bool HasFormalization => Inputs.Count > 0 || Rules.Count > 0;

    /// <summary>
    /// Abstraction pillar satisfied: a principle has been stated.
    /// </summary>
    public bool HasAbstraction => !string.IsNullOrWhiteSpace(Principle);

    /// <summary>
    /// Feedback pillar satisfied: at least one Evaluation OR a FeedbackPlan is present.
    /// </summary>
    public bool HasFeedback => Evaluations.Count > 0 || !string.IsNullOrWhiteSpace(FeedbackPlan);

    /// <summary>
    /// True when all three pillars are satisfied and Parti is allowed to proceed from
    /// decomposition to generation.
    /// </summary>
    public bool IsComplete => HasFormalization && HasAbstraction && HasFeedback;

    /// <summary>
    /// The pillars that are still missing, in a stable order. Used by the clarification
    /// sub-agent to phrase a focused question.
    /// </summary>
    public IReadOnlyList<string> MissingPillars
    {
        get
        {
            var missing = new List<string>(3);
            if (!HasFormalization) missing.Add("Formalization");
            if (!HasAbstraction) missing.Add("Abstraction");
            if (!HasFeedback) missing.Add("Feedback");
            return missing;
        }
    }
}
