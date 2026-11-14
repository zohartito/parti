// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using System.Linq;
using Parti.Core.Design;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Core.Tests.Design;

public class PluginIntentRouterTests
{
    [Fact]
    public void EmptyPrompt_ReturnsNoIntents()
    {
        Assert.Empty(PluginIntentRouter.Route(""));
        Assert.Empty(PluginIntentRouter.Route(null!));
        Assert.Empty(PluginIntentRouter.Route("   "));
    }

    [Fact]
    public void ExplicitPluginName_RoutesWithConfidenceOne()
    {
        var r = PluginIntentRouter.Route("Use Kangaroo2 to relax the mesh");
        Assert.Single(r, i => i.PluginName == "kangaroo");
        var k = r.First(i => i.PluginName == "kangaroo");
        Assert.Equal(1.0, k.Confidence);
        Assert.Contains("kangaroo2", k.MatchedTerms, System.StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImplicitCapability_RoutesWithLowerConfidence()
    {
        var r = PluginIntentRouter.Route("run a daylight study");
        var l = r.First(i => i.PluginName == "ladybug");
        Assert.True(l.Confidence < 1.0);
        Assert.True(l.Confidence > 0.0);
    }

    [Fact]
    public void MultipleMatches_UseMaxConfidenceAndListAllTerms()
    {
        var r = PluginIntentRouter.Route("use kangaroo for tension form finding");
        var k = r.First(i => i.PluginName == "kangaroo");
        Assert.Equal(1.0, k.Confidence);
        Assert.True(k.MatchedTerms.Count >= 2);
    }

    [Fact]
    public void IsInstalled_ReflectsScannerResults()
    {
        var installed = new[]
        {
            new DetectedPlugin("kangaroo", "gha", "C:/fake/kangaroo.gha", null),
        };
        var r = PluginIntentRouter.Route("use kangaroo2 and ladybug", installed);
        Assert.True(r.First(i => i.PluginName == "kangaroo").IsInstalled);
        Assert.False(r.First(i => i.PluginName == "ladybug").IsInstalled);
    }

    [Fact]
    public void IsInstalled_IsNullWhenNoListSupplied()
    {
        var r = PluginIntentRouter.Route("use kangaroo");
        Assert.Null(r.First(i => i.PluginName == "kangaroo").IsInstalled);
    }

    [Fact]
    public void MissingPlugins_ReturnsOnlyNotInstalled()
    {
        var installed = new[]
        {
            new DetectedPlugin("kangaroo", "gha", "C:/fake/kangaroo.gha", null),
        };
        var r = PluginIntentRouter.Route("use kangaroo and ladybug and honeybee", installed);
        var missing = PluginIntentRouter.MissingPlugins(r);
        Assert.Equal(2, missing.Count);
        Assert.DoesNotContain(missing, i => i.PluginName == "kangaroo");
    }

    [Fact]
    public void Results_SortedByConfidenceDescending()
    {
        var r = PluginIntentRouter.Route("a daylight study using kangaroo2");
        Assert.Equal("kangaroo", r[0].PluginName);
        Assert.True(r[0].Confidence >= r[r.Count - 1].Confidence);
    }

    [Fact]
    public void CaseInsensitive_MatchesRegardlessOfCase()
    {
        var lower = PluginIntentRouter.Route("kangaroo");
        var upper = PluginIntentRouter.Route("KANGAROO");
        var mixed = PluginIntentRouter.Route("KaNgArOo");
        Assert.Single(lower);
        Assert.Single(upper);
        Assert.Single(mixed);
    }
}
