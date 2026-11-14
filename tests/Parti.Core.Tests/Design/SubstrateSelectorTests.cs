// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using Parti.Core.Design;
using Xunit;

namespace Parti.Core.Tests.Design;

public class SubstrateSelectorTests
{
    [Fact]
    public void SpreadsheetKeyword_RoutesToSpreadsheet()
    {
        var r = SubstrateSelector.Select(
            Decomposition.Empty,
            "I have a CSV of panel orders, help me balance by supplier");
        Assert.Equal(Substrate.Spreadsheet, r.Recommended);
        Assert.Equal(Substrate.Python, r.Runnerup);
    }

    [Fact]
    public void MlKeyword_RoutesToMachineLearning()
    {
        var r = SubstrateSelector.Select(
            Decomposition.Empty,
            "I want to train a neural network on my dataset of tower proportions");
        Assert.Equal(Substrate.MachineLearning, r.Recommended);
    }

    [Fact]
    public void SketchKeyword_RoutesToPenAndPaper()
    {
        var r = SubstrateSelector.Select(
            Decomposition.Empty,
            "Let's sketch the pavilion on a napkin first");
        Assert.Equal(Substrate.PenAndPaper, r.Recommended);
    }

    [Fact]
    public void SimulationKeyword_RoutesToHoudini()
    {
        var r = SubstrateSelector.Select(
            Decomposition.Empty,
            "I need a fluid simulation across the facade");
        Assert.Equal(Substrate.Houdini, r.Recommended);
    }

    [Fact]
    public void GeometricFacadePrompt_DefaultsToGrasshopper()
    {
        var d = new Decomposition(
            Inputs: new[] { new Input("panel depth", "0.1..0.4", "m") },
            Rules: System.Array.Empty<Rule>(),
            Evaluations: System.Array.Empty<Evaluation>(),
            Principle: "staggered rainscreen facade",
            FeedbackPlan: null);
        var r = SubstrateSelector.Select(d, "design a facade with staggered panels");
        Assert.Equal(Substrate.Grasshopper, r.Recommended);
    }

    [Fact]
    public void NumericInputsNoGeometry_RoutesToSpreadsheet()
    {
        var d = new Decomposition(
            Inputs: new[]
            {
                new Input("monthly budget", "0..100000", "USD"),
                new Input("labor rate", "40..120", "USD/hr"),
            },
            Rules: System.Array.Empty<Rule>(),
            Evaluations: System.Array.Empty<Evaluation>(),
            Principle: "minimize total cost",
            FeedbackPlan: null);
        var r = SubstrateSelector.Select(d, "balance my project budget across trades");
        Assert.Equal(Substrate.Spreadsheet, r.Recommended);
    }

    [Fact]
    public void EmptyDecomposition_VaguePrompt_RoutesToPenAndPaper()
    {
        var r = SubstrateSelector.Select(Decomposition.Empty, "I have a vague idea");
        Assert.Equal(Substrate.PenAndPaper, r.Recommended);
    }

    [Fact]
    public void Rationale_AlwaysNonEmpty()
    {
        var cases = new[]
        {
            ("csv thing", Decomposition.Empty),
            ("train a model", Decomposition.Empty),
            ("facade", Decomposition.Empty),
            ("sketch it", Decomposition.Empty),
            ("simulation", Decomposition.Empty),
            ("nothing specific", Decomposition.Empty),
        };
        foreach (var (prompt, d) in cases)
        {
            var r = SubstrateSelector.Select(d, prompt);
            Assert.False(string.IsNullOrWhiteSpace(r.Rationale));
            Assert.NotEmpty(r.SignalsMatched);
        }
    }

    [Fact]
    public void NullPrompt_DoesNotThrow()
    {
        var r = SubstrateSelector.Select(Decomposition.Empty, null!);
        Assert.NotNull(r);
    }
}
