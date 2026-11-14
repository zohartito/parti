// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// GeneratedRouterTests — covers the tag-driven KeywordSkillRouter constructor that
// builds its keyword map from an ISkillSource's SkillManifest tags.

using System.Collections.Generic;
using Parti.Core.Prompts;
using Xunit;

namespace Parti.Core.Tests.Prompts;

public class GeneratedRouterTests
{
    private static InMemorySkillSource BuildSource(
        IReadOnlyDictionary<string, SkillManifest> manifests)
    {
        // Files map: each sub-skill needs a SKILL.md entry so ListSubSkills finds it.
        // The bodies are empty — routing only reads manifests here.
        var files = new Dictionary<string, string>
        {
            ["parti-foundations/SKILL.md"] = "# root\n",
        };
        foreach (var key in manifests.Keys)
        {
            files[$"parti-foundations/{key}/SKILL.md"] = $"# {key}\n";
        }
        return new InMemorySkillSource(files, manifests);
    }

    [Fact]
    public void Route_ThreeSkillsOverlappingTags_RoutesByHitCount()
    {
        var manifests = new Dictionary<string, SkillManifest>
        {
            ["parti-ghcanvas"] = new(
                "parti-ghcanvas", "canvas", "0.9.0",
                new List<string> { "slider", "component", "add" }, true, true, "/parti-ghcanvas"),
            ["parti-feedback"] = new(
                "parti-feedback", "feedback", "0.9.0",
                new List<string> { "metric", "evaluation", "maximize" }, true, true, "/parti-feedback"),
            ["parti-optimize"] = new(
                "parti-optimize", "optimize", "0.9.0",
                new List<string> { "galapagos", "solver", "genome" }, true, true, "/parti-optimize"),
        };
        var source = BuildSource(manifests);
        var router = new KeywordSkillRouter(source);

        var result = router.Route("add a slider and run galapagos");

        // ghcanvas has two hits ("add", "slider"), optimize has one ("galapagos").
        Assert.Equal("parti-ghcanvas", result.SubSkills[0]);
        Assert.Contains("parti-optimize", result.SubSkills);
    }

    [Fact]
    public void Route_EmptyPrompt_FallsBackToFormalize()
    {
        var manifests = new Dictionary<string, SkillManifest>
        {
            ["parti-ghcanvas"] = new(
                "parti-ghcanvas", "canvas", "0.9.0",
                new List<string> { "slider" }, true, true, "/parti-ghcanvas"),
        };
        var source = BuildSource(manifests);
        var router = new KeywordSkillRouter(source);

        var result = router.Route("");

        Assert.Single(result.SubSkills);
        Assert.Equal("parti-formalize", result.SubSkills[0]);
    }

    [Fact]
    public void Route_NoTagMatch_FallsBackToFormalize()
    {
        var manifests = new Dictionary<string, SkillManifest>
        {
            ["parti-ghcanvas"] = new(
                "parti-ghcanvas", "canvas", "0.9.0",
                new List<string> { "slider", "wire" }, true, true, "/parti-ghcanvas"),
        };
        var source = BuildSource(manifests);
        var router = new KeywordSkillRouter(source);

        var result = router.Route("something utterly unrelated to any tag");

        Assert.Single(result.SubSkills);
        Assert.Equal("parti-formalize", result.SubSkills[0]);
    }

    [Fact]
    public void Route_EmptyTagsManifest_IsIgnored()
    {
        var manifests = new Dictionary<string, SkillManifest>
        {
            ["parti-empty"] = new(
                "parti-empty", "empty", "0.9.0",
                new List<string>(), true, true, "/parti-empty"),
            ["parti-ghcanvas"] = new(
                "parti-ghcanvas", "canvas", "0.9.0",
                new List<string> { "slider" }, true, true, "/parti-ghcanvas"),
        };
        var source = BuildSource(manifests);
        var router = new KeywordSkillRouter(source);

        var result = router.Route("add a slider");

        Assert.Equal("parti-ghcanvas", result.SubSkills[0]);
        Assert.DoesNotContain("parti-empty", result.SubSkills);
    }

    [Fact]
    public void Route_TagCollision_PicksAlphabeticallyFirst()
    {
        // Two skills both claim the tag "shared". parti-abstract comes first alphabetically.
        var manifests = new Dictionary<string, SkillManifest>
        {
            ["parti-ghcanvas"] = new(
                "parti-ghcanvas", "gh", "0.9.0",
                new List<string> { "shared", "canvas" }, true, true, "/parti-ghcanvas"),
            ["parti-abstract"] = new(
                "parti-abstract", "abs", "0.9.0",
                new List<string> { "shared", "concept" }, true, true, "/parti-abstract"),
        };
        var source = BuildSource(manifests);
        var router = new KeywordSkillRouter(source);

        var result = router.Route("shared");

        // The "shared" tag should route to parti-abstract (alphabetically first).
        Assert.Equal("parti-abstract", result.SubSkills[0]);
    }

    [Fact]
    public void Route_PerSkillUniqueTags_StillRoutesCorrectly()
    {
        var manifests = new Dictionary<string, SkillManifest>
        {
            ["parti-abstract"] = new(
                "parti-abstract", "abs", "0.9.0",
                new List<string> { "concept" }, true, true, "/parti-abstract"),
            ["parti-ghcanvas"] = new(
                "parti-ghcanvas", "gh", "0.9.0",
                new List<string> { "canvas" }, true, true, "/parti-ghcanvas"),
        };
        var source = BuildSource(manifests);
        var router = new KeywordSkillRouter(source);

        Assert.Equal("parti-abstract", router.Route("the concept is organic").SubSkills[0]);
        Assert.Equal("parti-ghcanvas", router.Route("add canvas bits").SubSkills[0]);
    }

    [Fact]
    public void KnownSubSkillsInstance_IncludesManifestsAndFallback()
    {
        var manifests = new Dictionary<string, SkillManifest>
        {
            ["parti-ghcanvas"] = new(
                "parti-ghcanvas", "gh", "0.9.0",
                new List<string> { "canvas" }, true, true, "/parti-ghcanvas"),
        };
        var source = BuildSource(manifests);
        var router = new KeywordSkillRouter(source);

        Assert.Contains("parti-ghcanvas", router.KnownSubSkillsInstance);
        Assert.Contains("parti-formalize", router.KnownSubSkillsInstance); // Fallback always reachable.
    }

    [Fact]
    public void LlmRouter_SourceConstructor_DerivesKnownSubSkillsFromManifests()
    {
        var manifests = new Dictionary<string, SkillManifest>
        {
            ["parti-custom"] = new(
                "parti-custom", "custom", "0.9.0",
                new List<string> { "xyz" }, true, true, "/parti-custom"),
        };
        var source = BuildSource(manifests);

        // The LLM returns a sub-skill name present in the manifest — it must be accepted.
        var json = "{\"sub_skills\":[\"parti-custom\"],\"evidence\":{\"parti-custom\":[\"xyz\"]}}";
        var invoker = new FakeInvoker(json);
        var router = new LlmSkillRouter(invoker, source);

        var result = router.Route("do xyz");

        Assert.Equal(new[] { "parti-custom" }, result.SubSkills);
        Assert.Contains("parti-custom", router.KnownSubSkills);
        Assert.Contains("parti-formalize", router.KnownSubSkills);
    }

    private sealed class FakeInvoker : ILlmInvoker
    {
        private readonly string _response;
        public FakeInvoker(string response) => _response = response;

        public System.Threading.Tasks.Task<string> InvokeAsync(
            string systemPrompt, string userPrompt, System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult(_response);
    }
}
