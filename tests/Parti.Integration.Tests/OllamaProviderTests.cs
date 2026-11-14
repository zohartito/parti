// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Providers.Ollama;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// Hermetic tests for <see cref="OllamaProvider"/>. Uses <see cref="FakeHttpMessageHandler"/>
/// with canned <c>/api/chat</c> responses — no real Ollama server needed.
/// </summary>
public class OllamaProviderTests
{
    private static AIRequestCall MakeRequest()
    {
        return new AIRequestCall
        {
            Model = "llama3.1",
            Interactions = new List<IAIInteraction>
            {
                new AIInteractionText { Agent = AIAgent.User, Content = "hi" },
            },
        };
    }

    private static FakeHttpMessageHandler FromJson(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new FakeHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        });
    }

    [Fact]
    public async Task DecodesPlainTextResponse()
    {
        var body = new JObject
        {
            ["model"] = "llama3.1",
            ["message"] = new JObject
            {
                ["role"] = "assistant",
                ["content"] = "Hello, world!",
            },
            ["done"] = true,
            ["prompt_eval_count"] = 12,
            ["eval_count"] = 7,
        }.ToString();

        var handler = FromJson(body);
        using var http = new HttpClient(handler);
        var provider = new OllamaProvider(http);

        var ret = await provider.CallAsync(MakeRequest());

        Assert.Equal(AICallStatus.Finished, ret.Status);
        Assert.Equal("end_turn", ret.StopReason);
        Assert.Equal(12, ret.InputTokens);
        Assert.Equal(7, ret.OutputTokens);

        var text = Assert.IsType<AIInteractionText>(ret.Interactions.Single());
        Assert.Equal(AIAgent.Assistant, text.Agent);
        Assert.Equal("Hello, world!", text.Content);

        // The provider hit the Ollama chat endpoint with a POST.
        var req = Assert.Single(handler.ReceivedRequests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://localhost:11434/api/chat", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task DecodesToolCallWithObjectArguments()
    {
        var body = new JObject
        {
            ["model"] = "llama3.1",
            ["message"] = new JObject
            {
                ["role"] = "assistant",
                ["content"] = string.Empty,
                ["tool_calls"] = new JArray
                {
                    new JObject
                    {
                        ["function"] = new JObject
                        {
                            ["name"] = "gh_add_component",
                            ["arguments"] = new JObject
                            {
                                ["name"] = "Panel",
                            },
                        },
                    },
                },
            },
            ["done"] = true,
        }.ToString();

        var handler = FromJson(body);
        using var http = new HttpClient(handler);
        var provider = new OllamaProvider(http);

        var ret = await provider.CallAsync(MakeRequest());

        Assert.Equal(AICallStatus.CallingTools, ret.Status);
        Assert.Equal("tool_use", ret.StopReason);
        Assert.True(ret.HasToolCalls);

        var call = Assert.IsType<AIInteractionToolCall>(ret.Interactions.Single());
        Assert.Equal("gh_add_component", call.Name);
        Assert.Equal("Panel", call.Arguments["name"]?.ToString());
        Assert.False(string.IsNullOrEmpty(call.Id));
    }

    [Fact]
    public async Task DecodesToolCallWithStringArguments()
    {
        // Some Ollama models serialize arguments as a JSON-encoded string rather than an object.
        var body = new JObject
        {
            ["model"] = "llama3.1",
            ["message"] = new JObject
            {
                ["role"] = "assistant",
                ["content"] = string.Empty,
                ["tool_calls"] = new JArray
                {
                    new JObject
                    {
                        ["function"] = new JObject
                        {
                            ["name"] = "gh_add_component",
                            ["arguments"] = "{\"name\": \"Panel\"}",
                        },
                    },
                },
            },
            ["done"] = true,
        }.ToString();

        var handler = FromJson(body);
        using var http = new HttpClient(handler);
        var provider = new OllamaProvider(http);

        var ret = await provider.CallAsync(MakeRequest());

        var call = Assert.IsType<AIInteractionToolCall>(ret.Interactions.Single());
        Assert.Equal("gh_add_component", call.Name);
        // The string payload must have been parsed back into a JObject.
        Assert.Equal("Panel", call.Arguments["name"]?.ToString());
    }

    [Fact]
    public void ToolResultRoundTripPreservesMessageShape()
    {
        var toolCall = new AIInteractionToolCall
        {
            Id = "ollama_abc",
            Name = "gh_add_component",
            Arguments = new JObject { ["name"] = "Panel" },
            Agent = AIAgent.ToolCall,
        };

        var toolResult = new AIInteractionToolResult
        {
            Id = "ollama_abc",
            Name = "gh_add_component",
            Arguments = toolCall.Arguments,
            Result = new JObject { ["ok"] = true, ["guid"] = "1234" },
            Agent = AIAgent.ToolResult,
        };

        var request = new AIRequestCall
        {
            Model = "llama3.1",
            Interactions = new List<IAIInteraction>
            {
                new AIInteractionText { Agent = AIAgent.User, Content = "add a panel" },
                new AIInteractionText { Agent = AIAgent.Assistant, Content = "sure" },
                toolCall,
                toolResult,
            },
        };

        var provider = new OllamaProvider();
        var bodyJson = provider.CreateRequestBody(request);
        var body = JObject.Parse(bodyJson);

        var messages = Assert.IsType<JArray>(body["messages"]);
        // Expected: user -> assistant(text + tool_calls) -> tool
        Assert.Equal(3, messages.Count);

        Assert.Equal("user", messages[0]!["role"]?.ToString());
        Assert.Equal("add a panel", messages[0]!["content"]?.ToString());

        Assert.Equal("assistant", messages[1]!["role"]?.ToString());
        Assert.Equal("sure", messages[1]!["content"]?.ToString());
        var calls = Assert.IsType<JArray>(messages[1]!["tool_calls"]);
        var fn = Assert.IsType<JObject>(calls.Single()["function"]);
        Assert.Equal("gh_add_component", fn["name"]?.ToString());
        Assert.Equal("Panel", fn["arguments"]?["name"]?.ToString());

        Assert.Equal("tool", messages[2]!["role"]?.ToString());
        // Tool result content is a JSON-stringified payload.
        var resultPayload = JObject.Parse(messages[2]!["content"]!.ToString());
        Assert.True((bool)resultPayload["ok"]!);
        Assert.Equal("1234", resultPayload["guid"]?.ToString());

        // No streaming in v0.6.
        Assert.False((bool)body["stream"]!);
    }

    [Fact]
    public async Task ReturnsErrorReturnOnHttp500()
    {
        var handler = FakeHttpMessageHandler.FromStatus(HttpStatusCode.InternalServerError, "boom");
        using var http = new HttpClient(handler);
        var provider = new OllamaProvider(http);

        var ret = await provider.CallAsync(MakeRequest());

        Assert.Equal(AICallStatus.Error, ret.Status);
        Assert.False(ret.Success);
        Assert.Contains("HTTP 500", ret.ErrorMessage);
        Assert.Contains("boom", ret.ErrorMessage);
        Assert.Empty(ret.Interactions);
    }

    [Fact]
    public async Task Ollama_ErrorBodyTruncatedTo16KiB()
    {
        var huge = new string('e', 40_000);
        var server = ProviderHttp.ServeWith(HttpStatusCode.BadGateway, huge);
        using var provider = new OllamaProvider(server.Client);

        var ret = await provider.CallAsync(MakeRequest());
        var streamed = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(MakeRequest())))[^1].Final!;

        foreach (var failure in new[] { ret, streamed })
        {
            Assert.False(failure.Success);
            Assert.StartsWith("HTTP 502: ", failure.ErrorMessage);
            Assert.EndsWith(BoundedHttp.DiagnosticTruncatedMarker, failure.ErrorMessage);
            var diagnostic = failure.ErrorMessage!.Substring("HTTP 502: ".Length);
            Assert.Equal(OllamaProvider.MaxErrorResponseBytes + BoundedHttp.DiagnosticTruncatedMarker.Length, diagnostic.Length);
        }
    }

    [Fact]
    public async Task Ollama_NonStreamingBodyBudget_FailsClosed()
    {
        var body = new JObject
        {
            ["message"] = new JObject { ["role"] = "assistant", ["content"] = new string('x', OllamaProvider.MaxNonStreamingResponseBytes) },
            ["done"] = true,
        }.ToString();
        var handler = FromJson(body);
        using var http = new HttpClient(handler);
        var provider = new OllamaProvider(http);

        var ret = await provider.CallAsync(MakeRequest());

        Assert.False(ret.Success);
        Assert.Contains($"{OllamaProvider.MaxNonStreamingResponseBytes}-byte transport budget", ret.ErrorMessage);
    }

    [Fact]
    public async Task Ollama_NonStreamingThinkingIsReasoningNotContent()
    {
        var body = new JObject
        {
            ["message"] = new JObject { ["role"] = "assistant", ["content"] = "Answer", ["thinking"] = "hidden chain" },
            ["done"] = true,
            ["done_reason"] = "stop",
        }.ToString();
        var handler = FromJson(body);
        using var http = new HttpClient(handler);
        var provider = new OllamaProvider(http);

        var ret = await provider.CallAsync(MakeRequest());

        var text = Assert.IsType<AIInteractionText>(Assert.Single(ret.Interactions));
        Assert.Equal("Answer", text.Content);
        Assert.Equal("hidden chain", text.Reasoning);
    }

    [Fact]
    public async Task Ollama_NonStreamingSendsNumPredictNumCtxAndKeepAlive()
    {
        var server = ProviderHttp.Serve(new JObject
        {
            ["message"] = new JObject { ["role"] = "assistant", ["content"] = "ok" },
            ["done"] = true,
        }.ToString());
        using var provider = new OllamaProvider(server.Client);
        var request = MakeRequest();
        request.MaxTokens = 8192;

        await provider.CallAsync(request);

        var sent = JObject.Parse(Assert.Single(server.Requests).Body);
        Assert.False((bool)sent["stream"]!);
        Assert.Equal(8192, (int)sent["options"]!["num_predict"]!);
        Assert.Equal(32_768, (int)sent["options"]!["num_ctx"]!);
        Assert.Equal("10m", (string?)sent["keep_alive"]);
    }

    [Fact]
    public async Task Ollama_ListModels_ReturnsTagNamesFromConfiguredEndpoint()
    {
        var server = ProviderHttp.Serve(new JObject
        {
            ["models"] = new JArray
            {
                new JObject { ["name"] = "qwen3.6:27b" },
                new JObject { ["name"] = "gpt-oss:20b" },
                new JObject { ["name"] = 42 },
                new JObject { ["name"] = string.Empty },
            },
        }.ToString());
        using var provider = new OllamaProvider(server.Client, new OllamaProviderOptions { BaseUrl = new System.Uri("http://ollama-host:11434") });

        var models = await provider.ListModelsAsync();

        Assert.Equal(new[] { "qwen3.6:27b", "gpt-oss:20b" }, models);
        var request = Assert.Single(server.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http://ollama-host:11434/api/tags", request.Uri.ToString());
    }

    [Fact]
    public async Task Ollama_ListModels_OversizedBody_Throws()
    {
        var server = ProviderHttp.Serve("{\"models\":[\"" + new string('m', OllamaProvider.MaxNonStreamingResponseBytes) + "\"]}");
        using var provider = new OllamaProvider(server.Client);

        var ex = await Assert.ThrowsAnyAsync<System.InvalidOperationException>(() => provider.ListModelsAsync());
        Assert.Contains("byte transport budget", ex.Message);
    }

    [Fact]
    public async Task Ollama_WarmAsync_PostsEmptyGenerateWithKeepAlive()
    {
        var server = ProviderHttp.Serve("{\"done\":true}");
        using var provider = new OllamaProvider(server.Client);

        Assert.True(await provider.WarmAsync("qwen3.6:27b"));

        var request = Assert.Single(server.Requests);
        Assert.Equal("http://localhost:11434/api/generate", request.Uri.ToString());
        var sent = JObject.Parse(request.Body);
        Assert.Equal("qwen3.6:27b", (string?)sent["model"]);
        Assert.Equal(string.Empty, (string?)sent["prompt"]);
        Assert.Equal("10m", (string?)sent["keep_alive"]);
    }

    [Fact]
    public async Task Ollama_WarmAsync_ServerErrorReturnsFalse()
    {
        var server = ProviderHttp.ServeWith(HttpStatusCode.NotFound, "{\"error\":\"model not found\"}");
        using var provider = new OllamaProvider(server.Client);

        Assert.False(await provider.WarmAsync("missing"));
    }
}
