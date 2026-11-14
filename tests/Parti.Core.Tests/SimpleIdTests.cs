// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using Xunit;

namespace Parti.Core.Tests;

public class SimpleIdTests
{
    private static readonly Guid G1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid G2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid G3 = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void Name_FirstThreeLowercase()
    {
        var ids = SimpleId.Assign(new[] { (G1, "Number Slider", (string?)null) });
        Assert.Equal("num1", ids[G1]);
    }

    [Fact]
    public void Nickname_OverridesName_WhenDifferent()
    {
        var ids = SimpleId.Assign(new[] { (G1, "Number Slider", (string?)"radius") });
        Assert.Equal("radius1", ids[G1]);
    }

    [Fact]
    public void Nickname_IgnoredWhenEqualToName()
    {
        var ids = SimpleId.Assign(new[] { (G1, "Number Slider", (string?)"Number Slider") });
        Assert.Equal("num1", ids[G1]);
    }

    [Fact]
    public void Counter_IncrementsOnCollision()
    {
        var ids = SimpleId.Assign(new[]
        {
            (G1, "Addition", (string?)null),
            (G2, "Addition", (string?)null),
            (G3, "Addition", (string?)null),
        });
        Assert.Equal("add1", ids[G1]);
        Assert.Equal("add2", ids[G2]);
        Assert.Equal("add3", ids[G3]);
    }

    [Fact]
    public void ShortName_FewerThanThreeAlpha_UsesWhatsAvailable()
    {
        var ids = SimpleId.Assign(new[] { (G1, "XY Plane", (string?)null) });
        Assert.Equal("xy1", ids[G1]);
    }

    [Fact]
    public void Nickname_StripsSpecialChars()
    {
        var ids = SimpleId.Assign(new[] { (G1, "Number Slider", (string?)"my radius!") });
        Assert.Equal("myradius1", ids[G1]);
    }

    [Fact]
    public void Nickname_TruncatesAtTenChars()
    {
        var ids = SimpleId.Assign(new[] { (G1, "Panel", (string?)"ThisIsAVeryLongNickname") });
        Assert.Equal("thisisaver1", ids[G1]);
    }

    [Fact]
    public void Empty_FallsBackToNode()
    {
        var ids = SimpleId.Assign(new[] { (G1, "", (string?)null) });
        Assert.Equal("node1", ids[G1]);
    }

    [Fact]
    public void SameNickname_IncrementsCounter()
    {
        var ids = SimpleId.Assign(new[]
        {
            (G1, "Number Slider", (string?)"radius"),
            (G2, "Number Slider", (string?)"radius"),
        });
        Assert.Equal("radius1", ids[G1]);
        Assert.Equal("radius2", ids[G2]);
    }

    [Fact]
    public void Nickname_WhitespaceTreatedAsAbsent()
    {
        var ids = SimpleId.Assign(new[] { (G1, "Number Slider", (string?)"   ") });
        Assert.Equal("num1", ids[G1]);
    }
}
