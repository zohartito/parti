// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Sessions;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Core.Tests.Tools;

public class GhComponentLockToolTests
{
    private const string A = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string B = "bbbbbbbb-0000-0000-0000-000000000002";

    private static Task<JObject> Run(ComponentLockRegistry reg, string json) =>
        GhComponentLockTool.HandlerFor(reg)(JObject.Parse(json), CancellationToken.None);

    [Fact]
    public async Task LockAction_AddsNormalizedIds()
    {
        var reg = new ComponentLockRegistry();
        var r = await Run(reg, $@"{{""action"":""lock"",""targets"":[""{A.ToUpperInvariant()}"",""{B}""]}}");
        Assert.Equal("ok", r["status"]!.Value<string>());
        Assert.Equal("lock", r["action"]!.Value<string>());
        Assert.Equal(new[] { A, B }, r["locked"]!.Values<string>().ToArray());
        Assert.Equal(new[] { A, B }, r["changed"]!.Values<string>().ToArray());
        Assert.True(reg.IsLocked(A));
        Assert.True(reg.IsLocked(B));
    }

    [Fact]
    public async Task LockAction_AlreadyLocked_IsNotReportedAsChanged()
    {
        var reg = new ComponentLockRegistry();
        reg.Lock(A);
        var r = await Run(reg, $@"{{""action"":""lock"",""targets"":[""{A}"",""{B}""]}}");
        Assert.Equal(new[] { B }, r["changed"]!.Values<string>().ToArray());
        Assert.Equal(2, ((JArray)r["locked"]!).Count);
    }

    [Fact]
    public async Task LockAction_WithSimpleId_LocksNothing()
    {
        var reg = new ComponentLockRegistry();
        var r = await Run(reg, $@"{{""action"":""lock"",""targets"":[""{A}"",""slider-1""]}}");
        Assert.Equal("error", r["status"]!.Value<string>());
        Assert.Equal(0, reg.Count);
    }

    [Fact]
    public async Task LockAction_TooManyTargets_IsError()
    {
        var reg = new ComponentLockRegistry();
        var targets = new JArray(Enumerable.Range(0, GhComponentLockTool.MaxTargets + 1)
            .Select(i => (object)System.Guid.NewGuid().ToString()).ToArray());
        var r = await GhComponentLockTool.HandlerFor(reg)(
            new JObject { ["action"] = "lock", ["targets"] = targets }, CancellationToken.None);
        Assert.Equal("error", r["status"]!.Value<string>());
        Assert.Equal(0, reg.Count);
    }

    [Fact]
    public async Task UnlockAction_IsRejected_ModelCannotUnlock()
    {
        var reg = new ComponentLockRegistry();
        reg.Lock(A);
        var r = await Run(reg, $@"{{""action"":""unlock"",""targets"":[""{A}""]}}");
        Assert.Equal("error", r["status"]!.Value<string>());
        Assert.True(reg.IsLocked(A));
    }

    [Fact]
    public void LockToolSchema_HasNoUnlockAction()
    {
        var schema = GhComponentLockTool.Definition.InputSchema;
        var actions = schema["properties"]!["action"]!["enum"]!.Values<string>().ToArray();
        Assert.Equal(new[] { "lock", "list" }, actions);
        Assert.DoesNotContain("unlock", schema.ToString(Newtonsoft.Json.Formatting.None));
        Assert.False(schema["additionalProperties"]!.Value<bool>());
        Assert.Equal(new[] { "action", "targets" }, ((JObject)schema["properties"]!).Properties().Select(p => p.Name));
        Assert.False(GhComponentLockTool.Definition.IsReversible);
    }

    [Fact]
    public async Task ListAction_ReturnsAllLocked()
    {
        var reg = new ComponentLockRegistry();
        reg.Lock(A); reg.Lock(B);
        var r = await Run(reg, @"{""action"":""list""}");
        Assert.Equal("ok", r["status"]!.Value<string>());
        Assert.Equal("list", r["action"]!.Value<string>());
        Assert.Equal(new[] { A, B }, r["locked"]!.Values<string>().ToArray());
        Assert.Empty((JArray)r["changed"]!);
    }

    [Fact]
    public async Task LockWithoutTargets_ReturnsError()
    {
        var reg = new ComponentLockRegistry();
        var r = await Run(reg, @"{""action"":""lock""}");
        Assert.Equal("error", r["status"]!.Value<string>());
    }

    [Fact]
    public async Task UnknownAction_ReturnsError()
    {
        var reg = new ComponentLockRegistry();
        var r = await Run(reg, @"{""action"":""whatever""}");
        Assert.Equal("error", r["status"]!.Value<string>());
    }

    [Fact]
    public async Task MissingAction_ReturnsError()
    {
        var reg = new ComponentLockRegistry();
        var r = await GhComponentLockTool.HandlerFor(reg)(new JObject(), CancellationToken.None);
        Assert.Equal("error", r["status"]!.Value<string>());
    }

    [Fact]
    public void HandlerFor_ThrowsOnNullRegistry()
    {
        Assert.Throws<System.ArgumentNullException>(() => GhComponentLockTool.HandlerFor(null!));
    }
}
