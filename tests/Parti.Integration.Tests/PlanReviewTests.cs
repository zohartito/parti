// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Sessions;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// Integration tests for the v0.7 plan-preview / approve-plan feature. Before the session runs
/// a batch of reversible tool calls, it surfaces a <see cref="ToolCallPlan"/> to the caller's
/// <see cref="ConversationSession.PlanReviewer"/>; the caller approves or cancels. Read-only
/// batches and the <c>ask_user</c> intercept bypass the reviewer.
/// </summary>
public class PlanReviewTests
{
    private const string ComponentGuid = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task OversizedResponse_IsRejectedBeforeAnyDispatch()
    {
        var provider = new FakeAIProvider(new[]
        {
            new AIReturn
            {
                Status = AICallStatus.Finished,
                StopReason = "tool_use",
                Interactions = new List<IAIInteraction>
                {
                    new AIInteractionToolCall { Id = "one", Name = "gh_add_component", Arguments = new JObject { ["componentGuid"] = ComponentGuid } },
                    new AIInteractionToolCall { Id = "two", Name = "gh_add_component", Arguments = new JObject { ["componentGuid"] = ComponentGuid } },
                },
            },
            MakeTextReturn("reformulated"),
        });
        var dispatcher = ReversibleDispatcher();
        var options = BuildOptions();
        options.MaxToolCallsPerResponse = 1;
        var session = new ConversationSession(provider, dispatcher, options);

        await session.RunAsync("add two panels");

        Assert.Equal(0, dispatcher.DispatchCount);
        Assert.All(session.History.OfType<AIInteractionToolResult>(), result =>
            Assert.True(result.Result["rejected"]?.Value<bool>()));
    }

    [Fact]
    public async Task ApprovalBindsFrozenEffectiveArguments_NotProviderOwnedObjects()
    {
        var providerArgs = new JObject { ["componentGuid"] = ComponentGuid };
        var provider = new FakeAIProvider(new[]
        {
            MakeToolUseReturn("toolu_1", "gh_add_component", providerArgs),
            MakeTextReturn("done"),
        });
        var dispatcher = ReversibleDispatcher();
        var session = new ConversationSession(provider, dispatcher, BuildOptions());
        string? rendered = null;
        session.PlanReviewer = (plan, _) =>
        {
            rendered = plan.Calls[0].RenderedArguments;
            providerArgs["x"] = 9999; // adversarial mutation after the approval view was built
            return Task.FromResult(true);
        };

        await session.RunAsync("add a panel");

        var dispatched = Assert.Single(dispatcher.Calls);
        Assert.Equal(0d, dispatched.Arguments["x"]!.Value<double>());
        Assert.Equal(0d, dispatched.Arguments["y"]!.Value<double>());
        Assert.NotNull(rendered);
        Assert.Contains("\"x\":0.0", rendered!);
    }

    [Fact]
    public async Task ReplayedProviderToolId_IsRejectedBeforeSecondDispatch()
    {
        var provider = new FakeAIProvider(new[]
        {
            MakeToolUseReturn("replay", "gh_add_component", new JObject { ["componentGuid"] = ComponentGuid }),
            MakeToolUseReturn("replay", "gh_add_component", new JObject { ["componentGuid"] = ComponentGuid }),
            MakeTextReturn("done"),
        });
        var dispatcher = ReversibleDispatcher();
        var session = new ConversationSession(provider, dispatcher, BuildOptions());
        // Approve every plan: without a reviewer the mutation fails closed before the first
        // dispatch, and the replay guard would never be exercised.
        session.PlanReviewer = (_, _) => Task.FromResult(true);

        await session.RunAsync("add a panel");

        Assert.Equal(1, dispatcher.DispatchCount);
        Assert.Contains(session.History.OfType<AIInteractionToolResult>(), r =>
            r.Result["rejected"]?.Value<bool>() == true &&
            r.Result["message"]!.ToString().Contains("replayed provider tool-call id"));
    }

    [Fact]
    public async Task AskUserMixedWithMutation_RejectsWholeBatchWithoutSiblingDispatch()
    {
        var provider = new FakeAIProvider(new[]
        {
            new AIReturn
            {
                Status = AICallStatus.Finished,
                StopReason = "tool_use",
                Interactions = new List<IAIInteraction>
                {
                    new AIInteractionToolCall { Id = "ask", Name = AskUserTool.ToolName, Arguments = new JObject { ["questions"] = new JArray() } },
                    new AIInteractionToolCall { Id = "mutate", Name = "gh_add_component", Arguments = new JObject { ["componentGuid"] = ComponentGuid } },
                },
            },
        });
        var dispatcher = ReversibleDispatcher();
        var session = new ConversationSession(provider, dispatcher, BuildOptions());

        await session.RunAsync("ask and mutate");

        Assert.Equal(0, dispatcher.DispatchCount);
        Assert.False(session.Clarification.IsPaused);
        Assert.All(session.History.OfType<AIInteractionToolResult>(), result =>
            Assert.True(result.Result["rejected"]?.Value<bool>()));
    }

    [Fact]
    public async Task DescribeWithSimpleId_IsRejectedBeforeDispatch()
    {
        var provider = new FakeAIProvider(new[]
        {
            MakeToolUseReturn("describe", "gh_describe", new JObject { ["target"] = "slider1" }),
            MakeTextReturn("reformulate"),
        });
        var dispatcher = new FakeToolDispatcher(
            new[] { new AIToolDefinition("gh_describe", "Describe", new JObject { ["type"] = "object" }) },
            (_, _) => new JObject { ["status"] = "success" });
        var session = new ConversationSession(provider, dispatcher, BuildOptions());

        await session.RunAsync("describe slider1");

        Assert.Equal(0, dispatcher.DispatchCount);
        Assert.Contains(session.History.OfType<AIInteractionToolResult>(), result =>
            result.Result["rejected"]?.Value<bool>() == true);
    }

    private static FakeToolDispatcher ReversibleDispatcher() => new(
        new[] { new AIToolDefinition("gh_add_component", "Add", new JObject { ["type"] = "object" }) { IsReversible = true } },
        (_, _) => new JObject { ["success"] = true });

    [Fact]
    public async Task ReviewerApprove_ToolsExecuteNormally()
    {
        var provider = new FakeAIProvider(new[]
        {
            MakeToolUseReturn("toolu_1", "gh_add_component", new JObject
            {
                ["componentGuid"] = ComponentGuid,
                ["nickname"] = "Number Slider",
                ["x"] = 100,
                ["y"] = 200,
            }),
            MakeTextReturn("added the slider"),
        });

        var dispatcher = new FakeToolDispatcher(
            definitions: new[]
            {
                new AIToolDefinition(
                    "gh_add_component",
                    "Add a component.",
                    new JObject { ["type"] = "object", ["properties"] = new JObject() })
                    { IsReversible = true },
            },
            dispatch: (_, _) => new JObject { ["success"] = true });

        var session = new ConversationSession(provider, dispatcher, BuildOptions());

        ToolCallPlan? observed = null;
        int reviewerInvocations = 0;
        session.PlanReviewer = (plan, _) =>
        {
            reviewerInvocations++;
            observed = plan;
            return Task.FromResult(true);
        };

        var result = await session.RunAsync("add a slider at (100, 200)");

        Assert.True(result.Success);
        Assert.Equal(SessionStatus.Idle, session.Status);
        Assert.Equal(1, reviewerInvocations);
        Assert.NotNull(observed);
        Assert.Single(observed!.Calls);
        Assert.Equal("gh_add_component", observed.Calls[0].Name);
        Assert.True(observed.Calls[0].IsReversible);
        Assert.NotNull(observed.Calls[0].ArgumentPreview);
        // A1/S18: the preview names the exact componentGuid that executes, never the model's
        // nickname (which here claims "Number Slider" for an arbitrary GUID).
        Assert.Contains(ComponentGuid, observed.Calls[0].ArgumentPreview!);
        Assert.DoesNotContain("Number Slider", observed.Calls[0].ArgumentPreview!);
        Assert.Contains("100", observed.Calls[0].ArgumentPreview!);

        // Approved => the tool actually ran and produced a tool_result.
        Assert.Equal(1, dispatcher.DispatchCount);
        Assert.Contains(session.History, i => i is AIInteractionToolResult r && r.Id == "toolu_1");
        var toolResult = session.History.OfType<AIInteractionToolResult>().First(r => r.Id == "toolu_1");
        Assert.False(toolResult.Result["cancelled"]?.Value<bool>() == true);
    }

    [Fact]
    public async Task ReviewerCancel_ToolsNotExecuted_AndModelSeesCancellation()
    {
        var provider = new FakeAIProvider(new[]
        {
            MakeToolUseReturn("toolu_1", "gh_add_component", new JObject { ["componentGuid"] = ComponentGuid }),
            // A defensive second response in case the session tried to loop again (it must not).
            MakeTextReturn("should not be reached"),
        });

        var dispatcher = new FakeToolDispatcher(
            definitions: new[]
            {
                new AIToolDefinition(
                    "gh_add_component",
                    "Add a component.",
                    new JObject { ["type"] = "object", ["properties"] = new JObject() })
                    { IsReversible = true },
            },
            dispatch: (_, _) => new JObject { ["success"] = true });

        var session = new ConversationSession(provider, dispatcher, BuildOptions());
        session.PlanReviewer = (_, _) => Task.FromResult(false);

        var result = await session.RunAsync("add a panel");

        Assert.True(result.Success);
        Assert.Equal(SessionStatus.Idle, session.Status);

        // Cancel => dispatcher never ran, and exactly one provider call was made (no looping).
        Assert.Equal(0, dispatcher.DispatchCount);
        Assert.Equal(1, provider.CallCount);

        // A synthetic cancellation tool_result was recorded so history is well-formed.
        var cancelResult = session.History
            .OfType<AIInteractionToolResult>()
            .FirstOrDefault(r => r.Id == "toolu_1");
        Assert.NotNull(cancelResult);
        Assert.True(cancelResult!.Result["cancelled"]?.Value<bool>());
        Assert.Equal("User cancelled plan before execution.", cancelResult.Result["message"]?.ToString());

        // Continuing the conversation: the model would see this tool_result in its next turn.
        // Verify the outgoing request shape by kicking a follow-up prompt and inspecting what
        // the provider was called with.
        var followupProvider = new FakeAIProvider(new[]
        {
            MakeTextReturn("ok, understood"),
        });
        // Swap in a new provider so we can inspect the next request (the original session is
        // reused to preserve history).
        var followupSession = new ConversationSession(
            followupProvider,
            dispatcher,
            BuildOptions());

        // Replay the cancelled history into the new session as a quick simulation.
        // (We cannot swap providers on the real session, so we just assert the history bit above.)
        Assert.NotNull(followupSession);
    }

    [Fact]
    public async Task ReadOnlyBatch_ReviewerNotInvoked()
    {
        var provider = new FakeAIProvider(new[]
        {
            MakeToolUseReturn("toolu_1", "gh_describe", new JObject { ["target"] = "7a5d683b-5226-46db-b73a-f3076c781a41" }),
            MakeTextReturn("S1 is a Number Slider"),
        });

        var dispatcher = new FakeToolDispatcher(
            definitions: new[]
            {
                new AIToolDefinition(
                    "gh_describe",
                    "Describe a component.",
                    new JObject { ["type"] = "object", ["properties"] = new JObject() }),
                    // No IsReversible => false. Read-only.
            },
            dispatch: (_, _) => new JObject { ["name"] = "Number Slider" });

        var session = new ConversationSession(provider, dispatcher, BuildOptions());

        int reviewerInvocations = 0;
        session.PlanReviewer = (_, _) =>
        {
            reviewerInvocations++;
            return Task.FromResult(false); // would cancel if invoked
        };

        var result = await session.RunAsync("what is S1?");

        Assert.True(result.Success);
        // Reviewer was NOT called because no reversible tool was in the batch.
        Assert.Equal(0, reviewerInvocations);
        // And the read-only tool did run.
        Assert.Equal(1, dispatcher.DispatchCount);
    }

    [Fact]
    public async Task NullReviewer_MutationIsRejectedFailClosed()
    {
        var provider = new FakeAIProvider(new[]
        {
            MakeToolUseReturn("toolu_1", "gh_add_component", new JObject { ["componentGuid"] = ComponentGuid }),
            MakeTextReturn("added"),
        });

        var dispatcher = new FakeToolDispatcher(
            definitions: new[]
            {
                new AIToolDefinition(
                    "gh_add_component",
                    "Add a component.",
                    new JObject { ["type"] = "object", ["properties"] = new JObject() })
                    { IsReversible = true },
            },
            dispatch: (_, _) => new JObject { ["success"] = true });

        var session = new ConversationSession(provider, dispatcher, BuildOptions());
        // Do NOT install a reviewer.

        var result = await session.RunAsync("add a panel");

        Assert.True(result.Success);
        Assert.Equal(SessionStatus.Idle, session.Status);
        Assert.Equal(0, dispatcher.DispatchCount);
        Assert.Contains(session.History.OfType<AIInteractionToolResult>(), r =>
            r.Result["rejected"]?.Value<bool>() == true);
    }

    [Fact]
    public async Task AskUser_NotAffectedByReviewer()
    {
        var provider = new FakeAIProvider(new[]
        {
            MakeAskUserReturn("toolu_ask", "q1", "How many?"),
        });
        var dispatcher = new FakeToolDispatcher(
            definitions: Array.Empty<AIToolDefinition>(),
            dispatch: (_, _) => new JObject());

        var session = new ConversationSession(provider, dispatcher, BuildOptions());

        int reviewerInvocations = 0;
        session.PlanReviewer = (_, _) =>
        {
            reviewerInvocations++;
            return Task.FromResult(false);
        };

        var result = await session.RunAsync("set up a slider");

        Assert.True(result.Success);
        Assert.Equal(SessionStatus.Paused, session.Status);
        Assert.True(session.Clarification.IsPaused);
        // Reviewer must not have been called — ask_user takes priority over plan review.
        Assert.Equal(0, reviewerInvocations);
    }

    [Fact]
    public async Task ReviewerReceivesAssistantText()
    {
        var provider = new FakeAIProvider(new[]
        {
            MakeToolUseReturnWithText(
                "I'll add a slider and a panel.",
                "toolu_1",
                "gh_add_component",
                new JObject { ["componentGuid"] = ComponentGuid, ["nickname"] = "Number Slider" }),
            MakeTextReturn("done"),
        });

        var dispatcher = new FakeToolDispatcher(
            definitions: new[]
            {
                new AIToolDefinition(
                    "gh_add_component",
                    "Add a component.",
                    new JObject { ["type"] = "object", ["properties"] = new JObject() })
                    { IsReversible = true },
            },
            dispatch: (_, _) => new JObject { ["success"] = true });

        var session = new ConversationSession(provider, dispatcher, BuildOptions());
        ToolCallPlan? observed = null;
        session.PlanReviewer = (plan, _) =>
        {
            observed = plan;
            return Task.FromResult(true);
        };

        await session.RunAsync("add stuff");

        Assert.NotNull(observed);
        Assert.Equal("I'll add a slider and a panel.", observed!.AssistantText);
    }

    [Fact]
    public async Task MixedBatch_WithAnyReversible_InvokesReviewer()
    {
        // Provider emits two tool calls in one turn: one read-only (gh_describe) and one
        // reversible (gh_add_component). Any reversible tool in the batch must trigger review.
        var toolUse = new AIReturn
        {
            Status = AICallStatus.Finished,
            StopReason = "tool_use",
            InputTokens = 10,
            OutputTokens = 5,
            Interactions = new List<IAIInteraction>
            {
                new AIInteractionToolCall
                {
                    Id = "toolu_describe",
                    Name = "gh_describe",
                    Arguments = new JObject { ["target"] = "7a5d683b-5226-46db-b73a-f3076c781a41" },
                    Agent = AIAgent.ToolCall,
                },
                new AIInteractionToolCall
                {
                    Id = "toolu_add",
                    Name = "gh_add_component",
                    Arguments = new JObject { ["componentGuid"] = ComponentGuid },
                    Agent = AIAgent.ToolCall,
                },
            },
        };

        var provider = new FakeAIProvider(new[]
        {
            toolUse,
            MakeTextReturn("done"),
        });

        var dispatcher = new FakeToolDispatcher(
            definitions: new[]
            {
                new AIToolDefinition(
                    "gh_describe",
                    "Describe.",
                    new JObject { ["type"] = "object" }),
                new AIToolDefinition(
                    "gh_add_component",
                    "Add a component.",
                    new JObject { ["type"] = "object" }) { IsReversible = true },
            },
            dispatch: (_, _) => new JObject { ["success"] = true });

        var session = new ConversationSession(provider, dispatcher, BuildOptions());

        ToolCallPlan? observed = null;
        session.PlanReviewer = (plan, _) =>
        {
            observed = plan;
            return Task.FromResult(true);
        };

        var result = await session.RunAsync("describe S1 and add a panel");

        Assert.True(result.Success);
        Assert.NotNull(observed);
        Assert.Equal(2, observed!.Calls.Count);
        // Calls surface in the same order the provider emitted them.
        Assert.Equal("gh_describe", observed.Calls[0].Name);
        Assert.False(observed.Calls[0].IsReversible);
        Assert.Equal("gh_add_component", observed.Calls[1].Name);
        Assert.True(observed.Calls[1].IsReversible);
        // Both tools ran (reviewer approved).
        Assert.Equal(2, dispatcher.DispatchCount);
    }

    [Fact]
    public void AddComponent_RendersCatalogNameNotNickname()
    {
        // S18/A1: the model labels a Circle as "Number Slider"; the plan must show what executes.
        const string circle = "807b86e3-be8d-4970-92b5-f8cdcb45b06b";
        var catalog = JsonComponentCatalog.Load("tests/shared/rhino8-gh-catalog.json");
        var call = new AIInteractionToolCall
        {
            Id = "toolu_1",
            Name = "gh_add_component",
            Arguments = new JObject { ["componentGuid"] = circle, ["nickname"] = "Number Slider", ["x"] = 10, ["y"] = 20 },
        };
        var reversibility = new Dictionary<string, bool> { ["gh_add_component"] = true };

        var summary = Assert.Single(PlanReviewBuilder.BuildPlan(new[] { call }, null, reversibility, catalog).Calls);

        Assert.Equal($"Circle [Curve/Primitive · Curve Components] {circle}", summary.ResolvedLabel);
        Assert.Equal("Circle [Curve/Primitive · Curve Components] @ (10, 20)", summary.ArgumentPreview);
        Assert.DoesNotContain("Number Slider", summary.ResolvedLabel);
        Assert.DoesNotContain("Number Slider", summary.ArgumentPreview);
    }

    [Fact]
    public void AddComponent_NumberSliderLabel_MatchesTheLiveReceiptFormat()
    {
        var catalog = JsonComponentCatalog.Load("tests/shared/rhino8-gh-catalog.json");
        var call = new AIInteractionToolCall
        {
            Id = "toolu_1",
            Name = "gh_add_component",
            Arguments = new JObject { ["componentGuid"] = "57DA07BD-ECAB-415D-9D86-AF36D7073ABC" },
        };

        var summary = PlanReviewBuilder.BuildPlan(new[] { call }, null, new Dictionary<string, bool>(), catalog).Calls[0];

        Assert.StartsWith("Number Slider [Params/Input · Grasshopper]", summary.ResolvedLabel);
        Assert.EndsWith("57da07bd-ecab-415d-9d86-af36d7073abc", summary.ResolvedLabel);
    }

    [Fact]
    public void AddComponent_ObsoleteEntry_IsFlaggedInTheLabel()
    {
        var entry = new ComponentCatalogEntry(Guid.Parse(ComponentGuid), "Old Thing", "Old", "Maths", "Util", "Math Components", true, "hidden", "");
        var catalog = new ComponentCatalogSnapshot(new[] { entry }, Array.Empty<LibraryEntry>());
        var call = new AIInteractionToolCall { Id = "t", Name = "gh_add_component", Arguments = new JObject { ["componentGuid"] = ComponentGuid } };

        var summary = PlanReviewBuilder.BuildPlan(new[] { call }, null, new Dictionary<string, bool>(), catalog).Calls[0];

        Assert.Equal($"Old Thing [Maths/Util · Math Components] (obsolete) {ComponentGuid}", summary.ResolvedLabel);
    }

    [Fact]
    public void AddComponent_UnresolvedGuid_HasNoResolvedLabel_AndPreviewShowsTheGuid()
    {
        var call = new AIInteractionToolCall
        {
            Id = "t",
            Name = "gh_add_component",
            Arguments = new JObject { ["componentGuid"] = ComponentGuid, ["nickname"] = "Number Slider" },
        };

        var summary = PlanReviewBuilder.BuildPlan(new[] { call }, null, new Dictionary<string, bool>(), EmptyComponentCatalog.Instance).Calls[0];

        Assert.Null(summary.ResolvedLabel);
        Assert.Contains($"unresolved component {ComponentGuid}", summary.ArgumentPreview);
        Assert.DoesNotContain("Number Slider", summary.ArgumentPreview);
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
        InputTokens = 10,
        OutputTokens = 3,
        Interactions = new List<IAIInteraction>
        {
            new AIInteractionText { Agent = AIAgent.Assistant, Content = content },
        },
    };

    private static AIReturn MakeToolUseReturn(string toolUseId, string toolName, JObject arguments) => new()
    {
        Status = AICallStatus.Finished,
        StopReason = "tool_use",
        InputTokens = 10,
        OutputTokens = 5,
        Interactions = new List<IAIInteraction>
        {
            new AIInteractionToolCall
            {
                Id = toolUseId,
                Name = toolName,
                Arguments = arguments,
                Agent = AIAgent.ToolCall,
            },
        },
    };

    private static AIReturn MakeToolUseReturnWithText(
        string assistantText,
        string toolUseId,
        string toolName,
        JObject arguments) => new()
    {
        Status = AICallStatus.Finished,
        StopReason = "tool_use",
        InputTokens = 10,
        OutputTokens = 5,
        Interactions = new List<IAIInteraction>
        {
            new AIInteractionText { Agent = AIAgent.Assistant, Content = assistantText },
            new AIInteractionToolCall
            {
                Id = toolUseId,
                Name = toolName,
                Arguments = arguments,
                Agent = AIAgent.ToolCall,
            },
        },
    };

    [Fact]
    public void WithInstanceLabels_NamesDeleteTargetsFromTheHost_AndLeavesTheApprovedArgumentsAlone()
    {
        var circle = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var missing = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var calls = new[]
        {
            Call("toolu_1", "gh_delete", new JObject { ["target"] = circle.ToString("D") }),
            Call("toolu_2", "gh_delete", new JObject { ["target"] = missing.ToString("D") }),
            Call("toolu_3", "gh_delete", new JObject { ["target"] = "slider nickname" }),
            Call("toolu_4", "gh_connect", new JObject { ["from"] = circle.ToString("D"), ["to"] = missing.ToString("D") }),
        };
        var plan = PlanReviewBuilder.BuildPlan(calls, null, new Dictionary<string, bool>
        {
            ["gh_delete"] = true,
            ["gh_connect"] = true,
        }) with { BindingId = "doc:binding" };

        var shown = PlanReviewBuilder.WithInstanceLabels(
            plan,
            id => id == circle ? PlanReviewBuilder.FormatInstanceLabel("Circle", "Curve", "Primitive", id) : null);

        Assert.Equal("Circle [Curve/Primitive] 22222222-2222-2222-2222-222222222222", shown.Calls[0].ResolvedLabel);
        Assert.Equal("not found on the pinned canvas 33333333-3333-3333-3333-333333333333", shown.Calls[1].ResolvedLabel);
        Assert.Null(shown.Calls[2].ResolvedLabel);
        Assert.Null(shown.Calls[3].ResolvedLabel);
        Assert.Equal(plan.Calls.Select(c => c.RenderedArguments), shown.Calls.Select(c => c.RenderedArguments));
        Assert.Equal("doc:binding", shown.BindingId);
    }

    [Fact]
    public void FormatInstanceLabel_UsesTypeNameAndCategory_NeverANickname()
    {
        var id = Guid.Parse("44444444-4444-4444-4444-444444444444");
        Assert.Equal("Number Slider [Params/Input] 44444444-4444-4444-4444-444444444444", PlanReviewBuilder.FormatInstanceLabel("Number Slider", "Params", "Input", id));
        Assert.Equal("(unnamed object) 44444444-4444-4444-4444-444444444444", PlanReviewBuilder.FormatInstanceLabel(null, null, " ", id));
    }

    [Fact]
    public void CanvasObjectLabels_ResolveOnlyOnTheDocumentThePlanIsBoundTo()
    {
        Assert.True(Parti.Core.Grasshopper.Chat.CanvasObjectLabels.IsBoundTo("abc123:binding", "abc123"));
        Assert.False(Parti.Core.Grasshopper.Chat.CanvasObjectLabels.IsBoundTo("other:binding", "abc123"));
        Assert.False(Parti.Core.Grasshopper.Chat.CanvasObjectLabels.IsBoundTo("abc123", "abc123"));
        Assert.False(Parti.Core.Grasshopper.Chat.CanvasObjectLabels.IsBoundTo(null, "abc123"));
    }

    private static AIInteractionToolCall Call(string id, string name, JObject args) => new()
    {
        Id = id,
        Name = name,
        Arguments = args,
        Agent = AIAgent.ToolCall,
    };

    private static AIReturn MakeAskUserReturn(string toolUseId, string questionId, string prompt) => new()
    {
        Status = AICallStatus.Finished,
        StopReason = "tool_use",
        InputTokens = 20,
        OutputTokens = 5,
        Interactions = new List<IAIInteraction>
        {
            new AIInteractionToolCall
            {
                Id = toolUseId,
                Name = AskUserTool.ToolName,
                Arguments = new JObject
                {
                    ["questions"] = new JArray
                    {
                        new JObject
                        {
                            ["id"] = questionId,
                            ["prompt"] = prompt,
                            ["kind"] = "text",
                        },
                    },
                },
                Agent = AIAgent.ToolCall,
            },
        },
    };
}
