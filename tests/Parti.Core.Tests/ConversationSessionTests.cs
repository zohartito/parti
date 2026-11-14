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

namespace Parti.Core.Tests;

public class ConversationSessionTests
{
    [Fact]
    public async Task RespectsMaxTurns()
    {
        // Provider that always returns a tool_use block, forcing the session into an infinite
        // tool loop unless MaxTurns / MaxToolPasses kicks in.
        var provider = new AlwaysToolCallProvider();
        var dispatcher = new StubDispatcher();
        var options = new SessionOptions
        {
            MaxTurns = 3,
            MaxToolPasses = 10, // high so MaxTurns is the actual cap
            ApiKey = "unused",
            Model = "test-model",
        };

        var session = new ConversationSession(provider, dispatcher, options);
        var result = await session.RunAsync("please loop forever");

        Assert.Equal(AICallStatus.Error, result.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Max turns", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal(3, provider.CallCount);
        // 3 turns, 3 dispatches (one per turn since each provider turn asked for a tool).
        Assert.Equal(3, dispatcher.DispatchCount);
    }

    [Fact]
    public async Task ToolPassCap_IsCheckedBeforeDispatch_ThenOneClosingCallWithToolsOff()
    {
        var provider = new ScriptedProvider(new[]
        {
            MakeToolUseReturn("tool_1", "loop_forever", new JObject()),
            MakeToolUseReturn("tool_2", "loop_forever", new JObject()),
            MakeToolUseReturn("tool_3", "loop_forever", new JObject()), // one pass over the cap: not run
            MakeTextReturn("Two checks ran; the rest is left."),
        });
        var dispatcher = new StubDispatcher();
        var session = new ConversationSession(provider, dispatcher,
            new SessionOptions { MaxTurns = 8, MaxToolPasses = 2, ApiKey = "unused" });

        var result = await session.RunAsync("keep checking");

        Assert.Equal(2, dispatcher.DispatchCount);
        Assert.Equal(4, provider.CallCount);
        Assert.Equal(new[] { "auto", "auto", "auto", "none" }, provider.Requests.Select(r => r.ToolChoice));
        Assert.NotEmpty(provider.Requests[3].Tools); // still advertised: history holds tool_use blocks
        var note = Assert.IsType<AIInteractionText>(provider.Requests[3].Interactions.Last());
        Assert.Equal(AIAgent.User, note.Agent);
        Assert.Equal(ConversationSession.ToolPassCapNote(2), note.Content);
        Assert.DoesNotContain(session.History.OfType<AIInteractionToolCall>(), c => c.Id == "tool_3");

        Assert.Equal(AICallStatus.Error, result.Status);
        Assert.Equal("Tool-pass cap (2) reached without resolution.", result.ErrorMessage);
        Assert.Equal("Two checks ran; the rest is left.", Assert.Single(result.Interactions.OfType<AIInteractionText>()).Content);
        Assert.Equal(SessionStatus.Error, session.Status);
    }

    [Fact]
    public async Task ClosingCall_ToolCallsAreDropped_WhenTheProviderIgnoresToolChoice()
    {
        var provider = new ScriptedProvider(new[]
        {
            MakeToolUseReturn("tool_1", "loop_forever", new JObject()),
            MakeToolUseReturn("tool_2", "loop_forever", new JObject()), // over the cap
            MakeToolUseReturn("tool_3", "loop_forever", new JObject()), // closing reply still asks for a tool
        });
        var dispatcher = new StubDispatcher();
        var session = new ConversationSession(provider, dispatcher,
            new SessionOptions { MaxTurns = 8, MaxToolPasses = 1, ApiKey = "unused" });

        var result = await session.RunAsync("keep checking");

        Assert.Equal(1, dispatcher.DispatchCount);
        Assert.Equal(3, provider.CallCount);
        Assert.False(result.HasToolCalls);
        var uses = session.History.Where(i => i is AIInteractionToolCall and not AIInteractionToolResult).Select(i => ((AIInteractionToolCall)i).Id);
        Assert.Equal(new[] { "tool_1" }, uses);
        Assert.Equal(new[] { "tool_1" }, session.History.OfType<AIInteractionToolResult>().Select(r => r.Id));
        Assert.Equal("Tool-pass cap (1) reached without resolution.", result.ErrorMessage);
    }

    [Fact]
    public async Task ToolPassCap_WithNoTurnLeft_EndsWithoutAClosingCall()
    {
        var provider = new AlwaysToolCallProvider();
        var dispatcher = new StubDispatcher();
        var session = new ConversationSession(provider, dispatcher,
            new SessionOptions { MaxTurns = 4, MaxToolPasses = 3, ApiKey = "unused" });

        var result = await session.RunAsync("please loop forever");

        // The 4th response hits the pass cap and is the last turn the budget allows.
        Assert.Equal(3, dispatcher.DispatchCount);
        Assert.Equal(4, provider.CallCount);
        Assert.Equal(AICallStatus.Error, result.Status);
        Assert.Equal("Tool-pass cap (3) reached without resolution.", result.ErrorMessage);
        Assert.DoesNotContain(session.History.OfType<AIInteractionText>(), t => t.Content == ConversationSession.ToolPassCapNote(3));
        Assert.Equal(SessionStatus.Error, session.Status);
    }

    [Fact]
    public async Task ClosingCall_ProviderError_LeavesTheCapError()
    {
        var provider = new ScriptedProvider(new[]
        {
            MakeToolUseReturn("tool_1", "loop_forever", new JObject()),
            MakeToolUseReturn("tool_2", "loop_forever", new JObject()),
            new AIReturn
            {
                Status = AICallStatus.Error,
                ErrorMessage = "rate limited",
                Interactions = new List<IAIInteraction> { new AIInteractionText { Agent = AIAgent.Assistant, Content = "partial" } },
            },
        });
        var session = new ConversationSession(provider, new StubDispatcher(),
            new SessionOptions { MaxTurns = 8, MaxToolPasses = 1, ApiKey = "unused" });

        var result = await session.RunAsync("keep checking");

        Assert.Equal(3, provider.CallCount);
        Assert.Equal(AICallStatus.Error, result.Status);
        Assert.Equal("Tool-pass cap (1) reached without resolution.", result.ErrorMessage);
        Assert.Equal(SessionStatus.Error, session.Status);
        // Neither the failed reply nor the note it failed to answer stays in history.
        Assert.DoesNotContain(session.History.OfType<AIInteractionText>(),
            t => t.Content == "partial" || t.Content == ConversationSession.ToolPassCapNote(1));
    }

    [Fact]
    public async Task ClosingCall_UnansweredNote_IsNotInTheNextRequest()
    {
        var provider = new ScriptedProvider(new[]
        {
            MakeToolUseReturn("tool_1", "loop_forever", new JObject()),
            MakeToolUseReturn("tool_2", "loop_forever", new JObject()), // over the cap
            MakeToolUseReturn("tool_3", "loop_forever", new JObject()), // closing reply: a tool call only
            MakeTextReturn("Continuing."),
        });
        var session = new ConversationSession(provider, new StubDispatcher(),
            new SessionOptions { MaxTurns = 8, MaxToolPasses = 1, ApiKey = "unused" });

        await session.RunAsync("keep checking");
        Assert.DoesNotContain(session.History.OfType<AIInteractionText>(), t => t.Content == ConversationSession.ToolPassCapNote(1));

        var next = await session.ContinueAsync("go on");

        Assert.True(next.Success);
        Assert.DoesNotContain(provider.Requests[3].Interactions.OfType<AIInteractionText>(), t => t.Content == ConversationSession.ToolPassCapNote(1));
        Assert.Equal("go on", Assert.IsType<AIInteractionText>(provider.Requests[3].Interactions.Last()).Content);
    }

    [Fact]
    public async Task ClosingCall_ObserverThrowsOnTheNote_LeavesNoNoteForTheNextMessage()
    {
        var provider = new ScriptedProvider(new[]
        {
            MakeToolUseReturn("tool_1", "loop_forever", new JObject()),
            MakeToolUseReturn("tool_2", "loop_forever", new JObject()), // over the cap
            MakeTextReturn("Summary."),
            MakeTextReturn("ok"),
        });
        var session = new ConversationSession(provider, new StubDispatcher(),
            new SessionOptions { MaxTurns = 8, MaxToolPasses = 1, ApiKey = "unused" }, new ThrowOnNoteObserver());

        var result = await session.RunAsync("keep checking");
        Assert.Equal("Tool-pass cap (1) reached without resolution.", result.ErrorMessage);

        await session.ContinueAsync("go on");

        Assert.DoesNotContain(provider.Requests[3].Interactions.OfType<AIInteractionText>(), t => t.Content == ConversationSession.ToolPassCapNote(1));
    }

    [Fact]
    public async Task ToolPassCap_SoleAskUser_IsDropped_AndTheClosingReplyAsksInText()
    {
        var ask = MakeToolUseReturn("toolu_ask", AskUserTool.ToolName, new JObject
        {
            ["questions"] = new JArray { new JObject { ["id"] = "q1", ["prompt"] = "Units?", ["kind"] = "text" } },
        });
        var provider = new ScriptedProvider(new[]
        {
            MakeToolUseReturn("tool_1", "loop_forever", new JObject()),
            ask, // over the cap: dropped, so the loop does not pause
            MakeTextReturn("Which units should the tower use?"),
        });
        var session = new ConversationSession(provider, new StubDispatcher(),
            new SessionOptions { MaxTurns = 8, MaxToolPasses = 1, ApiKey = "unused" });

        var result = await session.RunAsync("make a tower");

        Assert.Equal(SessionStatus.Error, session.Status);
        Assert.DoesNotContain(session.History.OfType<AIInteractionToolCall>(), c => c.Id == "toolu_ask");
        Assert.Contains("If you need an answer from the user, ask for it in that paragraph.", ConversationSession.ToolPassCapNote(1), StringComparison.Ordinal);
        Assert.Equal("Which units should the tower use?", Assert.Single(result.Interactions.OfType<AIInteractionText>()).Content);
    }

    [Fact]
    public async Task ClosingCall_Throws_LeavesTheCapError_AndNoNote()
    {
        var provider = new ThrowOnCallProvider(throwAt: 3, new InvalidOperationException("socket closed"));
        var session = new ConversationSession(provider, new StubDispatcher(),
            new SessionOptions { MaxTurns = 8, MaxToolPasses = 1, ApiKey = "unused" });

        var result = await session.RunAsync("keep checking");

        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal("none", provider.Requests[2].ToolChoice);
        Assert.Equal("Tool-pass cap (1) reached without resolution.", result.ErrorMessage);
        Assert.Equal(SessionStatus.Error, session.Status);
        Assert.DoesNotContain(session.History.OfType<AIInteractionText>(), t => t.Content == ConversationSession.ToolPassCapNote(1));
    }

    [Fact]
    public async Task ClosingCall_Cancelled_EndsAsACancel_AndLeavesNoNote()
    {
        var provider = new ThrowOnCallProvider(throwAt: 3, new OperationCanceledException());
        var session = new ConversationSession(provider, new StubDispatcher(),
            new SessionOptions { MaxTurns = 8, MaxToolPasses = 1, ApiKey = "unused" });

        var result = await session.RunAsync("keep checking");

        Assert.Equal("Call cancelled or timed out", result.ErrorMessage);
        Assert.Equal(SessionStatus.Error, session.Status);
        Assert.DoesNotContain(session.History.OfType<AIInteractionText>(), t => t.Content == ConversationSession.ToolPassCapNote(1));
    }

    [Fact]
    public async Task ErrorRepairCap_OnTheLastTurn_EndsWithoutAClosingCall()
    {
        var provider = new AlwaysToolCallProvider();
        var dispatcher = new StubDispatcher(new JObject { ["success"] = false });
        var session = new ConversationSession(provider, dispatcher,
            new SessionOptions { MaxTurns = 3, MaxToolPasses = 10, ErrorRepair = new ErrorRepairPolicy(3), ApiKey = "unused" });

        var result = await session.RunAsync("keep checking");

        Assert.Equal(3, provider.CallCount);
        Assert.Equal(3, dispatcher.DispatchCount);
        Assert.StartsWith("Error-repair cap (3)", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(session.History.OfType<AIInteractionText>(), t => t.Content == ConversationSession.ErrorRepairCapNote(3));
    }

    [Fact]
    public void ErrorRepairCapNote_NamesThePassCount()
    {
        Assert.StartsWith(ConversationSession.AutomaticNotePrefix + " The last 5 tool passes all failed", ConversationSession.ErrorRepairCapNote(5), StringComparison.Ordinal);
        Assert.StartsWith(ConversationSession.AutomaticNotePrefix + " The last tool pass failed", ConversationSession.ErrorRepairCapNote(0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopsOnEndTurn_WithoutToolUse()
    {
        // Provider returns plain text on the first call, no tools.
        var provider = new ScriptedProvider(new[]
        {
            MakeTextReturn("hello there"),
        });
        var dispatcher = new StubDispatcher();
        var options = new SessionOptions
        {
            MaxTurns = 5,
            ApiKey = "unused",
        };

        var session = new ConversationSession(provider, dispatcher, options);
        var result = await session.RunAsync("hi");

        Assert.True(result.Success);
        Assert.Equal("end_turn", result.StopReason);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(0, dispatcher.DispatchCount);
    }

    [Fact]
    public async Task DispatchesTool_ThenResolvesOnSecondTurn()
    {
        // First call: provider asks for a tool. Second call: provider returns plain text.
        var provider = new ScriptedProvider(new[]
        {
            MakeToolUseReturn("tool_abc", "fake_tool", new JObject { ["x"] = 1 }),
            MakeTextReturn("done"),
        });
        var dispatcher = new StubDispatcher();
        var options = new SessionOptions
        {
            MaxTurns = 5,
            ApiKey = "unused",
        };

        var session = new ConversationSession(provider, dispatcher, options);
        var result = await session.RunAsync("use a tool");

        Assert.True(result.Success);
        Assert.Equal("end_turn", result.StopReason);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(1, dispatcher.DispatchCount);

        // History should include: user, assistant tool_use, tool_result, assistant text.
        Assert.Equal(4, session.History.Count);
        Assert.IsType<AIInteractionText>(session.History[0]); // user
        Assert.IsType<AIInteractionToolCall>(session.History[1]); // tool_use (not a result)
        Assert.IsType<AIInteractionToolResult>(session.History[2]); // tool result
        Assert.IsType<AIInteractionText>(session.History[3]); // final assistant text
    }

    [Fact]
    public async Task EmptyReply_IsNudgedOnceVisibly_ThenTheLoopContinues()
    {
        // F2: qwen3.6:27b sometimes ends with end_turn after reasoning only (no text, no tool call).
        var provider = new ScriptedProvider(new[]
        {
            MakeReasoningOnlyReturn(),
            MakeTextReturn("Here is the answer."),
        });
        var session = new ConversationSession(provider, new StubDispatcher(), new SessionOptions { MaxTurns = 5, ApiKey = "unused" });

        var result = await session.RunAsync("build it");

        Assert.True(result.Success);
        Assert.Equal(SessionStatus.Idle, session.Status);
        Assert.Equal(2, provider.CallCount);
        var nudge = Assert.Single(session.History.OfType<AIInteractionText>(),
            t => t.Content == ConversationSession.EmptyResponseNudgeText);
        Assert.Equal(AIAgent.User, nudge.Agent);
        Assert.StartsWith("[Parti automatic note]", nudge.Content, StringComparison.Ordinal);
        Assert.Contains(provider.Requests[1].Interactions.OfType<AIInteractionText>(),
            t => t.Content == ConversationSession.EmptyResponseNudgeText);
    }

    [Fact]
    public async Task EmptyReply_StillEmptyAfterTheNudge_EndsWithOneError()
    {
        var provider = new ScriptedProvider(new[] { MakeReasoningOnlyReturn() });
        var session = new ConversationSession(provider, new StubDispatcher(), new SessionOptions { MaxTurns = 5, ApiKey = "unused" });

        var result = await session.RunAsync("build it");

        Assert.Equal(AICallStatus.Error, result.Status);
        Assert.Equal(ConversationSession.EmptyResponseErrorText, result.ErrorMessage);
        Assert.Equal(SessionStatus.Error, session.Status);
        Assert.Equal(2, provider.CallCount);
        Assert.Single(session.History.OfType<AIInteractionText>(), t => t.Content == ConversationSession.EmptyResponseNudgeText);
    }

    [Fact]
    public async Task EmptyReply_WithNudgesDisabled_FailsImmediately()
    {
        var provider = new ScriptedProvider(new[] { MakeReasoningOnlyReturn(), MakeTextReturn("late") });
        var session = new ConversationSession(provider, new StubDispatcher(),
            new SessionOptions { MaxTurns = 5, ApiKey = "unused", EmptyResponseNudges = 0 });

        var result = await session.RunAsync("build it");

        Assert.Equal(AICallStatus.Error, result.Status);
        Assert.Equal(1, provider.CallCount);
        Assert.DoesNotContain(session.History.OfType<AIInteractionText>(), t => t.Content == ConversationSession.EmptyResponseNudgeText);
    }

    [Fact]
    public async Task EmptyReply_AfterAToolPass_IsNudgedToo()
    {
        var provider = new ScriptedProvider(new[]
        {
            MakeToolUseReturn("tool_1", "fake_tool", new JObject()),
            MakeReasoningOnlyReturn(),
            MakeTextReturn("Done: the tool ran."),
        });
        var dispatcher = new StubDispatcher();
        var session = new ConversationSession(provider, dispatcher, new SessionOptions { MaxTurns = 5, ApiKey = "unused" });

        var result = await session.RunAsync("use a tool");

        Assert.True(result.Success);
        Assert.Equal(3, provider.CallCount);
        Assert.Equal(1, dispatcher.DispatchCount);
    }

    [Fact]
    public void EmptyResponseNudges_DefaultsToOne()
    {
        Assert.Equal(1, new SessionOptions().EmptyResponseNudges);
    }

    [Fact]
    public async Task History_IsASnapshot_SafeToReadWhileTheLoopAppends()
    {
        // F5: the chat UI (and the live harness) read History on the Rhino UI thread while the
        // loop appends on a worker thread. Enumerating the live list during an append throws.
        var script = Enumerable.Range(0, 6)
            .Select(i => MakeToolUseReturn($"tool_{i}", "fake_tool", new JObject()))
            .Append(MakeTextReturn("done"))
            .ToList();
        var provider = new ScriptedProvider(script);
        var session = new ConversationSession(provider, new StubDispatcher(),
            new SessionOptions { MaxTurns = 10, MaxToolPasses = 10, ApiKey = "unused" });

        var before = session.History;
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested)
            {
                foreach (var interaction in session.History)
                {
                    Assert.NotNull(interaction);
                }
                reads++;
            }
            return reads;
        });

        var result = await session.RunAsync("loop through tools");
        stop.Cancel();
        await reader; // rethrows if an enumeration failed

        Assert.True(result.Success);
        Assert.Empty(before);
        Assert.Equal(1 + 6 * 2 + 1, session.History.Count);
    }

    [Fact]
    public async Task ResumeAfterNudge_RoutesOnUserPrompt()
    {
        // R14: the nudge is a user-role message; on resume the router must see the real prompt.
        var ask = MakeToolUseReturn("toolu_ask", AskUserTool.ToolName, new JObject
        {
            ["questions"] = new JArray { new JObject { ["id"] = "q1", ["prompt"] = "Units?", ["kind"] = "text" } },
        });
        var provider = new ScriptedProvider(new[] { MakeReasoningOnlyReturn(), ask, MakeTextReturn("done") });
        var router = new RecordingRouter();
        var session = new ConversationSession(provider, new StubDispatcher(), new SessionOptions { MaxTurns = 5, ApiKey = "unused" })
        {
            PromptBuilder = new SessionPromptBuilder(
                new SkillLoader(new InMemorySkillSource(new Dictionary<string, string> { ["parti-foundations/SKILL.md"] = "# L0" })),
                router),
        };

        await session.RunAsync("make a tower of floor plates");
        Assert.Equal(SessionStatus.Paused, session.Status);
        Assert.Contains(session.History.OfType<AIInteractionText>(), t => t.Content == ConversationSession.EmptyResponseNudgeText);

        await session.ResumeAsync(new[] { new ClarificationAnswer("q1", "m") });

        Assert.Equal(new[] { "make a tower of floor plates", "make a tower of floor plates" }, router.Routed);
    }

    private sealed class RecordingRouter : ISkillRouter
    {
        public List<string> Routed { get; } = new();

        public SkillRouteResult Route(string prompt)
        {
            Routed.Add(prompt);
            return new SkillRouteResult(Array.Empty<string>(), new Dictionary<string, IReadOnlyList<string>>());
        }
    }

    private static AIReturn MakeReasoningOnlyReturn()
    {
        return new AIReturn
        {
            Status = AICallStatus.Finished,
            StopReason = "end_turn",
            Interactions = new List<IAIInteraction>
            {
                new AIInteractionText { Agent = AIAgent.Assistant, Content = "", Reasoning = "How do I express an upper bound..." },
            },
        };
    }

    private static AIReturn MakeTextReturn(string content)
    {
        return new AIReturn
        {
            Status = AICallStatus.Finished,
            StopReason = "end_turn",
            Interactions = new List<IAIInteraction>
            {
                new AIInteractionText { Agent = AIAgent.Assistant, Content = content },
            },
        };
    }

    private static AIReturn MakeToolUseReturn(string id, string name, JObject args)
    {
        return new AIReturn
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

    /// <summary>
    /// Fake provider that responds to every call with a tool_use block. Each response has a unique
    /// tool-call id so the session doesn't dedupe.
    /// </summary>
    private sealed class AlwaysToolCallProvider : IAIProvider
    {
        public int CallCount { get; private set; }

        public string Name => "fake-always-tool";

        public Uri DefaultServerUrl { get; } = new("https://example.invalid");

        public AIRequestCall PreCall(AIRequestCall request) => request;

        public string CreateRequestBody(AIRequestCall request) => "{}";

        public List<IAIInteraction> Decode(JObject response) => new();

        public string Encode(IAIInteraction interaction) => "{}";

        public Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var ret = new AIReturn
            {
                Status = AICallStatus.Finished,
                StopReason = "tool_use",
                Request = request,
                Interactions = new List<IAIInteraction>
                {
                    new AIInteractionToolCall
                    {
                        Id = $"tool_{CallCount}",
                        Name = "loop_forever",
                        Arguments = new JObject(),
                        Agent = AIAgent.ToolCall,
                    },
                },
            };
            return Task.FromResult(ret);
        }
    }

    /// <summary>
    /// Fake provider that returns a pre-scripted sequence of AIReturns. If the session calls it
    /// more times than the script has entries, the last entry is returned.
    /// </summary>
    private sealed class ScriptedProvider : IAIProvider
    {
        private readonly IReadOnlyList<AIReturn> _script;

        public int CallCount { get; private set; }

        public List<AIRequestCall> Requests { get; } = new();

        public ScriptedProvider(IReadOnlyList<AIReturn> script)
        {
            _script = script;
        }

        public string Name => "fake-scripted";

        public Uri DefaultServerUrl { get; } = new("https://example.invalid");

        public AIRequestCall PreCall(AIRequestCall request) => request;

        public string CreateRequestBody(AIRequestCall request) => "{}";

        public List<IAIInteraction> Decode(JObject response) => new();

        public string Encode(IAIInteraction interaction) => "{}";

        public Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var index = Math.Min(CallCount, _script.Count - 1);
            CallCount++;
            var templated = _script[index];
            // Return a shallow clone so the session can mutate TurnIds without affecting subsequent runs.
            return Task.FromResult(new AIReturn
            {
                Status = templated.Status,
                StopReason = templated.StopReason,
                Request = request,
                Interactions = new List<IAIInteraction>(templated.Interactions),
                ErrorMessage = templated.ErrorMessage,
                InputTokens = templated.InputTokens,
                OutputTokens = templated.OutputTokens,
            });
        }
    }

    /// <summary>A host observer with a bug: it throws on every automatic note.</summary>
    private sealed class ThrowOnNoteObserver : IConversationObserver
    {
        public void OnStart(AIRequestCall request) { }

        public void OnTurnStart(string turnId, int turnNumber) { }

        public void OnInteractionCompleted(IAIInteraction interaction)
        {
            if (interaction is AIInteractionText { Content: { } content }
                && content.StartsWith(ConversationSession.AutomaticNotePrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("observer failed");
            }
        }

        public void OnToolCall(AIInteractionToolCall toolCall) { }

        public void OnToolResult(AIInteractionToolResult toolResult) { }

        public void OnFinal(AIReturn finalResult) { }

        public void OnError(Exception error) { }
    }

    /// <summary>
    /// Asks for one tool on every call, and throws <c>error</c> on call number <c>throwAt</c>.
    /// </summary>
    private sealed class ThrowOnCallProvider : IAIProvider
    {
        private readonly int _throwAt;
        private readonly Exception _error;

        public ThrowOnCallProvider(int throwAt, Exception error)
        {
            _throwAt = throwAt;
            _error = error;
        }

        public List<AIRequestCall> Requests { get; } = new();

        public string Name => "fake-throw";

        public Uri DefaultServerUrl { get; } = new("https://example.invalid");

        public AIRequestCall PreCall(AIRequestCall request) => request;

        public string CreateRequestBody(AIRequestCall request) => "{}";

        public List<IAIInteraction> Decode(JObject response) => new();

        public string Encode(IAIInteraction interaction) => "{}";

        public Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Requests.Count == _throwAt)
            {
                throw _error;
            }
            return Task.FromResult(MakeToolUseReturn($"tool_{Requests.Count}", "loop_forever", new JObject()));
        }
    }

    /// <summary>
    /// Stub dispatcher: exposes one tool, counts dispatches, and returns a canned payload (success
    /// unless the test supplies another).
    /// </summary>
    private sealed class StubDispatcher : IToolDispatcher
    {
        private readonly JObject _payload;

        public StubDispatcher(JObject? payload = null) => _payload = payload ?? new JObject { ["success"] = true };

        public int DispatchCount { get; private set; }

        private static readonly IReadOnlyList<AIToolDefinition> _defs = new[]
        {
            new AIToolDefinition(
                "loop_forever",
                "A test tool that does nothing useful.",
                new JObject { ["type"] = "object", ["properties"] = new JObject() }),
        };

        public IReadOnlyList<AIToolDefinition> GetDefinitions() => _defs;

        public Task<AIInteractionToolResult> DispatchAsync(AIInteractionToolCall call, CancellationToken ct = default)
        {
            DispatchCount++;
            var result = new AIInteractionToolResult
            {
                Id = call.Id,
                Name = call.Name,
                Agent = AIAgent.ToolResult,
                Result = (JObject)_payload.DeepClone(),
            };
            return Task.FromResult(result);
        }

        public bool HasTool(string name) => name == "loop_forever" || name == "fake_tool";
    }
}
