// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Globalization;
using System.Text;
using Parti.Core.Design;
using Parti.Core.Sessions;

namespace Parti.Components.Chat;

/// <summary>
/// Plain-text rendering of plans and decompositions for the plan dialog and the decomposition
/// card. No Eto types, so it is testable without a UI.
/// </summary>
public static class PlanText
{
    /// <summary>Marker appended to items the model assumed rather than the user stated.</summary>
    public const string AssumedMarker = "[assumed]";

    /// <summary>Shown in the "Why" block when the plan carries no recorded decomposition.</summary>
    public const string NoDecompositionText = "No design brief was recorded for this change.";

    /// <summary>
    /// The numbered call list, each call's catalog label when it resolved one, the reviewed binding,
    /// then the "Why" block.
    /// </summary>
    public static string Build(ToolCallPlan plan)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        var sb = new StringBuilder();
        for (int i = 0; i < plan.Calls.Count; i++)
        {
            var call = plan.Calls[i];
            sb.Append(i + 1).Append(". ").Append(call.Name);
            if (!call.IsReversible)
            {
                sb.Append("  [read-only]");
            }
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(call.ResolvedLabel))
            {
                sb.Append("   component: ").Append(call.ResolvedLabel).AppendLine();
            }
            sb.Append("   ").Append(string.IsNullOrEmpty(call.RenderedArguments) ? "{}" : call.RenderedArguments);
            if (i < plan.Calls.Count - 1)
            {
                sb.AppendLine().AppendLine();
            }
        }

        if (!string.IsNullOrWhiteSpace(plan.BindingId))
        {
            sb.AppendLine().Append("Reviewed binding: ").Append(plan.BindingId);
        }

        sb.AppendLine().AppendLine().Append(BuildWhy(plan.Decomposition, plan.DecompositionRevision));
        return sb.ToString();
    }

    /// <summary>
    /// The "Why" block: the recorded decomposition behind a plan, with assumed items flagged.
    /// </summary>
    public static string BuildWhy(Decomposition? decomposition, int revision)
    {
        var sb = new StringBuilder("Why");
        if (decomposition is null || IsEmpty(decomposition))
        {
            return sb.Append(": ").Append(NoDecompositionText).ToString();
        }

        sb.Append(" (design brief, revision ").Append(revision.ToString(CultureInfo.InvariantCulture)).Append(')')
            .AppendLine()
            .Append(FormatDecomposition(decomposition));
        if (HasAssumed(decomposition))
        {
            sb.AppendLine().Append("Items marked ").Append(AssumedMarker)
                .Append(" were assumed by the model; confirm them before approving.");
        }
        return sb.ToString();
    }

    /// <summary>The decomposition's pillars as indented lines, assumed items flagged.</summary>
    public static string FormatDecomposition(Decomposition decomposition)
    {
        if (decomposition is null)
        {
            throw new ArgumentNullException(nameof(decomposition));
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(decomposition.Principle))
        {
            sb.Append("  Principle: ").Append(decomposition.Principle!.Trim()).AppendLine();
        }

        if (decomposition.Inputs.Count > 0)
        {
            sb.AppendLine("  Inputs:");
            foreach (var input in decomposition.Inputs)
            {
                sb.Append("    - ").Append(input.Name).Append(": ").Append(input.Domain);
                if (!string.IsNullOrWhiteSpace(input.Unit) && !EndsWithUnit(input.Domain, input.Unit!))
                {
                    sb.Append(' ').Append(input.Unit);
                }
                AppendSource(sb, input.Source);
            }
        }

        if (decomposition.Rules.Count > 0)
        {
            sb.AppendLine("  Rules:");
            foreach (var rule in decomposition.Rules)
            {
                sb.Append("    - ").Append(rule.Statement);
                AppendSource(sb, rule.Source);
            }
        }

        if (decomposition.Evaluations.Count > 0)
        {
            sb.AppendLine("  Evaluations:");
            foreach (var evaluation in decomposition.Evaluations)
            {
                sb.Append("    - ").Append(evaluation.Describe());
                AppendSource(sb, evaluation.Source);
            }
        }

        if (!string.IsNullOrWhiteSpace(decomposition.FeedbackPlan))
        {
            sb.Append("  Feedback: ").Append(decomposition.FeedbackPlan!.Trim()).AppendLine();
        }

        // Same check the host enforces before any canvas write (includes the user-stated rule).
        var gate = PillarGate.Evaluate(decomposition);
        if (gate.Outcome != PillarGateOutcome.Pass)
        {
            sb.Append("  Missing: ").Append(string.Join(", ", gate.MissingPillars)).AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// True when <paramref name="domain"/> already ends with <paramref name="unit"/> as its own
    /// token ("1 to 5 m" with "m", "0-10mm" with "mm"), so the card does not print the unit twice.
    /// "1 to 5 mm" with "m" is not a match.
    /// </summary>
    private static bool EndsWithUnit(string? domain, string unit)
    {
        var trimmedDomain = domain?.TrimEnd() ?? string.Empty;
        var trimmedUnit = unit.Trim();
        if (trimmedUnit.Length == 0 || !trimmedDomain.EndsWith(trimmedUnit, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var before = trimmedDomain.Length - trimmedUnit.Length - 1;
        return before < 0 || !char.IsLetter(trimmedDomain[before]);
    }

    private static void AppendSource(StringBuilder sb, PillarSource source)
    {
        if (source == PillarSource.Assumed)
        {
            sb.Append(' ').Append(AssumedMarker);
        }
        sb.AppendLine();
    }

    private static bool IsEmpty(Decomposition decomposition) =>
        !decomposition.HasFormalization && !decomposition.HasAbstraction && !decomposition.HasFeedback;

    private static bool HasAssumed(Decomposition decomposition)
    {
        foreach (var input in decomposition.Inputs)
        {
            if (input.Source == PillarSource.Assumed) return true;
        }
        foreach (var rule in decomposition.Rules)
        {
            if (rule.Source == PillarSource.Assumed) return true;
        }
        foreach (var evaluation in decomposition.Evaluations)
        {
            if (evaluation.Source == PillarSource.Assumed) return true;
        }
        return false;
    }
}
