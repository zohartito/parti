// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using System;
using Parti.Core.Sessions;
using Xunit;

namespace Parti.Core.Tests.Sessions;

public class ComponentLockRegistryTests
{
    private const string A = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string B = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string C = "cccccccc-0000-0000-0000-000000000003";

    [Fact]
    public void InitialState_IsEmpty()
    {
        var r = new ComponentLockRegistry();
        Assert.Equal(0, r.Count);
        Assert.False(r.IsLocked(A));
        Assert.Empty(r.ListLocked());
    }

    [Fact]
    public void Lock_AddsId_AndIsIdempotent()
    {
        var r = new ComponentLockRegistry();
        Assert.True(r.Lock(A));
        Assert.True(r.IsLocked(A));
        Assert.False(r.Lock(A)); // idempotent second call returns false
        Assert.Equal(1, r.Count);
    }

    [Fact]
    public void Unlock_RemovesId_AndIsIdempotent()
    {
        var r = new ComponentLockRegistry();
        r.Lock(A);
        Assert.True(r.Unlock(A));
        Assert.False(r.Unlock(A)); // idempotent second call returns false
        Assert.False(r.IsLocked(A));
    }

    [Fact]
    public void ListLocked_ReturnsSortedSnapshot()
    {
        var r = new ComponentLockRegistry();
        r.Lock(C);
        r.Lock(A);
        r.Lock(B);
        Assert.Equal(new[] { A, B, C }, r.ListLocked());
    }

    [Fact]
    public void NullOrEmpty_IdsAreRejected()
    {
        var r = new ComponentLockRegistry();
        Assert.False(r.Lock(""));
        Assert.False(r.Lock(null!));
        Assert.False(r.IsLocked(""));
        Assert.False(r.IsLocked(null!));
        Assert.Equal(0, r.Count);
    }

    [Theory]
    [InlineData("Slider-a1b2")]
    [InlineData("slider-1")]
    [InlineData("Number Slider")]
    public void NonGuidIds_AreNeverStoredOrReportedLocked(string id)
    {
        var r = new ComponentLockRegistry();
        Assert.False(r.Lock(id));
        Assert.False(r.IsLocked(id));
        Assert.Equal(0, r.Count);
    }

    [Fact]
    public void GuidSpellings_AreNormalizedToOneLock()
    {
        var r = new ComponentLockRegistry();
        Assert.True(r.Lock("{" + A.ToUpperInvariant() + "}"));
        Assert.True(r.IsLocked(A));
        Assert.True(r.IsLocked(A.Replace("-", string.Empty)));
        Assert.True(r.IsLocked(Guid.Parse(A)));
        Assert.False(r.Lock(A));
        Assert.Equal(new[] { A }, r.ListLocked());
        Assert.True(r.Unlock(Guid.Parse(A)));
        Assert.Equal(0, r.Count);
    }

    [Fact]
    public void Clear_EmptiesAll()
    {
        var r = new ComponentLockRegistry();
        r.Lock(A); r.Lock(B);
        r.Clear();
        Assert.Equal(0, r.Count);
        Assert.Empty(r.ListLocked());
    }
}
