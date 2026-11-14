// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;
using Xunit;

namespace Parti.Core.Tests;

public class NameResolverTests
{
    private static readonly IReadOnlyList<ComponentCandidate> Proxies = new[]
    {
        new ComponentCandidate("Number Slider", "57da07bd-ecab-415d-9d86-af36d7073abc", "Params"),
        new ComponentCandidate("Panel", "59e0b89a-e487-49f8-bab8-b5bab16be14c", "Params"),
        new ComponentCandidate("XY Plane", "17b7152b-d30d-4d50-b9ef-c9fe25576fc2", "Vector"),
        new ComponentCandidate("Addition", "a0d62394-a118-422d-abb3-6af115c75b25", "Maths"),
        new ComponentCandidate("Subtraction", "9c007a04-d0d9-48e4-9da3-9ba142bc4d46", "Maths"),
        new ComponentCandidate("Line", "4c619bc9-39fd-4d46-9173-b6a59c47b3b5", "Curve"),
        new ComponentCandidate("Line SDL", "4c4e56eb-2f04-43f9-95a3-cc46a14f495a", "Curve"),
        new ComponentCandidate("Line 4Pt", "aa9d4b25-8e5d-4e6b-a17c-59e3cba50e34", "Curve"),
        new ComponentCandidate("Circle", "807b86e3-be8d-4970-92b5-f8cdcb45b06b", "Curve"),
        new ComponentCandidate("Rectangle", "d93100b6-d50b-40b2-831a-814659dc38e3", "Curve"),
    };

    private readonly NameResolver _resolver = new();

    [Fact]
    public void AliasShortcut_Slider_ReturnsNumberSlider()
    {
        var result = _resolver.Resolve("slider", Proxies);
        Assert.Equal(ResolveKind.Success, result.Kind);
        Assert.Equal("Number Slider", result.Match!.Name);
    }

    [Fact]
    public void AliasShortcut_XY_ReturnsXYPlane()
    {
        var result = _resolver.Resolve("xy", Proxies);
        Assert.Equal(ResolveKind.Success, result.Kind);
        Assert.Equal("XY Plane", result.Match!.Name);
    }

    [Fact]
    public void GuidMatch_ReturnsCorrectComponent()
    {
        var result = _resolver.Resolve("57da07bd-ecab-415d-9d86-af36d7073abc", Proxies);
        Assert.Equal(ResolveKind.Success, result.Kind);
        Assert.Equal("Number Slider", result.Match!.Name);
    }

    [Fact]
    public void GuidMatch_UnknownGuid_ReturnsNotFound()
    {
        var result = _resolver.Resolve("00000000-0000-0000-0000-000000000000", Proxies);
        Assert.Equal(ResolveKind.NotFound, result.Kind);
    }

    [Fact]
    public void ExactNameMatch_CaseInsensitive()
    {
        var result = _resolver.Resolve("CIRCLE", Proxies);
        Assert.Equal(ResolveKind.Success, result.Kind);
        Assert.Equal("Circle", result.Match!.Name);
    }

    [Fact]
    public void PartialMatch_SingleHit_ReturnsSuccess()
    {
        var result = _resolver.Resolve("Rect", Proxies);
        Assert.Equal(ResolveKind.Success, result.Kind);
        Assert.Equal("Rectangle", result.Match!.Name);
    }

    [Fact]
    public void ExactMatch_WinsOverSubstringSiblings()
    {
        var result = _resolver.Resolve("Line", Proxies);
        Assert.Equal(ResolveKind.Success, result.Kind);
        Assert.Equal("Line", result.Match!.Name);
    }

    [Fact]
    public void PartialMatch_MultipleHits_ReturnsAmbiguousShortestFirst()
    {
        var result = _resolver.Resolve("Lin", Proxies);
        Assert.Equal(ResolveKind.Ambiguous, result.Kind);
        Assert.Equal(3, result.Candidates.Count);
        Assert.Equal("Line", result.Candidates[0].Name);
        Assert.Equal("Line 4Pt", result.Candidates[1].Name);
        Assert.Equal("Line SDL", result.Candidates[2].Name);
    }

    [Fact]
    public void NoMatch_ReturnsNotFoundWithEmptyCandidates()
    {
        var result = _resolver.Resolve("Xylophone", Proxies);
        Assert.Equal(ResolveKind.NotFound, result.Kind);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void EmptyQuery_ReturnsNotFound()
    {
        var result = _resolver.Resolve("", Proxies);
        Assert.Equal(ResolveKind.NotFound, result.Kind);
    }

    [Fact]
    public void WhitespaceQuery_ReturnsNotFound()
    {
        var result = _resolver.Resolve("   ", Proxies);
        Assert.Equal(ResolveKind.NotFound, result.Kind);
    }

    [Fact]
    public void NormalizedWhitespace_Matches()
    {
        var result = _resolver.Resolve("  xy   plane  ", Proxies);
        Assert.Equal(ResolveKind.Success, result.Kind);
        Assert.Equal("XY Plane", result.Match!.Name);
    }
}
