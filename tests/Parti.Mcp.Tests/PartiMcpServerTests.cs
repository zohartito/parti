// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Mcp.Tests;

/// <summary>
/// End-to-end tests for <see cref="PartiMcpServer"/>. Wire two anonymous pipe pairs as a
/// duplex transport and run a real <see cref="McpClient"/> against the server in the same
/// process. Hermetic — no sockets, no subprocess, no Rhino.
/// </summary>
public sealed class PartiMcpServerTests
{
    [Fact]
    public async Task ListToolsAsync_returns_registered_tools_with_schema()
    {
        await using var fixture = await TestFixture.StartAsync(BuildFakeRegistry(out var calls));
        var tools = await fixture.Client.ListToolsAsync();

        Assert.Equal(2, tools.Count);
        var add = tools.Single(t => t.Name == "gh_add_component");
        Assert.Contains("Grasshopper component", add.Description);
        Assert.Equal("object", add.JsonSchema.GetProperty("type").GetString());
        Assert.True(add.JsonSchema.GetProperty("properties").TryGetProperty("name", out _));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task CallToolAsync_routes_to_dispatcher_and_returns_payload()
    {
        await using var fixture = await TestFixture.StartAsync(BuildFakeRegistry(out var calls));
        var result = await fixture.Client.CallToolAsync(
            "gh_add_component",
            new Dictionary<string, object?> { ["name"] = "Panel", ["x"] = 10, ["y"] = -5 });

        Assert.False(result.IsError);
        var content = Assert.Single(result.Content.OfType<TextContentBlock>());
        var payload = JObject.Parse(content.Text);
        Assert.Equal("success", payload["status"]?.ToString());
        Assert.Equal("Panel", payload["echo"]?["name"]?.ToString());

        var call = Assert.Single(calls);
        Assert.Equal("gh_add_component", call.Name);
        Assert.Equal("Panel", call.Arguments?["name"]?.ToString());
    }

    [Fact]
    public async Task CallToolAsync_cancellation_surfaces_to_caller()
    {
        // Client-side cancellation is the contract MCP consumers rely on. Server-side
        // handler ct propagation is best-effort per the MCP spec (cancelled notifications
        // are optional) — not asserted here.
        var gate = new TaskCompletionSource<bool>();

        var registry = new ToolRegistry();
        registry.Register(
            new AIToolDefinition("slow_tool", "blocks until cancelled",
                JObject.Parse("{\"type\":\"object\",\"properties\":{}}")),
            async (_, ct) =>
            {
                gate.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return new JObject();
            });

        await using var fixture = await TestFixture.StartAsync(registry);

        using var cts = new CancellationTokenSource();
        var call = fixture.Client.CallToolAsync("slow_tool", new Dictionary<string, object?>(), null, null, cts.Token);
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
    }

    [Fact]
    public async Task DispatcherException_returns_error_content_block()
    {
        var registry = new ToolRegistry();
        registry.Register(
            new AIToolDefinition("boom", "always throws",
                JObject.Parse("{\"type\":\"object\",\"properties\":{}}")),
            (_, _) => throw new InvalidOperationException("boom!"));

        await using var fixture = await TestFixture.StartAsync(registry);
        var result = await fixture.Client.CallToolAsync("boom", new Dictionary<string, object?>());

        Assert.True(result.IsError);
        var content = Assert.Single(result.Content.OfType<TextContentBlock>());
        Assert.Contains("boom!", content.Text);
    }

    private static ToolRegistry BuildFakeRegistry(out List<AIInteractionToolCall> observedCalls)
    {
        var calls = new List<AIInteractionToolCall>();
        observedCalls = calls;

        var registry = new ToolRegistry();
        registry.Register(
            new AIToolDefinition(
                "gh_add_component",
                "Add a Grasshopper component to the active canvas.",
                JObject.Parse("{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"}},\"required\":[\"name\"]}")),
            (args, _) =>
            {
                calls.Add(new AIInteractionToolCall { Name = "gh_add_component", Arguments = args });
                return Task.FromResult(new JObject
                {
                    ["status"] = "success",
                    ["stub"] = true,
                    ["echo"] = args.DeepClone(),
                });
            });

        registry.Register(
            new AIToolDefinition(
                "gh_get_all",
                "Return every component on the canvas.",
                JObject.Parse("{\"type\":\"object\",\"properties\":{}}")),
            (args, _) =>
            {
                calls.Add(new AIInteractionToolCall { Name = "gh_get_all", Arguments = args });
                return Task.FromResult(new JObject { ["status"] = "success", ["count"] = 0 });
            });

        return registry;
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly PartiMcpServer _server;
        private readonly AnonymousPipeServerStream _clientToServerWrite;
        private readonly AnonymousPipeClientStream _clientToServerRead;
        private readonly AnonymousPipeServerStream _serverToClientWrite;
        private readonly AnonymousPipeClientStream _serverToClientRead;
        private readonly Task _serverTask;
        private readonly CancellationTokenSource _cts;

        public McpClient Client { get; }

        private TestFixture(
            PartiMcpServer server,
            AnonymousPipeServerStream clientToServerWrite,
            AnonymousPipeClientStream clientToServerRead,
            AnonymousPipeServerStream serverToClientWrite,
            AnonymousPipeClientStream serverToClientRead,
            Task serverTask,
            CancellationTokenSource cts,
            McpClient client)
        {
            _server = server;
            _clientToServerWrite = clientToServerWrite;
            _clientToServerRead = clientToServerRead;
            _serverToClientWrite = serverToClientWrite;
            _serverToClientRead = serverToClientRead;
            _serverTask = serverTask;
            _cts = cts;
            Client = client;
        }

        public static async Task<TestFixture> StartAsync(IToolDispatcher dispatcher)
        {
            var clientToServerWrite = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
            var clientToServerRead = new AnonymousPipeClientStream(PipeDirection.In, clientToServerWrite.GetClientHandleAsString());
            var serverToClientWrite = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
            var serverToClientRead = new AnonymousPipeClientStream(PipeDirection.In, serverToClientWrite.GetClientHandleAsString());

            var options = new PartiMcpOptions
            {
                Transport = PartiMcpTransportKind.Stream,
                InputStream = clientToServerRead,
                OutputStream = serverToClientWrite,
                Trace = _ => { /* silence for tests */ },
            };

            var server = new PartiMcpServer(dispatcher, options);
            var cts = new CancellationTokenSource();
            var serverTask = server.RunAsync(cts.Token);

            var clientTransport = new StreamClientTransport(clientToServerWrite, serverToClientRead, loggerFactory: null!);
            var client = await McpClient.CreateAsync(clientTransport).WaitAsync(TimeSpan.FromSeconds(10));

            return new TestFixture(server, clientToServerWrite, clientToServerRead,
                serverToClientWrite, serverToClientRead, serverTask, cts, client);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Client.DisposeAsync(); } catch { /* ignore */ }
            _cts.Cancel();
            try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
            await _server.DisposeAsync();
            _clientToServerWrite.Dispose();
            _clientToServerRead.Dispose();
            _serverToClientWrite.Dispose();
            _serverToClientRead.Dispose();
            _cts.Dispose();
        }
    }
}
