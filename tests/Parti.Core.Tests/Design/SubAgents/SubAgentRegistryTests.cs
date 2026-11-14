// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using System.Linq;
using Parti.Core.Design.SubAgents;
using Xunit;

namespace Parti.Core.Tests.Design.SubAgents;

public class SubAgentRegistryTests
{
    [Fact]
    public void All_Contains_SixPersonas()
    {
        Assert.Equal(6, SubAgentRegistry.All.Count);
    }

    [Fact]
    public void All_HasUniqueNames()
    {
        var names = SubAgentRegistry.All.Select(a => a.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Theory]
    [InlineData("parti-formalize")]
    [InlineData("parti-abstract")]
    [InlineData("parti-feedback")]
    [InlineData("parti-substrate")]
    [InlineData("parti-clarify")]
    [InlineData("parti-optimize")]
    public void Get_ReturnsNamedPersona(string name)
    {
        var agent = SubAgentRegistry.Get(name);
        Assert.Equal(name, agent.Name);
        Assert.False(string.IsNullOrWhiteSpace(agent.Description));
        Assert.False(string.IsNullOrWhiteSpace(agent.SystemPrompt));
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        Assert.Equal("parti-formalize", SubAgentRegistry.Get("PARTI-FORMALIZE").Name);
    }

    [Fact]
    public void Get_ThrowsOnUnknownName()
    {
        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(
            () => SubAgentRegistry.Get("parti-zoom-and-enhance"));
    }

    [Fact]
    public void TryGet_ReturnsNullForUnknown()
    {
        Assert.Null(SubAgentRegistry.TryGet("parti-zoom-and-enhance"));
    }

    [Fact]
    public void ForPillar_Formalization_IncludesOnlyFormalize()
    {
        var agents = SubAgentRegistry.ForPillar("Formalization");
        Assert.Single(agents);
        Assert.Equal("parti-formalize", agents[0].Name);
    }

    [Fact]
    public void ForPillar_Abstraction_IncludesOnlyAbstract()
    {
        var agents = SubAgentRegistry.ForPillar("Abstraction");
        Assert.Single(agents);
        Assert.Equal("parti-abstract", agents[0].Name);
    }

    [Fact]
    public void ForPillar_Feedback_IncludesOnlyFeedback()
    {
        var agents = SubAgentRegistry.ForPillar("Feedback");
        Assert.Single(agents);
        Assert.Equal("parti-feedback", agents[0].Name);
    }

    [Fact]
    public void ForPillar_UnknownName_ReturnsEmpty()
    {
        Assert.Empty(SubAgentRegistry.ForPillar("BananaPillar"));
    }

    [Fact]
    public void SubstrateAndClarify_CoverNoPillars()
    {
        Assert.Empty(SubAgentRegistry.Get("parti-substrate").PillarsCovered);
        Assert.Empty(SubAgentRegistry.Get("parti-clarify").PillarsCovered);
    }

    [Fact]
    public void Optimize_CoversNoPillars()
    {
        Assert.Empty(SubAgentRegistry.Get("parti-optimize").PillarsCovered);
    }

    [Fact]
    public void AllPersonas_HaveOutputFormatInPrompt()
    {
        foreach (var agent in SubAgentRegistry.All)
        {
            Assert.Contains("OUTPUT FORMAT", agent.SystemPrompt);
        }
    }
}
