// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Sessions;
using Xunit;

namespace Parti.Core.Tests.Sessions;

public class ErrorRepairPolicyTests
{
    private static AIInteractionToolResult Err(string id = "t1")
        => new()
        {
            Id = id,
            Name = "fake",
            Result = new JObject { ["status"] = "error", ["message"] = "x" },
        };

    private static AIInteractionToolResult Ok(string id = "t1")
        => new()
        {
            Id = id,
            Name = "fake",
            Result = new JObject { ["status"] = "ok" },
        };

    [Fact]
    public void IsErrorResult_TrueForStatusError()
    {
        Assert.True(ErrorRepairPolicy.IsErrorResult(Err()));
        Assert.False(ErrorRepairPolicy.IsErrorResult(Ok()));
    }

    [Fact]
    public void IsErrorResult_FalseForNullOrMissingStatus()
    {
        Assert.False(ErrorRepairPolicy.IsErrorResult(null!));
        var missing = new AIInteractionToolResult { Id = "x", Name = "y", Result = new JObject() };
        Assert.False(ErrorRepairPolicy.IsErrorResult(missing));
    }

    [Fact]
    public void IsAllErrors_FalseForEmptyList()
    {
        Assert.False(ErrorRepairPolicy.IsAllErrors(System.Array.Empty<AIInteractionToolResult>()));
    }

    [Fact]
    public void IsAllErrors_TrueWhenEveryResultIsError()
    {
        Assert.True(ErrorRepairPolicy.IsAllErrors(new[] { Err("a"), Err("b") }));
    }

    [Fact]
    public void IsAllErrors_FalseWhenAnyResultIsOk()
    {
        Assert.False(ErrorRepairPolicy.IsAllErrors(new[] { Err("a"), Ok("b") }));
    }

    [Fact]
    public void Tracker_StopsWhenTheNthConsecutiveErrorPassCompletes()
    {
        // A4: the cap is exact. N = 3 stops on the 3rd consecutive all-error pass.
        var t = new ErrorRepairTracker(new ErrorRepairPolicy(MaxConsecutiveErrorPasses: 3));
        Assert.False(t.RecordPassAndShouldStop(new[] { Err("1") }));
        Assert.Equal(1, t.ConsecutiveErrorPasses);
        Assert.False(t.RecordPassAndShouldStop(new[] { Err("2") }));
        Assert.Equal(2, t.ConsecutiveErrorPasses);
        Assert.True(t.RecordPassAndShouldStop(new[] { Err("3") }));
        Assert.Equal(3, t.ConsecutiveErrorPasses);
    }

    [Fact]
    public void Tracker_ProductCapFive_StopsOnFifthErrorPass()
    {
        var t = new ErrorRepairTracker(new ErrorRepairPolicy(MaxConsecutiveErrorPasses: 5));
        for (var i = 1; i <= 4; i++)
        {
            Assert.False(t.RecordPassAndShouldStop(new[] { Err(i.ToString()) }));
        }
        Assert.True(t.RecordPassAndShouldStop(new[] { Err("5") }));
    }

    [Fact]
    public void Tracker_NonErrorPass_NeverStops()
    {
        var t = new ErrorRepairTracker(new ErrorRepairPolicy(MaxConsecutiveErrorPasses: 0));
        Assert.False(t.RecordPassAndShouldStop(new[] { Ok("1") }));
        Assert.False(t.RecordPassAndShouldStop(System.Array.Empty<AIInteractionToolResult>()));
    }

    [Fact]
    public void IsErrorResult_TrueForRejectedOrUnsuccessfulResults()
    {
        var rejected = new AIInteractionToolResult { Id = "r", Name = "gh_add_component", Result = new JObject { ["success"] = false, ["rejected"] = true, ["message"] = "gate" } };
        var failed = new AIInteractionToolResult { Id = "f", Name = "gh_add_component", Result = new JObject { ["success"] = false, ["error"] = "boom" } };
        var rejectedOnly = new AIInteractionToolResult { Id = "o", Name = "gh_add_component", Result = new JObject { ["rejected"] = true } };
        var succeeded = new AIInteractionToolResult { Id = "s", Name = "gh_add_component", Result = new JObject { ["success"] = true } };

        Assert.True(ErrorRepairPolicy.IsErrorResult(rejected));
        Assert.True(ErrorRepairPolicy.IsErrorResult(failed));
        Assert.True(ErrorRepairPolicy.IsErrorResult(rejectedOnly));
        Assert.False(ErrorRepairPolicy.IsErrorResult(succeeded));
    }

    [Fact]
    public void Tracker_ResetsOnNonErrorPass()
    {
        var t = new ErrorRepairTracker(new ErrorRepairPolicy(MaxConsecutiveErrorPasses: 3));
        t.RecordPassAndShouldStop(new[] { Err("1") });
        t.RecordPassAndShouldStop(new[] { Err("2") });
        Assert.Equal(2, t.ConsecutiveErrorPasses);
        // Good pass — counter resets.
        t.RecordPassAndShouldStop(new[] { Ok("3") });
        Assert.Equal(0, t.ConsecutiveErrorPasses);
    }

    [Fact]
    public void Tracker_ZeroCap_StopsImmediatelyOnFirstErrorPass()
    {
        var t = new ErrorRepairTracker(new ErrorRepairPolicy(MaxConsecutiveErrorPasses: 0));
        Assert.True(t.RecordPassAndShouldStop(new[] { Err("1") }));
    }

    [Fact]
    public void Tracker_Reset_ZeroesCounter()
    {
        var t = new ErrorRepairTracker(new ErrorRepairPolicy(MaxConsecutiveErrorPasses: 3));
        t.RecordPassAndShouldStop(new[] { Err("1") });
        t.Reset();
        Assert.Equal(0, t.ConsecutiveErrorPasses);
    }

    [Fact]
    public void Default_PolicyStopsOnFourthErrorPass()
    {
        // Three repair attempts after a failed pass: the library's historical behaviour under
        // the exact cap definition (stop when the Nth consecutive all-error pass completes).
        Assert.Equal(4, ErrorRepairPolicy.Default.MaxConsecutiveErrorPasses);
    }

    [Fact]
    public void SessionOptions_DefaultsToDefaultRepairPolicy()
    {
        var opts = new SessionOptions();
        Assert.Same(ErrorRepairPolicy.Default, opts.ErrorRepair);
    }
}
