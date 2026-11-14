// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Parti.Core.Ai;
using Parti.Core.Grasshopper;
using Parti.Core.Grasshopper.Chat;
using Parti.Core.Grasshopper.Tools;
using Parti.Core.Sessions;
using Parti.Core.Sessions.Policies;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// W0: <see cref="PartiComposition.CreateController"/> reproduces the wiring ChatForm built
/// inline — product tools, model, API key, max tokens, system prompt, and plan reviewer.
/// </summary>
public class PartiCompositionTests
{
    private static AIReturn EndTurn() => new()
    {
        Status = AICallStatus.Finished,
        StopReason = "end_turn",
        Interactions = new List<IAIInteraction>
        {
            new AIInteractionText { Agent = AIAgent.Assistant, Content = "ok" },
        },
    };

    private static PartiCompositionOptions Options(FakeAIProvider provider, PlanReviewer? reviewer = null, string? systemPrompt = null) => new(
        Provider: provider,
        UndoFactory: new NoOpUndoScopeFactory(),
        Skills: null,
        Catalog: EmptyComponentCatalog.Instance,
        Reviewer: reviewer,
        Model: "test-model",
        MaxTokens: PartiComposition.DefaultMaxTokens,
        ApiKey: "test-key",
        SystemPrompt: systemPrompt);

    [Fact]
    public async Task CreateController_WiresProductToolsAndSessionOptions()
    {
        var provider = new FakeAIProvider(new[] { EndTurn() });
        PlanReviewer reviewer = (_, _) => Task.FromResult(true);

        using var controller = PartiComposition.CreateController(Options(provider, reviewer, "custom prompt"), new SilentChatView());
        Assert.Same(reviewer, controller.PlanReviewer);

        Assert.True(await controller.SendAsync("hi"));

        var request = Assert.Single(provider.ReceivedRequests);
        // The product tool set plus the two session tools the unified loop advertises itself.
        var expectedTools = ProductToolSet
            .Definitions(new ProductToolContext(EmptyComponentCatalog.Instance, new ComponentLockRegistry()))
            .Select(d => d.Name)
            .Concat(new[] { AskUserTool.ToolName, RecordDecompositionTool.ToolName });
        Assert.Equal(expectedTools, request.Tools.Select(t => t.Name));
        Assert.Equal("test-model", request.Model);
        Assert.Equal("test-key", request.ApiKey);
        Assert.Equal(PartiComposition.DefaultMaxTokens, request.MaxTokens);
        Assert.Equal("custom prompt", request.SystemPrompt);
    }

    [Fact]
    public async Task CreateController_NullSystemPrompt_UsesFallbackWithUntrustedDataRule()
    {
        var provider = new FakeAIProvider(new[] { EndTurn() });

        using var controller = PartiComposition.CreateController(Options(provider), new SilentChatView());
        Assert.Null(controller.PlanReviewer);
        Assert.True(await controller.SendAsync("hi"));

        var request = Assert.Single(provider.ReceivedRequests);
        Assert.Equal(PartiComposition.FallbackSystemPrompt, request.SystemPrompt);
        Assert.Contains("untrusted data", request.SystemPrompt);
    }

    [Fact]
    public async Task CreateController_StartsFromCopiedSessionOverrides()
    {
        var provider = new FakeAIProvider(new[] { EndTurn() });
        var overrides = new SessionOptions { MaxTurns = 2, Temperature = 0.3, SystemPrompt = "from overrides" };
        var options = Options(provider) with { ApiKey = null, SessionOverrides = overrides };

        using var controller = PartiComposition.CreateController(options, new SilentChatView());
        Assert.True(await controller.SendAsync("hi"));

        var request = Assert.Single(provider.ReceivedRequests);
        Assert.Equal("test-model", request.Model);
        Assert.Equal(0.3, request.Temperature);
        Assert.Equal("from overrides", request.SystemPrompt);
        Assert.Equal(string.Empty, request.ApiKey);

        // The caller's overrides object is never mutated.
        Assert.Equal(new SessionOptions().Model, overrides.Model);
        Assert.Equal(2, overrides.MaxTurns);
    }

    [Fact]
    public async Task Controller_W0Stubs_ReportIdleUnpausedState()
    {
        using var controller = PartiComposition.CreateController(
            Options(new FakeAIProvider(new[] { EndTurn() })), new SilentChatView());

        Assert.Equal(SessionStatus.Idle, controller.Status);
        Assert.Null(controller.PendingQuestions);
        Assert.Null(controller.LastReturn);
        Assert.Empty(controller.LockedComponents);
        Assert.False(controller.CurrentDecomposition.HasFormalization);
        Assert.False(await controller.AnswerAsync(new[] { new ClarificationAnswer("q1", "yes") }));
        controller.Unlock(System.Guid.NewGuid());
    }

    [Fact]
    public void PolicyStubs_AcceptEveryResponse()
    {
        var reversibility = new Dictionary<string, bool> { ["gh_delete"] = true };
        var calls = new[] { new AIInteractionToolCall { Id = "toolu_1", Name = "gh_delete" } };
        IToolCallPolicy[] policies =
        {
            new ComponentLockPolicy(new ComponentLockRegistry()),
            new ComponentCatalogPolicy(JsonComponentCatalog.Load("tests/shared/rhino8-gh-catalog.json")),
        };

        foreach (var policy in policies)
        {
            Assert.True(policy.TryValidate(calls, reversibility, out var error));
            Assert.Equal(string.Empty, error);
        }
    }

    private sealed class SilentChatView : IChatView
    {
        public void AppendUserMessage(string text) { }
        public void AppendAssistantText(string delta) { }
        public void SetBusy(bool busy) { }
        public void ShowError(string message) { }
        public void ClearHistory() { }
    }
}
