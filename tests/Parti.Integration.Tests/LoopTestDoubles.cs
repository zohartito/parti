// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Design;
using Parti.Core.Grasshopper;
using Parti.Core.Grasshopper.Chat;
using Parti.Core.Sessions;

namespace Parti.Integration.Tests;

/// <summary>
/// One scripted provider response for <see cref="StepProvider"/>: optional text and reasoning
/// deltas, tool calls, and a stop reason (or an error).
/// </summary>
internal sealed class Step
{
    public List<string> TextDeltas { get; } = new();
    public List<string> ReasoningDeltas { get; } = new();
    public List<(string Id, string Name, JObject Args)> Calls { get; } = new();
    public string StopReason { get; private set; } = "end_turn";
    public string? Error { get; private set; }
    public bool OmitDone { get; private set; }
    public Func<CancellationToken, Task>? BeforeDone { get; private set; }

    public static Step Text(params string[] deltas)
    {
        var step = new Step();
        step.TextDeltas.AddRange(deltas);
        return step;
    }

    public static Step Tools(params (string Id, string Name, JObject Args)[] calls)
    {
        var step = new Step { StopReason = "tool_use" };
        step.Calls.AddRange(calls);
        return step;
    }

    public static Step Tool(string id, string name, JObject args) => Tools((id, name, args));

    public static Step Failure(string message) => new() { Error = message, StopReason = "error" };

    public static Step NoDone() => new() { OmitDone = true };

    public Step WithReasoning(params string[] deltas)
    {
        ReasoningDeltas.AddRange(deltas);
        return this;
    }

    public Step WithText(params string[] deltas)
    {
        TextDeltas.AddRange(deltas);
        return this;
    }

    public Step WithStopReason(string stopReason)
    {
        StopReason = stopReason;
        return this;
    }

    public Step Before(Func<CancellationToken, Task> action)
    {
        BeforeDone = action;
        return this;
    }
}

/// <summary>
/// Scripted provider that serves both <see cref="CallAsync"/> and <see cref="CallStreamAsync"/>.
/// Every request is snapshotted to JSON at the moment it is received, so later history mutation
/// cannot hide what the provider actually saw. The last step replays once the script runs out.
/// </summary>
internal sealed class StepProvider : IAIProvider
{
    private readonly List<Step> _steps;
    private int _index;

    public StepProvider(params Step[] steps) => _steps = steps.ToList();

    public List<AIRequestCall> Requests { get; } = new();
    public List<string> RequestSnapshots { get; } = new();
    public int StreamCalls { get; private set; }
    public int NonStreamCalls { get; private set; }

    public string Name => "step";
    public Uri DefaultServerUrl { get; } = new("https://example.invalid");
    public AIRequestCall PreCall(AIRequestCall request) => request;
    public string CreateRequestBody(AIRequestCall request) => "{}";
    public List<IAIInteraction> Decode(JObject response) => new();
    public string Encode(IAIInteraction interaction) => "{}";

    public async Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken cancellationToken = default)
    {
        NonStreamCalls++;
        var step = Next(request);
        if (step.BeforeDone is { } before) await before(cancellationToken).ConfigureAwait(false);
        return Final(step, request);
    }

    public async IAsyncEnumerable<AIStreamChunk> CallStreamAsync(
        AIRequestCall request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StreamCalls++;
        var step = Next(request);
        foreach (var delta in step.ReasoningDeltas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AIStreamChunk(AIStreamChunkKind.ReasoningDelta, Text: delta);
        }
        foreach (var delta in step.TextDeltas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new AIStreamChunk(AIStreamChunkKind.TextDelta, Text: delta);
        }
        if (step.BeforeDone is { } before) await before(cancellationToken).ConfigureAwait(false);
        if (step.OmitDone) yield break;
        yield return new AIStreamChunk(AIStreamChunkKind.Done, Final: Final(step, request), StopReason: step.StopReason);
    }

    private Step Next(AIRequestCall request)
    {
        Requests.Add(request);
        RequestSnapshots.Add(Snapshot(request.Interactions));
        var step = _steps[Math.Min(_index, _steps.Count - 1)];
        _index++;
        return step;
    }

    private static AIReturn Final(Step step, AIRequestCall request)
    {
        if (step.Error is not null)
        {
            var error = new AIReturn();
            error.CreateError(step.Error, request);
            return error;
        }

        var interactions = new List<IAIInteraction>();
        if (step.TextDeltas.Count > 0 || step.ReasoningDeltas.Count > 0)
        {
            interactions.Add(new AIInteractionText
            {
                Agent = AIAgent.Assistant,
                Content = string.Concat(step.TextDeltas),
                Reasoning = step.ReasoningDeltas.Count > 0 ? string.Concat(step.ReasoningDeltas) : null,
            });
        }
        foreach (var (id, name, args) in step.Calls)
        {
            interactions.Add(new AIInteractionToolCall { Id = id, Name = name, Arguments = (JObject)args.DeepClone(), Agent = AIAgent.ToolCall });
        }

        return new AIReturn
        {
            Status = step.Calls.Count > 0 ? AICallStatus.CallingTools : AICallStatus.Finished,
            StopReason = step.StopReason,
            Interactions = interactions,
            InputTokens = 1,
            OutputTokens = 1,
        };
    }

    public static string Snapshot(IEnumerable<IAIInteraction> interactions) =>
        JsonConvert.SerializeObject(interactions.Select(i => i switch
        {
            AIInteractionToolResult r => new JObject { ["kind"] = "result", ["id"] = r.Id, ["name"] = r.Name, ["args"] = r.Arguments?.DeepClone(), ["result"] = r.Result?.DeepClone() },
            AIInteractionToolCall c => new JObject { ["kind"] = "call", ["id"] = c.Id, ["name"] = c.Name, ["args"] = c.Arguments?.DeepClone() },
            AIInteractionText t => new JObject { ["kind"] = "text", ["agent"] = t.Agent.ToString(), ["content"] = t.Content },
            _ => new JObject { ["kind"] = "other" },
        }));
}

/// <summary>Undo scope that records every call and optionally carries a document lease.</summary>
internal sealed class RecordingScope : IUndoScope, IGrasshopperDocumentLease
{
    public RecordingScope(string label, string lease)
    {
        Label = label;
        DocumentLeaseToken = lease;
    }

    public string Label { get; }
    public string DocumentLeaseToken { get; }
    public int RecordMutationCount { get; private set; }
    public List<object> Recorded { get; } = new();
    public bool Abandoned { get; private set; }
    public bool Disposed { get; private set; }

    public void RecordMutation(object document)
    {
        RecordMutationCount++;
        Recorded.Add(document);
    }

    public void Abandon() => Abandoned = true;
    public void Dispose() => Disposed = true;
}

internal sealed class RecordingScopeFactory : IUndoScopeFactory
{
    private readonly string _lease;

    public RecordingScopeFactory(string lease = "") => _lease = lease;

    public List<RecordingScope> Scopes { get; } = new();
    public RecordingScope? Last => Scopes.LastOrDefault();

    public IUndoScope Begin(string label)
    {
        var scope = new RecordingScope(label, _lease);
        Scopes.Add(scope);
        return scope;
    }
}

/// <summary>Envelope factory with a fixed binding prefix that records every envelope call.</summary>
internal sealed class PrefixEnvelopeFactory : ITurnEnvelopeFactory
{
    private readonly string _prefix;

    public PrefixEnvelopeFactory(string prefix) => _prefix = prefix;

    public int Begun { get; private set; }
    public List<bool> Completed { get; } = new();

    public ITurnEnvelope Begin(string label)
    {
        Begun++;
        return new Envelope(this);
    }

    private sealed class Envelope : ITurnEnvelope
    {
        private readonly PrefixEnvelopeFactory _owner;

        public Envelope(PrefixEnvelopeFactory owner) => _owner = owner;

        public string? BindingPrefix => _owner._prefix;
        public void BeforeReversibleDispatch(AIInteractionToolCall call) { }
        public IReadOnlyList<AIInteractionToolCall> AttachCapabilities(IReadOnlyList<AIInteractionToolCall> materialized) => materialized;
        public void ScrubResult(AIInteractionToolResult result) { }
        public void Complete(bool mutationAttempted) => _owner.Completed.Add(mutationAttempted);
        public void Dispose() { }
    }
}

/// <summary>Observer that snapshots everything it is handed, as JSON, at the time it is handed.</summary>
internal sealed class SnapshotObserver : IConversationObserver
{
    public List<string> Snapshots { get; } = new();
    public List<Exception> Errors { get; } = new();
    public List<AIReturn> Finals { get; } = new();
    public List<string> TextDeltas { get; } = new();
    public List<string> ReasoningDeltas { get; } = new();
    public List<PausedState> Paused { get; } = new();
    public List<(Decomposition Decomposition, int Revision)> Decompositions { get; } = new();
    public Action<AIInteractionToolCall>? OnToolCallHook { get; set; }

    public void OnStart(AIRequestCall request) => Snapshots.Add(StepProvider.Snapshot(request.Interactions));
    public void OnTurnStart(string turnId, int turnNumber) { }
    public void OnInteractionCompleted(IAIInteraction interaction) => Snapshots.Add(StepProvider.Snapshot(new[] { interaction }));

    public void OnToolCall(AIInteractionToolCall toolCall)
    {
        Snapshots.Add(StepProvider.Snapshot(new[] { toolCall }));
        OnToolCallHook?.Invoke(toolCall);
    }

    public void OnToolResult(AIInteractionToolResult toolResult) => Snapshots.Add(StepProvider.Snapshot(new[] { toolResult }));
    public void OnFinal(AIReturn finalResult) => Finals.Add(finalResult);
    public void OnError(Exception error) => Errors.Add(error);
    public void OnTextDelta(string turnId, string delta) => TextDeltas.Add(delta);
    public void OnReasoningDelta(string turnId, string delta) => ReasoningDeltas.Add(delta);
    public void OnPaused(PausedState state) => Paused.Add(state);
    public void OnDecompositionRecorded(Decomposition decomposition, int revision) => Decompositions.Add((decomposition, revision));
}

/// <summary>Chat view that records every call.</summary>
internal sealed class RecordingView : IChatView
{
    public List<string> UserMessages { get; } = new();
    public List<string> AssistantDeltas { get; } = new();
    public List<string> ReasoningDeltas { get; } = new();
    public List<string> Errors { get; } = new();
    public List<IReadOnlyList<Question>> Questions { get; } = new();
    public List<(Decomposition Decomposition, int Revision)> Decompositions { get; } = new();
    public List<bool> Busy { get; } = new();
    public bool Cleared { get; private set; }
    public Action<string>? OnDelta { get; set; }

    public void AppendUserMessage(string text) => UserMessages.Add(text);

    public void AppendAssistantText(string delta)
    {
        AssistantDeltas.Add(delta);
        OnDelta?.Invoke(delta);
    }

    public void SetBusy(bool busy) => Busy.Add(busy);
    public void ShowError(string message) => Errors.Add(message);
    public void ClearHistory() => Cleared = true;
    public void AppendReasoningText(string delta) => ReasoningDeltas.Add(delta);
    public void ShowQuestions(IReadOnlyList<Question> questions) => Questions.Add(questions);
    public void ShowDecomposition(Decomposition decomposition, int revision) => Decompositions.Add((decomposition, revision));
}

/// <summary>Shared builders for unified-loop tests.</summary>
internal static class Loop
{
    public const string Lease = "lease-token-7f3a";
    public const string SliderGuid = "57da07bd-ecab-415d-9d86-af36d7073abc";

    public static SessionOptions Options(bool requireDecomposition = false) => new()
    {
        Model = "test-model",
        MaxTurns = 8,
        MaxToolPasses = 6,
        MaxTokens = 256,
        RequireDecomposition = requireDecomposition,
    };

    public static AIToolDefinition Read(string name) =>
        new(name, name, new JObject { ["type"] = "object", ["properties"] = new JObject() });

    public static AIToolDefinition Write(string name) =>
        new(name, name, new JObject { ["type"] = "object" }) { IsReversible = true };

    public static JObject AddArgs() => new() { ["componentGuid"] = SliderGuid, ["x"] = 10, ["y"] = 20 };

    public static JObject CompleteDecomposition() => JObject.Parse(@"{
        ""inputs"":[{""name"":""panel depth"",""domain"":""0.1..0.4"",""unit"":""m"",""source"":""user_stated""}],
        ""rules"":[{""statement"":""thermal break preserved"",""source"":""user_stated""}],
        ""principle"":""staggered rainscreen with alternating course depths"",
        ""evaluations"":[{""metric"":""solar gain"",""direction"":""MINIMIZE"",""target"":40,""source"":""assumed""}],
        ""feedbackPlan"":""Ladybug annual radiation""}");

    public static JObject AskArgs(params (string Id, string Prompt, string Kind)[] questions) => new()
    {
        // A radio question must offer choices (ask_user is validated strictly), so give it two.
        ["questions"] = new JArray(questions.Select(q => q.Kind == "radio"
            ? new JObject { ["id"] = q.Id, ["prompt"] = q.Prompt, ["kind"] = q.Kind, ["options"] = new JArray("mm", "m") }
            : new JObject { ["id"] = q.Id, ["prompt"] = q.Prompt, ["kind"] = q.Kind })),
    };

    /// <summary>
    /// Asserts that each tool_use in <paramref name="history"/> has exactly one tool_result with
    /// the same id.
    /// </summary>
    public static void AssertOneResultPerToolUse(IReadOnlyList<AIInteractionBase> history)
    {
        var uses = history.OfType<AIInteractionToolCall>().Where(c => c is not AIInteractionToolResult).Select(c => c.Id).ToList();
        var results = history.OfType<AIInteractionToolResult>().Select(r => r.Id).ToList();
        Xunit.Assert.Equal(uses.OrderBy(i => i, StringComparer.Ordinal), results.OrderBy(i => i, StringComparer.Ordinal));
    }
}
