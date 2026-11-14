// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using Parti.Core.Design;
using Parti.Core.Sessions;
using Xunit;

namespace Parti.Core.Tests.Design;

public class PillarGateTests
{
    [Fact]
    public void Empty_ProducesAskUser_WithAllThreeQuestions()
    {
        var r = PillarGate.Evaluate(Decomposition.Empty);
        Assert.Equal(PillarGateOutcome.AskUser, r.Outcome);
        Assert.Equal(3, r.MissingPillars.Count);
        Assert.Equal(3, r.SuggestedQuestions.Count);
        Assert.Contains(r.SuggestedQuestions, q => q.Id == "pillar.formalization");
        Assert.Contains(r.SuggestedQuestions, q => q.Id == "pillar.abstraction");
        Assert.Contains(r.SuggestedQuestions, q => q.Id == "pillar.feedback");
    }

    [Fact]
    public void Complete_ProducesPass_WithNoQuestions()
    {
        var d = new Decomposition(
            Inputs: new[] { new Input("panel depth", "0.1..0.4", "m") },
            Rules: new[] { new Rule("panels anchored to girts") },
            Evaluations: new[] { new Evaluation("glare hours", EvalDirection.Minimize, null) },
            Principle: "modular rainscreen",
            FeedbackPlan: "Ladybug sun hours");
        var r = PillarGate.Evaluate(d);
        Assert.Equal(PillarGateOutcome.Pass, r.Outcome);
        Assert.Empty(r.MissingPillars);
        Assert.Empty(r.SuggestedQuestions);
    }

    [Fact]
    public void AllAssumed_OtherwiseComplete_AsksForAUserStatedItem()
    {
        // ROADMAP non-goal: no canvas edit without the user having stated inputs, rules, or evaluations.
        var d = new Decomposition(
            Inputs: new[] { new Input("value", "0 to 100", null, PillarSource.Assumed) },
            Rules: new[] { new Rule("slider sits on the canvas", PillarSource.Assumed) },
            Evaluations: new[] { new Evaluation("slider placement", EvalDirection.Keep, null, PillarSource.Assumed) },
            Principle: "provide a numeric input",
            FeedbackPlan: null);
        var r = PillarGate.Evaluate(d);
        Assert.Equal(PillarGateOutcome.AskUser, r.Outcome);
        Assert.Equal(new[] { PillarGate.UserStatedPillar }, r.MissingPillars);
        Assert.Equal("pillar.user_stated", Assert.Single(r.SuggestedQuestions).Id);
    }

    [Fact]
    public void OneUserStatedItem_AnyPillar_Passes()
    {
        var d = new Decomposition(
            Inputs: new[] { new Input("radius", "1..5", "m", PillarSource.Assumed) },
            Rules: new[] { new Rule("circle at the origin", PillarSource.Assumed) },
            Evaluations: new[] { new Evaluation("circumference", EvalDirection.Keep, 30, PillarSource.UserStated, EvalComparator.AtMost) },
            Principle: "plaza sized by one radius",
            FeedbackPlan: null);
        Assert.Equal(PillarGateOutcome.Pass, PillarGate.Evaluate(d).Outcome);
    }

    [Fact]
    public void BlankUserStatedItem_DoesNotCount()
    {
        var d = new Decomposition(
            Inputs: new[] { new Input("radius", "1..5", "m", PillarSource.Assumed), new Input("  ", "x", null, PillarSource.UserStated) },
            Rules: System.Array.Empty<Rule>(),
            Evaluations: new[] { new Evaluation("area", EvalDirection.Minimize, null, PillarSource.Assumed) },
            Principle: "plaza",
            FeedbackPlan: null);
        Assert.Equal(new[] { PillarGate.UserStatedPillar }, PillarGate.Evaluate(d).MissingPillars);
    }

    [Fact]
    public void MissingOnlyFeedback_AsksSingleQuestion()
    {
        var d = new Decomposition(
            Inputs: new[] { new Input("panel depth", "0.1..0.4", "m") },
            Rules: System.Array.Empty<Rule>(),
            Evaluations: System.Array.Empty<Evaluation>(),
            Principle: "modular rainscreen",
            FeedbackPlan: null);
        var r = PillarGate.Evaluate(d);
        Assert.Equal(PillarGateOutcome.AskUser, r.Outcome);
        Assert.Single(r.MissingPillars);
        Assert.Equal("Feedback", r.MissingPillars[0]);
        Assert.Single(r.SuggestedQuestions);
        Assert.Equal("pillar.feedback", r.SuggestedQuestions[0].Id);
    }

    [Fact]
    public void BlankContent_DoesNotSatisfyAnyPillar()
    {
        // A13: structure alone does not pass; every pillar needs non-blank content, and a
        // feedback plan without an evaluation metric is not Feedback.
        var d = new Decomposition(
            Inputs: new[] { new Input(" ", "0.1..0.4", "m"), new Input("depth", "", null) },
            Rules: new[] { new Rule("   ") },
            Evaluations: new[] { new Evaluation(" ", EvalDirection.Minimize, null) },
            Principle: "  ",
            FeedbackPlan: "Ladybug sun hours");
        var r = PillarGate.Evaluate(d);
        Assert.Equal(PillarGateOutcome.AskUser, r.Outcome);
        Assert.Equal(new[] { "Formalization", "Abstraction", "Feedback" }, r.MissingPillars);
    }

    [Fact]
    public void RuleWithoutPrinciple_SatisfiesAbstraction()
    {
        var d = new Decomposition(
            Inputs: new[] { new Input("panel depth", "0.1..0.4", "m") },
            Rules: new[] { new Rule("panels anchored to girts") },
            Evaluations: new[] { new Evaluation("glare hours", EvalDirection.Minimize, null) },
            Principle: null,
            FeedbackPlan: null);
        Assert.Equal(PillarGateOutcome.Pass, PillarGate.Evaluate(d).Outcome);
    }

    [Fact]
    public void DefaultQuestions_AreTextKind()
    {
        var r = PillarGate.Evaluate(Decomposition.Empty);
        foreach (var q in r.SuggestedQuestions)
        {
            Assert.Equal(QuestionKind.Text, q.Kind);
            Assert.False(string.IsNullOrWhiteSpace(q.Prompt));
        }
    }
}

public class DecompositionStateTests
{
    [Fact]
    public void InitialState_IsEmpty_NotComplete()
    {
        var state = new DecompositionState();
        Assert.Same(Decomposition.Empty, state.Current);
        Assert.False(state.IsComplete);
    }

    // Update/Reset are internal and exercised via ConversationSession integration tests.
}
