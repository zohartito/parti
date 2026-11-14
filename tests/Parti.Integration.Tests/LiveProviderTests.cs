// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Providers.Ollama;
using Parti.Providers.OpenAI;
using Xunit;
using Xunit.Abstractions;

namespace Parti.Integration.Tests;

/// <summary>
/// Opt-in live receipts R6 (native Ollama streaming) and R8 (OpenAI-compatible provider against
/// Ollama's <c>/v1</c>). Skipped unless <c>PARTI_LIVE_PROVIDERS=1</c>. The server defaults to
/// <c>http://ollama-host:11434</c>; override with <c>PARTI_LIVE_OLLAMA_URL</c>. Every request host is
/// recorded and asserted to equal the configured host (egress check).
/// </summary>
public class LiveProviderTests
{
    private const string EchoMarker = "parti-live-7431";
    private readonly ITestOutputHelper output;

    public LiveProviderTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    private static Uri OllamaUrl => new(Environment.GetEnvironmentVariable("PARTI_LIVE_OLLAMA_URL") ?? "http://ollama-host:11434");

    private static AIToolDefinition EchoTool => new(
        "echo",
        "Echo the given text back unchanged.",
        JObject.Parse("{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"Text to echo\"}},\"required\":[\"text\"]}"));

    [LiveProviderFact]
    public async Task R6_OllamaNativeStreaming_ReasoningAndToolRoundTrip()
    {
        var model = Environment.GetEnvironmentVariable("PARTI_LIVE_OLLAMA_MODEL") ?? "qwen3.6:27b";
        var recorder = new EgressRecorder(new HttpClientHandler());
        using var client = new HttpClient(recorder) { Timeout = Timeout.InfiniteTimeSpan };
        using var provider = new OllamaProvider(client, new OllamaProviderOptions { BaseUrl = OllamaUrl });

        var history = new List<IAIInteraction>
        {
            new AIInteractionText
            {
                Agent = AIAgent.User,
                Content = $"Call the echo tool exactly once with text \"{EchoMarker}\". After you receive the tool result, reply with one short sentence that includes the echoed text.",
            },
        };

        var first = await RunTurnAsync(provider, model, history, "R6 turn 1");
        Assert.True(first.Final.Success, first.Final.ErrorMessage);
        Assert.True(first.Reasoning.Length > 0, "Expected non-empty ReasoningDelta from a thinking model.");
        var call = Assert.Single(first.Final.Interactions.OfType<AIInteractionToolCall>());
        Assert.Equal("echo", call.Name);
        Assert.Equal(EchoMarker, (string?)call.Arguments["text"]);

        history.AddRange(first.Final.Interactions);
        history.Add(new AIInteractionToolResult { Id = call.Id, Name = call.Name, Arguments = call.Arguments, Result = new JObject { ["text"] = EchoMarker } });

        var second = await RunTurnAsync(provider, model, history, "R6 turn 2 (continuation)");
        Assert.True(second.Final.Success, second.Final.ErrorMessage);
        Assert.True(second.TextDeltas > 1, $"Expected more than one TextDelta, got {second.TextDeltas}.");
        history.AddRange(second.Final.Interactions);

        // History content is free of reasoning.
        foreach (var text in history.OfType<AIInteractionText>().Where(t => t.Agent == AIAgent.Assistant))
        {
            Assert.DoesNotContain(first.Reasoning, text.Content ?? string.Empty);
            if (second.Reasoning.Length > 0)
            {
                Assert.DoesNotContain(second.Reasoning, text.Content ?? string.Empty);
            }
        }

        // The continuation replayed the first turn's thinking to the same provider.
        var continuationBody = JObject.Parse(recorder.Bodies[1]);
        var assistant = continuationBody["messages"]!.OfType<JObject>().Single(m => (string?)m["role"] == "assistant");
        Assert.Equal(first.Reasoning, (string?)assistant["thinking"]);
        Assert.All(recorder.Hosts, host => Assert.Equal(OllamaUrl.Host, host));
        output.WriteLine($"egress hosts: {string.Join(",", recorder.Hosts.Distinct())}; requests: {recorder.Hosts.Count}");
    }

    [LiveProviderFact]
    public async Task R8_OpenAICompatibleAgainstOllamaV1_StreamAndToolRoundTrip()
    {
        var model = Environment.GetEnvironmentVariable("PARTI_LIVE_OPENAI_MODEL") ?? "gpt-oss:20b";
        var recorder = new EgressRecorder(new HttpClientHandler());
        using var client = new HttpClient(recorder) { Timeout = Timeout.InfiniteTimeSpan };
        using var provider = new OpenAICompatibleProvider(client, OpenAICompatibleProviderOptions.OllamaOpenAI(OllamaUrl.Host));

        var history = new List<IAIInteraction>
        {
            new AIInteractionText
            {
                Agent = AIAgent.User,
                Content = $"Call the echo tool exactly once with text \"{EchoMarker}\". After you receive the tool result, reply with one short sentence that includes the echoed text.",
            },
        };

        var first = await RunTurnAsync(provider, model, history, "R8 turn 1");
        Assert.True(first.Final.Success, first.Final.ErrorMessage);
        var call = Assert.Single(first.Final.Interactions.OfType<AIInteractionToolCall>());
        Assert.Equal("echo", call.Name);
        Assert.Equal(EchoMarker, (string?)call.Arguments["text"]);

        history.AddRange(first.Final.Interactions);
        history.Add(new AIInteractionToolResult { Id = call.Id, Name = call.Name, Arguments = call.Arguments, Result = new JObject { ["text"] = EchoMarker } });

        var second = await RunTurnAsync(provider, model, history, "R8 turn 2 (continuation)");
        Assert.True(second.Final.Success, second.Final.ErrorMessage);
        Assert.True(second.TextDeltas > 1, $"Expected more than one TextDelta, got {second.TextDeltas}.");
        Assert.Contains(EchoMarker, second.Text);

        var continuationBody = JObject.Parse(recorder.Bodies[1]);
        var toolMessage = continuationBody["messages"]!.OfType<JObject>().Single(m => (string?)m["role"] == "tool");
        Assert.Equal(call.Id, (string?)toolMessage["tool_call_id"]);
        Assert.All(recorder.Hosts, host => Assert.Equal(OllamaUrl.Host, host));
        output.WriteLine($"egress hosts: {string.Join(",", recorder.Hosts.Distinct())}; requests: {recorder.Hosts.Count}");
    }

    [LiveProviderFact]
    public async Task OllamaListModels_ReturnsInstalledTags()
    {
        using var provider = new OllamaProvider(new OllamaProviderOptions { BaseUrl = OllamaUrl });
        var models = await provider.ListModelsAsync();
        output.WriteLine($"{models.Count} models; qwen3.6:27b={models.Contains("qwen3.6:27b")}, gpt-oss:20b={models.Contains("gpt-oss:20b")}");
        Assert.NotEmpty(models);
    }

    private async Task<TurnResult> RunTurnAsync(IAIProvider provider, string model, List<IAIInteraction> history, string label)
    {
        var request = new AIRequestCall
        {
            Model = model,
            MaxTokens = 8192,
            Temperature = 0,
            TimeoutSeconds = 600,
            BaseUrl = provider.DefaultServerUrl,
            Interactions = history.ToList(),
            Tools = new List<AIToolDefinition> { EchoTool },
        };

        var stopwatch = Stopwatch.StartNew();
        long firstTokenMs = -1;
        var textDeltas = 0;
        var reasoningDeltas = 0;
        var text = new System.Text.StringBuilder();
        var reasoning = new System.Text.StringBuilder();
        AIReturn? final = null;
        await foreach (var chunk in provider.CallStreamAsync(request))
        {
            if (firstTokenMs < 0 && chunk.Kind is AIStreamChunkKind.TextDelta or AIStreamChunkKind.ReasoningDelta)
            {
                firstTokenMs = stopwatch.ElapsedMilliseconds;
            }

            switch (chunk.Kind)
            {
                case AIStreamChunkKind.TextDelta:
                    textDeltas++;
                    text.Append(chunk.Text);
                    break;
                case AIStreamChunkKind.ReasoningDelta:
                    reasoningDeltas++;
                    reasoning.Append(chunk.Text);
                    break;
                case AIStreamChunkKind.Done:
                    final = chunk.Final;
                    break;
            }
        }

        stopwatch.Stop();
        Assert.NotNull(final);
        var calls = final!.Interactions.OfType<AIInteractionToolCall>().Select(c => $"{c.Name}({c.Arguments.ToString(Newtonsoft.Json.Formatting.None)})");
        output.WriteLine(
            $"[{label}] provider={provider.Name} model={model} status={final.Status} stop={final.StopReason} " +
            $"in={final.InputTokens} out={final.OutputTokens} textDeltas={textDeltas} reasoningDeltas={reasoningDeltas} " +
            $"reasoningChars={reasoning.Length} firstTokenMs={firstTokenMs} totalMs={stopwatch.ElapsedMilliseconds} " +
            $"calls=[{string.Join(";", calls)}] error={final.ErrorMessage}");
        output.WriteLine($"[{label}] text: {text}");
        return new TurnResult(final, textDeltas, text.ToString(), reasoning.ToString());
    }

    private sealed record TurnResult(AIReturn Final, int TextDeltas, string Text, string Reasoning);

    /// <summary>
    /// Records the host and body of every outgoing request.
    /// </summary>
    private sealed class EgressRecorder : DelegatingHandler
    {
        public EgressRecorder(HttpMessageHandler inner)
            : base(inner)
        {
        }

        public List<string> Hosts { get; } = new();

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hosts.Add(request.RequestUri!.Host);
            Bodies.Add(request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A fact that runs only when <c>PARTI_LIVE_PROVIDERS=1</c>.
/// </summary>
public sealed class LiveProviderFactAttribute : FactAttribute
{
    public LiveProviderFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PARTI_LIVE_PROVIDERS") != "1")
        {
            Skip = "Live provider test; set PARTI_LIVE_PROVIDERS=1 to run against a real Ollama server.";
        }
    }
}
