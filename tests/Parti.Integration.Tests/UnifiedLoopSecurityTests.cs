// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Grasshopper;
using Parti.Core.Grasshopper.Chat;
using Parti.Core.Sessions;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// Security invariants of the unified loop (product-wiring spec §2: S1, S3, S4, S5, S9, S15, S16,
/// and amendments A3, A5, A6, A7, A9). Scenarios that exist at both entry points run as a
/// <c>[Theory]</c> over the bare session and the chat controller.
/// </summary>
public class UnifiedLoopSecurityTests
{
    private const string LeaseKey = "__parti_document_lease";
    private static readonly string TargetGuid = Guid.NewGuid().ToString();

    public static IEnumerable<object[]> EntryPoints => new[] { new object[] { "session" }, new object[] { "controller" } };

    // ----- S1: the lease -------------------------------------------------------------------

    [Fact]
    public async Task LeaseNeverReachesObserverPlanOrMemory()
    {
        var instanceGuid = Guid.NewGuid().ToString();
        var provider = new StepProvider(Step.Tool("toolu_add", "gh_add_component", Loop.AddArgs()), Step.Text("added"));
        var dispatcher = new FakeToolDispatcher(
            new[] { Loop.Write("gh_add_component") },
            (_, args) => new JObject
            {
                ["status"] = "success",
                ["instanceGuid"] = instanceGuid,
                ["canonicalName"] = "Number Slider",
                ["echo"] = args.DeepClone(), // a dispatcher that echoes its (leased) arguments
            });
        var observer = new SnapshotObserver();
        var plans = new List<string>();
        var session = new ConversationSession(
            provider, dispatcher, Loop.Options(), observer,
            new UndoScopeTurnEnvelopeFactory(new RecordingScopeFactory(Loop.Lease)))
        {
            PlanReviewer = (plan, _) =>
            {
                plans.Add(JsonConvert.SerializeObject(plan));
                return Task.FromResult(true);
            },
        };

        await session.RunAsync("add a slider");

        // The lease really was delivered to the approved dispatch...
        Assert.Equal(Loop.Lease, dispatcher.Calls.Single().Arguments[LeaseKey]?.ToString());

        // ...and reached nothing else.
        var everything = string.Join("\n", observer.Snapshots
            .Concat(plans)
            .Concat(provider.RequestSnapshots)
            .Append(StepProvider.Snapshot(session.History))
            .Append(JsonConvert.SerializeObject(session.Memory.RecentByType("Number Slider"))));
        Assert.DoesNotContain(Loop.Lease, everything);
        Assert.DoesNotContain(LeaseKey, everything);
        Assert.Single(plans);
        Assert.Contains(session.Memory.RecentByType("Number Slider"), m => m.InstanceGuid == instanceGuid);
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task LeaseStrippedFromAliasedResultArguments(string entry)
    {
        // FakeToolDispatcher aliases the dispatched call's Arguments into its result (dcad4e9).
        var provider = new StepProvider(Step.Tool("toolu_1", "gh_get_all", new JObject()), Step.Text("done"));
        var dispatcher = new FakeToolDispatcher(new[] { Loop.Read("gh_get_all") }, (_, _) => new JObject { ["status"] = "success" });
        var undo = new RecordingScopeFactory(Loop.Lease);

        var history = await RunAsync(entry, provider, dispatcher, reviewer: null, undo: undo);

        var result = Assert.Single(history.OfType<AIInteractionToolResult>());
        Assert.Null(result.Arguments[LeaseKey]);
        // The dispatcher's own object is not mutated after dispatch.
        Assert.Equal(Loop.Lease, dispatcher.Calls.Single().Arguments[LeaseKey]?.ToString());
        Assert.DoesNotContain(Loop.Lease, provider.RequestSnapshots[1]);
    }

    // ----- S3: consent ----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task NullReviewer_MutationIsRejectedFailClosed(string entry)
    {
        var provider = new StepProvider(Step.Tool("toolu_1", "gh_set_input", SetArgs()), Step.Text("ok"));
        var dispatcher = WriteDispatcher();

        var history = await RunAsync(entry, provider, dispatcher, reviewer: null);

        Assert.Equal(0, dispatcher.DispatchCount);
        var result = Assert.Single(history.OfType<AIInteractionToolResult>());
        Assert.True(result.Result["rejected"]?.Value<bool>());
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task ReviewerThrows_IsTreatedAsCancel(string entry)
    {
        var provider = new StepProvider(Step.Tool("toolu_1", "gh_set_input", SetArgs()), Step.Text("ok"));
        var dispatcher = WriteDispatcher();

        var history = await RunAsync(entry, provider, dispatcher, (_, _) => throw new InvalidOperationException("dialog crashed"));

        Assert.Equal(0, dispatcher.DispatchCount);
        var result = Assert.Single(history.OfType<AIInteractionToolResult>());
        Assert.True(result.Result["cancelled"]?.Value<bool>());
        Assert.Single(provider.Requests);
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task ReviewerCancel_NothingDispatched_ModelSeesCancellation(string entry)
    {
        var provider = new StepProvider(Step.Tool("toolu_1", "gh_set_input", SetArgs()), Step.Text("ok"));
        var dispatcher = WriteDispatcher();

        var history = await RunAsync(entry, provider, dispatcher, (_, _) => Task.FromResult(false));

        Assert.Equal(0, dispatcher.DispatchCount);
        var result = Assert.Single(history.OfType<AIInteractionToolResult>());
        Assert.Equal("User cancelled plan before execution.", result.Result["message"]?.ToString());
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task MixedBatch_InvokesReviewerOnce_AndDispatchesInOrder(string entry)
    {
        var provider = new StepProvider(
            Step.Tools(("toolu_r", "gh_get_all", new JObject()), ("toolu_w", "gh_set_input", SetArgs())),
            Step.Text("done"));
        var dispatcher = new FakeToolDispatcher(
            new[] { Loop.Read("gh_get_all"), Loop.Write("gh_set_input") },
            (_, _) => new JObject { ["status"] = "success" });
        var reviews = 0;

        await RunAsync(entry, provider, dispatcher, (plan, _) =>
        {
            reviews++;
            Assert.Equal(new[] { "gh_get_all", "gh_set_input" }, plan.Calls.Select(c => c.Name));
            return Task.FromResult(true);
        });

        Assert.Equal(1, reviews);
        Assert.Equal(new[] { "gh_get_all", "gh_set_input" }, dispatcher.Calls.Select(c => c.Name));
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task ReadOnlyBatch_BypassesReviewer(string entry)
    {
        var provider = new StepProvider(Step.Tool("toolu_1", "gh_get_all", new JObject()), Step.Text("done"));
        var dispatcher = new FakeToolDispatcher(new[] { Loop.Read("gh_get_all") }, (_, _) => new JObject { ["status"] = "success" });

        await RunAsync(entry, provider, dispatcher, (_, _) => throw new InvalidOperationException("must not be called"));

        Assert.Equal(1, dispatcher.DispatchCount);
    }

    // ----- S4: binding -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task PlanBindingIncludesDocumentIdentity(string entry)
    {
        var provider = new StepProvider(Step.Tool("toolu_1", "gh_set_input", SetArgs()), Step.Text("ok"));
        string? binding = null;

        await RunAsync(entry, provider, WriteDispatcher(), (plan, _) =>
        {
            binding = plan.BindingId;
            return Task.FromResult(true);
        }, envelope: new PrefixEnvelopeFactory("doc-42"));

        Assert.NotNull(binding);
        Assert.StartsWith("doc-42:", binding);
        Assert.Matches("^doc-42:[0-9a-f]{64}$", binding);
    }

    // ----- S5: replay ------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task ReplayedToolIdAcrossUserTurns_IsRejected(string entry)
    {
        var provider = new StepProvider(
            Step.Tool("toolu_same", "gh_get_all", new JObject()),
            Step.Text("first answer"),
            Step.Tool("toolu_same", "gh_get_all", new JObject()),
            Step.Text("second answer"));
        var dispatcher = new FakeToolDispatcher(new[] { Loop.Read("gh_get_all") }, (_, _) => new JObject { ["status"] = "success" });

        IReadOnlyList<AIInteractionBase> history;
        if (entry == "session")
        {
            var session = new ConversationSession(provider, dispatcher, Loop.Options());
            await session.RunAsync("first");
            await session.ContinueAsync("second");
            history = session.History;
        }
        else
        {
            using var controller = new ChatController(provider, dispatcher, new NoOpUndoScopeFactory(), Loop.Options(), new RecordingView());
            await controller.SendAsync("first");
            await controller.SendAsync("second");
            history = controller.History;
        }

        Assert.Equal(1, dispatcher.DispatchCount);
        Assert.Contains(history.OfType<AIInteractionToolResult>(), r =>
            r.Result["rejected"]?.Value<bool>() == true &&
            r.Result["message"]!.ToString().Contains("replayed provider tool-call id"));
    }

    [Fact]
    public async Task Reset_ClearsSeenToolIds()
    {
        var provider = new StepProvider(
            Step.Tool("toolu_same", "gh_get_all", new JObject()),
            Step.Text("first"),
            Step.Tool("toolu_same", "gh_get_all", new JObject()),
            Step.Text("second"));
        var dispatcher = new FakeToolDispatcher(new[] { Loop.Read("gh_get_all") }, (_, _) => new JObject { ["status"] = "success" });
        var session = new ConversationSession(provider, dispatcher, Loop.Options());

        await session.RunAsync("first");
        session.Reset();
        await session.RunAsync("fresh conversation");

        Assert.Equal(2, dispatcher.DispatchCount);
    }

    // ----- S9 / A3: session tools --------------------------------------------------------------

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task AskUser_ResumeProducesExactlyOneToolResultPerToolUse(string entry)
    {
        var provider = new StepProvider(
            Step.Tool("toolu_ask", AskUserTool.ToolName, Loop.AskArgs(("q1", "How many panels?", "text"))),
            Step.Text("using 12 panels"));
        var dispatcher = new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject());

        IReadOnlyList<AIInteractionBase> history;
        if (entry == "session")
        {
            var session = new ConversationSession(provider, dispatcher, Loop.Options());
            await session.RunAsync("make a facade");
            Assert.Equal(SessionStatus.Paused, session.Status);
            Assert.Empty(session.History.OfType<AIInteractionToolResult>()); // no synthetic "paused" result
            await session.ResumeAsync(new[] { new ClarificationAnswer("q1", "12") });
            history = session.History;
        }
        else
        {
            using var controller = new ChatController(provider, dispatcher, new NoOpUndoScopeFactory(), Loop.Options(), new RecordingView());
            await controller.SendAsync("make a facade");
            Assert.Equal(SessionStatus.Paused, controller.Status);
            Assert.True(await controller.AnswerAsync(new[] { new ClarificationAnswer("q1", "12") }));
            history = controller.History;
        }

        Loop.AssertOneResultPerToolUse(history);
        var resumed = JArray.Parse(provider.RequestSnapshots[1]);
        var results = resumed.Where(t => (string?)t["kind"] == "result" && (string?)t["id"] == "toolu_ask").ToList();
        var answer = Assert.Single(results);
        Assert.Equal("resumed", (string?)answer["result"]!["status"]);
        Assert.Equal("12", (string?)answer["result"]!["answers"]![0]!["answer"]);
    }

    [Fact]
    public async Task AskUser_AbandonedByNewMessage_GetsDeclinedResult()
    {
        var provider = new StepProvider(
            Step.Tool("toolu_ask", AskUserTool.ToolName, Loop.AskArgs(("q1", "Which facade?", "text"))),
            Step.Text("ok, the roof then"));
        var session = new ConversationSession(provider, new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject()), Loop.Options());

        await session.RunAsync("model the facade");
        await session.ContinueAsync("actually, do the roof");

        Loop.AssertOneResultPerToolUse(session.History);
        var declined = Assert.Single(session.History.OfType<AIInteractionToolResult>());
        Assert.Equal("declined", declined.Result["status"]?.ToString());
        Assert.False(session.Clarification.IsPaused);

        // The declined result precedes the new user message.
        var declinedIndex = session.History.ToList().IndexOf(declined);
        var userIndex = session.History.ToList().FindLastIndex(i => i is AIInteractionText { Agent: AIAgent.User });
        Assert.True(declinedIndex < userIndex);
    }

    [Fact]
    public async Task AskUser_ArgumentsMutatedAfterFreeze_DoNotChangeQuestions()
    {
        var provider = new StepProvider(Step.Tool("toolu_ask", AskUserTool.ToolName, Loop.AskArgs(("q1", "Original?", "text"))));
        var observer = new SnapshotObserver();
        var session = new ConversationSession(provider, new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject()), Loop.Options(), observer);
        observer.OnToolCallHook = copy =>
        {
            // Mutate both the observer's copy and the provider-owned object in history.
            copy.Arguments["questions"] = new JArray();
            var original = session.History.OfType<AIInteractionToolCall>().First(c => c.Id == "toolu_ask");
            original.Arguments["questions"]![0]!["prompt"] = "Injected?";
        };

        await session.RunAsync("go");

        var question = Assert.Single(session.Clarification.Current!.Questions);
        Assert.Equal("Original?", question.Prompt);
    }

    [Fact]
    public async Task RecordDecomposition_ArgumentsMutatedAfterFreeze_DoNotChangeRecord()
    {
        var provider = new StepProvider(
            Step.Tool("toolu_rec", RecordDecompositionTool.ToolName, Loop.CompleteDecomposition()),
            Step.Text("recorded"));
        var observer = new SnapshotObserver();
        var session = new ConversationSession(provider, new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject()), Loop.Options(), observer);
        observer.OnToolCallHook = copy =>
        {
            copy.Arguments["principle"] = "observer edit";
            var original = session.History.OfType<AIInteractionToolCall>().First(c => c.Id == "toolu_rec");
            original.Arguments["principle"] = "provider edit";
        };

        await session.RunAsync("go");

        Assert.Equal("staggered rainscreen with alternating course depths", session.Decomposition.Current.Principle);
        Assert.Equal(1, session.Decomposition.Revision);
    }

    [Fact]
    public async Task SessionTools_ReplayedId_IsRejected()
    {
        var provider = new StepProvider(
            Step.Tool("toolu_rec", RecordDecompositionTool.ToolName, Loop.CompleteDecomposition()),
            Step.Tool("toolu_rec", RecordDecompositionTool.ToolName, Loop.CompleteDecomposition()),
            Step.Tool("toolu_rec", AskUserTool.ToolName, Loop.AskArgs(("q1", "?", "text"))),
            Step.Text("done"));
        var session = new ConversationSession(provider, new FakeToolDispatcher(Array.Empty<AIToolDefinition>(), (_, _) => new JObject()), Loop.Options());

        await session.RunAsync("go");

        Assert.Equal(1, session.Decomposition.Revision);
        Assert.False(session.Clarification.IsPaused);
        Assert.Equal(2, session.History.OfType<AIInteractionToolResult>().Count(r => r.Result["rejected"]?.Value<bool>() == true));
    }

    // ----- S15: serial dispatch ----------------------------------------------------------------

    [Fact]
    public async Task DispatchIsSerialThroughController()
    {
        var inFlight = 0;
        var maxInFlight = 0;
        var order = new List<string>();
        var dispatcher = new DelegateDispatcher(new[] { Loop.Read("gh_describe") }, async (call, ct) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            maxInFlight = Math.Max(maxInFlight, now);
            order.Add(call.Id!);
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref inFlight);
            return new JObject { ["status"] = "success" };
        });
        var provider = new StepProvider(
            Step.Tools(
                ("toolu_a", "gh_describe", new JObject { ["target"] = Guid.NewGuid().ToString() }),
                ("toolu_b", "gh_describe", new JObject { ["target"] = Guid.NewGuid().ToString() }),
                ("toolu_c", "gh_describe", new JObject { ["target"] = Guid.NewGuid().ToString() })),
            Step.Text("done"));
        using var controller = new ChatController(provider, dispatcher, new NoOpUndoScopeFactory(), Loop.Options(), new RecordingView());

        await controller.SendAsync("describe three");

        Assert.Equal(1, maxInFlight);
        Assert.Equal(new[] { "toolu_a", "toolu_b", "toolu_c" }, order);
    }

    // ----- S16 / A6: one undo record per loop, keep/abandon ------------------------------------

    [Fact]
    public async Task Undo_TextOnlyCompletion_KeepsScopeWithoutMutation()
    {
        var undo = new RecordingScopeFactory();
        using var controller = new ChatController(new StepProvider(Step.Text("hi")), NoTools(), undo, Loop.Options(), new RecordingView());

        await controller.SendAsync("hello");

        var scope = Assert.Single(undo.Scopes);
        Assert.False(scope.Abandoned);
        Assert.Equal(0, scope.RecordMutationCount);
        Assert.True(scope.Disposed);
    }

    [Fact]
    public async Task Undo_ReadOnlyPass_KeepsScopeWithoutMutation()
    {
        var undo = new RecordingScopeFactory();
        var dispatcher = new FakeToolDispatcher(new[] { Loop.Read("gh_get_all") }, (_, _) => new JObject { ["status"] = "success" });
        using var controller = new ChatController(
            new StepProvider(Step.Tool("toolu_1", "gh_get_all", new JObject()), Step.Text("done")),
            dispatcher, undo, Loop.Options(), new RecordingView());

        await controller.SendAsync("look");

        var scope = Assert.Single(undo.Scopes);
        Assert.False(scope.Abandoned);
        Assert.Equal(0, scope.RecordMutationCount);
    }

    [Fact]
    public async Task Undo_RejectedMutation_KeepsScopeWithoutMutation()
    {
        // Today's controller keeps the record once a tool pass completes, even a rejected one.
        var undo = new RecordingScopeFactory();
        using var controller = new ChatController(
            new StepProvider(Step.Tool("toolu_1", "gh_set_input", SetArgs()), Step.Text("ok")),
            WriteDispatcher(), undo, Loop.Options(), new RecordingView());

        await controller.SendAsync("set it"); // no reviewer: fail-closed rejection

        var scope = Assert.Single(undo.Scopes);
        Assert.False(scope.Abandoned);
        Assert.Equal(0, scope.RecordMutationCount);
    }

    [Fact]
    public async Task Undo_ProviderErrorBeforeAnyPass_AbandonsScope()
    {
        var undo = new RecordingScopeFactory();
        using var controller = new ChatController(new StepProvider(Step.Failure("boom")), NoTools(), undo, Loop.Options(), new RecordingView());

        await controller.SendAsync("hello");

        Assert.True(Assert.Single(undo.Scopes).Abandoned);
    }

    [Fact]
    public async Task Undo_CancelBeforeAnyPass_AbandonsScope()
    {
        var undo = new RecordingScopeFactory();
        var view = new RecordingView();
        var provider = new StepProvider(Step.Text("partial").Before(ct => Task.Delay(TimeSpan.FromSeconds(5), ct)));
        using var controller = new ChatController(provider, NoTools(), undo, Loop.Options(), view);
        view.OnDelta = _ => controller.Cancel();

        await controller.SendAsync("hello");

        Assert.True(Assert.Single(undo.Scopes).Abandoned);
        Assert.Empty(view.Errors);
    }

    [Fact]
    public async Task CancelDuringSecondCall_FirstResultStays_UndoKept()
    {
        var undo = new RecordingScopeFactory();
        var view = new RecordingView();
        ChatController? controller = null;
        var dispatcher = new DelegateDispatcher(new[] { Loop.Write("gh_set_input") }, async (call, ct) =>
        {
            if (call.Id == "toolu_2")
            {
                controller!.Cancel();
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            return new JObject { ["status"] = "success" };
        });
        var provider = new StepProvider(
            Step.Tools(("toolu_1", "gh_set_input", SetArgs()), ("toolu_2", "gh_set_input", SetArgs())),
            Step.Text("never"));
        using var actual = new ChatController(provider, dispatcher, undo, Loop.Options(), view)
        {
            PlanReviewer = (_, _) => Task.FromResult(true),
        };
        controller = actual;

        Assert.True(await actual.SendAsync("set both"));

        var results = actual.History.OfType<AIInteractionToolResult>().ToList();
        Assert.Equal("success", results.Single(r => r.Id == "toolu_1").Result["status"]?.ToString());
        Assert.True(results.Single(r => r.Id == "toolu_2").Result["cancelled"]?.Value<bool>());
        Loop.AssertOneResultPerToolUse(actual.History);

        var scope = Assert.Single(undo.Scopes);
        Assert.False(scope.Abandoned);
        Assert.Equal(2, scope.RecordMutationCount);
        Assert.Empty(view.Errors);
        Assert.Equal(SessionStatus.Idle, actual.Status);
        Assert.Single(provider.Requests);
    }

    // ----- A7: one error path -------------------------------------------------------------------

    [Fact]
    public async Task StreamWithoutDone_ShowsNoResponseErrorOnce()
    {
        var view = new RecordingView();
        using var controller = new ChatController(new StepProvider(Step.NoDone()), NoTools(), new NoOpUndoScopeFactory(), Loop.Options(), view);

        await controller.SendAsync("hello");

        Assert.Equal("Provider returned no response.", Assert.Single(view.Errors));
        Assert.Equal(SessionStatus.Error, controller.Status);
    }

    [Fact]
    public async Task ProviderError_ShowsExactlyOneError()
    {
        var view = new RecordingView();
        using var controller = new ChatController(new StepProvider(Step.Failure("model overloaded")), NoTools(), new NoOpUndoScopeFactory(), Loop.Options(), view);

        await controller.SendAsync("hello");

        Assert.Equal("model overloaded", Assert.Single(view.Errors));
    }

    // ----- A9: truncated responses ---------------------------------------------------------------

    [Theory]
    [InlineData("session", "max_tokens")]
    [InlineData("controller", "max_tokens")]
    [InlineData("session", "length")]
    [InlineData("controller", "length")]
    public async Task TruncatedResponseWithToolCalls_IsNeverDispatched(string entry, string stopReason)
    {
        var provider = new StepProvider(Step.Tool("toolu_1", "gh_get_all", new JObject()).WithText("partial").WithStopReason(stopReason));
        var dispatcher = new FakeToolDispatcher(new[] { Loop.Read("gh_get_all") }, (_, _) => new JObject { ["status"] = "success" });

        var history = await RunAsync(entry, provider, dispatcher, reviewer: null);

        Assert.Equal(0, dispatcher.DispatchCount);
        Assert.Empty(history.OfType<AIInteractionToolCall>());
        Assert.Contains(history, i => i is AIInteractionText { Agent: AIAgent.Assistant, Content: "partial" });
    }

    // ----- cross-review regressions -----------------------------------------------------------

    [Fact]
    public async Task LeaseEmbeddedInExceptionMessage_IsRedacted()
    {
        var provider = new StepProvider(Step.Tool("toolu_1", "gh_get_all", new JObject()), Step.Text("done"));
        var dispatcher = new DelegateDispatcher(new[] { Loop.Read("gh_get_all") },
            (call, _) => throw new InvalidOperationException("bad arguments: " + call.Arguments.ToString(Formatting.None)));
        var observer = new SnapshotObserver();
        var session = new ConversationSession(provider, dispatcher, Loop.Options(), observer,
            new UndoScopeTurnEnvelopeFactory(new RecordingScopeFactory(Loop.Lease)));

        await session.RunAsync("look");

        var result = Assert.Single(session.History.OfType<AIInteractionToolResult>());
        Assert.Contains("[redacted]", result.Result["error"]!.ToString());
        Assert.DoesNotContain(Loop.Lease, StepProvider.Snapshot(session.History));
        Assert.DoesNotContain(Loop.Lease, string.Join("\n", observer.Snapshots.Concat(provider.RequestSnapshots)));
    }

    [Fact]
    public async Task CancelWhileReviewerIsOpen_EveryToolUseGetsAResult()
    {
        var view = new RecordingView();
        ChatController? controller = null;
        var provider = new StepProvider(Step.Tool("toolu_1", "gh_set_input", SetArgs()), Step.Text("never"));
        using var actual = new ChatController(provider, WriteDispatcher(), new RecordingScopeFactory(), Loop.Options(), view)
        {
            PlanReviewer = async (_, ct) =>
            {
                controller!.Cancel();
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return true;
            },
        };
        controller = actual;

        await actual.SendAsync("set it");

        Loop.AssertOneResultPerToolUse(actual.History);
        Assert.Empty(view.Errors);
        Assert.Equal(SessionStatus.Idle, actual.Status);
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task ReviewerOwnCancellation_IsAPlanCancel_NotAnError(string entry)
    {
        var provider = new StepProvider(Step.Tool("toolu_1", "gh_set_input", SetArgs()), Step.Text("ok"));
        var dispatcher = WriteDispatcher();

        var history = await RunAsync(entry, provider, dispatcher, (_, _) => Task.FromCanceled<bool>(new CancellationToken(true)));

        Assert.Equal(0, dispatcher.DispatchCount);
        Assert.True(Assert.Single(history.OfType<AIInteractionToolResult>()).Result["cancelled"]?.Value<bool>());
    }

    [Fact]
    public async Task FailedResponseWithToolCalls_KeepsNoUnansweredToolUse()
    {
        var failed = new AIReturn
        {
            Status = AICallStatus.Error,
            ErrorMessage = "stream broke",
            Interactions = new List<IAIInteraction>
            {
                new AIInteractionText { Agent = AIAgent.Assistant, Content = "partial" },
                new AIInteractionToolCall { Id = "toolu_x", Name = "gh_get_all", Arguments = new JObject() },
            },
        };
        var session = new ConversationSession(new FakeAIProvider(new[] { failed }), NoTools(), Loop.Options());

        await session.RunAsync("look");

        Assert.Empty(session.History.OfType<AIInteractionToolCall>());
        Assert.Contains(session.History, i => i is AIInteractionText { Content: "partial" });
    }

    [Fact]
    public async Task ThrowingView_DoesNotLeaveControllerBusy()
    {
        var view = new ThrowingBusyView();
        using var controller = new ChatController(new StepProvider(Step.Text("hi")), NoTools(), new NoOpUndoScopeFactory(), Loop.Options(), view);

        await controller.SendAsync("one");

        Assert.False(controller.IsBusy);
        view.Throw = false;
        Assert.True(await controller.SendAsync("two"));
    }

    [Fact]
    public async Task Reset_WaitsForTheActiveLoop_SoNoLateResultLandsInClearedHistory()
    {
        var entered = new TaskCompletionSource();
        var dispatcher = new DelegateDispatcher(new[] { Loop.Read("gh_get_all") }, async (_, _) =>
        {
            entered.TrySetResult();
            await Task.Delay(700); // ignores cancellation, like a slow host call
            return new JObject { ["status"] = "success" };
        });
        var provider = new StepProvider(Step.Tool("toolu_1", "gh_get_all", new JObject()), Step.Text("done"));
        using var controller = new ChatController(provider, dispatcher, new NoOpUndoScopeFactory(), Loop.Options(), new RecordingView());

        var send = controller.SendAsync("look");
        await entered.Task;
        await controller.ResetAsync();
        await send;

        Assert.Empty(controller.History);
        Assert.False(controller.IsBusy);
    }

    private sealed class ThrowingBusyView : IChatView
    {
        public bool Throw { get; set; } = true;
        public void AppendUserMessage(string text) { }
        public void AppendAssistantText(string delta) { }
        public void SetBusy(bool busy)
        {
            if (Throw) throw new InvalidOperationException("view disposed");
        }
        public void ShowError(string message) { }
        public void ClearHistory() { }
    }

    // ----- helpers ---------------------------------------------------------------------------------

    private static JObject SetArgs() => new() { ["target"] = TargetGuid, ["value"] = 5 };

    private static FakeToolDispatcher WriteDispatcher() =>
        new(new[] { Loop.Write("gh_set_input") }, (_, _) => new JObject { ["status"] = "success" });

    private static FakeToolDispatcher NoTools() => new(Array.Empty<AIToolDefinition>(), (_, _) => new JObject());

    /// <summary>Runs one user prompt through the bare session or the chat controller.</summary>
    private static async Task<IReadOnlyList<AIInteractionBase>> RunAsync(
        string entry,
        StepProvider provider,
        IToolDispatcher dispatcher,
        PlanReviewer? reviewer,
        IUndoScopeFactory? undo = null,
        ITurnEnvelopeFactory? envelope = null)
    {
        undo ??= new NoOpUndoScopeFactory();
        if (entry == "session")
        {
            var session = new ConversationSession(
                provider, dispatcher, Loop.Options(), observer: null,
                envelope ?? new UndoScopeTurnEnvelopeFactory(undo))
            {
                PlanReviewer = reviewer,
            };
            await session.RunAsync("go");
            return session.History;
        }

        using var controller = new ChatController(
            provider, dispatcher, undo, Loop.Options(), new RecordingView(),
            new ChatControllerWiring(EnvelopeFactory: envelope))
        {
            PlanReviewer = reviewer,
        };
        Assert.True(await controller.SendAsync("go"));
        Assert.Equal(0, provider.NonStreamCalls); // the controller always streams
        return controller.History.ToList();
    }
}

/// <summary>Async dispatcher driven by a delegate, for concurrency and cancellation tests.</summary>
internal sealed class DelegateDispatcher : IToolDispatcher
{
    private readonly IReadOnlyList<AIToolDefinition> _definitions;
    private readonly Func<AIInteractionToolCall, CancellationToken, Task<JObject>> _handler;

    public DelegateDispatcher(IReadOnlyList<AIToolDefinition> definitions, Func<AIInteractionToolCall, CancellationToken, Task<JObject>> handler)
    {
        _definitions = definitions;
        _handler = handler;
    }

    public IReadOnlyList<AIToolDefinition> GetDefinitions() => _definitions;

    public bool HasTool(string name) => _definitions.Any(d => d.Name == name);

    public async Task<AIInteractionToolResult> DispatchAsync(AIInteractionToolCall call, CancellationToken ct = default)
    {
        var payload = await _handler(call, ct).ConfigureAwait(false);
        return new AIInteractionToolResult { Id = call.Id, Name = call.Name, Arguments = call.Arguments, Result = payload };
    }
}
