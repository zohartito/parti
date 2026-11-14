// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Design;
using Parti.Core.Grasshopper;
using Parti.Core.Grasshopper.Chat;
using Parti.Core.Prompts;
using Parti.Core.Sessions;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// L2 acceptance tests: the controller runs the session loop (streaming, ask_user, memory,
/// error repair) and <see cref="PartiComposition"/> wires the product the way the spec says.
/// </summary>
public class UnifiedLoopTests
{
    // ----- controller over the session loop ---------------------------------------------------

    [Fact]
    public async Task Controller_UsesSessionLoop_AskUserPausesAndResumes()
    {
        var view = new RecordingView();
        var provider = new StepProvider(
            Step.Tool("toolu_ask", AskUserTool.ToolName, Loop.AskArgs(("q1", "How many panels?", "text"))),
            Step.Text("Using 12 panels."));
        using var controller = new ChatController(provider, new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject()),
            new NoOpUndoScopeFactory(), Loop.Options(), view);

        Assert.True(await controller.SendAsync("make a facade"));

        Assert.Equal(SessionStatus.Paused, controller.Status);
        Assert.Equal("How many panels?", Assert.Single(controller.PendingQuestions!).Prompt);
        Assert.Equal("q1", Assert.Single(Assert.Single(view.Questions)).Id);

        // With exactly one text question pending, the next message is the answer.
        Assert.True(await controller.SendAsync("12"));

        Assert.Equal(SessionStatus.Idle, controller.Status);
        Assert.Null(controller.PendingQuestions);
        Assert.Contains("\"answer\":\"12\"", provider.RequestSnapshots[1]);
        Assert.Equal("Using 12 panels.", string.Concat(view.AssistantDeltas));
        Assert.NotNull(controller.LastReturn);
        Assert.Empty(view.Errors);
    }

    [Fact]
    public async Task Controller_SendWhileSeveralQuestionsPending_AsksForAnswers()
    {
        var view = new RecordingView();
        var provider = new StepProvider(
            Step.Tool("toolu_ask", AskUserTool.ToolName, Loop.AskArgs(("q1", "Depth?", "text"), ("q2", "Units?", "radio"))),
            Step.Text("ok"));
        using var controller = new ChatController(provider, new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject()),
            new NoOpUndoScopeFactory(), Loop.Options(), view);

        await controller.SendAsync("make a facade");
        Assert.False(await controller.SendAsync("0.3"));

        Assert.Equal(ChatController.AnswerQuestionsMessage, Assert.Single(view.Errors));
        Assert.Equal(SessionStatus.Paused, controller.Status);
        Assert.Single(provider.Requests);

        Assert.True(await controller.AnswerAsync(new[] { new ClarificationAnswer("q1", "0.3"), new ClarificationAnswer("q2", "m") }));
        Assert.Equal(SessionStatus.Idle, controller.Status);
        Assert.False(await controller.AnswerAsync(new[] { new ClarificationAnswer("q1", "again") }));
    }

    [Fact]
    public async Task Controller_StreamsTextDeltasViaObserver()
    {
        var view = new RecordingView();
        var provider = new StepProvider(Step.Text("Hel", "lo", "!"));
        var options = Loop.Options();
        options.Stream = false; // the controller turns streaming on for its own copy

        using var controller = new ChatController(provider, new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject()),
            new NoOpUndoScopeFactory(), options, view);
        await controller.SendAsync("hi");

        Assert.Equal(new[] { "Hel", "lo", "!" }, view.AssistantDeltas);
        Assert.Equal(1, provider.StreamCalls);
        Assert.Equal(0, provider.NonStreamCalls);
        Assert.False(options.Stream); // the caller's options are not mutated
    }

    [Fact]
    public async Task Controller_ReasoningDeltasGoToReasoningView_NotHistoryContent()
    {
        var view = new RecordingView();
        var provider = new StepProvider(Step.Text("The answer.").WithReasoning("thinking ", "hard"));
        using var controller = new ChatController(provider, new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject()),
            new NoOpUndoScopeFactory(), Loop.Options(), view);

        await controller.SendAsync("why?");

        Assert.Equal(new[] { "thinking ", "hard" }, view.ReasoningDeltas);
        Assert.DoesNotContain(view.AssistantDeltas, d => d.Contains("thinking", StringComparison.Ordinal));
        var assistant = Assert.Single(controller.History.OfType<AIInteractionText>(), t => t.Agent == AIAgent.Assistant);
        Assert.Equal("The answer.", assistant.Content);
        Assert.DoesNotContain(controller.History.OfType<AIInteractionText>(), t => (t.Content ?? string.Empty).Contains("thinking", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Controller_ShowsRecordedDecomposition()
    {
        var view = new RecordingView();
        var provider = new StepProvider(
            Step.Tool("toolu_rec", RecordDecompositionTool.ToolName, Loop.CompleteDecomposition()),
            Step.Text("Recorded."));
        using var controller = new ChatController(provider, new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject()),
            new NoOpUndoScopeFactory(), Loop.Options(), view);

        await controller.SendAsync("rainscreen, minimize solar gain");

        var (decomposition, revision) = Assert.Single(view.Decompositions);
        Assert.Equal(1, revision);
        Assert.Equal("panel depth", Assert.Single(decomposition.Inputs).Name);
        Assert.Equal(PillarSource.Assumed, Assert.Single(controller.CurrentDecomposition.Evaluations).Source);
    }

    [Fact]
    public async Task Memory_HarvestsInstanceGuidFromAddResult()
    {
        var added = Guid.NewGuid().ToString();
        var existing = Guid.NewGuid().ToString();
        var provider = new StepProvider(
            Step.Tools(("toolu_add", "gh_add_component", Loop.AddArgs()), ("toolu_all", "gh_get_all", new JObject())),
            Step.Text("done"));
        var dispatcher = new FakeToolDispatcher(
            new[] { Loop.Write("gh_add_component"), Loop.Read("gh_get_all") },
            (name, _) => name == "gh_add_component"
                ? new JObject { ["status"] = "success", ["instanceGuid"] = added, ["canonicalName"] = "Number Slider", ["simpleId"] = "slider-1" }
                : new JObject { ["nodes"] = new JArray(new JObject { ["id"] = existing, ["n"] = "Panel" }, new JObject { ["id"] = "panel-2", ["n"] = "Panel" }) });
        var session = new ConversationSession(provider, dispatcher, Loop.Options()) { PlanReviewer = (_, _) => Task.FromResult(true) };

        await session.RunAsync("add a slider");

        Assert.Equal(added, Assert.Single(session.Memory.RecentByType("Number Slider")).InstanceGuid);
        Assert.Equal(existing, Assert.Single(session.Memory.RecentByType("Panel")).InstanceGuid);
        Assert.Equal(added, session.Memory.ResolveReference("the slider I just added"));
    }

    // ----- product composition ----------------------------------------------------------------

    [Fact]
    public async Task ErrorRepairCap_StopsProductLoopAfterFive()
    {
        var steps = Enumerable.Range(1, 5)
            .Select(i => Step.Tool($"toolu_{i}", "gh_get_all", new JObject()))
            .Append(Step.Text("The canvas could not be read, so nothing changed."))
            .Append(Step.Tool("toolu_never", "gh_get_all", new JObject()))
            .ToArray();
        var provider = new StepProvider(steps);
        var dispatcher = new FakeToolDispatcher(new[] { Loop.Read("gh_get_all") }, (_, _) => new JObject { ["status"] = "error", ["message"] = "canvas unavailable" });
        var view = new RecordingView();

        using var controller = PartiComposition.CreateController(ProductOptions(provider) with { ToolsOverride = dispatcher }, view);
        await controller.SendAsync("what's on my canvas?");

        // The product budgets (24 turns, 20 passes) are larger than the cap, so the cap ends tool
        // use after five failing passes. One closing call with tools off sees the fifth failure.
        Assert.Equal(6, provider.Requests.Count);
        Assert.Equal(5, dispatcher.DispatchCount);
        var closing = provider.Requests[5];
        Assert.Equal("none", closing.ToolChoice);
        Assert.Contains(closing.Interactions.OfType<AIInteractionToolResult>(), r => r.Id == "toolu_5");
        Assert.Equal(ConversationSession.ErrorRepairCapNote(5), Assert.IsType<AIInteractionText>(closing.Interactions.Last()).Content);
        Assert.Equal("The canvas could not be read, so nothing changed.", string.Concat(view.AssistantDeltas));
        Assert.Contains("Error-repair cap (5)", Assert.Single(view.Errors));
        Assert.Equal(SessionStatus.Error, controller.Status);
        Assert.True(PartiComposition.ProductMaxToolPasses > PartiComposition.ProductErrorRepairCap);
        Assert.True(PartiComposition.ProductMaxTurns > PartiComposition.ProductErrorRepairCap + 1);
    }

    [Fact]
    public async Task ToolPassCap_ProductRunsTwentyPasses_ThenOneClosingReplyWithToolsOff()
    {
        var cap = PartiComposition.ProductMaxToolPasses;
        var steps = Enumerable.Range(1, cap + 1)
            .Select(i => Step.Tool($"toolu_{i}", "gh_get_all", new JObject()))
            .Append(Step.Text("The sliders are on the canvas; the wiring is left."))
            .ToArray();
        var provider = new StepProvider(steps);
        var dispatcher = new FakeToolDispatcher(new[] { Loop.Read("gh_get_all") }, (_, _) => new JObject { ["status"] = "success", ["nodes"] = new JArray() });
        var view = new RecordingView();

        using var controller = PartiComposition.CreateController(ProductOptions(provider) with { ToolsOverride = dispatcher }, view);
        await controller.SendAsync("keep checking the canvas");

        // Twenty passes run. The 21st response's call is dropped without running, and one closing
        // call with tools off turns the stop into a reply.
        Assert.Equal(cap, dispatcher.DispatchCount);
        Assert.Equal(cap + 2, provider.Requests.Count);
        Assert.True(PartiComposition.ProductMaxTurns >= cap + 2);
        Assert.DoesNotContain(controller.History.OfType<AIInteractionToolCall>(), c => c.Id == $"toolu_{cap + 1}");
        Assert.Equal(
            controller.History.Count(i => i is AIInteractionToolCall and not AIInteractionToolResult),
            controller.History.OfType<AIInteractionToolResult>().Count());
        var closing = provider.Requests.Last();
        Assert.Equal("none", closing.ToolChoice);
        Assert.Equal(ConversationSession.ToolPassCapNote(cap), Assert.IsType<AIInteractionText>(closing.Interactions.Last()).Content);
        Assert.Equal("The sliders are on the canvas; the wiring is left.", string.Concat(view.AssistantDeltas));
        Assert.Contains($"Tool-pass cap ({cap})", Assert.Single(view.Errors));
        Assert.Equal(SessionStatus.Error, controller.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapClosingRequest_EncodesForAnthropic_WithToolResultsBeforeTheNote(bool repairCap)
    {
        // Anthropic rejects a user message whose text comes before its tool_result blocks. The
        // closing note merges into the message that carries the last pass's results.
        var provider = new StepProvider(
            Step.Tool("toolu_1", "gh_get_all", new JObject()),
            Step.Tool("toolu_2", "gh_get_all", new JObject()),
            Step.Text("Summary."));
        var dispatcher = new FakeToolDispatcher(new[] { Loop.Read("gh_get_all") }, (_, _) => repairCap
            ? new JObject { ["status"] = "error", ["message"] = "canvas unavailable" }
            : new JObject { ["status"] = "success" });
        var options = Loop.Options();
        options.MaxToolPasses = 1;
        options.ErrorRepair = new ErrorRepairPolicy(1);
        var session = new ConversationSession(provider, dispatcher, options);

        await session.RunAsync("check the canvas");

        var closing = Assert.Single(provider.Requests, r => r.ToolChoice == "none");
        using var http = new System.Net.Http.HttpClient();
        var body = JObject.Parse(new Parti.Providers.Anthropic.AnthropicProvider(http).CreateRequestBody(closing));
        Assert.Equal("none", body["tool_choice"]!["type"]!.ToString());
        Assert.NotEmpty((JArray)body["tools"]!);
        var last = (JObject)((JArray)body["messages"]!).Last();
        Assert.Equal("user", last["role"]!.ToString());
        var content = (JArray)last["content"]!;
        Assert.Equal("tool_result", content.First()["type"]!.ToString());
        Assert.Equal("text", content.Last()["type"]!.ToString());
        var expectedNote = repairCap ? ConversationSession.ErrorRepairCapNote(1) : ConversationSession.ToolPassCapNote(1);
        Assert.Equal(expectedNote, content.Last()["text"]!.ToString());
    }

    [Fact]
    public async Task ErrorRepairCap_GateRejectionsCountTowardTheCap()
    {
        var steps = Enumerable.Range(1, 8)
            .Select(i => Step.Tool($"toolu_{i}", "gh_add_component", Loop.AddArgs()))
            .ToArray();
        var provider = new StepProvider(steps);
        var dispatcher = new FakeToolDispatcher(new[] { ProductDefinition("gh_add_component") }, (_, _) => new JObject { ["status"] = "success" });
        var reviews = 0;
        var view = new RecordingView();

        using var controller = PartiComposition.CreateController(
            ProductOptions(provider, catalog: SliderCatalog(), reviewer: (_, _) => { reviews++; return Task.FromResult(true); }) with { ToolsOverride = dispatcher },
            view);
        await controller.SendAsync("add a slider");

        // Five rejected passes, then the closing call. That reply still asks for an add (the
        // provider ignored tool_choice "none"); the call is dropped, never reviewed or run.
        Assert.Equal(6, provider.Requests.Count);
        Assert.Equal("none", provider.Requests[5].ToolChoice);
        Assert.Equal(0, dispatcher.DispatchCount);
        Assert.Equal(0, reviews);
        Assert.Equal(5, controller.History.OfType<AIInteractionToolResult>().Count());
        Assert.All(controller.History.OfType<AIInteractionToolResult>(), r => Assert.Equal("three_pillar", r.Result["gate"]?.ToString()));
        Assert.DoesNotContain(controller.History.OfType<AIInteractionToolCall>(), c => c.Id == "toolu_6");
        Assert.Contains("Error-repair cap (5)", Assert.Single(view.Errors));
    }

    [Fact]
    public async Task Composition_FixesProductLoopSettings_WhateverTheOverridesSay()
    {
        var provider = new StepProvider(Step.Tool("toolu_1", "gh_add_component", Loop.AddArgs()), Step.Text("ok"));
        var dispatcher = new FakeToolDispatcher(new[] { ProductDefinition("gh_add_component") }, (_, _) => new JObject { ["status"] = "success" });
        var overrides = new SessionOptions { Stream = false, RequireDecomposition = false, ErrorRepair = new ErrorRepairPolicy(50) };

        using var controller = PartiComposition.CreateController(
            ProductOptions(provider, catalog: SliderCatalog(), reviewer: (_, _) => Task.FromResult(true)) with { ToolsOverride = dispatcher, SessionOverrides = overrides },
            new RecordingView());
        await controller.SendAsync("add a slider");

        Assert.Equal(0, dispatcher.DispatchCount); // gate still on
        Assert.Equal("three_pillar", controller.History.OfType<AIInteractionToolResult>().First().Result["gate"]?.ToString());
        Assert.Equal(0, provider.NonStreamCalls); // streaming still on
        Assert.False(overrides.RequireDecomposition); // overrides not mutated
    }

    private static Step Adds(string prefix, int count) =>
        Step.Tools(Enumerable.Range(1, count).Select(i => ($"{prefix}_{i}", "gh_add_component", Loop.AddArgs())).ToArray());

    [Fact]
    public async Task Composition_StagedBuild_SixteenChangesPerResponse_WhateverTheOverridesSay()
    {
        var provider = new StepProvider(
            Step.Tool("toolu_rec", RecordDecompositionTool.ToolName, Loop.CompleteDecomposition()),
            Adds("big", PartiComposition.ProductMaxMutationsPerResponse + 1),
            Adds("stage", PartiComposition.ProductMaxMutationsPerResponse),
            Step.Text("Added the sliders."));
        var dispatcher = new FakeToolDispatcher(new[] { ProductDefinition("gh_add_component") }, (_, _) => new JObject { ["status"] = "success", ["instanceGuid"] = Guid.NewGuid().ToString() });
        var reviews = 0;
        var overrides = new SessionOptions { MaxMutationCallsPerResponse = 1, MaxToolCallsPerResponse = 1 };

        using var controller = PartiComposition.CreateController(
            ProductOptions(provider, catalog: SliderCatalog(), reviewer: (_, _) => { reviews++; return Task.FromResult(true); }) with { ToolsOverride = dispatcher, SessionOverrides = overrides },
            new RecordingView());
        await controller.SendAsync("sixteen sliders, one per storey height, judged by total height");

        // The oversized response is rejected whole with the way to recover; the staged one runs under one review.
        var rejected = controller.History.OfType<AIInteractionToolResult>().First(r => r.Id == "big_1").Result["message"]!.ToString();
        Assert.Contains($"stages of at most {PartiComposition.ProductMaxMutationsPerResponse} changes", rejected);
        Assert.Equal(PartiComposition.ProductMaxMutationsPerResponse, dispatcher.DispatchCount);
        Assert.Equal(1, reviews);
        Assert.Equal(SessionStatus.Idle, controller.Status);
    }

    [Fact]
    public async Task Composition_TurnChangeBudget_StopsCleanly_AndResetsWithTheNextMessage()
    {
        var perResponse = PartiComposition.ProductMaxMutationsPerResponse;
        var stages = PartiComposition.ProductMaxMutationsPerTurn / perResponse;
        var steps = new List<Step> { Step.Tool("toolu_rec", RecordDecompositionTool.ToolName, Loop.CompleteDecomposition()) };
        steps.AddRange(Enumerable.Range(1, stages).Select(i => Adds($"s{i}", perResponse)));
        steps.Add(Adds("over", 1));
        steps.Add(Step.Text("Built the first part; send a message to continue."));
        steps.Add(Adds("next", 1));
        steps.Add(Step.Text("Continued."));
        var provider = new StepProvider(steps.ToArray());
        var dispatcher = new FakeToolDispatcher(new[] { ProductDefinition("gh_add_component") }, (_, _) => new JObject { ["status"] = "success", ["instanceGuid"] = Guid.NewGuid().ToString() });
        var view = new RecordingView();

        using var controller = PartiComposition.CreateController(
            ProductOptions(provider, catalog: SliderCatalog(), reviewer: (_, _) => Task.FromResult(true)) with { ToolsOverride = dispatcher },
            view);
        await controller.SendAsync("a large grid of sliders, judged by count");

        Assert.Equal(PartiComposition.ProductMaxMutationsPerTurn, dispatcher.DispatchCount);
        var exhausted = controller.History.OfType<AIInteractionToolResult>().Single(r => r.Id == "over_1").Result["message"]!.ToString();
        Assert.Contains("Make no more changes this message", exhausted);
        Assert.Contains($"{PartiComposition.ProductMaxMutationsPerTurn} of {PartiComposition.ProductMaxMutationsPerTurn} changes requested this message", exhausted);
        Assert.Equal(SessionStatus.Idle, controller.Status);
        Assert.Empty(view.Errors);

        await controller.SendAsync("continue");
        Assert.Equal(PartiComposition.ProductMaxMutationsPerTurn + 1, dispatcher.DispatchCount);
    }

    [Fact]
    public async Task Composition_DecomposeThenAdd_ReachesReviewerWithCatalogLabelAndWhy()
    {
        var provider = new StepProvider(
            Step.Tool("toolu_rec", RecordDecompositionTool.ToolName, Loop.CompleteDecomposition()),
            Step.Tool("toolu_add", "gh_add_component", Loop.AddArgs()),
            Step.Text("Added a slider."));
        var dispatcher = new FakeToolDispatcher(new[] { ProductDefinition("gh_add_component") }, (_, _) => new JObject { ["status"] = "success", ["instanceGuid"] = Guid.NewGuid().ToString() });
        ToolCallPlan? reviewed = null;

        using var controller = PartiComposition.CreateController(
            ProductOptions(provider, catalog: SliderCatalog(), reviewer: (plan, _) => { reviewed = plan; return Task.FromResult(true); }) with { ToolsOverride = dispatcher },
            new RecordingView());
        await controller.SendAsync("rainscreen with depth input, minimize solar gain");

        Assert.Equal(1, dispatcher.DispatchCount);
        Assert.NotNull(reviewed);
        Assert.Equal(1, reviewed!.DecompositionRevision);
        Assert.Equal("staggered rainscreen with alternating course depths", reviewed.Decomposition!.Principle);
        Assert.Contains("Number Slider", Assert.Single(reviewed.Calls).ResolvedLabel);
    }

    [Fact]
    public async Task Composition_UnknownComponentGuid_IsRejectedByCatalogPolicy()
    {
        var unknown = Guid.NewGuid().ToString();
        var provider = new StepProvider(
            Step.Tool("toolu_rec", RecordDecompositionTool.ToolName, Loop.CompleteDecomposition()),
            Step.Tool("toolu_add", "gh_add_component", new JObject { ["componentGuid"] = unknown }),
            Step.Text("ok"));
        var dispatcher = new FakeToolDispatcher(new[] { ProductDefinition("gh_add_component") }, (_, _) => new JObject { ["status"] = "success" });
        var reviews = 0;

        using var controller = PartiComposition.CreateController(
            ProductOptions(provider, catalog: SliderCatalog(), reviewer: (_, _) => { reviews++; return Task.FromResult(true); }) with { ToolsOverride = dispatcher },
            new RecordingView());
        await controller.SendAsync("add something");

        Assert.Equal(0, dispatcher.DispatchCount);
        Assert.Equal(0, reviews);
        Assert.True(controller.History.OfType<AIInteractionToolResult>().Single(r => r.Id == "toolu_add").Result["rejected"]?.Value<bool>());
    }

    [Fact]
    public async Task Composition_UndeclaredArgument_IsRejected()
    {
        var provider = new StepProvider(
            Step.Tool("toolu_1", "gh_get_all", new JObject { ["inject"] = "x" }),
            Step.Tool("toolu_2", AskUserTool.ToolName, new JObject { ["questions"] = new JArray(), ["extra"] = 1 }),
            Step.Text("ok"));
        var dispatcher = new FakeToolDispatcher(new[] { Loop.Read("gh_get_all") }, (_, _) => new JObject { ["status"] = "success" });

        using var controller = PartiComposition.CreateController(ProductOptions(provider) with { ToolsOverride = dispatcher }, new RecordingView());
        await controller.SendAsync("look");

        Assert.Equal(0, dispatcher.DispatchCount);
        Assert.Null(controller.PendingQuestions);
        Assert.Equal(2, controller.History.OfType<AIInteractionToolResult>().Count(r =>
            r.Result["message"]!.ToString().Contains("does not declare the argument")));
    }

    [Fact]
    public async Task Reset_ClearsDecompositionKeepsLocks()
    {
        var locked = Guid.NewGuid().ToString("D");
        var provider = new StepProvider(
            Step.Tool("toolu_rec", RecordDecompositionTool.ToolName, Loop.CompleteDecomposition()),
            Step.Tool("toolu_lock", "gh_component_lock", new JObject { ["action"] = "lock", ["targets"] = new JArray(locked) }),
            Step.Text("Locked."));

        using var controller = PartiComposition.CreateController(ProductOptions(provider, catalog: SliderCatalog()), new RecordingView());
        await controller.SendAsync("lock that and remember the brief");

        Assert.True(controller.CurrentDecomposition.HasAbstraction);
        Assert.Equal(new[] { locked }, controller.LockedComponents);

        await controller.ResetAsync();

        Assert.Same(Decomposition.Empty, controller.CurrentDecomposition);
        Assert.Empty(controller.History);
        Assert.Equal(new[] { locked }, controller.LockedComponents);

        controller.Unlock(Guid.Parse(locked));
        Assert.Empty(controller.LockedComponents);
    }

    [Fact]
    public async Task Composition_LockToolPolicyAndController_ShareOneRegistry()
    {
        var target = Guid.NewGuid().ToString("D");
        var provider = new StepProvider(
            Step.Tool("toolu_lock", "gh_component_lock", new JObject { ["action"] = "lock", ["targets"] = new JArray(target) }),
            Step.Tool("toolu_rec", RecordDecompositionTool.ToolName, Loop.CompleteDecomposition()),
            Step.Tool("toolu_set", "gh_set_input", new JObject { ["target"] = target, ["value"] = 3 }),
            Step.Text("ok"));
        var reviews = 0;

        using var controller = PartiComposition.CreateController(
            ProductOptions(provider, catalog: SliderCatalog(), reviewer: (_, _) => { reviews++; return Task.FromResult(true); }),
            new RecordingView());
        await controller.SendAsync("lock it, then change it");

        Assert.Contains(target, controller.LockedComponents);
        Assert.Equal(0, reviews);
        Assert.True(controller.History.OfType<AIInteractionToolResult>().Single(r => r.Id == "toolu_set").Result["rejected"]?.Value<bool>());
    }

    [Fact]
    public async Task Composition_UsesSkillBundleNotDeprecatedPrompt()
    {
        var provider = new StepProvider(Step.Text("ok"));

        using var controller = PartiComposition.CreateController(
            ProductOptions(provider) with { Skills = new FileSystemSkillSource(PromptsRoot()) },
            new RecordingView());
        await controller.SendAsync("hello");

        var prompt = Assert.Single(provider.Requests).SystemPrompt!;
        Assert.Contains("# parti-foundations", prompt);
        Assert.Contains("## Tool catalogue", prompt);
        Assert.Contains("untrusted data", prompt);
        Assert.NotEqual(PartiComposition.FallbackSystemPrompt, prompt);

        // The plugin no longer embeds the deprecated monolithic prompt, and does embed the bundle.
        var resources = typeof(Parti.Components.PartiInfo).Assembly.GetManifestResourceNames();
        Assert.DoesNotContain("prompts.system_v0.6.md", resources);
        Assert.Contains("Parti.Prompts.parti-foundations/SKILL.md", resources);
    }

    [Fact]
    public async Task Composition_EmbeddedBundle_BuildsTheProductPrompt()
    {
        var provider = new StepProvider(Step.Text("ok"));

        using var controller = PartiComposition.CreateController(
            ProductOptions(provider) with { Skills = new EmbeddedResourceSkillSource(typeof(Parti.Components.PartiInfo).Assembly) },
            new RecordingView());
        await controller.SendAsync("add a slider");

        var prompt = Assert.Single(provider.Requests).SystemPrompt!;
        Assert.Contains("# parti-foundations", prompt);
        Assert.Contains("# parti-ghcanvas", prompt);
    }

    [Fact]
    public async Task Composition_UnreadableBundle_FallsBackToSafePrompt()
    {
        var provider = new StepProvider(Step.Text("ok"));

        using var controller = PartiComposition.CreateController(
            ProductOptions(provider) with { Skills = new InMemorySkillSource(new Dictionary<string, string>()) },
            new RecordingView());
        await controller.SendAsync("hello");

        Assert.Equal(PartiComposition.FallbackSystemPrompt, Assert.Single(provider.Requests).SystemPrompt);
    }

    [Fact]
    public async Task KeywordRouter_ActivatesGhCanvasForAddRequest()
    {
        var provider = new StepProvider(Step.Text("ok"));

        using var controller = PartiComposition.CreateController(
            ProductOptions(provider) with { Skills = new FileSystemSkillSource(PromptsRoot()) },
            new RecordingView());
        await controller.SendAsync("add a slider and wire it to a panel");

        Assert.Contains("# parti-ghcanvas", Assert.Single(provider.Requests).SystemPrompt);
    }

    // ----- helpers -----------------------------------------------------------------------------

    private static PartiCompositionOptions ProductOptions(
        StepProvider provider,
        IComponentCatalog? catalog = null,
        PlanReviewer? reviewer = null) => new(
            Provider: provider,
            UndoFactory: new NoOpUndoScopeFactory(),
            Skills: null,
            Catalog: catalog ?? EmptyComponentCatalog.Instance,
            Reviewer: reviewer,
            Model: "test-model",
            MaxTokens: PartiComposition.DefaultMaxTokens);

    /// <summary>The real product schema, so the composition's strict argument check applies.</summary>
    private static AIToolDefinition ProductDefinition(string name) => Parti.Core.Grasshopper.Tools.ProductToolSet
        .Definitions(new Parti.Core.Grasshopper.Tools.ProductToolContext(EmptyComponentCatalog.Instance, new ComponentLockRegistry()))
        .Single(d => d.Name == name);

    private static IComponentCatalog SliderCatalog() => new SingleEntryCatalog(new ComponentCatalogEntry(
        Guid.Parse(Loop.SliderGuid), "Number Slider", "Slider", "Params", "Input", "Grasshopper", false, "primary", "Numeric slider"));

    private static string PromptsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var prompts = Path.Combine(dir.FullName, "prompts");
            if (Directory.Exists(Path.Combine(prompts, "parti-foundations")))
            {
                return prompts;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("prompts/parti-foundations not found above " + AppContext.BaseDirectory);
    }

    private sealed class SingleEntryCatalog : IComponentCatalog
    {
        private readonly ComponentCatalogEntry _entry;

        public SingleEntryCatalog(ComponentCatalogEntry entry) => _entry = entry;

        public IReadOnlyList<ComponentCatalogEntry> All => new[] { _entry };

        public bool TryGet(Guid componentGuid, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ComponentCatalogEntry entry)
        {
            entry = componentGuid == _entry.ComponentGuid ? _entry : null;
            return entry is not null;
        }

        public IReadOnlyList<LibraryEntry> Libraries() => new[] { new LibraryEntry("Grasshopper", "8.35", "McNeel", true) };
    }
}
