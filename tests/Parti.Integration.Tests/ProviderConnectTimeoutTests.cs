// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Parti.Core.Ai;
using Parti.Providers.Ollama;
using Parti.Providers.OpenAI;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// R11: an unreachable provider host fails fast with a clear message instead of holding the chat
/// for the OS TCP connect timeout. The hang is simulated with a connect callback that never
/// completes, so the test needs no network.
/// </summary>
public class ProviderConnectTimeoutTests
{
    private static AIRequestCall Request() => new()
    {
        Model = "m",
        TimeoutSeconds = 60,
        Interactions = new List<IAIInteraction> { new AIInteractionText { Agent = AIAgent.User, Content = "hi" } },
    };

    private static HttpClient HangingConnectClient()
    {
        var handler = BoundedHttp.CreateProviderHandler(
            TimeSpan.FromMilliseconds(200),
            async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return Stream.Null;
            });

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    [Fact]
    public void ProviderHandler_HasBoundedConnect()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), BoundedHttp.DefaultConnectTimeout);
        using var handler = BoundedHttp.CreateProviderHandler(BoundedHttp.DefaultConnectTimeout);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task OwnedClient_ConnectsToARealLoopbackServer()
    {
        // The handler's own connect callback must still open real sockets.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var serve = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync();
                var stream = socket.GetStream();
                var buffer = new byte[4096];
                var received = new System.Text.StringBuilder();
                while (!received.ToString().Contains("\r\n\r\n"))
                {
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0)
                    {
                        break;
                    }
                    received.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
                }
                var reply = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                await stream.WriteAsync(reply);
            });

            using var client = BoundedHttp.CreateProviderClient();
            var body = await client.GetStringAsync(new Uri($"http://127.0.0.1:{port}/"));

            Assert.Equal("ok", body);
            await serve;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task InjectedClientTimeout_IsNotReportedAsAConnectFailure()
    {
        // HttpClient.Timeout throws the same OperationCanceledException(TimeoutException) shape as
        // .NET's own ConnectTimeout; only the provider handler's own exception maps to "connect".
        var hanging = new FakeHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });
        using var client = new HttpClient(hanging) { Timeout = TimeSpan.FromMilliseconds(200) };
        using var ollama = new OllamaProvider(client, new OllamaProviderOptions { BaseUrl = new Uri("http://localhost:11434") });
        using var openAi = new OpenAICompatibleProvider(client, OpenAICompatibleProviderOptions.OllamaOpenAI("localhost"));

        var ollamaFinal = (await ProviderHttp.CollectAsync(ollama.CallStreamAsync(Request())))[^1].Final!;
        var openAiFinal = await openAi.CallAsync(Request());

        Assert.False(ollamaFinal.Success);
        Assert.NotEqual(BoundedHttp.ConnectTimeoutMessage, ollamaFinal.ErrorMessage);
        Assert.False(openAiFinal.Success);
        Assert.NotEqual(BoundedHttp.ConnectTimeoutMessage, openAiFinal.ErrorMessage);
        Assert.False(BoundedHttp.IsConnectTimeout(new TaskCanceledException("t", new TimeoutException())));
    }

    [Fact]
    public async Task Ollama_UnreachableHost_FailsFastWithConnectMessage()
    {
        using var client = HangingConnectClient();
        using var provider = new OllamaProvider(client, new OllamaProviderOptions { BaseUrl = new Uri("http://ollama-host.invalid:11434") });
        var watch = Stopwatch.StartNew();

        var final = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request())))[^1].Final!;

        Assert.False(final.Success);
        Assert.Equal(BoundedHttp.ConnectTimeoutMessage, final.ErrorMessage);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Ollama_UnreachableHost_NonStreaming_ReportsConnectMessage()
    {
        using var client = HangingConnectClient();
        using var provider = new OllamaProvider(client, new OllamaProviderOptions { BaseUrl = new Uri("http://ollama-host.invalid:11434") });

        var final = await provider.CallAsync(Request());

        Assert.False(final.Success);
        Assert.Equal(BoundedHttp.ConnectTimeoutMessage, final.ErrorMessage);
    }

    [Fact]
    public async Task OpenAICompat_UnreachableHost_FailsFastWithConnectMessage()
    {
        using var client = HangingConnectClient();
        using var provider = new OpenAICompatibleProvider(client, OpenAICompatibleProviderOptions.OllamaOpenAI("ollama-host.invalid"));

        var final = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request())))[^1].Final!;

        Assert.False(final.Success);
        Assert.Equal(BoundedHttp.ConnectTimeoutMessage, final.ErrorMessage);
    }

    [Fact]
    public async Task OpenAICompat_UnreachableHost_NonStreaming_ReportsConnectMessage()
    {
        using var client = HangingConnectClient();
        using var provider = new OpenAICompatibleProvider(client, OpenAICompatibleProviderOptions.OllamaOpenAI("ollama-host.invalid"));

        var final = await provider.CallAsync(Request());

        Assert.False(final.Success);
        Assert.Equal(BoundedHttp.ConnectTimeoutMessage, final.ErrorMessage);
    }
}
