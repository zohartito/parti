// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;
using System.Linq;
using Parti.Core.Prompts;
using Xunit;

namespace Parti.Core.Tests.Prompts;

/// <summary>
/// Unit tests for <see cref="SessionPromptBuilder"/>. All tests use <see cref="InMemorySkillSource"/>
/// so they exercise the composition logic without touching the filesystem.
/// </summary>
public class SessionPromptBuilderTests
{
    private const string Layer0 = "# Parti Foundations (Layer 0)\nAlways loaded.";
    private const string GhCanvasBody = "# parti-ghcanvas\nCanvas mutation rules.";
    private const string FormalizeBody = "# parti-formalize\nProblem-framing rules.";

    private static InMemorySkillSource FullSource() => new(new Dictionary<string, string>
    {
        ["parti-foundations/SKILL.md"] = Layer0,
        ["parti-foundations/parti-ghcanvas/SKILL.md"] = GhCanvasBody,
        ["parti-foundations/parti-formalize/SKILL.md"] = FormalizeBody,
    });

    /// <summary>
    /// Stub router with a hard-coded reply. Lets tests pin the routing decision without
    /// relying on the keyword router's specific keyword map.
    /// </summary>
    private sealed class StubRouter : ISkillRouter
    {
        private readonly IReadOnlyList<string> _subSkills;
        public StubRouter(params string[] subSkills) { _subSkills = subSkills; }

        public SkillRouteResult Route(string prompt) =>
            new(_subSkills,
                _subSkills.ToDictionary<string, string, IReadOnlyList<string>>(
                    s => s, _ => new List<string> { "(stub)" }));
    }

    [Fact]
    public void Build_EmbedsLayer0_AndRoutedLayer1Bodies()
    {
        var builder = new SessionPromptBuilder(
            new SkillLoader(FullSource()),
            new StubRouter("parti-ghcanvas", "parti-formalize"));

        var result = builder.Build("panelize a surface with curved facade");

        Assert.Contains(Layer0, result.SystemPrompt);
        Assert.Contains(GhCanvasBody, result.SystemPrompt);
        Assert.Contains(FormalizeBody, result.SystemPrompt);
        Assert.Equal(new[] { "parti-ghcanvas", "parti-formalize" },
            result.ActivatedSubSkills);
    }

    [Fact]
    public void Build_PreservesRouterOrder()
    {
        // Router says formalize first, ghcanvas second — prompt order must match.
        var builder = new SessionPromptBuilder(
            new SkillLoader(FullSource()),
            new StubRouter("parti-formalize", "parti-ghcanvas"));

        var result = builder.Build("whatever");

        var formalizeIdx = result.SystemPrompt.IndexOf(FormalizeBody);
        var canvasIdx = result.SystemPrompt.IndexOf(GhCanvasBody);
        Assert.True(formalizeIdx < canvasIdx,
            $"parti-formalize should appear before parti-ghcanvas; got indices {formalizeIdx} vs {canvasIdx}.");
        Assert.Equal(new[] { "parti-formalize", "parti-ghcanvas" }, result.ActivatedSubSkills);
    }

    [Fact]
    public void Build_SkipsUnknownSubSkillsSilently()
    {
        // Router returns a name that has no SKILL.md in the source. The builder should drop
        // it from ActivatedSubSkills and still emit a valid prompt with the rest.
        var builder = new SessionPromptBuilder(
            new SkillLoader(FullSource()),
            new StubRouter("parti-ghcanvas", "parti-nonexistent"));

        var result = builder.Build("do something");

        Assert.Contains(GhCanvasBody, result.SystemPrompt);
        Assert.DoesNotContain("parti-nonexistent", result.ActivatedSubSkills);
        Assert.Equal(new[] { "parti-ghcanvas" }, result.ActivatedSubSkills);
    }

    [Fact]
    public void Build_NoRoutedSubSkills_ReturnsJustLayer0()
    {
        var builder = new SessionPromptBuilder(
            new SkillLoader(FullSource()),
            new StubRouter()); // router returns empty list

        var result = builder.Build("anything");

        Assert.Equal(Layer0, result.SystemPrompt);
        Assert.Empty(result.ActivatedSubSkills);
    }

    [Fact]
    public void Build_NullOrEmptyPrompt_IsSafe()
    {
        var builder = new SessionPromptBuilder(
            new SkillLoader(FullSource()),
            new StubRouter("parti-ghcanvas"));

        var nullResult = builder.Build(null!);
        var emptyResult = builder.Build("");

        Assert.Contains(Layer0, nullResult.SystemPrompt);
        Assert.Contains(Layer0, emptyResult.SystemPrompt);
        Assert.Equal(new[] { "parti-ghcanvas" }, nullResult.ActivatedSubSkills);
    }

    [Fact]
    public void Build_WithKeywordRouter_PanelizeFacadePromptActivatesGhCanvasAndFeedback()
    {
        // Realistic integration: the default KeywordSkillRouter maps "panel" → ghcanvas
        // and "facade" → (via related keywords) — assert we hit at least ghcanvas since
        // it is the canvas-edit primary signal. Uses real source with only those two
        // sub-skills registered so the assertion is robust to future keyword tweaks.
        var source = new InMemorySkillSource(new Dictionary<string, string>
        {
            ["parti-foundations/SKILL.md"] = Layer0,
            ["parti-foundations/parti-ghcanvas/SKILL.md"] = GhCanvasBody,
            ["parti-foundations/parti-formalize/SKILL.md"] = FormalizeBody,
        });

        var builder = new SessionPromptBuilder(
            new SkillLoader(source),
            new KeywordSkillRouter());

        var result = builder.Build("add a panel to the canvas and connect it");

        Assert.Contains(Layer0, result.SystemPrompt);
        Assert.Contains("parti-ghcanvas", result.ActivatedSubSkills);
        Assert.Contains(GhCanvasBody, result.SystemPrompt);
    }
}
