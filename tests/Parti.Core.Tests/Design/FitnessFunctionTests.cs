// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using Parti.Core.Design;
using Xunit;

namespace Parti.Core.Tests.Design;

public class FitnessFunctionTests
{
    [Fact]
    public void Empty_HasNoClausesNoGenomesDefaultGalapagos()
    {
        var f = FitnessFunction.Empty;
        Assert.Empty(f.Clauses);
        Assert.Empty(f.Genomes);
        Assert.Equal(Solver.Galapagos, f.ChosenSolver);
        Assert.False(f.IsSingleObjective);
        Assert.False(f.IsMultiObjective);
    }

    [Fact]
    public void IsSingleObjective_TrueForOneClause()
    {
        var f = new FitnessFunction(
            Clauses: new[] { new FitnessClause("glare hours", EvalDirection.Minimize) },
            Genomes: System.Array.Empty<Genome>(),
            ChosenSolver: Solver.Galapagos);
        Assert.True(f.IsSingleObjective);
        Assert.False(f.IsMultiObjective);
    }

    [Fact]
    public void IsMultiObjective_TrueForTwoOrMoreClauses()
    {
        var f = new FitnessFunction(
            Clauses: new[]
            {
                new FitnessClause("glare hours", EvalDirection.Minimize),
                new FitnessClause("daylight autonomy", EvalDirection.Maximize)
            },
            Genomes: System.Array.Empty<Genome>(),
            ChosenSolver: Solver.Octopus);
        Assert.False(f.IsSingleObjective);
        Assert.True(f.IsMultiObjective);
    }

    [Fact]
    public void RecommendSolver_ZeroOrOneClauseGivesGalapagos()
    {
        Assert.Equal(Solver.Galapagos, FitnessFunction.RecommendSolver(0));
        Assert.Equal(Solver.Galapagos, FitnessFunction.RecommendSolver(1));
    }

    [Fact]
    public void RecommendSolver_TwoOrThreeClausesGivesOctopus()
    {
        Assert.Equal(Solver.Octopus, FitnessFunction.RecommendSolver(2));
        Assert.Equal(Solver.Octopus, FitnessFunction.RecommendSolver(3));
    }

    [Fact]
    public void RecommendSolver_FourOrMoreClausesGivesWallacei()
    {
        Assert.Equal(Solver.Wallacei, FitnessFunction.RecommendSolver(4));
        Assert.Equal(Solver.Wallacei, FitnessFunction.RecommendSolver(10));
    }

    [Fact]
    public void Solver_AllThreeValuesExist()
    {
        Assert.Equal(0, (int)Solver.Galapagos);
        Assert.Equal(1, (int)Solver.Octopus);
        Assert.Equal(2, (int)Solver.Wallacei);
    }

    [Fact]
    public void FitnessFunction_RecordEqualityByValue()
    {
        var clauses = new[] { new FitnessClause("mass", EvalDirection.Minimize, 100.0, 2.0) };
        var genomes = new[] { new Genome("panel depth", 0.1, 0.4, 0.05) };
        var a = new FitnessFunction(clauses, genomes, Solver.Galapagos);
        var b = new FitnessFunction(clauses, genomes, Solver.Galapagos);
        Assert.Equal(a, b);
        Assert.Equal(
            new FitnessClause("mass", EvalDirection.Minimize, 100.0, 2.0),
            new FitnessClause("mass", EvalDirection.Minimize, 100.0, 2.0));
        Assert.Equal(
            new Genome("panel depth", 0.1, 0.4, 0.05),
            new Genome("panel depth", 0.1, 0.4, 0.05));
    }

    [Fact]
    public void FitnessClause_DefaultWeightIsOne()
    {
        var c = new FitnessClause("glare", EvalDirection.Minimize);
        Assert.Equal(1.0, c.Weight);
        Assert.Null(c.Target);
    }

    [Fact]
    public void Genome_DefaultStepIsNull()
    {
        var g = new Genome("depth", 0.1, 0.4);
        Assert.Null(g.Step);
    }
}
