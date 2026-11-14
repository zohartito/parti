// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Components.Chat;
using Parti.Core.Ai;
using Parti.Core.Design;
using Parti.Core.Sessions;
using Parti.Providers.Anthropic;
using Parti.Providers.Ollama;
using Parti.Providers.OpenAI;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// W5: the non-UI logic behind ChatForm — provider settings validation, Ollama model
/// preselection, provider construction with the per-request timeout, plan and decomposition text,
/// and the ask_user answer rules.
/// </summary>
public class ChatFormLogicTests
{
    // --- ProviderSettings -------------------------------------------------

    [Fact]
    public void DefaultProvider_IsClaudeOnTheSubscription_FallingBackToOllamaWithoutTheCli()
    {
        Assert.Equal(ProviderSelection.SubscriptionClaude, ProviderSettings.DefaultProvider);

        var found = ProviderSettings.DefaultSubscription(name => name == "claude" ? "/opt/homebrew/bin/claude" : null);
        Assert.Equal(new ProviderConfiguration(ProviderSelection.SubscriptionClaude, "claude-opus-5-5", null, null, "medium"), found);

        Assert.Null(ProviderSettings.DefaultSubscription(_ => null)); // no claude CLI: the window looks for Ollama
    }

    [Theory]
    [InlineData(new[] { "gemma4:12b-it-qat", "gemma4:31b-65k", "qwen3.6:27b" }, "qwen3.6:27b")]
    [InlineData(new[] { "gpt-oss:20b", "gemma4:31b-65k" }, "gemma4:31b-65k")]
    [InlineData(new[] { "gemma4:12b-it-qat", "gpt-oss:20b" }, "gemma4:12b-it-qat")]
    public void PreselectModel_PrefersQwenThenGemmaThenFirstTag(string[] installed, string expected)
    {
        Assert.Equal(expected, ProviderSettings.PreselectModel(installed));
    }

    [Fact]
    public void PreselectModel_NoModels_ReturnsNull()
    {
        Assert.Null(ProviderSettings.PreselectModel(Array.Empty<string>()));
        Assert.Null(ProviderSettings.PreselectModel(null));
    }

    [Fact]
    public void PreselectModel_MatchesExactTagsOnly()
    {
        // "qwen3.6:27b-q8" is a different tag; the list order decides.
        Assert.Equal("llama3.1", ProviderSettings.PreselectModel(new[] { "llama3.1", "qwen3.6:27b-q8" }));
    }

    [Fact]
    public void TryValidate_OllamaBlankEndpoint_UsesLocalDefault()
    {
        Assert.True(ProviderSettings.TryValidate(ProviderSelection.Ollama, " qwen3.6:27b ", "  ", null, out var config, out var error), error);
        Assert.Equal(OllamaProviderOptions.DefaultBaseUrl, config!.Endpoint);
        Assert.Equal("qwen3.6:27b", config.Model);
        Assert.Null(config.ApiKeyOverride);
    }

    [Fact]
    public void TryValidate_OllamaIgnoresTypedKey()
    {
        Assert.True(ProviderSettings.TryValidate(ProviderSelection.Ollama, "m", "http://ollama-host:11434", "sk-typed", out var config, out _));
        Assert.Null(config!.ApiKeyOverride);
        Assert.Equal(new Uri("http://ollama-host:11434"), config.Endpoint);
    }

    [Theory]
    [InlineData("ftp://ollama-host:11434")]
    [InlineData("http://user:pw@ollama-host:11434")]
    [InlineData("http://ollama-host:11434?x=1")]
    [InlineData("http://ollama-host:11434#frag")]
    [InlineData("ollama-host:11434/api")]
    [InlineData("not a url")]
    public void TryValidate_RejectsEndpointsTheProvidersReject(string endpoint)
    {
        Assert.False(ProviderSettings.TryValidate(ProviderSelection.Ollama, "m", endpoint, null, out var config, out var error));
        Assert.Null(config);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryValidate_CustomUrlRequiresEndpoint()
    {
        Assert.False(ProviderSettings.TryValidate(ProviderSelection.OpenAICompatible, "gpt-oss:20b", "", null, out _, out var error));
        Assert.Contains("server URL", error);
    }

    [Fact]
    public void TryValidate_RequiresModel()
    {
        Assert.False(ProviderSettings.TryValidate(ProviderSelection.Anthropic, "  ", null, null, out _, out var error));
        Assert.Contains("model", error);
    }

    [Fact]
    public void TryValidate_RefusesKeyOverPlainHttpToRemoteHost()
    {
        Assert.False(ProviderSettings.TryValidate(ProviderSelection.OpenAICompatible, "m", "http://ollama-host:11434/v1", "sk-x", out _, out var error));
        Assert.Contains("plain http", error);

        Assert.True(ProviderSettings.TryValidate(ProviderSelection.OpenAICompatible, "m", "http://localhost:11434/v1", "sk-x", out var local, out _));
        Assert.Equal("sk-x", local!.ApiKeyOverride);
        Assert.True(ProviderSettings.TryValidate(ProviderSelection.OpenAICompatible, "m", "https://llm.example.com/v1", "sk-x", out _, out _));
        Assert.True(ProviderSettings.TryValidate(ProviderSelection.OpenAICompatible, "m", "http://ollama-host:11434/v1", null, out _, out _));
    }

    [Fact]
    public void TryValidate_HostedProvidersIgnoreEndpointBox()
    {
        Assert.True(ProviderSettings.TryValidate(ProviderSelection.OpenRouter, "openai/gpt-5", "http://ignored", null, out var config, out _));
        Assert.Null(config!.Endpoint);
    }

    [Fact]
    public void ResolveApiKey_OverrideThenEnvironment()
    {
        var env = new Dictionary<string, string?>
        {
            ["ANTHROPIC_API_KEY"] = "env-anthropic",
            ["OPENAI_API_KEY"] = "env-openai",
            ["OPENROUTER_API_KEY"] = " ",
        };
        string? Lookup(string name) => env.TryGetValue(name, out var v) ? v : null;

        Assert.Equal("typed", ProviderSettings.ResolveApiKey(ProviderSelection.Anthropic, " typed ", Lookup));
        Assert.Equal("env-anthropic", ProviderSettings.ResolveApiKey(ProviderSelection.Anthropic, null, Lookup));
        Assert.Equal("env-openai", ProviderSettings.ResolveApiKey(ProviderSelection.OpenAI, "", Lookup));
        Assert.Null(ProviderSettings.ResolveApiKey(ProviderSelection.OpenRouter, null, Lookup));
        Assert.Null(ProviderSettings.ResolveApiKey(ProviderSelection.Ollama, null, Lookup));
    }

    [Fact]
    public void KeyRules_MatchProviders()
    {
        Assert.False(ProviderSettings.RequiresApiKey(ProviderSelection.Ollama));
        Assert.False(ProviderSettings.RequiresApiKey(ProviderSelection.OpenAICompatible));
        Assert.True(ProviderSettings.RequiresApiKey(ProviderSelection.Anthropic));
        Assert.True(ProviderSettings.RequiresApiKey(ProviderSelection.OpenAI));
        Assert.True(ProviderSettings.RequiresApiKey(ProviderSelection.OpenRouter));
        Assert.False(ProviderSettings.AcceptsApiKey(ProviderSelection.Ollama));
        Assert.True(ProviderSettings.AcceptsApiKey(ProviderSelection.OpenAICompatible));
    }

    [Fact]
    public void SessionApiKey_OnlyAnthropicSendsTheRequestKey()
    {
        string? Lookup(string name) => name == "ANTHROPIC_API_KEY" ? "env-anthropic" : "env-other";

        Assert.Equal("env-anthropic", ProviderSettings.SessionApiKey(new ProviderConfiguration(ProviderSelection.Anthropic, "m", null, null), Lookup));
        Assert.Equal(string.Empty, ProviderSettings.SessionApiKey(new ProviderConfiguration(ProviderSelection.OpenAI, "m", null, "typed"), Lookup));
        Assert.Equal(string.Empty, ProviderSettings.SessionApiKey(new ProviderConfiguration(ProviderSelection.Ollama, "m", OllamaProviderOptions.DefaultBaseUrl, null), Lookup));
    }

    [Fact]
    public void CreateProvider_Ollama_UsesEndpointAndLongTimeout()
    {
        var endpoint = new Uri("http://ollama-host:11434");
        var handle = ProviderSettings.CreateProvider(new ProviderConfiguration(ProviderSelection.Ollama, "qwen3.6:27b", endpoint, null));

        var wrapper = Assert.IsType<RequestTimeoutProvider>(handle.Provider);
        Assert.Same(handle.Ollama, wrapper.Inner);
        Assert.Equal(endpoint, handle.Provider.DefaultServerUrl);
        Assert.Equal(ProviderSettings.LocalTimeoutSeconds, wrapper.TimeoutSeconds);
        Assert.True(ProviderSettings.LocalTimeoutSeconds > 120);
    }

    [Theory]
    [InlineData(ProviderSelection.OpenAI, "https://api.openai.com/v1", "OpenAI")]
    [InlineData(ProviderSelection.OpenRouter, "https://openrouter.ai/api/v1", "OpenRouter")]
    public void CreateProvider_HostedPresets(ProviderSelection selection, string baseUrl, string name)
    {
        var handle = ProviderSettings.CreateProvider(new ProviderConfiguration(selection, "m", null, null));

        var wrapper = Assert.IsType<RequestTimeoutProvider>(handle.Provider);
        Assert.IsType<OpenAICompatibleProvider>(wrapper.Inner);
        Assert.Null(handle.Ollama);
        Assert.Equal(new Uri(baseUrl), handle.Provider.DefaultServerUrl);
        Assert.Equal(name, handle.Provider.Name);
        Assert.Equal(ProviderSettings.HostedTimeoutSeconds, wrapper.TimeoutSeconds);
    }

    [Fact]
    public void CreateProvider_CustomUrl_UsesMaxTokensFieldAndLongTimeout()
    {
        var endpoint = new Uri("http://ollama-host:11434/v1");
        var handle = ProviderSettings.CreateProvider(new ProviderConfiguration(ProviderSelection.OpenAICompatible, "gpt-oss:20b", endpoint, null));

        var wrapper = Assert.IsType<RequestTimeoutProvider>(handle.Provider);
        var inner = Assert.IsType<OpenAICompatibleProvider>(wrapper.Inner);
        Assert.Equal(endpoint, inner.DefaultServerUrl);
        Assert.Equal(OpenAICompatibleProviderOptions.MaxTokensField, inner.TokenLimitField);
        Assert.Equal(ProviderSettings.LocalTimeoutSeconds, wrapper.TimeoutSeconds);
    }

    [Fact]
    public void CreateProvider_Anthropic()
    {
        var handle = ProviderSettings.CreateProvider(new ProviderConfiguration(ProviderSelection.Anthropic, "m", null, null));
        Assert.IsType<AnthropicProvider>(Assert.IsType<RequestTimeoutProvider>(handle.Provider).Inner);
    }

    // --- RequestTimeoutProvider -------------------------------------------

    [Fact]
    public async Task RequestTimeoutProvider_StampsTimeoutOnCallAndStream()
    {
        var inner = new RecordingProvider();
        var provider = new RequestTimeoutProvider(inner, 600);

        await provider.CallAsync(new AIRequestCall());
        var chunks = new List<AIStreamChunk>();
        await foreach (var chunk in ((IAIProvider)provider).CallStreamAsync(new AIRequestCall()))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(new[] { 600, 600 }, inner.Timeouts);
        Assert.Equal(1, inner.StreamCalls);
        Assert.Equal(new[] { AIStreamChunkKind.TextDelta, AIStreamChunkKind.Done }, chunks.Select(c => c.Kind));
        Assert.Equal("recording", provider.Name);
    }

    [Fact]
    public void RequestTimeoutProvider_RejectsNonPositiveTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequestTimeoutProvider(new RecordingProvider(), 0));
    }

    // --- PlanText -----------------------------------------------------------

    private static Decomposition Brief() => new(
        Inputs: new[]
        {
            new Input("panel depth", "0.1..0.4", "m"),
            new Input("stagger", "0..0.5", null, PillarSource.Assumed),
        },
        Rules: new[] { new Rule("thermal break between panels") },
        Evaluations: new[] { new Evaluation("solar gain", EvalDirection.Minimize, 120) },
        Principle: "modular rainscreen with staggered courses",
        FeedbackPlan: "Ladybug sun hours");

    [Fact]
    public void PlanText_ShowsResolvedLabelBindingAndWhy()
    {
        var plan = new ToolCallPlan(
            new[]
            {
                new ToolCallSummary(
                    "gh_add_component",
                    IsReversible: true,
                    ArgumentPreview: "Number Slider",
                    RenderedArguments: "{\"componentGuid\":\"57da07bd-ecab-415d-9d86-af36d7073abc\"}",
                    ResolvedLabel: "Number Slider [Params/Input · Grasshopper] 57da07bd-ecab-415d-9d86-af36d7073abc"),
                new ToolCallSummary("gh_get_all", IsReversible: false, ArgumentPreview: null),
            },
            AssistantText: "Adding a slider.",
            BindingId: "binding-1",
            Decomposition: Brief(),
            DecompositionRevision: 2);

        var text = PlanText.Build(plan);

        Assert.Contains("1. gh_add_component", text);
        Assert.Contains("   component: Number Slider [Params/Input · Grasshopper] 57da07bd-ecab-415d-9d86-af36d7073abc", text);
        Assert.Contains("2. gh_get_all  [read-only]", text);
        Assert.Contains("   {}", text);
        Assert.Contains("Reviewed binding: binding-1", text);
        Assert.Contains("Why (design brief, revision 2)", text);
        Assert.Contains("Principle: modular rainscreen with staggered courses", text);
        Assert.Contains("- stagger: 0..0.5 [assumed]", text);
        Assert.Contains("- panel depth: 0.1..0.4 m\n", text.Replace("\r\n", "\n"));
        Assert.Contains("- minimize solar gain (target 120)", text);
        Assert.Contains("were assumed by the model", text);
        Assert.True(text.IndexOf("Reviewed binding", StringComparison.Ordinal) < text.IndexOf("Why", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanText_NoDecomposition_SaysSo()
    {
        var plan = new ToolCallPlan(new[] { new ToolCallSummary("gh_delete", true, null) }, null);
        Assert.Contains("Why: " + PlanText.NoDecompositionText, PlanText.Build(plan));
        Assert.Contains("Why: " + PlanText.NoDecompositionText, PlanText.BuildWhy(Decomposition.Empty, 0));
    }

    [Fact]
    public void PlanText_NoAssumedItems_NoAssumptionFooter()
    {
        var stated = Brief() with { Inputs = new[] { new Input("panel depth", "0.1..0.4", "m") } };
        var why = PlanText.BuildWhy(stated, 1);
        Assert.DoesNotContain(PlanText.AssumedMarker, why);
    }

    [Fact]
    public void PlanText_ShowsEvaluationComparator()
    {
        var bounded = Brief() with { Evaluations = new[] { new Evaluation("section area", EvalDirection.Keep, 5, Comparator: EvalComparator.AtMost) } };
        Assert.Contains("- keep section area <= 5", PlanText.BuildWhy(bounded, 1));
    }

    [Fact]
    public void FormatDecomposition_ListsMissingPillars()
    {
        var partial = new Decomposition(
            new[] { new Input("depth", "0..1", null) },
            Array.Empty<Rule>(),
            Array.Empty<Evaluation>(),
            Principle: null,
            FeedbackPlan: null);
        Assert.Contains("Missing: Abstraction, Feedback", PlanText.FormatDecomposition(partial));
    }

    // --- QuestionAnswers ----------------------------------------------------

    [Fact]
    public void EffectiveKind_RadioWithoutOptionsFallsBackToText()
    {
        Assert.Equal(QuestionKind.Text, QuestionAnswers.EffectiveKind(new Question("q", "p", QuestionKind.Radio)));
        Assert.Equal(QuestionKind.Text, QuestionAnswers.EffectiveKind(new Question("q", "p", QuestionKind.Radio, new[] { " " })));
        Assert.Equal(QuestionKind.Radio, QuestionAnswers.EffectiveKind(new Question("q", "p", QuestionKind.Radio, new[] { "a" })));
        Assert.Equal(QuestionKind.ComponentPicker, QuestionAnswers.EffectiveKind(new Question("q", "p", QuestionKind.ComponentPicker)));
    }

    [Fact]
    public void TryBuild_OneAnswerPerQuestionInOrder()
    {
        var questions = new[]
        {
            new Question("depth", "Panel depth?", QuestionKind.Text),
            new Question("shape", "Shape?", QuestionKind.Radio, new[] { "square", "hex" }),
        };
        var raw = new Dictionary<string, string?> { ["shape"] = "hex", ["depth"] = " 0.2 m " };

        Assert.True(QuestionAnswers.TryBuild(questions, raw, out var answers, out var error), error);
        Assert.Equal(new[] { new ClarificationAnswer("depth", "0.2 m"), new ClarificationAnswer("shape", "hex") }, answers);
        Assert.Equal("Panel depth?: 0.2 m\nShape?: hex", QuestionAnswers.Summarize(questions, answers));
    }

    [Fact]
    public void TryBuild_RejectsBlankAndOffListAnswers()
    {
        var text = new[] { new Question("q", "Depth?", QuestionKind.Text) };
        Assert.False(QuestionAnswers.TryBuild(text, new Dictionary<string, string?> { ["q"] = "  " }, out _, out var blank));
        Assert.Contains("Depth?", blank);
        Assert.False(QuestionAnswers.TryBuild(text, new Dictionary<string, string?>(), out _, out _));

        var radio = new[] { new Question("s", "Shape?", QuestionKind.Radio, new[] { "square", "hex" }) };
        Assert.False(QuestionAnswers.TryBuild(radio, new Dictionary<string, string?> { ["s"] = "circle" }, out _, out var offList));
        Assert.Contains("options", offList);
    }

    [Fact]
    public void TryBuild_RadioKeepsOptionExactly()
    {
        var radio = new[] { new Question("s", "Shape?", QuestionKind.Radio, new[] { " square ", "hex" }) };
        Assert.True(QuestionAnswers.TryBuild(radio, new Dictionary<string, string?> { ["s"] = " square " }, out var answers, out var error), error);
        Assert.Equal(" square ", answers[0].Answer);
    }

    [Fact]
    public void TryBuild_RejectsRepeatedQuestionIds()
    {
        var questions = new[]
        {
            new Question("q", "Depth?", QuestionKind.Text),
            new Question("q", "Width?", QuestionKind.Text),
        };
        Assert.False(QuestionAnswers.TryBuild(questions, new Dictionary<string, string?> { ["q"] = "1" }, out _, out var error));
        Assert.Contains("repeated ids", error);
    }

    [Fact]
    public void FormatComponentChoice_LeadsWithInstanceGuid()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("11111111-2222-3333-4444-555555555555 (Number Slider \"depth\")", QuestionAnswers.FormatComponentChoice(id, "Number Slider", "depth"));
        Assert.Equal("11111111-2222-3333-4444-555555555555 (Circle)", QuestionAnswers.FormatComponentChoice(id, "Circle", "Circle"));
        Assert.Equal("11111111-2222-3333-4444-555555555555", QuestionAnswers.FormatComponentChoice(id, null, null));
    }

    // --- Decomposition card units ------------------------------------------

    [Theory]
    [InlineData("1 to 5 m", "m", "1 to 5 m")]
    [InlineData("0-10mm", "mm", "0-10mm")]
    [InlineData("1 to 5", "m", "1 to 5 m")]
    [InlineData("1 to 5 mm", "m", "1 to 5 mm m")]
    [InlineData("2..8 M", "m", "2..8 M")]
    public void FormatDecomposition_DoesNotRepeatAUnitTheDomainAlreadyEndsWith(string domain, string unit, string shown)
    {
        var brief = Brief() with { Inputs = new[] { new Input("radius", domain, unit) } };
        Assert.Contains($"- radius: {shown}\n", PlanText.FormatDecomposition(brief).Replace("\r\n", "\n") + "\n");
    }

    // --- ChatControllerHost (settings apply) --------------------------------

    [Fact]
    public async Task ApplyConfiguration_Unchanged_KeepsLocksAndHistory()
    {
        var builds = 0;
        var host = new ChatControllerHost((configuration, locks) =>
        {
            builds++;
            return BuildTestController(configuration, locks);
        });
        var ollama = new ProviderConfiguration(ProviderSelection.Ollama, "qwen3.6:27b", new Uri("http://localhost:11434"), null);

        Assert.Equal(ApplyOutcome.Created, host.Apply(ollama));
        var controller = host.Controller!;
        Assert.True(await controller.SendAsync("hello"));
        var historyCount = controller.History.Count;
        var locked = Guid.NewGuid().ToString("D");
        Assert.True(host.Locks.Lock(locked));

        // Saving Settings without a change: same controller, nothing lost.
        Assert.Equal(ApplyOutcome.Unchanged, host.Apply(ollama with { }));

        Assert.Equal(1, builds);
        Assert.Same(controller, host.Controller);
        Assert.Equal(historyCount, controller.History.Count);
        Assert.True(historyCount > 0);
        Assert.Equal(new[] { locked }, controller.LockedComponents);
    }

    [Fact]
    public async Task ProviderSwitch_KeepsLocks()
    {
        var locked = Guid.NewGuid().ToString("D");
        // The replacement model's first reply deletes the locked component.
        var host = new ChatControllerHost((configuration, locks) =>
            BuildTestController(configuration, locks, configuration.Model == "gpt-oss:20b" ? locked : null));
        var ollama = new ProviderConfiguration(ProviderSelection.Ollama, "qwen3.6:27b", new Uri("http://localhost:11434"), null);
        Assert.Equal(ApplyOutcome.Created, host.Apply(ollama));
        var first = host.Controller!;
        Assert.True(await first.SendAsync("hello"));
        Assert.True(host.Locks.Lock(locked));

        Assert.Equal(ApplyOutcome.Replaced, host.Apply(ollama with { Model = "gpt-oss:20b" }));

        var second = host.Controller!;
        Assert.NotSame(first, second);
        Assert.Equal("gpt-oss:20b", host.Configuration!.Model);
        // A new conversation, the same locks: the lock policy of the new controller still blocks them.
        Assert.Empty(second.History);
        Assert.Equal(new[] { locked }, second.LockedComponents);

        // Enforced, not just listed: a delete of the locked component is rejected before dispatch.
        Assert.True(await second.SendAsync("delete it"));
        var rejection = second.History.OfType<AIInteractionToolResult>().Single(r => r.Name == "gh_delete");
        Assert.Contains("is locked by the user", rejection.Result.ToString());
    }


    [Fact]
    public void ApplyConfiguration_BuildFailure_KeepsThePreviousController()
    {
        var fail = false;
        var host = new ChatControllerHost((configuration, locks) =>
            fail ? throw new InvalidOperationException("no such model") : BuildTestController(configuration, locks));
        var ollama = new ProviderConfiguration(ProviderSelection.Ollama, "qwen3.6:27b", new Uri("http://localhost:11434"), null);
        Assert.Equal(ApplyOutcome.Created, host.Apply(ollama));
        var controller = host.Controller;

        fail = true;
        Assert.Equal(ApplyOutcome.Failed, host.Apply(ollama with { Model = "missing" }));

        Assert.Equal("no such model", host.LastError);
        Assert.Same(controller, host.Controller);
        Assert.Equal(ollama, host.Configuration);
    }

    private static ChatControllerBuild BuildTestController(ProviderConfiguration configuration, Parti.Core.Sessions.ComponentLockRegistry locks) =>
        BuildTestController(configuration, locks, deleteTarget: null);

    // With a delete target, the provider's first reply is a gh_delete of it; every other reply is text.
    private static ChatControllerBuild BuildTestController(
        ProviderConfiguration configuration,
        Parti.Core.Sessions.ComponentLockRegistry locks,
        string? deleteTarget)
    {
        var calls = 0;
        var provider = new FakeAIProvider(_ => calls++ == 0 && deleteTarget is not null
            ? new AIReturn
            {
                Status = AICallStatus.CallingTools,
                StopReason = "tool_use",
                Interactions = new List<IAIInteraction>
                {
                    new AIInteractionToolCall
                    {
                        Id = "toolu_delete",
                        Name = "gh_delete",
                        Agent = AIAgent.ToolCall,
                        Arguments = new JObject { ["target"] = deleteTarget },
                    },
                },
            }
            : new AIReturn
            {
                Status = AICallStatus.Finished,
                StopReason = "end_turn",
                Interactions = new List<IAIInteraction> { new AIInteractionText { Agent = AIAgent.Assistant, Content = "ok" } },
            });
        var controller = Parti.Core.Grasshopper.Chat.PartiComposition.CreateController(
            new Parti.Core.Grasshopper.Chat.PartiCompositionOptions(
                Provider: provider,
                UndoFactory: new Parti.Core.Grasshopper.NoOpUndoScopeFactory(),
                Skills: null,
                Catalog: Parti.Core.Tools.EmptyComponentCatalog.Instance,
                Reviewer: null,
                Model: configuration.Model,
                MaxTokens: ProviderSettings.DefaultMaxTokens,
                Locks: locks),
            new SilentView());
        return new ChatControllerBuild(controller, new ProviderHandle(provider, null));
    }

    // --- ReasoningPaneBuffer ---------------------------------------------------

    [Fact]
    public void ReasoningPaneBuffer_SchedulesOneFlushPerBatch()
    {
        var buffer = new ReasoningPaneBuffer();

        Assert.True(buffer.Append("a"));
        Assert.False(buffer.Append("b"));
        Assert.False(buffer.Append("c"));
        Assert.Equal("abc", buffer.Drain(out _));
        Assert.Equal(string.Empty, buffer.Drain(out _));
        Assert.True(buffer.Append("d"));
        Assert.False(buffer.Append(string.Empty));
    }

    [Fact]
    public void ReasoningPaneBuffer_ClearDropsQueuedText()
    {
        var buffer = new ReasoningPaneBuffer();
        buffer.Append("old turn");
        buffer.Drain(out var before);
        buffer.Append("old tail");
        var generation = buffer.Clear();
        Assert.Equal(before + 1, generation);

        // A flush scheduled before the clear drains only the new generation's text, tagged as such,
        // so the pane clears itself before showing it.
        Assert.False(buffer.Append("new"));
        Assert.Equal("new", buffer.Drain(out var drained));
        Assert.Equal(generation, drained);
    }

    [Fact]
    public void ReasoningPaneBuffer_KeepsOnlyTheTail()
    {
        var buffer = new ReasoningPaneBuffer();
        buffer.Append(new string('x', ReasoningPaneBuffer.MaxCharacters));
        buffer.Append("END");
        var drained = buffer.Drain(out _);
        Assert.Equal(ReasoningPaneBuffer.MaxCharacters, drained.Length);
        Assert.EndsWith("END", drained);

        var combined = ReasoningPaneBuffer.Combine(new string('y', ReasoningPaneBuffer.MaxCharacters - 1), "tail");
        Assert.Equal(ReasoningPaneBuffer.MaxCharacters, combined.Length);
        Assert.EndsWith("tail", combined);
        Assert.Equal("ab", ReasoningPaneBuffer.Combine("a", "b"));
    }

    [Fact]
    public void ProductMaxTokens_IsOneConstant()
    {
        Assert.Equal(Parti.Core.Grasshopper.Chat.PartiComposition.DefaultMaxTokens, ProviderSettings.DefaultMaxTokens);
        Assert.Equal(8192, ProviderSettings.DefaultMaxTokens);
    }

    private sealed class SilentView : Parti.Core.Grasshopper.Chat.IChatView
    {
        public void AppendUserMessage(string text) { }

        public void AppendAssistantText(string delta) { }

        public void SetBusy(bool busy) { }

        public void ShowError(string message) { }

        public void ClearHistory() { }
    }

    private sealed class RecordingProvider : IAIProvider
    {
        public List<int> Timeouts { get; } = new();

        public int StreamCalls { get; private set; }

        public string Name => "recording";

        public Uri DefaultServerUrl { get; } = new("http://localhost:11434");

        public AIRequestCall PreCall(AIRequestCall request) => request;

        public string CreateRequestBody(AIRequestCall request) => "{}";

        public List<IAIInteraction> Decode(JObject response) => new();

        public string Encode(IAIInteraction interaction) => string.Empty;

        public Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken cancellationToken = default)
        {
            Timeouts.Add(request.TimeoutSeconds);
            return Task.FromResult(new AIReturn { Status = AICallStatus.Finished, StopReason = "end_turn" });
        }

        public async IAsyncEnumerable<AIStreamChunk> CallStreamAsync(
            AIRequestCall request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            Timeouts.Add(request.TimeoutSeconds);
            await Task.Yield();
            yield return new AIStreamChunk(AIStreamChunkKind.TextDelta, Text: "hi");
            yield return new AIStreamChunk(AIStreamChunkKind.Done, Final: new AIReturn { Status = AICallStatus.Finished });
        }
    }
}
