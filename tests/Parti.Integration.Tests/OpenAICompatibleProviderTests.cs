// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Providers.Ollama;
using Parti.Providers.OpenAI;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// Hermetic tests for <see cref="OpenAICompatibleProvider"/> over canned SSE: delta mapping,
/// tool-call accumulation by index, reasoning as continuation metadata (A8), atomic batch
/// finalization and the usage-only final chunk (A9), and the bounded-output budgets (A10, S14).
/// </summary>
public class OpenAICompatibleProviderTests
{
    private static OpenAICompatibleProviderOptions LocalOptions() => OpenAICompatibleProviderOptions.OllamaOpenAI("localhost");

    private static AIRequestCall Request(params IAIInteraction[] history)
    {
        return new AIRequestCall
        {
            Model = "gpt-oss:20b",
            MaxTokens = 4096,
            Interactions = history.Length > 0
                ? history.ToList()
                : new List<IAIInteraction> { new AIInteractionText { Agent = AIAgent.User, Content = "hi" } },
        };
    }

    private static JObject Delta(JObject delta, string? finish = null)
    {
        return new JObject
        {
            ["id"] = "chatcmpl-1",
            ["object"] = "chat.completion.chunk",
            ["choices"] = new JArray
            {
                new JObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish == null ? JValue.CreateNull() : finish },
            },
        };
    }

    private static JObject Finish(string reason) => Delta(new JObject(), reason);

    private static JObject UsageOnly(int prompt, int completion)
    {
        return new JObject
        {
            ["id"] = "chatcmpl-1",
            ["choices"] = new JArray(),
            ["usage"] = new JObject { ["prompt_tokens"] = prompt, ["completion_tokens"] = completion, ["total_tokens"] = prompt + completion },
        };
    }

    private static JObject ToolDelta(int index, string? id = null, string? name = null, string? args = null)
    {
        var function = new JObject();
        if (name != null)
        {
            function["name"] = name;
        }

        if (args != null)
        {
            function["arguments"] = args;
        }

        var call = new JObject { ["index"] = index, ["function"] = function };
        if (id != null)
        {
            call["id"] = id;
            call["type"] = "function";
        }

        return Delta(new JObject { ["tool_calls"] = new JArray { call } });
    }

    private static async Task<AIReturn> FinalAsync(OpenAICompatibleProvider provider, AIRequestCall request)
    {
        return (await ProviderHttp.CollectAsync(provider.CallStreamAsync(request)))[^1].Final!;
    }

    [Fact]
    public async Task OpenAICompat_StreamsTextDeltasToChatCompletions()
    {
        var server = ProviderHttp.Serve(ProviderHttp.Sse(
            Delta(new JObject { ["role"] = "assistant", ["content"] = "Hel" }),
            Delta(new JObject { ["content"] = "lo" }),
            Finish("stop"),
            UsageOnly(20, 2)) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var chunks = await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        Assert.Equal(new[] { "Hel", "lo" }, chunks.Where(c => c.Kind == AIStreamChunkKind.TextDelta).Select(c => c.Text));
        var final = chunks[^1].Final!;
        Assert.True(final.Success, final.ErrorMessage);
        Assert.Equal("end_turn", final.StopReason);
        Assert.Equal("Hello", Assert.IsType<AIInteractionText>(Assert.Single(final.Interactions)).Content);

        var request = Assert.Single(server.Requests);
        Assert.Equal("http://localhost:11434/v1/chat/completions", request.Uri.ToString());
        var sent = JObject.Parse(request.Body);
        Assert.True((bool)sent["stream"]!);
        Assert.True((bool)sent["stream_options"]!["include_usage"]!);
        Assert.Equal("gpt-oss:20b", (string?)sent["model"]);
    }

    [Fact]
    public async Task OpenAICompat_UsageFromFinalChunk()
    {
        var server = ProviderHttp.Serve(ProviderHttp.Sse(
            Delta(new JObject { ["content"] = "ok" }),
            Finish("stop"),
            UsageOnly(123, 45)) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.True(final.Success, final.ErrorMessage);
        Assert.Equal(123, final.InputTokens);
        Assert.Equal(45, final.OutputTokens);
    }

    [Fact]
    public async Task OpenAICompat_AccumulatesToolCallDeltasByIndex()
    {
        var server = ProviderHttp.Serve(ProviderHttp.Sse(
            ToolDelta(0, id: "call_a", name: "echo", args: string.Empty),
            ToolDelta(1, id: "call_b", name: "gh_get_all", args: "{"),
            ToolDelta(0, args: "{\"te"),
            ToolDelta(1, args: "}"),
            ToolDelta(0, args: "xt\":\"hi\"}"),
            Finish("tool_calls"),
            UsageOnly(50, 10)) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var chunks = await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        var final = chunks[^1].Final!;
        Assert.True(final.Success, final.ErrorMessage);
        Assert.Equal("tool_use", final.StopReason);
        Assert.Equal(AICallStatus.CallingTools, final.Status);
        var calls = final.Interactions.Cast<AIInteractionToolCall>().ToList();
        Assert.Equal(new[] { "call_a", "call_b" }, calls.Select(c => c.Id));
        Assert.Equal("hi", (string?)calls[0].Arguments["text"]);
        Assert.Empty(calls[1].Arguments);
        Assert.Equal(
            new[] { AIStreamChunkKind.ToolCallStart, AIStreamChunkKind.ToolCallComplete, AIStreamChunkKind.ToolCallStart, AIStreamChunkKind.ToolCallComplete, AIStreamChunkKind.Done },
            chunks.Select(c => c.Kind));
    }

    [Theory]
    [InlineData("reasoning")]
    [InlineData("reasoning_content")]
    public async Task OpenAICompat_ReasoningFieldBecomesReasoningDelta(string field)
    {
        var server = ProviderHttp.Serve(ProviderHttp.Sse(
            Delta(new JObject { [field] = "think " }),
            Delta(new JObject { [field] = "more" }),
            Delta(new JObject { ["content"] = "Answer" }),
            Finish("stop")) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var chunks = await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        Assert.Equal(new[] { "think ", "more" }, chunks.Where(c => c.Kind == AIStreamChunkKind.ReasoningDelta).Select(c => c.Text));
        Assert.Equal(new[] { "Answer" }, chunks.Where(c => c.Kind == AIStreamChunkKind.TextDelta).Select(c => c.Text));
        var text = Assert.IsType<AIInteractionText>(Assert.Single(chunks[^1].Final!.Interactions));
        Assert.Equal("Answer", text.Content);
        Assert.Equal("think more", text.Reasoning);
    }

    [Fact]
    public void OpenAICompat_EncodesToolResultsAsRoleTool()
    {
        using var provider = new OpenAICompatibleProvider(ProviderHttp.Serve("").Client, LocalOptions());
        var call = new AIInteractionToolCall { Id = "call_a", Name = "echo", Arguments = new JObject { ["text"] = "hi" } };
        var request = Request(
            new AIInteractionText { Agent = AIAgent.User, Content = "echo hi" },
            new AIInteractionText { Agent = AIAgent.Assistant, Content = "calling" },
            call,
            new AIInteractionToolResult { Id = "call_a", Name = "echo", Result = new JObject { ["text"] = "hi" } });

        var body = JObject.Parse(provider.CreateRequestBody(request));

        var messages = body["messages"]!.OfType<JObject>().ToList();
        Assert.Equal(new[] { "user", "assistant", "tool" }, messages.Select(m => (string?)m["role"]));
        var toolCall = (JObject)messages[1]["tool_calls"]![0]!;
        Assert.Equal("call_a", (string?)toolCall["id"]);
        Assert.Equal("function", (string?)toolCall["type"]);
        Assert.Equal(JTokenType.String, toolCall["function"]!["arguments"]!.Type);
        Assert.Equal("{\"text\":\"hi\"}", (string?)toolCall["function"]!["arguments"]);
        Assert.Equal("call_a", (string?)messages[2]["tool_call_id"]);
        Assert.Equal("{\"text\":\"hi\"}", (string?)messages[2]["content"]);
        Assert.False((bool)body["stream"]!);
    }

    [Theory]
    [InlineData(OpenAICompatibleProviderOptions.MaxCompletionTokensField, OpenAICompatibleProviderOptions.MaxTokensField)]
    [InlineData(OpenAICompatibleProviderOptions.MaxTokensField, OpenAICompatibleProviderOptions.MaxCompletionTokensField)]
    public void OpenAICompat_UsesConfiguredTokenLimitField(string field, string absent)
    {
        var options = LocalOptions();
        options.TokenLimitField = field;
        using var provider = new OpenAICompatibleProvider(ProviderHttp.Serve("").Client, options);
        var request = Request();
        request.MaxTokens = 50_000;

        var body = JObject.Parse(provider.CreateRequestBody(request));

        Assert.Equal(OpenAICompatibleProvider.MaxOutputTokens, (int)body[field]!);
        Assert.Null(body[absent]);
    }

    [Fact]
    public void OpenAICompat_PresetsMatchSpec()
    {
        var openai = OpenAICompatibleProviderOptions.OpenAI();
        Assert.Equal("https://api.openai.com/v1", openai.BaseUrl.ToString().TrimEnd('/'));
        Assert.Equal("max_completion_tokens", openai.TokenLimitField);
        Assert.Equal("OPENAI_API_KEY", openai.ApiKeyEnvironmentVariable);

        var openRouter = OpenAICompatibleProviderOptions.OpenRouter();
        Assert.Equal("https://openrouter.ai/api/v1", openRouter.BaseUrl.ToString().TrimEnd('/'));
        Assert.Equal("max_tokens", openRouter.TokenLimitField);
        Assert.Equal("OPENROUTER_API_KEY", openRouter.ApiKeyEnvironmentVariable);

        var ollama = OpenAICompatibleProviderOptions.OllamaOpenAI("ollama-host");
        Assert.Equal("http://ollama-host:11434/v1", ollama.BaseUrl.ToString().TrimEnd('/'));
        Assert.Equal("max_tokens", ollama.TokenLimitField);
        Assert.Null(ollama.ApiKeyEnvironmentVariable);
    }

    [Fact]
    public async Task OpenAICompat_NonStreamingCall_DecodesMessageAndUsage()
    {
        var server = ProviderHttp.Serve(new JObject
        {
            ["choices"] = new JArray
            {
                new JObject
                {
                    ["index"] = 0,
                    ["message"] = new JObject
                    {
                        ["role"] = "assistant",
                        ["content"] = JValue.CreateNull(),
                        ["reasoning"] = "hidden",
                        ["tool_calls"] = new JArray
                        {
                            new JObject { ["id"] = "call_x", ["type"] = "function", ["function"] = new JObject { ["name"] = "echo", ["arguments"] = "{\"text\":\"a\"}" } },
                        },
                    },
                    ["finish_reason"] = "tool_calls",
                },
            },
            ["usage"] = new JObject { ["prompt_tokens"] = 7, ["completion_tokens"] = 3 },
        }.ToString());
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var ret = await provider.CallAsync(Request());

        Assert.True(ret.Success, ret.ErrorMessage);
        var call = Assert.IsType<AIInteractionToolCall>(Assert.Single(ret.Interactions));
        Assert.Equal("call_x", call.Id);
        Assert.Equal("a", (string?)call.Arguments["text"]);
        Assert.Equal("hidden", call.Reasoning);
        Assert.Equal(7, ret.InputTokens);
        Assert.Equal(3, ret.OutputTokens);
    }

    // --- atomic batch finalization (A9) ----------------------------------------------------

    [Fact]
    public async Task OpenAICompat_ToolCallsWithoutDone_AreNotReturned()
    {
        var server = ProviderHttp.Serve(ProviderHttp.Sse(
            ToolDelta(0, id: "call_a", name: "echo", args: "{}"),
            Finish("tool_calls")));
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var chunks = await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        Assert.DoesNotContain(chunks, c => c.Kind is AIStreamChunkKind.ToolCallStart or AIStreamChunkKind.ToolCallComplete);
        Assert.False(chunks[^1].Final!.Success);
        Assert.Contains("[DONE]", chunks[^1].Final!.ErrorMessage);
        Assert.Empty(chunks[^1].Final!.Interactions);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    [InlineData("function_call")]
    public async Task OpenAICompat_AbnormalFinishWithToolCalls_IsErrorWithNoCalls(string finish)
    {
        var server = ProviderHttp.Serve(ProviderHttp.Sse(
            ToolDelta(0, id: "call_a", name: "echo", args: "{}"),
            Finish(finish)) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var chunks = await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        Assert.DoesNotContain(chunks, c => c.Kind is AIStreamChunkKind.ToolCallStart or AIStreamChunkKind.ToolCallComplete);
        var final = chunks[^1].Final!;
        Assert.False(final.Success);
        Assert.False(final.HasToolCalls);
        Assert.Contains("no tool calls were run", final.ErrorMessage);
    }

    [Fact]
    public async Task OpenAICompat_MissingFinishReason_IsError()
    {
        var server = ProviderHttp.Serve(ProviderHttp.Sse(ToolDelta(0, id: "call_a", name: "echo", args: "{}")) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.False(final.Success);
        Assert.Contains("finish_reason", final.ErrorMessage);
    }

    [Fact]
    public async Task OpenAICompat_LaterFinishReasonCannotOverwriteAbnormalOne()
    {
        var server = ProviderHttp.Serve(ProviderHttp.Sse(
            ToolDelta(0, id: "call_a", name: "echo", args: "{}"),
            Finish("length"),
            Finish("tool_calls")) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var chunks = await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        Assert.DoesNotContain(chunks, c => c.Kind is AIStreamChunkKind.ToolCallStart or AIStreamChunkKind.ToolCallComplete);
        var final = chunks[^1].Final!;
        Assert.False(final.Success);
        Assert.False(final.HasToolCalls);
        Assert.Contains("finish_reason", final.ErrorMessage);
    }

    [Fact]
    public async Task OpenAICompat_ToolCallDataAfterFinish_IsErrorWithNoCalls()
    {
        var server = ProviderHttp.Serve(ProviderHttp.Sse(
            ToolDelta(0, id: "call_a", name: "echo", args: "{}"),
            Finish("tool_calls"),
            ToolDelta(1, id: "call_b", name: "echo", args: "{}")) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var chunks = await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        Assert.DoesNotContain(chunks, c => c.Kind is AIStreamChunkKind.ToolCallStart or AIStreamChunkKind.ToolCallComplete);
        var final = chunks[^1].Final!;
        Assert.False(final.Success);
        Assert.False(final.HasToolCalls);
        Assert.Contains("after finish_reason", final.ErrorMessage);
    }

    [Fact]
    public async Task OpenAICompat_TextAfterFinish_IsError()
    {
        var server = ProviderHttp.Serve(ProviderHttp.Sse(
            Delta(new JObject { ["content"] = "ok" }),
            Finish("stop"),
            Delta(new JObject { ["content"] = " more" })) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.False(final.Success);
        Assert.Contains("after finish_reason", final.ErrorMessage);
    }

    public static IEnumerable<object[]> NonStringDataAfterFinish => new[]
    {
        new object[] { new JObject { ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = "x" } } } },
        new object[] { new JObject { ["reasoning_details"] = new JObject { ["type"] = "reasoning.text", ["text"] = "x" } } },
        new object[] { new JObject { ["reasoning"] = 42 } },
        new object[] { new JObject { ["tool_calls"] = new JObject { ["index"] = 1 } } },
    };

    [Theory]
    [MemberData(nameof(NonStringDataAfterFinish))]
    public async Task OpenAICompat_AnyShapeOfDataAfterFinish_IsErrorWithNoCalls(JObject lateDelta)
    {
        var server = ProviderHttp.Serve(ProviderHttp.Sse(
            ToolDelta(0, id: "call_a", name: "echo", args: "{}"),
            Finish("tool_calls"),
            Delta(lateDelta)) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var chunks = await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        Assert.DoesNotContain(chunks, c => c.Kind is AIStreamChunkKind.ToolCallStart or AIStreamChunkKind.ToolCallComplete);
        var final = chunks[^1].Final!;
        Assert.False(final.Success);
        Assert.False(final.HasToolCalls);
    }

    [Fact]
    public async Task OpenAICompat_RepeatedFinishAndEmptyDeltaThenUsage_IsAccepted()
    {
        var server = ProviderHttp.Serve(ProviderHttp.Sse(
            Delta(new JObject { ["content"] = "ok" }),
            Finish("stop"),
            Finish("stop"),
            Delta(new JObject()),
            UsageOnly(10, 2)) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.True(final.Success, final.ErrorMessage);
        Assert.Equal("ok", Assert.IsType<AIInteractionText>(Assert.Single(final.Interactions)).Content);
        Assert.Equal(10, final.InputTokens);
    }

    [Fact]
    public async Task OpenAICompat_ReasoningOnlyLengthStop_IsErrorWithGuidance()
    {
        var server = ProviderHttp.Serve(ProviderHttp.Sse(Delta(new JObject { ["reasoning"] = "..." }), Finish("length")) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.False(final.Success);
        Assert.Equal(OpenAICompatibleProvider.ReasoningBudgetExhaustedMessage, final.ErrorMessage);
    }

    [Fact]
    public async Task OpenAICompat_UnparsableToolArguments_FailClosed()
    {
        var server = ProviderHttp.Serve(ProviderHttp.Sse(
            ToolDelta(0, id: "call_a", name: "echo", args: "{\"text\":"),
            Finish("tool_calls")) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.False(final.Success);
        Assert.Empty(final.Interactions);
    }

    [Fact]
    public async Task OpenAICompat_ErrorEventMidStream_IsError()
    {
        var server = ProviderHttp.Serve(ProviderHttp.Sse(
            Delta(new JObject { ["content"] = "a" }),
            new JObject { ["error"] = new JObject { ["message"] = "upstream overloaded", ["code"] = 502 } }) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.False(final.Success);
        Assert.Equal("Provider error: upstream overloaded", final.ErrorMessage);
    }

    [Fact]
    public async Task OpenAICompat_CommentsAndEventFieldsAreIgnored()
    {
        var body = ": OPENROUTER PROCESSING\n\nevent: message\nid: 1\n" + ProviderHttp.Sse(
            Delta(new JObject { ["content"] = "x" }), Finish("stop")) + ": keep-alive\n\n" + ProviderHttp.SseDone;
        var server = ProviderHttp.Serve(body);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.True(final.Success, final.ErrorMessage);
    }

    // --- reasoning as continuation metadata (A8) -------------------------------------------

    [Fact]
    public async Task OpenAICompat_ReasoningReplayedOnToolLoopContinuation_SameProviderOnly()
    {
        var first = ProviderHttp.Sse(
            Delta(new JObject
            {
                ["reasoning"] = "plan ",
                ["reasoning_details"] = new JArray { new JObject { ["type"] = "reasoning.text", ["text"] = "plan ", ["index"] = 0, ["format"] = "unknown" } },
            }),
            Delta(new JObject
            {
                ["reasoning"] = "echo",
                ["reasoning_details"] = new JArray { new JObject { ["type"] = "reasoning.text", ["text"] = "echo", ["index"] = 0 } },
            }),
            ToolDelta(0, id: "call_a", name: "echo", args: "{\"text\":\"hi\"}"),
            Finish("tool_calls")) + ProviderHttp.SseDone;
        var second = ProviderHttp.Sse(Delta(new JObject { ["content"] = "done" }), Finish("stop")) + ProviderHttp.SseDone;
        var server = ProviderHttp.Serve(first, second);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var history = new List<IAIInteraction> { new AIInteractionText { Agent = AIAgent.User, Content = "echo hi" } };
        var turn1 = await FinalAsync(provider, Request(history.ToArray()));
        Assert.True(turn1.Success, turn1.ErrorMessage);
        var call = Assert.IsType<AIInteractionToolCall>(Assert.Single(turn1.Interactions));
        history.AddRange(turn1.Interactions);
        history.Add(new AIInteractionToolResult { Id = call.Id, Name = call.Name, Result = new JObject { ["text"] = "hi" } });

        await FinalAsync(provider, Request(history.ToArray()));

        var assistant = JObject.Parse(server.Requests[1].Body)["messages"]!.OfType<JObject>().Single(m => (string?)m["role"] == "assistant");
        Assert.Equal("plan echo", (string?)assistant["reasoning"]);
        var detail = (JObject)Assert.Single((JArray)assistant["reasoning_details"]!);
        Assert.Equal("plan echo", (string?)detail["text"]);
        Assert.Equal("unknown", (string?)detail["format"]);
        Assert.Equal(string.Empty, (string?)assistant["content"]);

        // Another provider (a second instance, or the native Ollama provider) never gets it.
        using var other = new OpenAICompatibleProvider(server.Client, LocalOptions());
        Assert.DoesNotContain("plan echo", other.CreateRequestBody(Request(history.ToArray())));
        using var ollama = new OllamaProvider(server.Client);
        Assert.DoesNotContain("plan echo", ollama.CreateRequestBody(Request(history.ToArray())));
    }

    [Fact]
    public void OpenAICompat_ReasoningFromOtherProviderNeverSent()
    {
        using var provider = new OpenAICompatibleProvider(ProviderHttp.Serve("").Client, LocalOptions());
        var request = Request(
            new AIInteractionText { Agent = AIAgent.User, Content = "q" },
            new AIInteractionText { Agent = AIAgent.Assistant, Content = "a", Reasoning = "FOREIGN" },
            new AIInteractionToolCall { Id = "c1", Name = "echo", Arguments = new JObject(), Reasoning = "FOREIGN2" },
            new AIInteractionToolResult { Id = "c1", Name = "echo", Result = new JObject() });

        var body = provider.CreateRequestBody(request);

        Assert.DoesNotContain("FOREIGN", body);
        Assert.DoesNotContain("reasoning", body);
    }

    // --- budgets (S14, A10) ----------------------------------------------------------------

    [Fact]
    public async Task OpenAICompat_StreamBytesBudget_FailsClosed()
    {
        var sb = new StringBuilder();
        var comment = ":" + new string('c', 250_000) + "\n";
        for (var i = 0; i < 70; i++)
        {
            sb.Append(comment);
        }

        var server = ProviderHttp.Serve(sb.ToString());
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.False(final.Success);
        Assert.Contains($"{OpenAICompatibleProvider.MaxStreamingResponseBytes}-byte", final.ErrorMessage);
    }

    [Fact]
    public async Task OpenAICompat_EventBudget_FailsClosed()
    {
        var sb = new StringBuilder();
        var evt = "data: " + Delta(new JObject { ["content"] = string.Empty }).ToString(Formatting.None) + "\n";
        for (var i = 0; i <= OpenAICompatibleProvider.MaxStreamEvents; i++)
        {
            sb.Append(evt);
        }

        var server = ProviderHttp.Serve(sb.ToString());
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.False(final.Success);
        Assert.Contains($"{OpenAICompatibleProvider.MaxStreamEvents}-event", final.ErrorMessage);
    }

    [Fact]
    public async Task OpenAICompat_PhysicalLineBudget_FailsClosed()
    {
        var server = ProviderHttp.Serve(new string('\n', OpenAICompatibleProvider.MaxPhysicalLines + 1));
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.False(final.Success);
        Assert.Contains($"{OpenAICompatibleProvider.MaxPhysicalLines}-line", final.ErrorMessage);
    }

    [Fact]
    public async Task OpenAICompat_LineLengthBudget_FailsClosed()
    {
        var server = ProviderHttp.Serve("data: " + new string('x', OpenAICompatibleProvider.MaxStreamLineCharacters) + "\n\n" + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.False(final.Success);
        Assert.Contains($"{OpenAICompatibleProvider.MaxStreamLineCharacters}-character budget", final.ErrorMessage);
    }

    [Fact]
    public async Task OpenAICompat_TextBudget_FailsClosed()
    {
        var piece = new string('t', 200_000);
        var server = ProviderHttp.Serve(ProviderHttp.Sse(
            Delta(new JObject { ["content"] = piece }), Delta(new JObject { ["content"] = piece }), Delta(new JObject { ["content"] = piece }),
            Finish("stop")) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.False(final.Success);
        Assert.Contains($"{OpenAICompatibleProvider.MaxStreamTextCharacters}-character text budget", final.ErrorMessage);
    }

    [Fact]
    public async Task OpenAICompat_ReasoningBudget_FailsClosed()
    {
        var piece = new string('r', 200_000);
        var server = ProviderHttp.Serve(ProviderHttp.Sse(
            Delta(new JObject { ["reasoning"] = piece }), Delta(new JObject { ["reasoning"] = piece }), Delta(new JObject { ["reasoning"] = piece }),
            Finish("stop")) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.False(final.Success);
        Assert.Contains($"{OpenAICompatibleProvider.MaxReasoningCharacters}-character reasoning budget", final.ErrorMessage);
    }

    [Fact]
    public async Task OpenAICompat_ReasoningDetailsBudget_FailsClosed()
    {
        var piece = new string('d', 200_000);
        JObject Details() => Delta(new JObject { ["reasoning_details"] = new JArray { new JObject { ["type"] = "reasoning.encrypted", ["data"] = piece, ["index"] = 0 } } });
        var server = ProviderHttp.Serve(ProviderHttp.Sse(Details(), Details(), Details(), Finish("stop")) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.False(final.Success);
        Assert.Contains("reasoning_details budget", final.ErrorMessage);
    }

    [Fact]
    public async Task OpenAICompat_ToolArgumentsBudget_FailsClosed()
    {
        var half = new string('a', (OpenAICompatibleProvider.MaxToolArgumentCharacters / 2) + 1);
        var server = ProviderHttp.Serve(ProviderHttp.Sse(
            ToolDelta(0, id: "call_a", name: "echo", args: half),
            ToolDelta(0, args: half),
            Finish("tool_calls")) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var chunks = await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        Assert.False(chunks[^1].Final!.Success);
        Assert.Contains($"{OpenAICompatibleProvider.MaxToolArgumentCharacters}-character budget", chunks[^1].Final!.ErrorMessage);
        Assert.DoesNotContain(chunks, c => c.Kind == AIStreamChunkKind.ToolCallComplete);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(OpenAICompatibleProvider.MaxToolCallsPerResponse)]
    public async Task OpenAICompat_ToolIndexBudget_FailsClosed(int index)
    {
        var server = ProviderHttp.Serve(ProviderHttp.Sse(ToolDelta(index, id: "call_a", name: "echo", args: "{}"), Finish("tool_calls")) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.False(final.Success);
        Assert.Contains("call budget", final.ErrorMessage);
    }

    [Fact]
    public async Task OpenAICompat_ToolCallAtArgumentLimit_Succeeds()
    {
        var args = ProviderHttp.ArgumentsOfSerializedLength(OpenAICompatibleProvider.MaxToolArgumentCharacters).ToString(Formatting.None);
        var server = ProviderHttp.Serve(ProviderHttp.Sse(ToolDelta(0, id: "call_a", name: "echo", args: args), Finish("tool_calls")) + ProviderHttp.SseDone);
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.True(final.Success, final.ErrorMessage);
        Assert.Single(final.Interactions);
    }

    [Fact]
    public async Task OpenAICompat_MaximumTokenStreamWithTerminalEvents_Succeeds()
    {
        var sb = new StringBuilder();
        var reasoningEvent = ProviderHttp.Sse(Delta(new JObject { ["reasoning"] = "r" }));
        var contentEvent = ProviderHttp.Sse(Delta(new JObject { ["content"] = "c" }));
        for (var i = 0; i < OpenAICompatibleProvider.MaxOutputTokens; i++)
        {
            sb.Append(reasoningEvent).Append(contentEvent);
        }

        sb.Append(ProviderHttp.Sse(Finish("stop"), UsageOnly(100, 2 * OpenAICompatibleProvider.MaxOutputTokens))).Append(ProviderHttp.SseDone);
        var server = ProviderHttp.Serve(sb.ToString());
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.True(final.Success, final.ErrorMessage);
        Assert.Equal(OpenAICompatibleProvider.MaxOutputTokens, Assert.IsType<AIInteractionText>(Assert.Single(final.Interactions)).Content!.Length);
        Assert.Equal(2 * OpenAICompatibleProvider.MaxOutputTokens, final.OutputTokens);
    }

    [Fact]
    public async Task OpenAICompat_ErrorBodyTruncatedTo16KiB()
    {
        var server = ProviderHttp.ServeWith(HttpStatusCode.TooManyRequests, new string('e', 40_000));
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var final = await FinalAsync(provider, Request());

        Assert.False(final.Success);
        Assert.StartsWith("HTTP 429: ", final.ErrorMessage);
        Assert.EndsWith(BoundedHttp.DiagnosticTruncatedMarker, final.ErrorMessage);
        Assert.Equal(
            "HTTP 429: ".Length + OpenAICompatibleProvider.MaxErrorResponseBytes + BoundedHttp.DiagnosticTruncatedMarker.Length,
            final.ErrorMessage!.Length);
    }

    [Fact]
    public async Task OpenAICompat_NonStreamingBodyBudget_FailsClosed()
    {
        var server = ProviderHttp.Serve("{\"choices\":[{\"message\":{\"content\":\"" + new string('x', OpenAICompatibleProvider.MaxNonStreamingResponseBytes) + "\"}}]}");
        using var provider = new OpenAICompatibleProvider(server.Client, LocalOptions());

        var ret = await provider.CallAsync(Request());

        Assert.False(ret.Success);
        Assert.Contains("byte transport budget", ret.ErrorMessage);
    }
}
