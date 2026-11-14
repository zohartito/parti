// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Core.Tests;

public class ToolRegistryTests
{
    private static AIToolDefinition MakeDef(string name) => new(
        Name: name,
        Description: $"test tool {name}",
        InputSchema: JObject.Parse(@"{ ""type"": ""object"", ""properties"": {} }"));

    [Fact]
    public void Register_StoresTool()
    {
        var registry = new ToolRegistry();
        var def = MakeDef("foo");
        registry.Register(def, (_, _) => Task.FromResult(new JObject()));

        var defs = registry.GetDefinitions();
        Assert.Single(defs);
        Assert.Equal("foo", defs[0].Name);
    }

    [Fact]
    public async Task DispatchAsync_CallsHandler_AndReturnsResult()
    {
        var registry = new ToolRegistry();
        var def = MakeDef("echo");
        registry.Register(def, (args, _) =>
        {
            var result = new JObject
            {
                ["status"] = "success",
                ["echoed"] = args["value"]?.ToString() ?? "",
            };
            return Task.FromResult(result);
        });

        var call = new AIInteractionToolCall
        {
            Id = "toolu_123",
            Name = "echo",
            Arguments = new JObject { ["value"] = "hello" },
        };

        var result = await registry.DispatchAsync(call, CancellationToken.None);

        Assert.Equal("toolu_123", result.Id);
        Assert.Equal("echo", result.Name);
        Assert.Equal("success", result.Result["status"]?.ToString());
        Assert.Equal("hello", result.Result["echoed"]?.ToString());
    }

    [Fact]
    public async Task DispatchAsync_UnknownTool_ReturnsError()
    {
        var registry = new ToolRegistry();

        var call = new AIInteractionToolCall
        {
            Id = "toolu_404",
            Name = "does_not_exist",
            Arguments = new JObject(),
        };

        var result = await registry.DispatchAsync(call, CancellationToken.None);

        Assert.Equal("toolu_404", result.Id);
        Assert.Equal("does_not_exist", result.Name);
        Assert.Equal("error", result.Result["status"]?.ToString());
        Assert.Contains("does_not_exist", result.Result["message"]?.ToString() ?? "");
    }

    [Fact]
    public void HasTool_ReturnsTrue_AfterRegister()
    {
        var registry = new ToolRegistry();
        registry.Register(MakeDef("bar"), (_, _) => Task.FromResult(new JObject()));

        Assert.True(registry.HasTool("bar"));
        Assert.False(registry.HasTool("nope"));
        Assert.False(registry.HasTool(""));
    }

    [Fact]
    public async Task DispatchAsync_HandlerThrows_ReturnsError()
    {
        var registry = new ToolRegistry();
        registry.Register(MakeDef("boom"), (_, _) =>
            Task.FromException<JObject>(new System.InvalidOperationException("kapow")));

        var call = new AIInteractionToolCall
        {
            Id = "toolu_err",
            Name = "boom",
            Arguments = new JObject(),
        };

        var result = await registry.DispatchAsync(call, CancellationToken.None);

        Assert.Equal("error", result.Result["status"]?.ToString());
        Assert.Equal("kapow", result.Result["message"]?.ToString());
    }
}
