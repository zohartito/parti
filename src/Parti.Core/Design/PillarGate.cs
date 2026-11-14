// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;
using System.Linq;
using Parti.Core.Sessions;

namespace Parti.Core.Design;

/// <summary>
/// Outcome of a <see cref="PillarGate"/> evaluation.
/// </summary>
public enum PillarGateOutcome
{
    /// <summary>All three pillars satisfied — generation may proceed.</summary>
    Pass,
    /// <summary>At least one pillar missing — surface a clarifying question to the user.</summary>
    AskUser,
}

/// <summary>
/// Result record for <see cref="PillarGate.Evaluate"/>.
/// </summary>
/// <param name="Outcome">Pass or AskUser.</param>
/// <param name="MissingPillars">Empty when Outcome == Pass.</param>
/// <param name="SuggestedQuestions">
/// When Outcome == AskUser, a set of <see cref="Question"/>s the clarification sub-agent
/// would surface. Empty otherwise.
/// </param>
public sealed record PillarGateResult(
    PillarGateOutcome Outcome,
    IReadOnlyList<string> MissingPillars,
    IReadOnlyList<Question> SuggestedQuestions);

/// <summary>
/// Pure, stateless check: given a <see cref="Decomposition"/>, decide whether generation
/// may proceed or a clarifying question must be asked first. The gate generates default
/// questions for each missing pillar; the clarifier sub-agent may override them with more
/// contextual phrasings.
/// </summary>
public static class PillarGate
{
    /// <summary>
    /// Evaluate the three-pillar gate against <paramref name="decomposition"/>. The gate checks
    /// content, not just structure: a pillar counts only when it carries non-blank text.
    /// <list type="bullet">
    ///   <item><b>Formalization</b>: at least one input with a non-blank name and domain.</item>
    ///   <item><b>Abstraction</b>: a non-blank principle, or at least one non-blank rule.</item>
    ///   <item><b>Feedback</b>: at least one evaluation with a non-blank metric.</item>
    /// </list>
    /// A structurally complete but empty record therefore does not pass. When all three pillars are
    /// present, at least one input, rule, or evaluation must also be <see cref="PillarSource.UserStated"/>:
    /// a decomposition made only of the model's assumptions does not let Parti write to the canvas
    /// (ROADMAP non-goal: no slider edit without the user having stated inputs, rules, or evaluations).
    /// </summary>
    public static PillarGateResult Evaluate(Decomposition decomposition)
    {
        if (decomposition is null)
        {
            throw new System.ArgumentNullException(nameof(decomposition));
        }

        var missing = MissingPillars(decomposition);
        if (missing.Count == 0)
        {
            return new PillarGateResult(
                PillarGateOutcome.Pass,
                System.Array.Empty<string>(),
                System.Array.Empty<Question>());
        }

        var questions = new List<Question>();
        foreach (var pillar in missing)
        {
            questions.Add(DefaultQuestionFor(pillar));
        }

        return new PillarGateResult(
            PillarGateOutcome.AskUser,
            missing,
            questions);
    }

    private static IReadOnlyList<string> MissingPillars(Decomposition d)
    {
        var missing = new List<string>(3);
        if (!d.Inputs.Any(i => i is not null && !string.IsNullOrWhiteSpace(i.Name) && !string.IsNullOrWhiteSpace(i.Domain)))
        {
            missing.Add("Formalization");
        }
        if (string.IsNullOrWhiteSpace(d.Principle) && !d.Rules.Any(r => r is not null && !string.IsNullOrWhiteSpace(r.Statement)))
        {
            missing.Add("Abstraction");
        }
        if (!d.Evaluations.Any(e => e is not null && !string.IsNullOrWhiteSpace(e.Metric)))
        {
            missing.Add("Feedback");
        }
        // Checked only once the three pillars are present, so an empty record still asks the three
        // pillar questions rather than a fourth one.
        if (missing.Count == 0 && !HasUserStatedItem(d))
        {
            missing.Add(UserStatedPillar);
        }
        return missing;
    }

    /// <summary>
    /// Name reported in <see cref="PillarGateResult.MissingPillars"/> when every item of an otherwise
    /// complete decomposition is <see cref="PillarSource.Assumed"/>.
    /// </summary>
    public const string UserStatedPillar = "UserStated";

    private static bool HasUserStatedItem(Decomposition d) =>
        d.Inputs.Any(i => i is not null && i.Source == PillarSource.UserStated && !string.IsNullOrWhiteSpace(i.Name))
        || d.Rules.Any(r => r is not null && r.Source == PillarSource.UserStated && !string.IsNullOrWhiteSpace(r.Statement))
        || d.Evaluations.Any(e => e is not null && e.Source == PillarSource.UserStated && !string.IsNullOrWhiteSpace(e.Metric));

    /// <summary>
    /// Default, pillar-agnostic clarification phrasing. The clarifier sub-agent typically
    /// replaces these with more specific prompts based on the user's message, but these
    /// provide a safe fallback when the sub-agent is unavailable.
    /// </summary>
    internal static Question DefaultQuestionFor(string pillar) => pillar switch
    {
        "Formalization" => new Question(
            Id: "pillar.formalization",
            Prompt: "What are the inputs that can vary, and what rules must stay fixed?",
            Kind: QuestionKind.Text),
        "Abstraction" => new Question(
            Id: "pillar.abstraction",
            Prompt: "What is the organizing principle behind this design? (one sentence)",
            Kind: QuestionKind.Text),
        "Feedback" => new Question(
            Id: "pillar.feedback",
            Prompt: "How will we know it's working? What would you measure or observe?",
            Kind: QuestionKind.Text),
        UserStatedPillar => new Question(
            Id: "pillar.user_stated",
            Prompt: "So far every input, rule, and evaluation is my assumption. In your own words: what should vary, and how will you judge the result?",
            Kind: QuestionKind.Text),
        _ => new Question(
            Id: $"pillar.{pillar.ToLowerInvariant()}",
            Prompt: $"Could you tell me more about the {pillar.ToLowerInvariant()} of this problem?",
            Kind: QuestionKind.Text),
    };
}
