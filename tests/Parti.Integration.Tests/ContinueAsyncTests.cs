// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Sessions;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// Integration tests for <see cref="ConversationSession.ContinueAsync(string, System.Threading.CancellationToken)"/>.
/// Verifies multi-turn conversation: follow-up prompts see full prior context, token counts
/// accumulate, and <see cref="ConversationSession.Reset"/> restores the "never called" state.
/// </summary>
public class ContinueAsyncTests
{
    [Fact]
    public async Task ContinueAsync_BeforeRunAsync_Throws()
    {
        var provider = new FakeAIProvider(new[] { MakeTextReturn("unused", 1, 1) });
        var dispatcher = new FakeToolDispatcher(
            definitions: Array.Empty<AIToolDefinition>(),
            dispatch: (_, _) => new JObject());

        var session = new ConversationSession(provider, dispatcher, BuildOptions());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ContinueAsync("follow-up without prior run"));
        Assert.Contains("RunAsync", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, provider.CallCount);
        Assert.Empty(session.History);
    }

    [Fact]
    public async Task TwoTurn_TextOnly_HistoryGrows()
    {
        var provider = new FakeAIProvider(new[]
        {
            MakeTextReturn("hi, I'm fake", inputTokens: 10, outputTokens: 5),
            MakeTextReturn("sure, here's the follow-up", inputTokens: 20, outputTokens: 7),
        });
        var dispatcher = new FakeToolDispatcher(
            definitions: Array.Empty<AIToolDefinition>(),
            dispatch: (_, _) => new JObject());

        var session = new ConversationSession(provider, dispatcher, BuildOptions());

        var first = await session.RunAsync("hello");
        Assert.True(first.Success);
        Assert.Equal(2, session.History.Count);

        var second = await session.ContinueAsync("tell me more");
        Assert.True(second.Success);

        // History now: user1, assistant1, user2, assistant2
        Assert.Equal(4, session.History.Count);
        Assert.IsType<AIInteractionText>(session.History[0]);
        Assert.Equal(AIAgent.User, session.History[0].Agent);
        Assert.Equal("hello", ((AIInteractionText)session.History[0]).Content);
        Assert.Equal(AIAgent.Assistant, session.History[1].Agent);
        Assert.Equal("hi, I'm fake", ((AIInteractionText)session.History[1]).Content);
        Assert.Equal(AIAgent.User, session.History[2].Agent);
        Assert.Equal("tell me more", ((AIInteractionText)session.History[2]).Content);
        Assert.Equal(AIAgent.Assistant, session.History[3].Agent);
        Assert.Equal("sure, here's the follow-up", ((AIInteractionText)session.History[3]).Content);

        // The second provider call must have received the full prior history as context.
        Assert.Equal(2, provider.ReceivedRequests.Count);
        var secondHistory = provider.ReceivedRequests[1].Interactions;
        Assert.Contains(secondHistory, i =>
            i is AIInteractionText t && t.Agent == AIAgent.User && t.Content == "hello");
        Assert.Contains(secondHistory, i =>
            i is AIInteractionText t && t.Agent == AIAgent.Assistant && t.Content == "hi, I'm fake");
        Assert.Contains(secondHistory, i =>
            i is AIInteractionText t && t.Agent == AIAgent.User && t.Content == "tell me more");

        // Per-turn token counts on each AIReturn reflect only that turn.
        Assert.Equal(10, first.InputTokens);
        Assert.Equal(5, first.OutputTokens);
        Assert.Equal(20, second.InputTokens);
        Assert.Equal(7, second.OutputTokens);
    }

    [Fact]
    public async Task TwoTurn_ToolCallPersists()
    {
        var definitions = new[]
        {
            new AIToolDefinition(
                "gh_add_component",
                "Add a component to the canvas.",
                new JObject { ["type"] = "object", ["properties"] = new JObject() }),
        };

        var provider = new FakeAIProvider(new[]
        {
            MakeToolUseReturn("toolu_first", "gh_add_component", new JObject { ["name"] = "Circle" }, inputTokens: 12, outputTokens: 4),
            MakeTextReturn("added the circle component", inputTokens: 18, outputTokens: 6),
            MakeTextReturn("yes, the circle is still at (0,0)", inputTokens: 30, outputTokens: 9),
        });

        var dispatcher = new FakeToolDispatcher(
            definitions,
            dispatch: (name, args) => new JObject
            {
                ["status"] = "success",
                ["simple_id"] = "stub_circle_01",
                ["name"] = args["name"]?.ToString() ?? name,
            });

        var session = new ConversationSession(provider, dispatcher, BuildOptions());

        var first = await session.RunAsync("add a circle");
        Assert.True(first.Success);
        // First turn history: user, assistant tool_use, tool_result, assistant text.
        Assert.Equal(4, session.History.Count);
        Assert.IsType<AIInteractionToolCall>(session.History[1]);
        Assert.False(session.History[1] is AIInteractionToolResult);
        Assert.IsType<AIInteractionToolResult>(session.History[2]);

        var second = await session.ContinueAsync("is the thing you just added still there?");
        Assert.True(second.Success);

        // Follow-up adds user + assistant text; no new tool call.
        Assert.Equal(6, session.History.Count);
        Assert.Equal(AIAgent.User, session.History[4].Agent);
        Assert.Equal("is the thing you just added still there?", ((AIInteractionText)session.History[4]).Content);
        Assert.Equal(AIAgent.Assistant, session.History[5].Agent);

        // The follow-up provider call must have received the tool_use + tool_result in history so
        // the model can resolve "the thing you just added".
        Assert.Equal(3, provider.ReceivedRequests.Count);
        var followUpHistory = provider.ReceivedRequests[2].Interactions;
        Assert.Contains(followUpHistory, i => i is AIInteractionToolCall call && call is not AIInteractionToolResult && call.Name == "gh_add_component");
        Assert.Contains(followUpHistory, i => i is AIInteractionToolResult result && result.Name == "gh_add_component");
        Assert.Contains(followUpHistory, i =>
            i is AIInteractionText t && t.Agent == AIAgent.User && t.Content == "is the thing you just added still there?");
    }

    [Fact]
    public async Task MultipleContinues_TokenAccountingAccumulates()
    {
        var provider = new FakeAIProvider(new[]
        {
            MakeTextReturn("r1", inputTokens: 100, outputTokens: 10),
            MakeTextReturn("r2", inputTokens: 200, outputTokens: 20),
            MakeTextReturn("r3", inputTokens: 300, outputTokens: 30),
        });
        var dispatcher = new FakeToolDispatcher(
            definitions: Array.Empty<AIToolDefinition>(),
            dispatch: (_, _) => new JObject());

        var session = new ConversationSession(provider, dispatcher, BuildOptions());

        var t1 = await session.RunAsync("turn 1");
        Assert.Equal(100, session.TotalInputTokens);
        Assert.Equal(10, session.TotalOutputTokens);
        Assert.Equal(100, t1.InputTokens);
        Assert.Equal(10, t1.OutputTokens);

        var t2 = await session.ContinueAsync("turn 2");
        Assert.Equal(300, session.TotalInputTokens);
        Assert.Equal(30, session.TotalOutputTokens);
        Assert.Equal(200, t2.InputTokens);
        Assert.Equal(20, t2.OutputTokens);

        var t3 = await session.ContinueAsync("turn 3");
        Assert.Equal(600, session.TotalInputTokens);
        Assert.Equal(60, session.TotalOutputTokens);
        Assert.Equal(300, t3.InputTokens);
        Assert.Equal(30, t3.OutputTokens);
    }

    [Fact]
    public async Task Reset_ClearsHistory_AllowsNewConversation()
    {
        var provider = new FakeAIProvider(new[]
        {
            MakeTextReturn("first-a", inputTokens: 5, outputTokens: 2),
            MakeTextReturn("first-b", inputTokens: 6, outputTokens: 3),
            MakeTextReturn("second-a", inputTokens: 7, outputTokens: 4),
        });
        var dispatcher = new FakeToolDispatcher(
            definitions: Array.Empty<AIToolDefinition>(),
            dispatch: (_, _) => new JObject());

        var session = new ConversationSession(provider, dispatcher, BuildOptions());

        await session.RunAsync("conversation one, turn one");
        await session.ContinueAsync("conversation one, turn two");
        Assert.Equal(4, session.History.Count);
        Assert.Equal(11, session.TotalInputTokens);
        Assert.Equal(5, session.TotalOutputTokens);

        session.Reset();
        Assert.Empty(session.History);
        Assert.Equal(0, session.TotalInputTokens);
        Assert.Equal(0, session.TotalOutputTokens);

        // ContinueAsync without a fresh RunAsync should throw again after Reset.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ContinueAsync("dangling continue after reset"));

        // A fresh RunAsync after Reset must succeed and start a new conversation.
        var fresh = await session.RunAsync("conversation two, turn one");
        Assert.True(fresh.Success);
        Assert.Equal(2, session.History.Count);
        Assert.Equal("conversation two, turn one", ((AIInteractionText)session.History[0]).Content);
        Assert.Equal("second-a", ((AIInteractionText)session.History[1]).Content);
        Assert.Equal(7, session.TotalInputTokens);
        Assert.Equal(4, session.TotalOutputTokens);
    }

    private static SessionOptions BuildOptions() => new()
    {
        MaxTurns = 5,
        MaxToolPasses = 4,
        ApiKey = "unused",
        Model = "test-model",
    };

    private static AIReturn MakeTextReturn(string content, int inputTokens, int outputTokens) => new()
    {
        Status = AICallStatus.Finished,
        StopReason = "end_turn",
        InputTokens = inputTokens,
        OutputTokens = outputTokens,
        Interactions = new List<IAIInteraction>
        {
            new AIInteractionText { Agent = AIAgent.Assistant, Content = content },
        },
    };

    private static AIReturn MakeToolUseReturn(string id, string name, JObject args, int inputTokens, int outputTokens) => new()
    {
        Status = AICallStatus.Finished,
        StopReason = "tool_use",
        InputTokens = inputTokens,
        OutputTokens = outputTokens,
        Interactions = new List<IAIInteraction>
        {
            new AIInteractionToolCall
            {
                Id = id,
                Name = name,
                Arguments = args,
                Agent = AIAgent.ToolCall,
            },
        },
    };
}
