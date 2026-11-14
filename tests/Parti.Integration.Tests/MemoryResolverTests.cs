// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using Parti.Core.Sessions;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// Pure unit tests for <see cref="MemoryResolver"/>: no session/provider needed.
/// </summary>
public class MemoryResolverTests
{
    private const string Slider1 = "00000000-0000-0000-0000-000000000001";
    private const string Slider2 = "00000000-0000-0000-0000-000000000002";
    private const string Slider3 = "00000000-0000-0000-0000-000000000003";
    private const string Panel1 = "00000000-0000-0000-0000-000000000004";
    private const string Panel2 = "00000000-0000-0000-0000-000000000005";

    [Fact]
    public void MentionThenResolveExactGuid()
    {
        var resolver = new MemoryResolver();
        var now = DateTime.UtcNow;

        resolver.Mention(Slider3, "Number Slider", now);

        Assert.Equal(Slider3, resolver.ResolveReference(Slider3));
    }

    [Fact]
    public void RetiredSimpleIds_AreNeitherRecordedNorPassedThrough()
    {
        var resolver = new MemoryResolver();

        resolver.Mention("sli-3", "Number Slider", DateTime.UtcNow);

        Assert.Empty(resolver.RecentByType("slider"));
        Assert.Null(resolver.ResolveReference("sli-3"));

        // Even with a slider in memory, a retired handle must not fuzzy-match it.
        resolver.Mention(Slider1, "Number Slider", DateTime.UtcNow);
        Assert.Null(resolver.ResolveReference("sli-3"));
        Assert.Null(resolver.ResolveReference("slider-3"));
        Assert.Equal(Slider1, resolver.ResolveReference("the slider"));
    }

    [Fact]
    public void ResolveReferenceToLatestSlider()
    {
        var resolver = new MemoryResolver();
        var t0 = new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc);

        resolver.Mention(Slider1, "Number Slider", t0);
        resolver.Mention(Slider2, "Number Slider", t0.AddSeconds(30));

        Assert.Equal(Slider2, resolver.ResolveReference("the slider I just added"));
    }

    [Fact]
    public void ResolveReferenceToRecentPanel()
    {
        var resolver = new MemoryResolver();
        var t0 = new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc);

        resolver.Mention(Slider1, "Number Slider", t0);
        resolver.Mention(Panel1, "Panel", t0.AddSeconds(10));
        resolver.Mention(Panel2, "Panel", t0.AddSeconds(20));

        Assert.Equal(Panel2, resolver.ResolveReference("that panel"));
    }

    [Fact]
    public void UnknownReferenceReturnsNull()
    {
        var resolver = new MemoryResolver();
        resolver.Mention(Slider1, "Number Slider", DateTime.UtcNow);

        // Reference shape that won't match an existing component type.
        Assert.Null(resolver.ResolveReference("the curve component"));
    }

    [Fact]
    public void RecentByTypeReturnsOrderedByTimestamp()
    {
        var resolver = new MemoryResolver();
        var t0 = new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc);

        resolver.Mention(Slider1, "Number Slider", t0);
        resolver.Mention(Slider2, "Number Slider", t0.AddSeconds(10));
        resolver.Mention(Slider3, "Number Slider", t0.AddSeconds(20));
        resolver.Mention(Panel1, "Panel", t0.AddSeconds(30));

        var recentSliders = resolver.RecentByType("slider");
        Assert.Equal(3, recentSliders.Count);
        // Newest first.
        Assert.Equal(Slider3, recentSliders[0].InstanceGuid);
        Assert.Equal(Slider2, recentSliders[1].InstanceGuid);
        Assert.Equal(Slider1, recentSliders[2].InstanceGuid);
    }

    [Fact]
    public void CaseInsensitiveReferenceResolution()
    {
        var resolver = new MemoryResolver();
        var now = DateTime.UtcNow;

        resolver.Mention(Slider1, "Number Slider", now);

        Assert.Equal(Slider1, resolver.ResolveReference("THE SLIDER"));
        Assert.Equal(Slider1, resolver.ResolveReference("The Slider I Just Added"));
        Assert.Equal(Slider1, resolver.ResolveReference("tHe mOsT ReCeNt sLiDeR"));
    }

    [Fact]
    public void RecentByTypeRespectsLimit()
    {
        var resolver = new MemoryResolver();
        var t0 = new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc);

        resolver.Mention(Slider1, "Number Slider", t0);
        resolver.Mention(Slider2, "Number Slider", t0.AddSeconds(10));
        resolver.Mention(Slider3, "Number Slider", t0.AddSeconds(20));

        var top2 = resolver.RecentByType("slider", limit: 2);
        Assert.Equal(2, top2.Count);
        Assert.Equal(Slider3, top2[0].InstanceGuid);
        Assert.Equal(Slider2, top2[1].InstanceGuid);
    }

    [Fact]
    public void MentionOverwritesTimestamp()
    {
        var resolver = new MemoryResolver();
        var t0 = new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc);

        resolver.Mention(Slider1, "Number Slider", t0);
        resolver.Mention(Slider2, "Number Slider", t0.AddSeconds(10));
        // Re-mention slider-1 with a newer timestamp — it should now beat slider-2.
        resolver.Mention(Slider1, "Number Slider", t0.AddSeconds(20));

        Assert.Equal(Slider1, resolver.ResolveReference("the slider I just added"));
    }
}
