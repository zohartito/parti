// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Grasshopper;
using Parti.Core.Grasshopper.Chat;
using Parti.Core.Sessions;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// Hermetic tests for <see cref="ChatController"/>. No Eto, no Rhino, no network —
/// everything runs against a <see cref="FakeChatView"/> and a streaming fake provider.
/// </summary>
public class ChatControllerTests
{
    // ----- fixtures -------------------------------------------------------

    private static SessionOptions MakeOptions() => new()
    {
        Model = "test-model",
        ApiKey = "test-key",
        MaxTurns = 5,
        MaxToolPasses = 4,
        Temperature = 0.1,
        MaxTokens = 256,
    };

    // ----- tests ----------------------------------------------------------

    [Fact]
    public async Task SendMessage_StreamsTextDeltas()
    {
        var view = new FakeChatView();
        var provider = new ScriptedStreamingProvider(new[]
        {
            TurnScript.Text("Hello", ", ", "world!"),
        });
        var dispatcher = new FakeToolDispatcher(
            Array.Empty<AIToolDefinition>(),
            (_, _) => new JObject());

        using var controller = new ChatController(
            provider,
            dispatcher,
            new NoOpUndoScopeFactory(),
            MakeOptions(),
            view);

        var ok = await controller.SendAsync("hi");

        Assert.True(ok);
        Assert.Equal("hi", Assert.Single(view.UserMessages));
        Assert.Equal(new[] { "Hello", ", ", "world!" }, view.AssistantDeltas);
        Assert.Equal("Hello, world!", view.AssistantText);
        Assert.False(view.BusyStates[^1]); // ends idle
        Assert.Contains(true, view.BusyStates); // was busy at some point
        Assert.Empty(view.Errors);
        Assert.False(controller.IsBusy);
    }

    [Fact]
    public async Task Cancel_InterruptsStream()
    {
        var view = new FakeChatView();

        // Provider that pauses between text deltas so we can cancel mid-stream.
        var provider = new ScriptedStreamingProvider(new[]
        {
            TurnScript.Text("partial-")
                .WithDelayBetweenDeltas(TimeSpan.FromMilliseconds(200))
                .ThenText("never-arrives"),
        });
        var dispatcher = new FakeToolDispatcher(
            Array.Empty<AIToolDefinition>(),
            (_, _) => new JObject());

        using var controller = new ChatController(
            provider,
            dispatcher,
            new NoOpUndoScopeFactory(),
            MakeOptions(),
            view);

        view.OnAppendAssistantText = delta =>
        {
            if (delta == "partial-")
            {
                controller.Cancel();
            }
        };

        var ok = await controller.SendAsync("hi");

        Assert.True(ok);
        // Partial text stayed visible.
        Assert.Contains("partial-", view.AssistantDeltas);
        Assert.DoesNotContain("never-arrives", view.AssistantDeltas);
        // No ghost error on cancel (cancel is a deliberate user action).
        Assert.Empty(view.Errors);
        Assert.False(controller.IsBusy);
    }

    [Fact]
    public async Task Reset_ClearsHistoryAndUi()
    {
        var view = new FakeChatView();
        var provider = new ScriptedStreamingProvider(new[]
        {
            TurnScript.Text("first reply"),
        });
        var dispatcher = new FakeToolDispatcher(
            Array.Empty<AIToolDefinition>(),
            (_, _) => new JObject());

        using var controller = new ChatController(
            provider,
            dispatcher,
            new NoOpUndoScopeFactory(),
            MakeOptions(),
            view);

        await controller.SendAsync("hi");
        Assert.True(controller.History.Count >= 2);

        await controller.ResetAsync();

        Assert.Empty(controller.History);
        Assert.True(view.HistoryCleared);
    }

    [Fact]
    public async Task ToolCall_WrapsInUndoScope()
    {
        var view = new FakeChatView();

        var toolDef = new AIToolDefinition(
            "gh_get_all",
            "Snapshot canvas",
            new JObject { ["type"] = "object", ["properties"] = new JObject() });

        var provider = new ScriptedStreamingProvider(new[]
        {
            TurnScript.ToolCall("toolu_1", "gh_get_all", new JObject()),
            TurnScript.Text("done"),
        });

        var dispatcher = new FakeToolDispatcher(
            new[] { toolDef },
            (_, _) => new JObject { ["status"] = "success" });

        var undoFactory = new RecordingUndoScopeFactory();

        using var controller = new ChatController(
            provider,
            dispatcher,
            undoFactory,
            MakeOptions(),
            view);

        var ok = await controller.SendAsync("what's on the canvas?");

        Assert.True(ok);
        Assert.Equal(1, undoFactory.BeginCount);
        // Snapshot-only calls do not dirty the undo scope.
        Assert.Equal(0, undoFactory.LastScope!.RecordMutationCount);
        Assert.True(undoFactory.LastScope.Disposed);
        Assert.Equal(1, dispatcher.DispatchCount);
        Assert.Empty(view.Errors);
    }

    [Fact]
    public async Task DocumentLeaseIsInjectedOnlyIntoDispatch_NotProviderHistory()
    {
        var provider = new ScriptedStreamingProvider(new[]
        {
            TurnScript.ToolCall("toolu_1", "gh_get_all", new JObject()),
            TurnScript.Text("done"),
        });
        var dispatcher = new FakeToolDispatcher(
            new[] { new AIToolDefinition("gh_get_all", "Snapshot", new JObject { ["type"] = "object" }) },
            (_, _) => new JObject { ["status"] = "success" });

        using var controller = new ChatController(
            provider,
            dispatcher,
            new LeaseUndoScopeFactory("test-lease-token"),
            MakeOptions(),
            new FakeChatView());

        Assert.True(await controller.SendAsync("snapshot"));
        Assert.Equal("test-lease-token", dispatcher.Calls.Single().Arguments["__parti_document_lease"]?.ToString());

        var followUpRequest = provider.ReceivedRequests[1];
        Assert.All(followUpRequest.Interactions.OfType<AIInteractionToolCall>(), call =>
            Assert.Null(call.Arguments["__parti_document_lease"]));
    }

    [Fact]
    public async Task Error_ShowsErrorMessage()
    {
        var view = new FakeChatView();

        var provider = new ScriptedStreamingProvider(new[]
        {
            TurnScript.Error("provider exploded"),
        });
        var dispatcher = new FakeToolDispatcher(
            Array.Empty<AIToolDefinition>(),
            (_, _) => new JObject());

        using var controller = new ChatController(
            provider,
            dispatcher,
            new NoOpUndoScopeFactory(),
            MakeOptions(),
            view);

        var ok = await controller.SendAsync("hi");

        Assert.True(ok);
        var err = Assert.Single(view.Errors);
        Assert.Contains("exploded", err);
        // History still contains the user turn — the form doesn't collapse on error.
        Assert.Contains(controller.History, h => h is AIInteractionText t && t.Agent == AIAgent.User);
        Assert.False(view.BusyStates[^1]);
    }

    [Fact]
    public async Task EmptyReplyTwice_ShowsOneHelpfulError_AndStatusIsError()
    {
        // F2: a reply with no text and no tool call must never be a silent success.
        var view = new FakeChatView();
        var provider = new ScriptedStreamingProvider(new[] { TurnScript.Text() });
        var dispatcher = new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject());

        using var controller = new ChatController(provider, dispatcher, new NoOpUndoScopeFactory(), MakeOptions(), view);

        await controller.SendAsync("build the column");

        var err = Assert.Single(view.Errors);
        Assert.Equal(ConversationSession.EmptyResponseErrorText, err);
        Assert.Equal(SessionStatus.Error, controller.Status);
        Assert.Equal(2, provider.ReceivedRequests.Count);
        Assert.Contains(controller.History, h => h is AIInteractionText t && t.Content == ConversationSession.EmptyResponseNudgeText);
    }

    [Fact]
    public async Task EmptyReplyThenText_RecoversWithoutError()
    {
        var view = new FakeChatView();
        var provider = new ScriptedStreamingProvider(new[] { TurnScript.Text(), TurnScript.Text("Built it.") });
        var dispatcher = new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject());

        using var controller = new ChatController(provider, dispatcher, new NoOpUndoScopeFactory(), MakeOptions(), view);

        await controller.SendAsync("build the column");

        Assert.Empty(view.Errors);
        Assert.Equal(SessionStatus.Idle, controller.Status);
    }

    [Fact]
    public async Task SendAsync_RejectsConcurrentSends()
    {
        var view = new FakeChatView();

        var provider = new ScriptedStreamingProvider(new[]
        {
            TurnScript.Text("slow")
                .WithDelayBetweenDeltas(TimeSpan.FromMilliseconds(150))
                .ThenText("response"),
        });
        var dispatcher = new FakeToolDispatcher(
            Array.Empty<AIToolDefinition>(),
            (_, _) => new JObject());

        using var controller = new ChatController(
            provider,
            dispatcher,
            new NoOpUndoScopeFactory(),
            MakeOptions(),
            view);

        var first = controller.SendAsync("first");
        await Task.Delay(30); // let the first call grab _busy
        var second = await controller.SendAsync("second");

        Assert.False(second); // rejected
        Assert.True(await first);
        Assert.Equal("first", Assert.Single(view.UserMessages)); // second never hit the view
    }

    [Fact]
    public async Task PartialMutationResults_KeepUndoScopeForEveryBatchTool()
    {
        var stable = Guid.NewGuid().ToString();
        var cases = new (string name, JObject args)[]
        {
            ("gh_group", new JObject { ["targets"] = new JArray(stable) }),
            ("gh_layout", new JObject { ["targets"] = new JArray(stable), ["arrangement"] = "row", ["origin"] = new JObject { ["x"] = 0, ["y"] = 0 }, ["spacing"] = new JObject { ["x"] = 10, ["y"] = 10 } }),
            ("gh_relayout", new JObject { ["root"] = stable, ["spacing"] = new JObject { ["x"] = 10, ["y"] = 10 } }),
        };

        foreach (var (name, args) in cases)
        {
            var view = new FakeChatView();
            var provider = new ScriptedStreamingProvider(new[]
            {
                TurnScript.ToolCall("toolu_" + name, name, args),
                TurnScript.Text("done"),
            });
            var dispatcher = new FakeToolDispatcher(
                new[] { new AIToolDefinition(name, name, new JObject { ["type"] = "object" }) { IsReversible = true } },
                (_, _) => new JObject { ["status"] = "partial" });
            var undoFactory = new RecordingUndoScopeFactory();

            using var controller = new ChatController(provider, dispatcher, undoFactory, MakeOptions(), view)
            {
                PlanReviewer = (_, _) => Task.FromResult(true),
            };

            Assert.True(await controller.SendAsync("mutate"));
            Assert.Equal(1, undoFactory.LastScope!.RecordMutationCount);
            Assert.False(undoFactory.LastScope.Abandoned);
        }
    }

    [Fact]
    public async Task CancellationAfterMutationAttempt_KeepsUndoScope()
    {
        var view = new FakeChatView();
        var provider = new ScriptedStreamingProvider(new[]
        {
            TurnScript.ToolCall("toolu_1", "gh_add_component", new JObject { ["componentGuid"] = "11111111-1111-1111-1111-111111111111" }),
        });
        ChatController? controller = null;
        var dispatcher = new FakeToolDispatcher(
            new[] { new AIToolDefinition("gh_add_component", "Add", new JObject { ["type"] = "object" }) { IsReversible = true } },
            (_, _) =>
            {
                controller!.Cancel(); // simulate cancellation racing a host mutation completion
                return new JObject { ["status"] = "success" };
            });
        var undoFactory = new RecordingUndoScopeFactory();
        using var actualController = new ChatController(provider, dispatcher, undoFactory, MakeOptions(), view)
        {
            PlanReviewer = (_, _) => Task.FromResult(true),
        };
        controller = actualController;
        Assert.True(await actualController.SendAsync("add"));

        Assert.Equal(1, undoFactory.LastScope!.RecordMutationCount);
        Assert.False(undoFactory.LastScope.Abandoned);
    }

    [Fact]
    public async Task ToolResults_AppearAsOneCompactLineEach_ThenTheReplyStartsANewAssistantBlock()
    {
        var view = new FakeChatView();
        var provider = new ScriptedStreamingProvider(new[]
        {
            TurnScript.ToolCall("toolu_1", "gh_find_component", new JObject { ["query"] = "Number Slider" }),
            TurnScript.Text("Found ", "it."),
        });
        var dispatcher = new FakeToolDispatcher(
            new[] { new AIToolDefinition("gh_find_component", "Find", new JObject { ["type"] = "object" }) },
            (_, _) => new JObject { ["status"] = "ok", ["count"] = 1 });

        using var controller = new ChatController(provider, dispatcher, new NoOpUndoScopeFactory(), MakeOptions(), view);

        Assert.True(await controller.SendAsync("find a slider"));

        Assert.Equal(
            new[]
            {
                "user: find a slider",
                "tool: \u00b7 gh_find_component \"Number Slider\" \u2192 1 match",
                "turn",
                "text: Found ",
                "text: it.",
            },
            view.Events);
    }

    [Fact]
    public async Task ToolLine_NeverShowsTheDocumentLease_EvenWhenAHandlerEchoesIt()
    {
        var view = new FakeChatView();
        var provider = new ScriptedStreamingProvider(new[]
        {
            TurnScript.ToolCall("toolu_1", "gh_delete", new JObject { ["target"] = "11111111-1111-1111-1111-111111111111" }),
            TurnScript.Text("done"),
        });
        var dispatcher = new FakeToolDispatcher(
            new[] { new AIToolDefinition("gh_delete", "Delete", new JObject { ["type"] = "object" }) { IsReversible = true } },
            (_, args) => new JObject
            {
                ["status"] = "error",
                ["message"] = "lease " + args[GrasshopperDocumentCapability.LeaseArgument] + " rejected",
            });
        var factory = new GrasshopperUndoScopeFactory(documentResolver: () => null, emitter: new NullEmitter());

        using var controller = new ChatController(provider, dispatcher, factory, MakeOptions(), view)
        {
            PlanReviewer = (_, _) => Task.FromResult(true),
        };

        Assert.True(await controller.SendAsync("delete it"));

        var token = (string?)Assert.Single(dispatcher.Calls).Arguments[GrasshopperDocumentCapability.LeaseArgument];
        Assert.False(string.IsNullOrEmpty(token));
        var line = Assert.Single(view.ToolLines);
        Assert.StartsWith("\u00b7 gh_delete 11111111-1111-1111-1111-111111111111 \u2192 error: lease ", line);
        Assert.DoesNotContain(token!, line);
        Assert.DoesNotContain(GrasshopperDocumentCapability.LeaseArgument, line);
        Assert.True(line.Length <= TranscriptLines.MaxLineLength);
    }

    [Fact]
    public async Task TextReplyCutOffAtTheTokenLimit_IsMarkedInTheView()
    {
        var view = new FakeChatView();
        var provider = new ScriptedStreamingProvider(new[] { TurnScript.Text("A long answer that sto").WithStopReason("max_tokens") });
        var dispatcher = new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject());

        using var controller = new ChatController(provider, dispatcher, new NoOpUndoScopeFactory(), MakeOptions(), view);

        Assert.True(await controller.SendAsync("explain"));

        Assert.Equal(new[] { TranscriptLines.TokenLimitNotice }, view.Notices);
        Assert.Empty(view.Errors);
    }

    [Fact]
    public async Task NormalTextReply_HasNoCutOffNotice()
    {
        var view = new FakeChatView();
        var provider = new ScriptedStreamingProvider(new[] { TurnScript.Text("Complete.") });
        var dispatcher = new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject());

        using var controller = new ChatController(provider, dispatcher, new NoOpUndoScopeFactory(), MakeOptions(), view);

        Assert.True(await controller.SendAsync("explain"));

        Assert.Empty(view.Notices);
    }

    [Fact]
    public async Task CanvasChangedOutsideParti_NextUserMessageCarriesTheNote_Once()
    {
        var view = new FakeChatView();
        var provider = new ScriptedStreamingProvider(new[] { TurnScript.Text("ok") });
        var dispatcher = new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject());
        var probe = new FakeCanvasProbe { State = "doc|undo:1" };

        using var controller = new ChatController(
            provider, dispatcher, new NoOpUndoScopeFactory(), MakeOptions(), view,
            new ChatControllerWiring(CanvasProbe: probe));

        Assert.True(await controller.SendAsync("first"));
        probe.State = "doc|undo:0|redo:1"; // the user pressed Undo in Grasshopper
        Assert.True(await controller.SendAsync("second"));
        Assert.True(await controller.SendAsync("third"));

        var prompts = provider.ReceivedRequests.Select(LastUserText).ToList();
        Assert.Equal("first", prompts[0]);
        Assert.Equal("second\n\n" + TranscriptLines.CanvasChangedNote, prompts[1]);
        Assert.Equal("third", prompts[2]);
        Assert.Equal(new[] { "first", "second", "third" }, view.UserMessages);
        Assert.Equal(new[] { TranscriptLines.CanvasChangedNotice }, view.Notices);
    }

    [Fact]
    public async Task UnchangedCanvas_OrNoEarlierLoop_AddsNoNote()
    {
        var view = new FakeChatView();
        var provider = new ScriptedStreamingProvider(new[] { TurnScript.Text("ok") });
        var dispatcher = new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject());
        var probe = new FakeCanvasProbe { State = "doc|undo:3" };

        using var controller = new ChatController(
            provider, dispatcher, new NoOpUndoScopeFactory(), MakeOptions(), view,
            new ChatControllerWiring(CanvasProbe: probe));

        Assert.True(await controller.SendAsync("first"));
        Assert.True(await controller.SendAsync("second"));
        await controller.ResetAsync();
        probe.State = "other doc";
        Assert.True(await controller.SendAsync("after reset"));

        Assert.Equal(new[] { "first", "second", "after reset" }, provider.ReceivedRequests.Select(LastUserText));
        Assert.Empty(view.Notices);
    }

    [Fact]
    public async Task CanvasChangedWhilePaused_AnswerCarriesTheNoteToTheResumedModel()
    {
        var view = new FakeChatView();
        var provider = new StepProvider(
            Step.Tool("toolu_ask", Parti.Core.Tools.AskUserTool.ToolName, Loop.AskArgs(("q1", "How many panels?", "text"))),
            Step.Text("ok"));
        var probe = new FakeCanvasProbe { State = "doc|undo:1" };

        using var controller = new ChatController(
            provider, new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject()),
            new NoOpUndoScopeFactory(), Loop.Options(), view,
            new ChatControllerWiring(CanvasProbe: probe));

        Assert.True(await controller.SendAsync("make a facade"));
        Assert.Equal(SessionStatus.Paused, controller.Status);
        probe.State = "doc|undo:0|redo:1"; // the user pressed Undo while Parti waited for an answer
        Assert.True(await controller.SendAsync("12"));

        Assert.Contains("[Parti automatic note] The canvas changed outside this chat", provider.RequestSnapshots[1]);
        Assert.Equal(new[] { TranscriptLines.CanvasChangedNotice }, view.Notices);
        Assert.Equal(SessionStatus.Idle, controller.Status);
    }

    [Fact]
    public void TranscriptLines_ShowTheHostIdentityAndOutcome_Bounded()
    {
        static AIInteractionToolResult Result(string name, JObject args, JObject payload) =>
            new() { Name = name, Arguments = args, Result = payload };

        Assert.Equal(
            "\u00b7 gh_add_component Number Slider \u2192 added",
            TranscriptLines.ToolResult(Result(
                "gh_add_component",
                new JObject { ["componentGuid"] = Guid.NewGuid().ToString(), ["nickname"] = "Evil Nick" },
                new JObject { ["status"] = "success", ["canonicalName"] = "Number Slider" })));
        Assert.Equal(
            "\u00b7 gh_find_component \"pipe\" \u2192 3 matches",
            TranscriptLines.ToolResult(Result("gh_find_component", new JObject { ["query"] = "pipe" }, new JObject { ["status"] = "ok", ["count"] = 3 })));
        Assert.Equal(
            "\u00b7 gh_get_all \u2192 cancelled",
            TranscriptLines.ToolResult(Result("gh_get_all", new JObject(), new JObject { ["success"] = false, ["cancelled"] = true })));

        var hostile = TranscriptLines.ToolResult(Result(
            "gh_describe",
            new JObject { ["target"] = "a\u001b[31m\nb", ["__parti_document_lease"] = "secret-token" },
            new JObject { ["status"] = "error", ["message"] = new string('x', 500) }));
        Assert.StartsWith("\u00b7 gh_describe a [31m b \u2192 error: xxx", hostile);
        Assert.DoesNotContain("secret-token", hostile);
        // Ordinal checks: a culture-aware search treats control characters as ignorable.
        Assert.False(hostile.Contains('\n'));
        Assert.False(hostile.Contains('\u001b'));
        Assert.True(hostile.Length <= TranscriptLines.MaxLineLength);
    }

    [Theory]
    [InlineData("gh_delete", "not_found", "Target not found.", "not_found: Target not found.")]
    [InlineData("gh_set_input", "unsupported", "Panel inputs are read-only.", "unsupported: Panel inputs are read-only.")]
    [InlineData("gh_group", "partial", "", "partial")]
    [InlineData("gh_delete", "success", "", "deleted")]
    public void TranscriptLines_UseSuccessVerbsOnlyForConfirmedSuccess(string tool, string status, string message, string shown)
    {
        var payload = new JObject { ["status"] = status };
        if (message.Length > 0)
        {
            payload["message"] = message;
        }
        var line = TranscriptLines.ToolResult(new AIInteractionToolResult { Name = tool, Arguments = new JObject(), Result = payload });
        Assert.EndsWith("\u2192 " + shown, line);
    }

    [Fact]
    public async Task DocumentSwitchedDuringALoop_NextMessageCarriesTheNote()
    {
        var view = new FakeChatView();
        var probe = new FakeCanvasProbe { State = "docA|undo:1" };
        var provider = new ScriptedStreamingProvider(new[] { TurnScript.Text("ok") });
        var dispatcher = new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject());

        using var controller = new ChatController(
            provider, dispatcher, new NoOpUndoScopeFactory(), MakeOptions(), view,
            new ChatControllerWiring(CanvasProbe: probe));

        // The user switches to document B while the reply streams; B's history never changes after.
        view.OnAppendAssistantText = _ => probe.State = "docB|undo:7";
        Assert.True(await controller.SendAsync("first"));
        view.OnAppendAssistantText = null;
        Assert.True(await controller.SendAsync("second"));

        var prompts = provider.ReceivedRequests.Select(LastUserText).ToList();
        Assert.Equal("second\n\n" + TranscriptLines.CanvasChangedNote, prompts[1]);
    }

    // ----- helpers --------------------------------------------------------

    private static string? LastUserText(AIRequestCall request) =>
        request.Interactions.OfType<AIInteractionText>().Last(i => i.Agent == AIAgent.User).Content;

    private sealed class FakeCanvasProbe : ICanvasStateProbe
    {
        public string? State { get; set; }

        public string? Capture() => State;
    }

    private sealed class NullEmitter : IGhUndoEmitter
    {
        public bool Emit(string label, object? document, IReadOnlyList<object> actions) => false;
    }

    /// <summary>
    /// In-memory <see cref="IChatView"/> used by the unit tests. Records every method
    /// call without doing any UI work.
    /// </summary>
    private sealed class FakeChatView : IChatView
    {
        public List<string> UserMessages { get; } = new();
        public List<string> AssistantDeltas { get; } = new();
        public string AssistantText => string.Concat(AssistantDeltas);
        public List<bool> BusyStates { get; } = new();
        public List<string> Errors { get; } = new();
        public bool HistoryCleared { get; private set; }
        public Action<string>? OnAppendAssistantText { get; set; }
        public List<string> ToolLines { get; } = new();
        public List<string> Notices { get; } = new();
        public List<string> Events { get; } = new();

        public void AppendUserMessage(string text)
        {
            UserMessages.Add(text);
            Events.Add("user: " + text);
        }

        public void AppendAssistantText(string delta)
        {
            AssistantDeltas.Add(delta);
            Events.Add("text: " + delta);
            OnAppendAssistantText?.Invoke(delta);
        }

        public void AppendToolActivity(string line)
        {
            ToolLines.Add(line);
            Events.Add("tool: " + line);
        }

        public void BeginAssistantTurn() => Events.Add("turn");

        public void AppendNotice(string text)
        {
            Notices.Add(text);
            Events.Add("notice: " + text);
        }

        public void SetBusy(bool busy) => BusyStates.Add(busy);

        public void ShowError(string message) => Errors.Add(message);

        public void ClearHistory()
        {
            HistoryCleared = true;
            AssistantDeltas.Clear();
            UserMessages.Clear();
        }
    }

    /// <summary>
    /// <see cref="IUndoScopeFactory"/> that records every Begin call and hands out
    /// scopes that count RecordMutation invocations and track disposal.
    /// </summary>
    private sealed class RecordingUndoScopeFactory : IUndoScopeFactory
    {
        public int BeginCount { get; private set; }
        public RecordingUndoScope? LastScope { get; private set; }

        public IUndoScope Begin(string label)
        {
            BeginCount++;
            LastScope = new RecordingUndoScope(label);
            return LastScope;
        }
    }

    private sealed class RecordingUndoScope : IUndoScope
    {
        public RecordingUndoScope(string label) { Label = label; }
        public string Label { get; }
        public int RecordMutationCount { get; private set; }
        public bool Abandoned { get; private set; }
        public bool Disposed { get; private set; }

        public void RecordMutation(object document) => RecordMutationCount++;
        public void Abandon() { Abandoned = true; }
        public void Dispose() { Disposed = true; }
    }

    private sealed class LeaseUndoScopeFactory : IUndoScopeFactory
    {
        private readonly string _token;
        public LeaseUndoScopeFactory(string token) => _token = token;
        public IUndoScope Begin(string label) => new LeaseUndoScope(label, _token);
    }

    private sealed class LeaseUndoScope : IUndoScope, IGrasshopperDocumentLease
    {
        public LeaseUndoScope(string label, string token)
        {
            Label = label;
            DocumentLeaseToken = token;
        }

        public string Label { get; }
        public string DocumentLeaseToken { get; }
        public void RecordMutation(object document) { }
        public void Abandon() { }
        public void Dispose() { }
    }

    /// <summary>
    /// Lightweight recipe for what one fake provider turn should stream: a sequence
    /// of text deltas, an optional tool_use block, optional delays, and a terminal
    /// (end_turn / tool_use / error) status.
    /// </summary>
    private sealed class TurnScript
    {
        public List<string> Deltas { get; } = new();
        public List<(string Id, string Name, JObject Args)> ToolCalls { get; } = new();
        public TimeSpan DelayBetweenDeltas { get; private set; } = TimeSpan.Zero;
        public string StopReason { get; private set; } = "end_turn";
        public string? ErrorMessage { get; private set; }

        public static TurnScript Text(params string[] deltas)
        {
            var s = new TurnScript();
            s.Deltas.AddRange(deltas);
            s.StopReason = "end_turn";
            return s;
        }

        public static TurnScript ToolCall(string id, string name, JObject args)
        {
            var s = new TurnScript();
            s.ToolCalls.Add((id, name, args));
            s.StopReason = "tool_use";
            return s;
        }

        public static TurnScript Error(string message)
        {
            var s = new TurnScript();
            s.ErrorMessage = message;
            s.StopReason = "error";
            return s;
        }

        public TurnScript ThenText(params string[] deltas)
        {
            Deltas.AddRange(deltas);
            return this;
        }

        public TurnScript WithStopReason(string stopReason)
        {
            StopReason = stopReason;
            return this;
        }

        public TurnScript WithDelayBetweenDeltas(TimeSpan delay)
        {
            DelayBetweenDeltas = delay;
            return this;
        }
    }

    /// <summary>
    /// Minimal streaming <see cref="IAIProvider"/> that replays a scripted list of
    /// <see cref="TurnScript"/>s in order, one per <see cref="CallStreamAsync"/>
    /// invocation. Each script turns into TextDelta / ToolCall* / Done chunks.
    /// </summary>
    private sealed class ScriptedStreamingProvider : IAIProvider
    {
        private readonly List<TurnScript> _scripts;
        private int _index;
        public List<AIRequestCall> ReceivedRequests { get; } = new();

        public ScriptedStreamingProvider(IEnumerable<TurnScript> scripts)
        {
            _scripts = scripts.ToList();
        }

        public string Name => "scripted";
        public Uri DefaultServerUrl { get; } = new("https://example.invalid");

        public AIRequestCall PreCall(AIRequestCall request) => request;
        public string CreateRequestBody(AIRequestCall request) => "{}";
        public List<IAIInteraction> Decode(JObject response) => new();
        public string Encode(IAIInteraction interaction) => "{}";

        public Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("This fake only supports CallStreamAsync.");
        }

        public async IAsyncEnumerable<AIStreamChunk> CallStreamAsync(
            AIRequestCall request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedRequests.Add(request);
            var script = _scripts[Math.Min(_index, _scripts.Count - 1)];
            _index++;

            if (script.ErrorMessage is not null)
            {
                var err = new AIReturn();
                err.CreateError(script.ErrorMessage, request);
                yield return new AIStreamChunk(AIStreamChunkKind.Done, Final: err, StopReason: "error");
                yield break;
            }

            var interactions = new List<IAIInteraction>();

            // Text deltas.
            if (script.Deltas.Count > 0)
            {
                var acc = new System.Text.StringBuilder();
                for (int i = 0; i < script.Deltas.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (i > 0 && script.DelayBetweenDeltas > TimeSpan.Zero)
                    {
                        await Task.Delay(script.DelayBetweenDeltas, cancellationToken).ConfigureAwait(false);
                    }
                    acc.Append(script.Deltas[i]);
                    yield return new AIStreamChunk(AIStreamChunkKind.TextDelta, Text: script.Deltas[i]);
                }

                interactions.Add(new AIInteractionText
                {
                    Agent = AIAgent.Assistant,
                    Content = acc.ToString(),
                });
            }

            // Tool calls.
            foreach (var (id, name, args) in script.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new AIStreamChunk(
                    AIStreamChunkKind.ToolCallStart,
                    ToolName: name,
                    ToolUseId: id);
                yield return new AIStreamChunk(
                    AIStreamChunkKind.ToolCallComplete,
                    ToolName: name,
                    ToolUseId: id,
                    ToolArgsPartial: args.ToString(Newtonsoft.Json.Formatting.None));

                interactions.Add(new AIInteractionToolCall
                {
                    Id = id,
                    Name = name,
                    Arguments = args,
                    Agent = AIAgent.ToolCall,
                });
            }

            var final = new AIReturn
            {
                Status = script.StopReason == "tool_use" ? AICallStatus.CallingTools : AICallStatus.Finished,
                StopReason = script.StopReason,
                Interactions = interactions,
                InputTokens = 10,
                OutputTokens = 10,
            };

            yield return new AIStreamChunk(AIStreamChunkKind.Done, Final: final, StopReason: script.StopReason);
        }
    }
}
