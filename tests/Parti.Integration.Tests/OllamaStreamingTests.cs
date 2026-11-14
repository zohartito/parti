// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Providers.Ollama;
using Parti.Providers.OpenAI;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// Hermetic tests for <see cref="OllamaProvider.CallStreamAsync"/> over canned NDJSON: deltas,
/// reasoning as continuation metadata (A8), atomic batch finalization (A9), and the bounded-output
/// budgets with consistent arithmetic (A10, S14).
/// </summary>
public class OllamaStreamingTests
{
    private static AIRequestCall Request(params IAIInteraction[] history)
    {
        return new AIRequestCall
        {
            Model = "qwen3.6:27b",
            MaxTokens = 8192,
            Interactions = history.Length > 0
                ? history.ToList()
                : new List<IAIInteraction> { new AIInteractionText { Agent = AIAgent.User, Content = "hi" } },
        };
    }

    [Fact]
    public async Task Ollama_StreamsContentAsTextDeltas()
    {
        var body = ProviderHttp.Ndjson(
            ProviderHttp.OllamaLine(content: "Hel"),
            ProviderHttp.OllamaLine(content: "lo"),
            ProviderHttp.OllamaLine(content: " world"),
            ProviderHttp.OllamaLine(done: true, doneReason: "stop", prompt: 12, eval: 3));
        var server = ProviderHttp.Serve(body);
        using var provider = new OllamaProvider(server.Client);

        var chunks = await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        var texts = chunks.Where(c => c.Kind == AIStreamChunkKind.TextDelta).Select(c => c.Text).ToList();
        Assert.Equal(new[] { "Hel", "lo", " world" }, texts);
        var done = Assert.Single(chunks, c => c.Kind == AIStreamChunkKind.Done);
        Assert.Same(done, chunks[^1]);
        var final = done.Final!;
        Assert.True(final.Success, final.ErrorMessage);
        Assert.Equal("end_turn", final.StopReason);
        Assert.Equal(AICallStatus.Finished, final.Status);
        Assert.Equal(12, final.InputTokens);
        Assert.Equal(3, final.OutputTokens);
        Assert.Equal("Hello world", Assert.IsType<AIInteractionText>(Assert.Single(final.Interactions)).Content);

        var sent = JObject.Parse(Assert.Single(server.Requests).Body);
        Assert.True((bool)sent["stream"]!);
        Assert.Equal("http://localhost:11434/api/chat", server.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task Ollama_ThinkingBecomesReasoningDelta_NotContent()
    {
        var body = ProviderHttp.Ndjson(
            ProviderHttp.OllamaLine(thinking: "Let me "),
            ProviderHttp.OllamaLine(thinking: "think."),
            ProviderHttp.OllamaLine(content: "Answer"),
            ProviderHttp.OllamaLine(done: true, doneReason: "stop"));
        var server = ProviderHttp.Serve(body);
        using var provider = new OllamaProvider(server.Client);

        var chunks = await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        Assert.Equal(new[] { "Let me ", "think." },
            chunks.Where(c => c.Kind == AIStreamChunkKind.ReasoningDelta).Select(c => c.Text));
        Assert.Equal(new[] { "Answer" },
            chunks.Where(c => c.Kind == AIStreamChunkKind.TextDelta).Select(c => c.Text));

        var text = Assert.IsType<AIInteractionText>(Assert.Single(chunks[^1].Final!.Interactions));
        Assert.Equal("Answer", text.Content);
        Assert.Equal("Let me think.", text.Reasoning);
        Assert.DoesNotContain("think", text.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ollama_ThinkingOnlyLengthStop_IsErrorWithGuidance()
    {
        var body = ProviderHttp.Ndjson(
            ProviderHttp.OllamaLine(thinking: "a long chain of thought"),
            ProviderHttp.OllamaLine(done: true, doneReason: "length", eval: 256));
        var server = ProviderHttp.Serve(body);
        using var provider = new OllamaProvider(server.Client);

        var chunks = await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        var final = chunks[^1].Final!;
        Assert.False(final.Success);
        Assert.Equal(OllamaProvider.ReasoningBudgetExhaustedMessage, final.ErrorMessage);
        Assert.Contains("raise num_predict", final.ErrorMessage);
        Assert.Empty(final.Interactions);
        Assert.DoesNotContain(chunks, c => c.Kind == AIStreamChunkKind.TextDelta);
    }

    [Fact]
    public async Task Ollama_ContentLengthStop_IsMaxTokensWithoutCalls()
    {
        var body = ProviderHttp.Ndjson(
            ProviderHttp.OllamaLine(content: "partial"),
            ProviderHttp.OllamaLine(done: true, doneReason: "length"));
        var server = ProviderHttp.Serve(body);
        using var provider = new OllamaProvider(server.Client);

        var final = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request())))[^1].Final!;

        Assert.True(final.Success);
        Assert.Equal("max_tokens", final.StopReason);
        Assert.False(final.HasToolCalls);
    }

    [Theory]
    [InlineData(100, 256)]
    [InlineData(8192, 8192)]
    [InlineData(50_000, 16_384)]
    public async Task Ollama_SendsNumPredictAndNumCtx(int maxTokens, int expectedNumPredict)
    {
        var server = ProviderHttp.Serve(ProviderHttp.Ndjson(ProviderHttp.OllamaLine(content: "ok"), ProviderHttp.OllamaLine(done: true, doneReason: "stop")));
        using var provider = new OllamaProvider(server.Client);
        var request = Request();
        request.MaxTokens = maxTokens;
        request.Temperature = 0;

        await ProviderHttp.CollectAsync(provider.CallStreamAsync(request));

        var sent = JObject.Parse(Assert.Single(server.Requests).Body);
        Assert.Equal(expectedNumPredict, (int)sent["options"]!["num_predict"]!);
        Assert.Equal(32_768, (int)sent["options"]!["num_ctx"]!);
        Assert.Equal(0d, (double)sent["options"]!["temperature"]!);
        Assert.Equal("10m", (string?)sent["keep_alive"]);
        Assert.Null(sent["think"]);
    }

    [Fact]
    public async Task Ollama_SendsConfiguredThinkAndContext()
    {
        var server = ProviderHttp.Serve(ProviderHttp.Ndjson(ProviderHttp.OllamaLine(content: "ok"), ProviderHttp.OllamaLine(done: true)));
        using var provider = new OllamaProvider(server.Client, new OllamaProviderOptions
        {
            NumCtx = 8192,
            KeepAlive = "30m",
            Think = "high",
        });

        await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        var sent = JObject.Parse(server.Requests[0].Body);
        Assert.Equal(8192, (int)sent["options"]!["num_ctx"]!);
        Assert.Equal("30m", (string?)sent["keep_alive"]);
        Assert.Equal("high", (string?)sent["think"]);
    }

    [Fact]
    public async Task Ollama_UsesConfiguredEndpoint()
    {
        var server = ProviderHttp.Serve(ProviderHttp.Ndjson(ProviderHttp.OllamaLine(content: "ok"), ProviderHttp.OllamaLine(done: true)));
        var endpoint = new Uri("http://ollama-host:11434");
        using var provider = new OllamaProvider(server.Client, new OllamaProviderOptions { BaseUrl = endpoint });

        Assert.Equal(endpoint, provider.DefaultServerUrl);
        var request = Request();
        request.BaseUrl = provider.DefaultServerUrl;

        await ProviderHttp.CollectAsync(provider.CallStreamAsync(request));

        Assert.Equal("http://ollama-host:11434/api/chat", Assert.Single(server.Requests).Uri.ToString());
    }

    [Fact]
    public async Task Ollama_ToolCallsInStream_ProduceToolUseStop()
    {
        var body = ProviderHttp.Ndjson(
            ProviderHttp.OllamaLine(thinking: "I should call echo."),
            ProviderHttp.OllamaLine(toolCalls: ProviderHttp.OllamaToolCalls(("echo", new JObject { ["text"] = "hi" }))),
            ProviderHttp.OllamaLine(done: true, doneReason: "stop", prompt: 40, eval: 9));
        var server = ProviderHttp.Serve(body);
        using var provider = new OllamaProvider(server.Client);

        var chunks = await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        var kinds = chunks.Select(c => c.Kind).ToList();
        Assert.Equal(
            new[] { AIStreamChunkKind.ReasoningDelta, AIStreamChunkKind.ToolCallStart, AIStreamChunkKind.ToolCallComplete, AIStreamChunkKind.Done },
            kinds);
        var final = chunks[^1].Final!;
        Assert.True(final.Success, final.ErrorMessage);
        Assert.Equal("tool_use", final.StopReason);
        Assert.Equal(AICallStatus.CallingTools, final.Status);
        var call = Assert.IsType<AIInteractionToolCall>(Assert.Single(final.Interactions));
        Assert.Equal("echo", call.Name);
        Assert.Equal("hi", (string?)call.Arguments["text"]);
        Assert.StartsWith("ollama_", call.Id);
        Assert.Equal(call.Id, chunks[1].ToolUseId);
        Assert.Equal("{\"text\":\"hi\"}", chunks[2].ToolArgsPartial);
        Assert.Equal("I should call echo.", call.Reasoning);
    }

    [Fact]
    public async Task Ollama_ToolCallsWithoutDone_AreNotReturned()
    {
        var body = ProviderHttp.Ndjson(
            ProviderHttp.OllamaLine(toolCalls: ProviderHttp.OllamaToolCalls(("echo", new JObject { ["text"] = "hi" }))));
        var server = ProviderHttp.Serve(body);
        using var provider = new OllamaProvider(server.Client);

        var chunks = await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        Assert.DoesNotContain(chunks, c => c.Kind is AIStreamChunkKind.ToolCallStart or AIStreamChunkKind.ToolCallComplete);
        var final = chunks[^1].Final!;
        Assert.False(final.Success);
        Assert.False(final.HasToolCalls);
        Assert.Contains("no tool calls were run", final.ErrorMessage);
    }

    [Fact]
    public async Task Ollama_ToolCallsThenLengthStop_IsErrorWithNoCalls()
    {
        var body = ProviderHttp.Ndjson(
            ProviderHttp.OllamaLine(toolCalls: ProviderHttp.OllamaToolCalls(("echo", new JObject { ["text"] = "hi" }))),
            ProviderHttp.OllamaLine(done: true, doneReason: "length"));
        var server = ProviderHttp.Serve(body);
        using var provider = new OllamaProvider(server.Client);

        var chunks = await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        Assert.DoesNotContain(chunks, c => c.Kind is AIStreamChunkKind.ToolCallStart or AIStreamChunkKind.ToolCallComplete);
        Assert.False(chunks[^1].Final!.Success);
        Assert.Empty(chunks[^1].Final!.Interactions);
    }

    [Fact]
    public async Task Ollama_ReasoningIsNotReEncodedIntoNextRequest()
    {
        // History whose reasoning did not come from this provider instance (another provider, a
        // restored transcript, or a hand-built interaction) must never be sent, and reasoning is
        // never folded into content.
        var server = ProviderHttp.Serve(ProviderHttp.Ndjson(ProviderHttp.OllamaLine(content: "ok"), ProviderHttp.OllamaLine(done: true)));
        using var provider = new OllamaProvider(server.Client);
        var foreignCall = new AIInteractionToolCall
        {
            Id = "call_1",
            Name = "echo",
            Arguments = new JObject { ["text"] = "hi" },
            Reasoning = "FOREIGN-TOOL-REASONING",
        };
        var request = Request(
            new AIInteractionText { Agent = AIAgent.User, Content = "q" },
            new AIInteractionText { Agent = AIAgent.Assistant, Content = "visible", Reasoning = "FOREIGN-REASONING" },
            foreignCall,
            new AIInteractionToolResult { Id = "call_1", Name = "echo", Result = new JObject { ["text"] = "hi" } });

        await ProviderHttp.CollectAsync(provider.CallStreamAsync(request));

        var body = server.Requests[0].Body;
        Assert.DoesNotContain("FOREIGN", body);
        Assert.DoesNotContain("thinking", body);
        var assistant = JObject.Parse(body)["messages"]!.OfType<JObject>().Single(m => (string?)m["role"] == "assistant");
        Assert.Equal("visible", (string?)assistant["content"]);
    }

    [Fact]
    public async Task Ollama_ReasoningReplayedOnToolLoopContinuation_SameProviderOnly()
    {
        var first = ProviderHttp.Ndjson(
            ProviderHttp.OllamaLine(thinking: "plan: call echo"),
            ProviderHttp.OllamaLine(toolCalls: ProviderHttp.OllamaToolCalls(("echo", new JObject { ["text"] = "hi" }))),
            ProviderHttp.OllamaLine(done: true, doneReason: "stop"));
        var second = ProviderHttp.Ndjson(ProviderHttp.OllamaLine(content: "done"), ProviderHttp.OllamaLine(done: true, doneReason: "stop"));
        var server = ProviderHttp.Serve(first, second, second, second);
        using var provider = new OllamaProvider(server.Client);

        var history = new List<IAIInteraction> { new AIInteractionText { Agent = AIAgent.User, Content = "echo hi" } };
        var turn1 = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request(history.ToArray()))))[^1].Final!;
        var call = Assert.IsType<AIInteractionToolCall>(Assert.Single(turn1.Interactions));
        history.AddRange(turn1.Interactions);
        history.Add(new AIInteractionToolResult { Id = call.Id, Name = call.Name, Arguments = call.Arguments, Result = new JObject { ["text"] = "hi" } });

        // Continuation through the same provider replays the thinking on the assistant message.
        await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request(history.ToArray())));
        var messages = JObject.Parse(server.Requests[1].Body)["messages"]!.OfType<JObject>().ToList();
        var assistant = messages.Single(m => (string?)m["role"] == "assistant");
        Assert.Equal("plan: call echo", (string?)assistant["thinking"]);
        Assert.Equal(string.Empty, (string?)assistant["content"]);
        var tool = messages.Single(m => (string?)m["role"] == "tool");
        Assert.Equal("echo", (string?)tool["tool_name"]);

        // A different provider instance never receives this reasoning.
        using var other = new OllamaProvider(server.Client);
        await ProviderHttp.CollectAsync(other.CallStreamAsync(Request(history.ToArray())));
        Assert.DoesNotContain("plan: call echo", server.Requests[2].Body);

        // Once the user speaks again the old loop's reasoning is no longer replayed.
        history.Add(new AIInteractionText { Agent = AIAgent.User, Content = "next" });
        await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request(history.ToArray())));
        Assert.DoesNotContain("plan: call echo", server.Requests[3].Body);
    }

    [Fact]
    public async Task Ollama_MalformedLine_FailsClosed()
    {
        var body = ProviderHttp.Ndjson(ProviderHttp.OllamaLine(content: "a")) + "{not json\n" + ProviderHttp.Ndjson(ProviderHttp.OllamaLine(done: true));
        var server = ProviderHttp.Serve(body);
        using var provider = new OllamaProvider(server.Client);

        var final = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request())))[^1].Final!;

        Assert.False(final.Success);
        Assert.Contains("Malformed", final.ErrorMessage);
    }

    [Fact]
    public async Task Ollama_ErrorLineMidStream_IsError()
    {
        var body = ProviderHttp.Ndjson(ProviderHttp.OllamaLine(content: "a"), new JObject { ["error"] = "model crashed" });
        var server = ProviderHttp.Serve(body);
        using var provider = new OllamaProvider(server.Client);

        var final = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request())))[^1].Final!;

        Assert.False(final.Success);
        Assert.Equal("Ollama error: model crashed", final.ErrorMessage);
    }

    [Fact]
    public async Task Ollama_ExternalCancellation_Throws()
    {
        var body = ProviderHttp.Ndjson(Enumerable.Range(0, 50).Select(_ => ProviderHttp.OllamaLine(content: "x")).ToArray());
        var handler = FakeHttpMessageHandler.FromSse(body, chunkSize: 64, perChunkDelay: TimeSpan.FromMilliseconds(20));
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in provider.CallStreamAsync(Request(), cts.Token))
            {
                if (chunk.Kind == AIStreamChunkKind.TextDelta)
                {
                    cts.Cancel();
                }
            }
        });
    }

    // --- budgets (S14, A10) ----------------------------------------------------------------

    [Fact]
    public async Task Ollama_StreamBytesBudget_FailsClosed()
    {
        var pad = new string('p', 250_000);
        var lines = Enumerable.Range(0, 70).Select(_ => new JObject { ["pad"] = pad, ["done"] = false }).ToArray();
        var server = ProviderHttp.Serve(ProviderHttp.Ndjson(lines));
        using var provider = new OllamaProvider(server.Client);

        var final = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request())))[^1].Final!;

        Assert.False(final.Success);
        Assert.Contains($"{OllamaProvider.MaxStreamingResponseBytes}-byte", final.ErrorMessage);
    }

    [Fact]
    public async Task Ollama_DeclaredContentLengthOverBudget_FailsBeforeReading()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var content = new ByteArrayContent(new byte[16]);
            content.Headers.ContentLength = (long)OllamaProvider.MaxStreamingResponseBytes + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client);

        var final = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request())))[^1].Final!;

        Assert.False(final.Success);
        Assert.Contains("byte transport budget", final.ErrorMessage);
    }

    [Fact]
    public async Task Ollama_EventBudget_FailsClosed()
    {
        var sb = new StringBuilder();
        for (var i = 0; i <= OllamaProvider.MaxStreamEvents; i++)
        {
            sb.Append("{\"message\":{\"content\":\"\"},\"done\":false}\n");
        }

        var server = ProviderHttp.Serve(sb.ToString());
        using var provider = new OllamaProvider(server.Client);

        var final = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request())))[^1].Final!;

        Assert.False(final.Success);
        Assert.Contains($"{OllamaProvider.MaxStreamEvents}-event", final.ErrorMessage);
    }

    [Fact]
    public async Task Ollama_PhysicalLineBudget_FailsClosed()
    {
        var server = ProviderHttp.Serve(new string('\n', OllamaProvider.MaxPhysicalLines + 1));
        using var provider = new OllamaProvider(server.Client);

        var final = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request())))[^1].Final!;

        Assert.False(final.Success);
        Assert.Contains($"{OllamaProvider.MaxPhysicalLines}-line", final.ErrorMessage);
    }

    [Fact]
    public async Task Ollama_LineLengthBudget_FailsClosed()
    {
        var line = new JObject { ["pad"] = new string('p', OllamaProvider.MaxStreamLineCharacters) }.ToString(Formatting.None);
        var server = ProviderHttp.Serve(line + "\n" + ProviderHttp.Ndjson(ProviderHttp.OllamaLine(done: true)));
        using var provider = new OllamaProvider(server.Client);

        var final = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request())))[^1].Final!;

        Assert.False(final.Success);
        Assert.Contains($"{OllamaProvider.MaxStreamLineCharacters}-character budget", final.ErrorMessage);
    }

    [Fact]
    public async Task Ollama_TextBudget_FailsClosed()
    {
        var piece = new string('t', 200_000);
        var server = ProviderHttp.Serve(ProviderHttp.Ndjson(
            ProviderHttp.OllamaLine(content: piece), ProviderHttp.OllamaLine(content: piece), ProviderHttp.OllamaLine(content: piece),
            ProviderHttp.OllamaLine(done: true)));
        using var provider = new OllamaProvider(server.Client);

        var final = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request())))[^1].Final!;

        Assert.False(final.Success);
        Assert.Contains($"{OllamaProvider.MaxStreamTextCharacters}-character text budget", final.ErrorMessage);
    }

    [Fact]
    public async Task Ollama_ReasoningBudget_FailsClosed()
    {
        var piece = new string('r', 200_000);
        var server = ProviderHttp.Serve(ProviderHttp.Ndjson(
            ProviderHttp.OllamaLine(thinking: piece), ProviderHttp.OllamaLine(thinking: piece), ProviderHttp.OllamaLine(thinking: piece),
            ProviderHttp.OllamaLine(done: true)));
        using var provider = new OllamaProvider(server.Client);

        var final = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request())))[^1].Final!;

        Assert.False(final.Success);
        Assert.Contains($"{OllamaProvider.MaxReasoningCharacters}-character reasoning budget", final.ErrorMessage);
    }

    [Fact]
    public async Task Ollama_ToolArgumentsBudget_FailsClosed()
    {
        var args = ProviderHttp.ArgumentsOfSerializedLength(OllamaProvider.MaxToolArgumentCharacters + 1);
        var server = ProviderHttp.Serve(ProviderHttp.Ndjson(
            ProviderHttp.OllamaLine(toolCalls: ProviderHttp.OllamaToolCalls(("echo", args))),
            ProviderHttp.OllamaLine(done: true)));
        using var provider = new OllamaProvider(server.Client);

        var chunks = await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        Assert.False(chunks[^1].Final!.Success);
        Assert.Contains($"{OllamaProvider.MaxToolArgumentCharacters}-character budget", chunks[^1].Final!.ErrorMessage);
        Assert.DoesNotContain(chunks, c => c.Kind == AIStreamChunkKind.ToolCallComplete);
    }

    [Fact]
    public async Task Ollama_WholeObjectToolCallAtSizeLimit_Succeeds()
    {
        // A10: Ollama sends a whole tool call in one line, so the line budget must admit a call
        // whose arguments are exactly at the tool-argument limit.
        var args = ProviderHttp.ArgumentsOfSerializedLength(OllamaProvider.MaxToolArgumentCharacters);
        var server = ProviderHttp.Serve(ProviderHttp.Ndjson(
            ProviderHttp.OllamaLine(toolCalls: ProviderHttp.OllamaToolCalls(("echo", args))),
            ProviderHttp.OllamaLine(done: true, doneReason: "stop")));
        using var provider = new OllamaProvider(server.Client);

        var final = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request())))[^1].Final!;

        Assert.True(final.Success, final.ErrorMessage);
        var call = Assert.IsType<AIInteractionToolCall>(Assert.Single(final.Interactions));
        Assert.Equal(OllamaProvider.MaxToolArgumentCharacters, call.Arguments.ToString(Formatting.None).Length);
    }

    [Fact]
    public async Task Ollama_MaximumTokenStreamWithTerminalEvents_Succeeds()
    {
        // A10: a stream at the num_predict clamp, where every token of both reasoning and content
        // arrives as its own realistic NDJSON line, plus the terminal event, fits every budget.
        var sb = new StringBuilder();
        for (var i = 0; i < OllamaProvider.MaxNumPredict; i++)
        {
            sb.Append(ProviderHttp.RealisticOllamaLine(thinking: "th"));
            sb.Append(ProviderHttp.RealisticOllamaLine(content: "co"));
        }

        sb.Append(ProviderHttp.Ndjson(ProviderHttp.OllamaLine(done: true, doneReason: "stop", prompt: 1000, eval: 2 * OllamaProvider.MaxNumPredict)));
        var server = ProviderHttp.Serve(sb.ToString());
        using var provider = new OllamaProvider(server.Client);

        var final = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request())))[^1].Final!;

        Assert.True(final.Success, final.ErrorMessage);
        var text = Assert.IsType<AIInteractionText>(Assert.Single(final.Interactions));
        Assert.Equal(2 * OllamaProvider.MaxNumPredict, text.Content!.Length);
        Assert.True(sb.Length < OllamaProvider.MaxStreamingResponseBytes);
    }

    [Fact]
    public void NumPredictClampAndLineBudgetAreConsistent()
    {
        // Events: every token of reasoning and content may be its own line, plus terminal/usage.
        Assert.True(OllamaProvider.MaxStreamEvents >= (2 * OllamaProvider.MaxNumPredict) + OllamaProvider.TerminalEventMargin);
        Assert.True(OllamaProvider.MaxPhysicalLines >= OllamaProvider.MaxStreamEvents);
        Assert.True(OllamaProvider.ClampNumPredict(int.MaxValue) == OllamaProvider.MaxNumPredict);
        Assert.True(OllamaProvider.ClampNumPredict(0) == OllamaProvider.MinNumPredict);

        // Bytes: every event may use its per-line allowance.
        Assert.True((long)OllamaProvider.MaxStreamEvents * OllamaProvider.StreamLineByteAllowance <= OllamaProvider.MaxStreamingResponseBytes);

        // Line length: a whole tool call at the argument limit plus its JSON envelope fits one line.
        Assert.True(OllamaProvider.MaxStreamLineCharacters >= OllamaProvider.MaxToolArgumentCharacters + 4_096);

        // The same arithmetic holds for the OpenAI-compatible provider (SSE events, not physical lines).
        Assert.True(OpenAICompatibleProvider.MaxStreamEvents >= (2 * OpenAICompatibleProvider.MaxOutputTokens) + OpenAICompatibleProvider.TerminalEventMargin);
        Assert.True(OpenAICompatibleProvider.MaxPhysicalLines >= 2 * OpenAICompatibleProvider.MaxStreamEvents);
        Assert.True((long)OpenAICompatibleProvider.MaxStreamEvents * OpenAICompatibleProvider.StreamEventByteAllowance <= OpenAICompatibleProvider.MaxStreamingResponseBytes);
        Assert.True(OpenAICompatibleProvider.MaxStreamLineCharacters >= OpenAICompatibleProvider.MaxToolArgumentCharacters + 4_096);

        // A realistic per-token line is well inside the allowance.
        Assert.True(Encoding.UTF8.GetByteCount(ProviderHttp.RealisticOllamaLine(content: "xx")) < OllamaProvider.StreamLineByteAllowance);
    }
}

/// <summary>
/// Shared canned-HTTP helpers for the provider tests. Captures each request's URI, headers and
/// body inside the handler, before the provider disposes the request.
/// </summary>
internal static class ProviderHttp
{
    internal sealed record CapturedRequest(Uri Uri, HttpMethod Method, string Body, string? Authorization, IReadOnlyDictionary<string, string> Headers);

    internal sealed class Server
    {
        public Server(FakeHttpMessageHandler handler)
        {
            Handler = handler;
            Client = new HttpClient(handler);
        }

        public FakeHttpMessageHandler Handler { get; }

        public HttpClient Client { get; }

        public List<CapturedRequest> Requests { get; } = new();
    }

    /// <summary>
    /// Serves the given bodies in order (the last one repeats) with a 200 status.
    /// </summary>
    public static Server Serve(params string[] bodies) => ServeWith(HttpStatusCode.OK, bodies);

    public static Server ServeWith(HttpStatusCode status, params string[] bodies)
    {
        Server? server = null;
        var handler = new FakeHttpMessageHandler(async (request, ct) =>
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            server!.Requests.Add(new CapturedRequest(request.RequestUri!, request.Method, body, request.Headers.Authorization?.ToString(), headers));
            var index = Math.Min(server.Requests.Count - 1, bodies.Length - 1);
            return new HttpResponseMessage(status)
            {
                Content = new StreamContent(new System.IO.MemoryStream(Encoding.UTF8.GetBytes(bodies[index]))),
            };
        });
        server = new Server(handler);
        return server;
    }

    public static async Task<List<AIStreamChunk>> CollectAsync(IAsyncEnumerable<AIStreamChunk> stream)
    {
        var chunks = new List<AIStreamChunk>();
        await foreach (var chunk in stream)
        {
            chunks.Add(chunk);
        }

        Assert.Equal(AIStreamChunkKind.Done, chunks[^1].Kind);
        Assert.Single(chunks, c => c.Kind == AIStreamChunkKind.Done);
        return chunks;
    }

    public static string Ndjson(params JObject[] lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.Append(line.ToString(Formatting.None)).Append('\n');
        }

        return sb.ToString();
    }

    public static JObject OllamaLine(
        string? content = null,
        string? thinking = null,
        JArray? toolCalls = null,
        bool done = false,
        string? doneReason = null,
        int? prompt = null,
        int? eval = null)
    {
        var message = new JObject { ["role"] = "assistant", ["content"] = content ?? string.Empty };
        if (thinking != null)
        {
            message["thinking"] = thinking;
        }

        if (toolCalls != null)
        {
            message["tool_calls"] = toolCalls;
        }

        var line = new JObject
        {
            ["model"] = "qwen3.6:27b",
            ["created_at"] = "2026-09-27T22:59:45.4823487Z",
            ["message"] = message,
            ["done"] = done,
        };
        if (doneReason != null)
        {
            line["done_reason"] = doneReason;
        }

        if (prompt != null)
        {
            line["prompt_eval_count"] = prompt;
        }

        if (eval != null)
        {
            line["eval_count"] = eval;
        }

        return line;
    }

    /// <summary>
    /// One NDJSON line shaped exactly like a live qwen3.6:27b token line (2026-09-27 capture).
    /// </summary>
    public static string RealisticOllamaLine(string? content = null, string? thinking = null)
    {
        return Ndjson(OllamaLine(content: content, thinking: thinking));
    }

    public static JArray OllamaToolCalls(params (string Name, JObject Arguments)[] calls)
    {
        var array = new JArray();
        for (var i = 0; i < calls.Length; i++)
        {
            array.Add(new JObject
            {
                ["id"] = $"call_{i}",
                ["function"] = new JObject { ["index"] = i, ["name"] = calls[i].Name, ["arguments"] = calls[i].Arguments },
            });
        }

        return array;
    }

    /// <summary>
    /// Builds <c>{"text":"xxx…"}</c> whose compact serialization is exactly <paramref name="length"/> characters.
    /// </summary>
    public static JObject ArgumentsOfSerializedLength(int length)
    {
        const int envelope = 11; // {"text":""}
        var args = new JObject { ["text"] = new string('a', length - envelope) };
        Assert.Equal(length, args.ToString(Formatting.None).Length);
        return args;
    }

    public static string Sse(params JObject[] events)
    {
        var sb = new StringBuilder();
        foreach (var evt in events)
        {
            sb.Append("data: ").Append(evt.ToString(Formatting.None)).Append("\n\n");
        }

        return sb.ToString();
    }

    public const string SseDone = "data: [DONE]\n\n";
}
