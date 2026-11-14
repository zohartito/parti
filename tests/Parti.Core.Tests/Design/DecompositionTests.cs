// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using Parti.Core.Design;
using Xunit;

namespace Parti.Core.Tests.Design;

public class DecompositionTests
{
    [Fact]
    public void Empty_HasNoPillarsSatisfied()
    {
        var d = Decomposition.Empty;
        Assert.False(d.HasFormalization);
        Assert.False(d.HasAbstraction);
        Assert.False(d.HasFeedback);
        Assert.False(d.IsComplete);
        Assert.Equal(new[] { "Formalization", "Abstraction", "Feedback" }, d.MissingPillars);
    }

    [Fact]
    public void Formalization_SatisfiedByInputAlone()
    {
        var d = new Decomposition(
            Inputs: new[] { new Input("panel depth", "0.1..0.4", "m") },
            Rules: System.Array.Empty<Rule>(),
            Evaluations: System.Array.Empty<Evaluation>(),
            Principle: null,
            FeedbackPlan: null);
        Assert.True(d.HasFormalization);
        Assert.False(d.HasAbstraction);
        Assert.False(d.HasFeedback);
    }

    [Fact]
    public void Formalization_SatisfiedByRuleAlone()
    {
        var d = new Decomposition(
            Inputs: System.Array.Empty<Input>(),
            Rules: new[] { new Rule("every panel must be anchored to a girt") },
            Evaluations: System.Array.Empty<Evaluation>(),
            Principle: null,
            FeedbackPlan: null);
        Assert.True(d.HasFormalization);
    }

    [Fact]
    public void Abstraction_RequiresNonEmptyPrinciple()
    {
        var d = new Decomposition(
            Inputs: System.Array.Empty<Input>(),
            Rules: System.Array.Empty<Rule>(),
            Evaluations: System.Array.Empty<Evaluation>(),
            Principle: "modular rainscreen with staggered courses",
            FeedbackPlan: null);
        Assert.True(d.HasAbstraction);
    }

    [Fact]
    public void Abstraction_WhitespacePrincipleDoesNotSatisfy()
    {
        var d = new Decomposition(
            Inputs: System.Array.Empty<Input>(),
            Rules: System.Array.Empty<Rule>(),
            Evaluations: System.Array.Empty<Evaluation>(),
            Principle: "   ",
            FeedbackPlan: null);
        Assert.False(d.HasAbstraction);
    }

    [Fact]
    public void Feedback_SatisfiedByEvaluationAlone()
    {
        var d = new Decomposition(
            Inputs: System.Array.Empty<Input>(),
            Rules: System.Array.Empty<Rule>(),
            Evaluations: new[] { new Evaluation("glare hours", EvalDirection.Minimize, null) },
            Principle: null,
            FeedbackPlan: null);
        Assert.True(d.HasFeedback);
    }

    [Fact]
    public void Feedback_SatisfiedByFeedbackPlanAlone()
    {
        var d = new Decomposition(
            Inputs: System.Array.Empty<Input>(),
            Rules: System.Array.Empty<Rule>(),
            Evaluations: System.Array.Empty<Evaluation>(),
            Principle: null,
            FeedbackPlan: "Ladybug sun hours, annual");
        Assert.True(d.HasFeedback);
    }

    [Fact]
    public void Complete_RequiresAllThreePillars()
    {
        var d = new Decomposition(
            Inputs: new[] { new Input("panel depth", "0.1..0.4", "m") },
            Rules: new[] { new Rule("panels anchored to girts") },
            Evaluations: new[] { new Evaluation("glare hours", EvalDirection.Minimize, null) },
            Principle: "modular rainscreen with staggered courses",
            FeedbackPlan: "Ladybug sun hours");
        Assert.True(d.IsComplete);
        Assert.Empty(d.MissingPillars);
    }

    [Fact]
    public void MissingPillars_ReturnedInStableOrder()
    {
        var d = new Decomposition(
            Inputs: System.Array.Empty<Input>(),
            Rules: System.Array.Empty<Rule>(),
            Evaluations: new[] { new Evaluation("glare hours", EvalDirection.Minimize, null) },
            Principle: null,
            FeedbackPlan: null);
        Assert.Equal(new[] { "Formalization", "Abstraction" }, d.MissingPillars);
    }

    [Fact]
    public void EvalDirection_AllThreeValuesExist()
    {
        Assert.Equal(0, (int)EvalDirection.Maximize);
        Assert.Equal(1, (int)EvalDirection.Minimize);
        Assert.Equal(2, (int)EvalDirection.Keep);
    }

    [Fact]
    public void Items_DefaultToUserStatedSource()
    {
        Assert.Equal(PillarSource.UserStated, new Input("depth", "0..1", null).Source);
        Assert.Equal(PillarSource.UserStated, new Rule("fixed").Source);
        Assert.Equal(PillarSource.UserStated, new Evaluation("gain", EvalDirection.Minimize, null).Source);
        Assert.Equal(PillarSource.Assumed, new Rule("fixed", PillarSource.Assumed).Source);
    }
}
