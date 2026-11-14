// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using System.Linq;
using Parti.Core.Prompts;
using Xunit;

namespace Parti.Core.Tests.Prompts;

public class SkillRouterTests
{
    [Theory]
    [InlineData("what parameters should I vary for this facade?", "parti-formalize")]
    [InlineData("set up a Galapagos run for daylight",            "parti-optimize")]
    [InlineData("add a number slider from 0 to 10",               "parti-ghcanvas")]
    [InlineData("what's the organizing principle here?",          "parti-abstract")]
    [InlineData("how do we know the design is any good?",         "parti-feedback")]
    [InlineData("can we CNC the parts from plywood?",             "parti-fabricate")]
    [InlineData("I want to maximize the afternoon sun on the east wall", "parti-feedback")]
    [InlineData("connect this slider to that component",          "parti-ghcanvas")]
    [InlineData("let's nest these on a 4x8 sheet",                "parti-fabricate")]
    [InlineData("run Octopus with three objectives",              "parti-optimize")]
    [InlineData("hello Parti",                                    "parti-formalize")] // fallback
    public void Route_ReturnsExpectedPrimarySubSkill(string query, string expected)
    {
        var result = SkillRouter.Route(query);
        Assert.NotEmpty(result.SubSkills);
        Assert.Equal(expected, result.SubSkills[0]);
    }

    [Fact]
    public void Route_Fallback_MarksEvidenceAsFallback()
    {
        var result = SkillRouter.Route("hello Parti");
        Assert.Single(result.SubSkills);
        Assert.Equal("parti-formalize", result.SubSkills[0]);
        Assert.True(result.Evidence.ContainsKey("parti-formalize"));
        Assert.Contains("(fallback - no keyword match)", result.Evidence["parti-formalize"]);
    }

    [Fact]
    public void Route_NullOrWhitespace_FallsBackToFormalize()
    {
        var nullResult = SkillRouter.Route(null!);
        var emptyResult = SkillRouter.Route("");
        var wsResult = SkillRouter.Route("   ");

        Assert.Equal("parti-formalize", nullResult.SubSkills[0]);
        Assert.Equal("parti-formalize", emptyResult.SubSkills[0]);
        Assert.Equal("parti-formalize", wsResult.SubSkills[0]);
    }

    [Fact]
    public void Route_MultipleSubSkills_ReturnsBothOrderedByKeywordCount()
    {
        // "add a slider" → two ghcanvas keywords ("add", "slider");
        // "maximize daylight" → one feedback keyword ("maximize").
        var result = SkillRouter.Route("add a slider and maximize daylight");
        Assert.Contains("parti-ghcanvas", result.SubSkills);
        Assert.Contains("parti-feedback", result.SubSkills);
        Assert.Equal("parti-ghcanvas", result.SubSkills[0]); // more keyword hits wins
    }

    [Fact]
    public void Route_Evidence_ListsMatchedKeywords()
    {
        var result = SkillRouter.Route("add a slider");
        Assert.True(result.Evidence.ContainsKey("parti-ghcanvas"));
        var terms = result.Evidence["parti-ghcanvas"];
        Assert.Contains("add", terms);
        Assert.Contains("slider", terms);
    }

    [Fact]
    public void Route_IsCaseInsensitive()
    {
        var lower = SkillRouter.Route("galapagos");
        var upper = SkillRouter.Route("GALAPAGOS");
        var mixed = SkillRouter.Route("GalApaGos");

        Assert.Equal("parti-optimize", lower.SubSkills[0]);
        Assert.Equal("parti-optimize", upper.SubSkills[0]);
        Assert.Equal("parti-optimize", mixed.SubSkills[0]);
    }

    [Fact]
    public void KeywordMap_IsNonEmpty_AndCoversAllSubSkills()
    {
        Assert.NotEmpty(SkillRouter.KeywordMap);

        var subSkills = SkillRouter.KeywordMap.Values.Distinct().ToList();
        Assert.Contains("parti-formalize", subSkills);
        Assert.Contains("parti-abstract", subSkills);
        Assert.Contains("parti-feedback", subSkills);
        Assert.Contains("parti-optimize", subSkills);
        Assert.Contains("parti-ghcanvas", subSkills);
        Assert.Contains("parti-fabricate", subSkills);
    }

    [Fact]
    public void Route_TiedKeywordCounts_BreaksByFirstMatchPosition()
    {
        // "maximize" (feedback, pos 0) vs "slider" (ghcanvas, pos later).
        // Each hits exactly one keyword in its sub-skill, so feedback wins by first-match.
        var result = SkillRouter.Route("maximize something, then add a slider");
        // ghcanvas has two hits ("add", "slider"), so it actually wins — verify count-first ordering.
        Assert.Equal("parti-ghcanvas", result.SubSkills[0]);

        // Now a real tie: one keyword each, earlier wins.
        var tie = SkillRouter.Route("maximize then place");
        Assert.Equal("parti-feedback", tie.SubSkills[0]);
    }
}
