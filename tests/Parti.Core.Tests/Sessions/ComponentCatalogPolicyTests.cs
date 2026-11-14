// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Sessions;
using Parti.Core.Sessions.Policies;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Core.Tests.Sessions;

/// <summary>
/// S8: a <c>gh_add_component.componentGuid</c> must resolve in the host catalog before review.
/// </summary>
public class ComponentCatalogPolicyTests
{
    private const string NumberSlider = "57da07bd-ecab-415d-9d86-af36d7073abc";
    private const string Circle = "807b86e3-be8d-4970-92b5-f8cdcb45b06b";

    private static readonly IComponentCatalog Catalog = JsonComponentCatalog.Load(TestCatalog.RelativePath);

    private static readonly IReadOnlyDictionary<string, bool> Reversibility =
        new Dictionary<string, bool> { ["gh_add_component"] = true, ["gh_delete"] = true };

    private static AIInteractionToolCall Add(string componentGuid, string id = "t1") => new()
    {
        Id = id,
        Name = "gh_add_component",
        Arguments = new JObject { ["componentGuid"] = componentGuid, ["x"] = 0, ["y"] = 0 },
    };

    [Fact]
    public void KnownComponentGuids_Pass()
    {
        var policy = new ComponentCatalogPolicy(Catalog);
        Assert.True(policy.TryValidate(new[] { Add(NumberSlider, "t1"), Add(Circle.ToUpperInvariant(), "t2") }, Reversibility, out var error), error);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void UnknownComponentGuid_IsRejectedBeforeReview()
    {
        var unknown = "11111111-1111-1111-1111-111111111111";
        var policy = new ComponentCatalogPolicy(Catalog);

        Assert.False(policy.TryValidate(new[] { Add(NumberSlider, "t1"), Add(unknown, "t2") }, Reversibility, out var error));
        Assert.Contains(unknown, error);
        Assert.Contains("gh_find_component", error);
    }

    [Fact]
    public void EmptyCatalog_RejectsEveryAdd()
    {
        var policy = new ComponentCatalogPolicy(EmptyComponentCatalog.Instance);
        Assert.False(policy.TryValidate(new[] { Add(NumberSlider) }, Reversibility, out _));
    }

    [Fact]
    public void NonGuidComponentGuid_IsRejected()
    {
        var policy = new ComponentCatalogPolicy(Catalog);
        Assert.False(policy.TryValidate(new[] { Add("Number Slider") }, Reversibility, out var error));
        Assert.Contains("exact Grasshopper component GUID", error);
    }

    [Fact]
    public void OtherTools_AreNotChecked()
    {
        var policy = new ComponentCatalogPolicy(EmptyComponentCatalog.Instance);
        var delete = new AIInteractionToolCall
        {
            Id = "t1",
            Name = "gh_delete",
            Arguments = new JObject { ["target"] = "aaaaaaaa-0000-0000-0000-000000000001" },
        };
        Assert.True(policy.TryValidate(new[] { delete }, Reversibility, out _));
    }
}
