// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Prompts;
using Parti.Core.Sessions;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Core.Tests.Sessions;

/// <summary>
/// Integration tests verifying that <see cref="ConversationSession"/> invokes
/// <see cref="SessionPromptBuilder"/> on each entry call and uses its output as the
/// top-level system prompt.
/// </summary>
public class ConversationSessionPromptBuilderTests
{
    private const string Layer0Body = "# LAYER0-MARKER foundations";
    private const string CanvasBody = "# GHCANVAS-MARKER canvas body";

    private static SessionOptions DefaultOptions() => new()
    {
        MaxTurns = 3,
        MaxToolPasses = 2,
        ApiKey = "unused",
        Model = "test-model",
    };

    private static SkillLoader BuildLoader() => new(new InMemorySkillSource(
        new Dictionary<string, string>
        {
            ["parti-foundations/SKILL.md"] = Layer0Body,
            ["parti-foundations/parti-ghcanvas/SKILL.md"] = CanvasBody,
        }));

    private sealed class StubRouter : ISkillRouter
    {
        private readonly IReadOnlyList<string> _subSkills;
        public StubRouter(params string[] subSkills) { _subSkills = subSkills; }
        public SkillRouteResult Route(string _) =>
            new(_subSkills,
                _subSkills.ToDictionary<string, string, IReadOnlyList<string>>(
                    s => s, _ => new List<string> { "(stub)" }));
    }

    /// <summary>
    /// Captures every request's SystemPrompt. Always replies with a single end_turn text, so the
    /// session terminates after one provider call.
    /// </summary>
    private sealed class CapturingProvider : IAIProvider
    {
        public List<string?> CapturedSystemPrompts { get; } = new();
        public int CallCount { get; private set; }

        public string Name => "fake-capturing";
        public Uri DefaultServerUrl { get; } = new("https://example.invalid");

        public AIRequestCall PreCall(AIRequestCall request) => request;
        public string CreateRequestBody(AIRequestCall request) => "{}";
        public List<IAIInteraction> Decode(JObject response) => new();
        public string Encode(IAIInteraction interaction) => "{}";

        public Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken ct = default)
        {
            CallCount++;
            CapturedSystemPrompts.Add(request.SystemPrompt);
            var ret = new AIReturn
            {
                Status = AICallStatus.Finished,
                StopReason = "end_turn",
                Request = request,
                Interactions = new List<IAIInteraction>
                {
                    new AIInteractionText { Agent = AIAgent.Assistant, Content = "ok" },
                },
            };
            return Task.FromResult(ret);
        }
    }

    private sealed class EmptyDispatcher : IToolDispatcher
    {
        public IReadOnlyList<AIToolDefinition> GetDefinitions() => Array.Empty<AIToolDefinition>();
        public bool HasTool(string name) => false;
        public Task<AIInteractionToolResult> DispatchAsync(AIInteractionToolCall call, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private sealed class RecordingObserver : IConversationObserver
    {
        public List<(string TurnId, IReadOnlyList<string> SubSkills)> Activations { get; } = new();

        public void OnStart(AIRequestCall request) { }
        public void OnTurnStart(string turnId, int turnNumber) { }
        public void OnInteractionCompleted(IAIInteraction interaction) { }
        public void OnToolCall(AIInteractionToolCall toolCall) { }
        public void OnToolResult(AIInteractionToolResult toolResult) { }
        public void OnFinal(AIReturn finalResult) { }
        public void OnError(Exception error) { }
        public void OnSkillsActivated(string turnId, IReadOnlyList<string> activatedSubSkills) =>
            Activations.Add((turnId, activatedSubSkills));
    }

    [Fact]
    public async Task RunAsync_WithPromptBuilder_SendsBuiltPromptToProvider()
    {
        var provider = new CapturingProvider();
        var session = new ConversationSession(provider, new EmptyDispatcher(), DefaultOptions())
        {
            PromptBuilder = new SessionPromptBuilder(BuildLoader(), new StubRouter("parti-ghcanvas")),
        };

        var result = await session.RunAsync("add a panel to the canvas");

        Assert.True(result.Success);
        Assert.Single(provider.CapturedSystemPrompts);
        var sent = provider.CapturedSystemPrompts[0];
        Assert.NotNull(sent);
        Assert.Contains(Layer0Body, sent!);
        Assert.Contains(CanvasBody, sent!);
        Assert.Equal(new[] { "parti-ghcanvas" }, session.LastActivatedSubSkills);
    }

    [Fact]
    public async Task RunAsync_WithPromptBuilder_OverridesOptionsSystemPrompt()
    {
        var provider = new CapturingProvider();
        var opts = DefaultOptions();
        opts.SystemPrompt = "STATIC-PROMPT-THAT-SHOULD-NOT-APPEAR";
        var session = new ConversationSession(provider, new EmptyDispatcher(), opts)
        {
            PromptBuilder = new SessionPromptBuilder(BuildLoader(), new StubRouter("parti-ghcanvas")),
        };

        await session.RunAsync("anything");

        var sent = provider.CapturedSystemPrompts[0];
        Assert.NotNull(sent);
        Assert.Contains(Layer0Body, sent!);
        Assert.DoesNotContain("STATIC-PROMPT-THAT-SHOULD-NOT-APPEAR", sent!);
    }

    [Fact]
    public async Task RunAsync_WithoutPromptBuilder_UsesOptionsSystemPromptUnchanged()
    {
        var provider = new CapturingProvider();
        var opts = DefaultOptions();
        opts.SystemPrompt = "STATIC-PROMPT";
        var session = new ConversationSession(provider, new EmptyDispatcher(), opts);

        await session.RunAsync("anything");

        Assert.Equal("STATIC-PROMPT", provider.CapturedSystemPrompts[0]);
        Assert.Empty(session.LastActivatedSubSkills);
    }

    [Fact]
    public async Task RunAsync_WithPromptBuilder_NotifiesObserverOnSkillsActivated()
    {
        var provider = new CapturingProvider();
        var observer = new RecordingObserver();
        var session = new ConversationSession(provider, new EmptyDispatcher(), DefaultOptions(), observer)
        {
            PromptBuilder = new SessionPromptBuilder(BuildLoader(), new StubRouter("parti-ghcanvas")),
        };

        await session.RunAsync("add a panel");

        Assert.Single(observer.Activations);
        Assert.Equal(new[] { "parti-ghcanvas" }, observer.Activations[0].SubSkills);
        Assert.False(string.IsNullOrWhiteSpace(observer.Activations[0].TurnId));
    }

    [Fact]
    public async Task ContinueAsync_WithPromptBuilder_RunsBuilderAgainForFollowUp()
    {
        // First call routes to ghcanvas; ContinueAsync invokes the builder fresh — both provider
        // calls should receive a prompt containing the ghcanvas body.
        var provider = new CapturingProvider();
        var session = new ConversationSession(provider, new EmptyDispatcher(), DefaultOptions())
        {
            PromptBuilder = new SessionPromptBuilder(BuildLoader(), new StubRouter("parti-ghcanvas")),
        };

        await session.RunAsync("first");
        await session.ContinueAsync("second");

        Assert.Equal(2, provider.CapturedSystemPrompts.Count);
        Assert.All(provider.CapturedSystemPrompts, p =>
        {
            Assert.NotNull(p);
            Assert.Contains(CanvasBody, p!);
        });
        Assert.Equal(new[] { "parti-ghcanvas" }, session.LastActivatedSubSkills);
    }
}
