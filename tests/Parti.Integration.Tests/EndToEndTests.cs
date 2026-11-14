// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Sessions;
using Xunit;

namespace Parti.Integration.Tests;

public class EndToEndTests
{
    [Fact]
    public async Task SingleTurn_TextOnly_NoTools_Succeeds()
    {
        var provider = new FakeAIProvider(new[] { MakeTextReturn("hello from fake") });
        var dispatcher = new FakeToolDispatcher(
            definitions: Array.Empty<AIToolDefinition>(),
            dispatch: (_, _) => new JObject());

        var session = new ConversationSession(provider, dispatcher, BuildOptions());
        var result = await session.RunAsync("hi");

        Assert.True(result.Success);
        Assert.Equal("end_turn", result.StopReason);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(0, dispatcher.DispatchCount);

        // History: user prompt + one assistant text.
        Assert.Equal(2, session.History.Count);
        Assert.IsType<AIInteractionText>(session.History[0]);
        Assert.Equal(AIAgent.User, session.History[0].Agent);
        Assert.IsType<AIInteractionText>(session.History[1]);
        Assert.Equal(AIAgent.Assistant, session.History[1].Agent);
        Assert.Equal("hello from fake", ((AIInteractionText)session.History[1]).Content);
    }

    [Fact]
    public async Task TwoTurn_ToolCall_ThenTextResponse()
    {
        var definitions = new[]
        {
            new AIToolDefinition(
                "gh_get_all",
                "Snapshot canvas",
                new JObject { ["type"] = "object", ["properties"] = new JObject() }),
        };

        var provider = new FakeAIProvider(new[]
        {
            MakeToolUseReturn("toolu_abc", "gh_get_all", new JObject()),
            MakeTextReturn("the canvas is empty"),
        });

        var dispatcher = new FakeToolDispatcher(
            definitions,
            dispatch: (name, args) => new JObject
            {
                ["status"] = "success",
                ["count"] = 0,
            });

        var session = new ConversationSession(provider, dispatcher, BuildOptions());
        var result = await session.RunAsync("what is on the canvas?");

        Assert.True(result.Success);
        Assert.Equal("end_turn", result.StopReason);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(1, dispatcher.DispatchCount);
        Assert.Single(dispatcher.Calls);
        Assert.Equal("gh_get_all", dispatcher.Calls[0].Name);

        // History: user, assistant tool_use, tool_result, assistant text.
        Assert.Equal(4, session.History.Count);
        Assert.IsType<AIInteractionText>(session.History[0]);
        Assert.IsType<AIInteractionToolCall>(session.History[1]);
        Assert.False(session.History[1] is AIInteractionToolResult);
        Assert.IsType<AIInteractionToolResult>(session.History[2]);
        Assert.IsType<AIInteractionText>(session.History[3]);

        // Final return interactions = the last provider response (one text block).
        var finalTexts = result.Interactions.OfType<AIInteractionText>().ToList();
        Assert.Single(finalTexts);
        Assert.Equal("the canvas is empty", finalTexts[0].Content);

        // The tool_result carries the canned payload forward.
        var toolResult = (AIInteractionToolResult)session.History[2];
        Assert.Equal("success", toolResult.Result["status"]?.ToString());
        Assert.Equal(0, toolResult.Result["count"]?.ToObject<int>());

        // The second provider call received the tool_result in its history.
        Assert.Equal(2, provider.ReceivedRequests.Count);
        var secondRequestHistory = provider.ReceivedRequests[1].Interactions;
        Assert.Contains(secondRequestHistory, i => i is AIInteractionToolResult);
    }

    [Fact]
    public async Task ExceedsMaxTurns_StopsAndSurfaces()
    {
        var definitions = new[]
        {
            new AIToolDefinition(
                "loop_tool",
                "A tool the model will keep requesting.",
                new JObject { ["type"] = "object", ["properties"] = new JObject() }),
        };

        var callCounter = 0;
        var provider = new FakeAIProvider(_ =>
        {
            callCounter++;
            return new AIReturn
            {
                Status = AICallStatus.Finished,
                StopReason = "tool_use",
                Interactions = new List<IAIInteraction>
                {
                    new AIInteractionToolCall
                    {
                        Id = $"toolu_{callCounter}",
                        Name = "loop_tool",
                        Arguments = new JObject(),
                        Agent = AIAgent.ToolCall,
                    },
                },
            };
        });

        var dispatcher = new FakeToolDispatcher(
            definitions,
            dispatch: (_, _) => new JObject { ["status"] = "success" });

        var options = new SessionOptions
        {
            MaxTurns = 3,
            MaxToolPasses = 10, // high, so MaxTurns is the cap
            ApiKey = "unused",
            Model = "test-model",
        };

        var session = new ConversationSession(provider, dispatcher, options);
        var result = await session.RunAsync("go forever");

        Assert.False(result.Success);
        Assert.Equal(AICallStatus.Error, result.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Max turns", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal(3, provider.CallCount);
        Assert.Equal(3, dispatcher.DispatchCount);
    }

    private static SessionOptions BuildOptions() => new()
    {
        MaxTurns = 5,
        MaxToolPasses = 4,
        ApiKey = "unused",
        Model = "test-model",
    };

    private static AIReturn MakeTextReturn(string content) => new()
    {
        Status = AICallStatus.Finished,
        StopReason = "end_turn",
        Interactions = new List<IAIInteraction>
        {
            new AIInteractionText { Agent = AIAgent.Assistant, Content = content },
        },
    };

    private static AIReturn MakeToolUseReturn(string id, string name, JObject args) => new()
    {
        Status = AICallStatus.Finished,
        StopReason = "tool_use",
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
