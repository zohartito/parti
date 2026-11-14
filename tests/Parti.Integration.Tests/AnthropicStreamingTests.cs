// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Parti.Core.Ai;
using Parti.Providers.Anthropic;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// Hermetic tests for <see cref="AnthropicProvider.CallStreamAsync"/>. Uses
/// <see cref="FakeHttpMessageHandler"/> with canned SSE bodies — no real network traffic.
/// </summary>
public class AnthropicStreamingTests
{
    private static AIRequestCall MakeRequest()
    {
        return new AIRequestCall
        {
            Model = "claude-opus-4-7",
            ApiKey = "test-key",
            MaxTokens = 128,
            Temperature = 0.2,
            SystemPrompt = "You are a helpful assistant.",
            Interactions = new List<IAIInteraction>
            {
                new AIInteractionText { Agent = AIAgent.User, Content = "hi" },
            },
        };
    }

    private static string TextOnlySse() =>
        "event: message_start\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"role\":\"assistant\",\"usage\":{\"input_tokens\":10,\"output_tokens\":0}}}\n\n" +
        "event: content_block_start\n" +
        "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\", \"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"world!\"}}\n\n" +
        "event: content_block_stop\n" +
        "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}\n\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private static string ToolUseSse() =>
        "event: message_start\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_2\",\"role\":\"assistant\",\"usage\":{\"input_tokens\":15,\"output_tokens\":0}}}\n\n" +
        "event: content_block_start\n" +
        "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_abc\",\"name\":\"rectangle_area\",\"input\":{}}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"width\\\":\"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"3,\\\"height\\\":\"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"4}\"}}\n\n" +
        "event: content_block_stop\n" +
        "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"input_tokens\":15,\"output_tokens\":18}}\n\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private static string TextThenToolUseSse() =>
        "event: message_start\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_3\",\"role\":\"assistant\",\"usage\":{\"input_tokens\":20,\"output_tokens\":0}}}\n\n" +
        "event: content_block_start\n" +
        "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Computing...\"}}\n\n" +
        "event: content_block_stop\n" +
        "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: content_block_start\n" +
        "data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_xyz\",\"name\":\"gh_get_all\",\"input\":{}}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{}\"}}\n\n" +
        "event: content_block_stop\n" +
        "data: {\"type\":\"content_block_stop\",\"index\":1}\n\n" +
        "event: message_delta\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"input_tokens\":20,\"output_tokens\":30}}\n\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private static async Task<List<AIStreamChunk>> CollectAsync(IAsyncEnumerable<AIStreamChunk> source, CancellationToken ct = default)
    {
        var list = new List<AIStreamChunk>();
        await foreach (var chunk in source.WithCancellation(ct))
        {
            list.Add(chunk);
        }
        return list;
    }

    [Fact]
    public async Task StreamsTextChunks_WhenResponseIsPlainText()
    {
        var handler = FakeHttpMessageHandler.FromSse(TextOnlySse());
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http);

        var chunks = await CollectAsync(provider.CallStreamAsync(MakeRequest()));

        var deltas = chunks.Where(c => c.Kind == AIStreamChunkKind.TextDelta).Select(c => c.Text).ToList();
        Assert.Equal(new[] { "Hello", ", ", "world!" }, deltas);

        var done = Assert.Single(chunks, c => c.Kind == AIStreamChunkKind.Done);
        Assert.NotNull(done.Final);
        Assert.Equal(AICallStatus.Finished, done.Final!.Status);
        Assert.Equal("end_turn", done.Final.StopReason);
        Assert.Equal("end_turn", done.StopReason);

        var text = Assert.IsType<AIInteractionText>(done.Final.Interactions.Single());
        Assert.Equal("Hello, world!", text.Content);
        Assert.Equal(AIAgent.Assistant, text.Agent);
        Assert.Equal(10, done.Final.InputTokens);
        Assert.Equal(5, done.Final.OutputTokens);

        // The last chunk is the Done terminator — no chunks follow it.
        Assert.Equal(AIStreamChunkKind.Done, chunks[^1].Kind);

        // The provider must have asked for SSE.
        var req = Assert.Single(handler.ReceivedRequests);
        Assert.Contains(req.Headers.Accept, h => h.MediaType == "text/event-stream");
        Assert.Equal("test-key", req.Headers.GetValues("x-api-key").Single());
    }

    [Fact]
    public async Task StreamsToolCallsAcrossBlocks()
    {
        var handler = FakeHttpMessageHandler.FromSse(ToolUseSse());
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http);

        var chunks = await CollectAsync(provider.CallStreamAsync(MakeRequest()));

        var start = Assert.Single(chunks, c => c.Kind == AIStreamChunkKind.ToolCallStart);
        Assert.Equal("rectangle_area", start.ToolName);
        Assert.Equal("toolu_abc", start.ToolUseId);

        var argDeltas = chunks.Where(c => c.Kind == AIStreamChunkKind.ToolArgDelta).ToList();
        Assert.Equal(3, argDeltas.Count);
        // Incremental — the provider no longer recopies every cumulative prefix into a chunk.
        Assert.Equal("{\"width\":", argDeltas[0].ToolArgsPartial);
        Assert.Equal("3,\"height\":", argDeltas[1].ToolArgsPartial);
        Assert.Equal("4}", argDeltas[2].ToolArgsPartial);

        var complete = Assert.Single(chunks, c => c.Kind == AIStreamChunkKind.ToolCallComplete);
        Assert.Equal("rectangle_area", complete.ToolName);
        Assert.Equal("toolu_abc", complete.ToolUseId);
        Assert.Equal("{\"width\":3,\"height\":4}", complete.ToolArgsPartial);

        var done = Assert.Single(chunks, c => c.Kind == AIStreamChunkKind.Done);
        Assert.NotNull(done.Final);
        Assert.Equal(AICallStatus.CallingTools, done.Final!.Status);
        Assert.Equal("tool_use", done.Final.StopReason);
        Assert.True(done.Final.HasToolCalls);

        var tc = Assert.Single(
            done.Final.Interactions,
            i => i is AIInteractionToolCall and not AIInteractionToolResult) as AIInteractionToolCall;
        Assert.NotNull(tc);
        Assert.Equal("rectangle_area", tc!.Name);
        Assert.Equal("toolu_abc", tc.Id);
        Assert.Equal(3, (int)tc.Arguments["width"]!);
        Assert.Equal(4, (int)tc.Arguments["height"]!);
    }

    [Fact]
    public async Task HandlesMixedTextThenToolUse()
    {
        var handler = FakeHttpMessageHandler.FromSse(TextThenToolUseSse());
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http);

        var chunks = await CollectAsync(provider.CallStreamAsync(MakeRequest()));

        Assert.Contains(chunks, c => c.Kind == AIStreamChunkKind.TextDelta && c.Text == "Computing...");
        Assert.Single(chunks, c => c.Kind == AIStreamChunkKind.ToolCallStart);
        Assert.Single(chunks, c => c.Kind == AIStreamChunkKind.ToolCallComplete);

        var done = Assert.Single(chunks, c => c.Kind == AIStreamChunkKind.Done);
        Assert.NotNull(done.Final);
        Assert.Equal("tool_use", done.Final!.StopReason);
        Assert.Equal(AICallStatus.CallingTools, done.Final.Status);

        // Final interactions carry both a text block and a tool call.
        var text = Assert.Single(done.Final.Interactions, i => i is AIInteractionText) as AIInteractionText;
        Assert.NotNull(text);
        Assert.Equal("Computing...", text!.Content);
        Assert.Equal(AIAgent.Assistant, text.Agent);

        var tc = Assert.Single(
            done.Final.Interactions,
            i => i is AIInteractionToolCall and not AIInteractionToolResult) as AIInteractionToolCall;
        Assert.NotNull(tc);
        Assert.Equal("gh_get_all", tc!.Name);
        Assert.Equal("toolu_xyz", tc.Id);
    }

    [Fact]
    public async Task CancellationTokenStopsStream()
    {
        // Pace byte delivery at 8 bytes / 50 ms so we can cancel mid-stream.
        var handler = FakeHttpMessageHandler.FromSse(
            TextOnlySse(),
            chunkSize: 8,
            perChunkDelay: TimeSpan.FromMilliseconds(50));
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http);

        using var cts = new CancellationTokenSource();

        async Task EnumerateAndCancel()
        {
            var seen = 0;
            await foreach (var chunk in provider.CallStreamAsync(MakeRequest(), cts.Token))
            {
                seen++;
                if (chunk.Kind == AIStreamChunkKind.TextDelta)
                {
                    cts.Cancel();
                }
            }
            // If we get here without throwing, the test should fail — but count stays > 0.
            Assert.Fail($"Expected OperationCanceledException after cancellation, saw {seen} chunks.");
        }

        // Safety net — don't wedge CI if cancellation is broken.
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(EnumerateAndCancel);
    }

    [Fact]
    public async Task MalformedEventProducesErrorDone()
    {
        // Every "data:" line is garbage — no usable events survive.
        var garbage =
            "data: {not-json}\n\n" +
            "data: still garbage, no way\n\n" +
            "data: \\x00\\x01\n\n";

        var handler = FakeHttpMessageHandler.FromSse(garbage);
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http);

        var chunks = await CollectAsync(provider.CallStreamAsync(MakeRequest()));

        // Should not throw. Enumeration must complete with a Done chunk.
        var done = Assert.Single(chunks);
        Assert.Equal(AIStreamChunkKind.Done, done.Kind);
        Assert.NotNull(done.Final);
        Assert.Equal(AICallStatus.Error, done.Final!.Status);
        Assert.False(done.Final.Success);
        Assert.False(string.IsNullOrEmpty(done.Final.ErrorMessage));
    }

    [Fact]
    public async Task RejectsOversizedSseLineBeforeJsonMaterialization()
    {
        var oversizedLine = "data: " + new string('x', AnthropicProvider.MaxSseLineCharacters + 1) + "\n\n";
        var handler = FakeHttpMessageHandler.FromSse(oversizedLine);
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http);

        var chunks = await CollectAsync(provider.CallStreamAsync(MakeRequest()));

        var done = Assert.Single(chunks);
        Assert.Equal(AIStreamChunkKind.Done, done.Kind);
        Assert.Equal(AICallStatus.Error, done.Final!.Status);
        Assert.Contains("line exceeds", done.Final.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsNonStreamingBodyAboveTransportBudget()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', AnthropicProvider.MaxNonStreamingResponseBytes + 1)),
            };
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http);

        var result = await provider.CallAsync(MakeRequest());

        Assert.Equal(AICallStatus.Error, result.Status);
        Assert.Contains("transport budget", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BoundsStreamingErrorDiagnostics()
    {
        const string marker = "MUST_NOT_APPEAR";
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var body = new string('e', AnthropicProvider.MaxErrorResponseBytes) + marker;
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(body),
            };
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http);

        var chunks = await CollectAsync(provider.CallStreamAsync(MakeRequest()));

        var done = Assert.Single(chunks);
        Assert.Equal(AICallStatus.Error, done.Final!.Status);
        Assert.Contains("diagnostic truncated", done.Final.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(marker, done.Final.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsEventFloodBeforeProviderCanConsumeUnlimitedStream()
    {
        var flood = string.Concat(Enumerable.Repeat("data: {\"type\":\"ping\"}\n\n", AnthropicProvider.MaxSseEvents + 1));
        var handler = FakeHttpMessageHandler.FromSse(flood);
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http);

        var chunks = await CollectAsync(provider.CallStreamAsync(MakeRequest()));

        var done = Assert.Single(chunks);
        Assert.Equal(AICallStatus.Error, done.Final!.Status);
        Assert.Contains("event budget", done.Final.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsCumulativeToolArgumentsBeforeTheyExceedBudget()
    {
        var fragment = new string('a', 60_000);
        var sse =
            "data: {\"type\":\"content_block_start\",\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_budget\",\"name\":\"gh_get_all\"}}\n\n" +
            string.Concat(Enumerable.Repeat(
                "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"" + fragment + "\"}}\n\n",
                5));
        var handler = FakeHttpMessageHandler.FromSse(sse);
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http);

        var chunks = await CollectAsync(provider.CallStreamAsync(MakeRequest()));

        var done = Assert.Single(chunks, c => c.Kind == AIStreamChunkKind.Done);
        Assert.Equal(AICallStatus.Error, done.Final!.Status);
        Assert.Contains("tool-argument budget", done.Final.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }
}
