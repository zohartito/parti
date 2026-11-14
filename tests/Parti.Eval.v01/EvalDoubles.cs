// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Grasshopper;
using Parti.Core.Grasshopper.Chat;
using Parti.Core.Sessions;

namespace Parti.Eval.V01;

/// <summary>
/// Scripted provider for hermetic fixtures. It streams: every text block becomes a
/// <see cref="AIStreamChunkKind.TextDelta"/>, every reasoning block a
/// <see cref="AIStreamChunkKind.ReasoningDelta"/>, and the step ends with a Done chunk carrying
/// the assembled <see cref="AIReturn"/>. Entry N answers provider call N; once the script runs out
/// the last entry replays with fresh tool ids (so a replay is not mistaken for a replayed id).
/// </summary>
public sealed class ScriptedProvider : IAIProvider
{
    private readonly IReadOnlyList<FixtureProviderStep> _script;
    private int _calls;

    public ScriptedProvider(IReadOnlyList<FixtureProviderStep> script)
    {
        if (script is null || script.Count == 0)
        {
            throw new ArgumentException("A provider script needs at least one step.", nameof(script));
        }
        _script = script;
    }

    public int CallCount => Volatile.Read(ref _calls);
    public int StreamCalls { get; private set; }

    public string Name => "eval-scripted";
    public Uri DefaultServerUrl { get; } = new("https://eval.invalid");
    public AIRequestCall PreCall(AIRequestCall request) => request;
    public string CreateRequestBody(AIRequestCall request) => "{}";
    public List<IAIInteraction> Decode(JObject response) => new();
    public string Encode(IAIInteraction interaction) => "{}";

    public Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken cancellationToken = default)
    {
        var (step, index) = Next(request);
        return Task.FromResult(Final(step, index));
    }

    public async IAsyncEnumerable<AIStreamChunk> CallStreamAsync(
        AIRequestCall request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StreamCalls++;
        var (step, index) = Next(request);
        foreach (var block in step.Interactions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (block.Kind == "reasoning")
            {
                yield return new AIStreamChunk(AIStreamChunkKind.ReasoningDelta, Text: block.Text ?? string.Empty);
            }
            else if (block.Kind == "text")
            {
                yield return new AIStreamChunk(AIStreamChunkKind.TextDelta, Text: block.Text ?? string.Empty);
            }
        }
        yield return new AIStreamChunk(AIStreamChunkKind.Done, Final: Final(step, index), StopReason: step.StopReason);
    }

    private (FixtureProviderStep Step, int Index) Next(AIRequestCall request)
    {
        var index = Interlocked.Increment(ref _calls) - 1;
        return (_script[Math.Min(index, _script.Count - 1)], index);
    }

    private AIReturn Final(FixtureProviderStep step, int callIndex)
    {
        var interactions = new List<IAIInteraction>();
        var texts = step.Interactions.Where(b => b.Kind == "text").Select(b => b.Text ?? string.Empty).ToList();
        var reasoning = step.Interactions.Where(b => b.Kind == "reasoning").Select(b => b.Text ?? string.Empty).ToList();
        if (texts.Count > 0 || reasoning.Count > 0)
        {
            interactions.Add(new AIInteractionText
            {
                Agent = AIAgent.Assistant,
                Content = string.Concat(texts),
                Reasoning = reasoning.Count > 0 ? string.Concat(reasoning) : null,
            });
        }

        var stepIndex = Math.Min(callIndex, _script.Count - 1);
        var replay = callIndex >= _script.Count ? $"_r{callIndex - _script.Count + 1}" : string.Empty;
        var blockIndex = 0;
        foreach (var block in step.Interactions)
        {
            blockIndex++;
            if (block.Kind != "tool_call")
            {
                continue;
            }
            var id = (block.ToolId ?? $"toolu_{stepIndex + 1}_{blockIndex}") + replay;
            interactions.Add(new AIInteractionToolCall
            {
                Id = id,
                Name = block.ToolName ?? string.Empty,
                Arguments = (JObject)(block.Input ?? new JObject()).DeepClone(),
                Agent = AIAgent.ToolCall,
            });
        }

        var hasCalls = interactions.OfType<AIInteractionToolCall>().Any();
        return new AIReturn
        {
            Status = hasCalls ? AICallStatus.CallingTools : AICallStatus.Finished,
            StopReason = step.StopReason,
            Interactions = interactions,
        };
    }
}

/// <summary>Serializes what a provider was handed, for leak checks.</summary>
public static class RequestSnapshot
{
    public static string Of(AIRequestCall request) =>
        JsonConvert.SerializeObject(new JObject
        {
            ["system"] = request.SystemPrompt,
            ["interactions"] = Interactions(request.Interactions ?? new List<IAIInteraction>()),
        });

    public static JArray Interactions(IEnumerable<IAIInteraction> interactions) =>
        new(interactions.Select(Interaction));

    public static JObject Interaction(IAIInteraction interaction) => interaction switch
    {
        AIInteractionToolResult r => new JObject
        {
            ["kind"] = "result", ["id"] = r.Id, ["name"] = r.Name,
            ["args"] = r.Arguments?.DeepClone(), ["result"] = r.Result?.DeepClone(), ["reasoning"] = r.Reasoning,
        },
        AIInteractionToolCall c => new JObject
        {
            ["kind"] = "call", ["id"] = c.Id, ["name"] = c.Name, ["args"] = c.Arguments?.DeepClone(), ["reasoning"] = c.Reasoning,
        },
        AIInteractionText t => new JObject
        {
            ["kind"] = "text", ["agent"] = t.Agent.ToString(), ["content"] = t.Content, ["reasoning"] = t.Reasoning,
        },
        _ => new JObject { ["kind"] = interaction.GetType().Name },
    };
}

/// <summary>
/// Undo factory whose scopes carry a document lease, like the real Grasshopper scope. A lease is
/// live from <see cref="Begin"/> until its scope is disposed; <see cref="IsLive"/> is what the eval
/// canvas checks before it touches anything (S2: primitives fail closed without a live lease).
/// </summary>
public sealed class LeaseUndoScopeFactory : IUndoScopeFactory
{
    private readonly ConcurrentDictionary<string, byte> _live = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<LeaseUndoScope> _scopes = new();

    /// <summary>Every lease token ever issued (none may appear outside dispatch copies).</summary>
    public IReadOnlyList<string> IssuedTokens => _scopes.Select(s => s.DocumentLeaseToken).ToList();

    public IReadOnlyList<LeaseUndoScope> Scopes => _scopes.ToList();

    public IUndoScope Begin(string label)
    {
        var scope = new LeaseUndoScope(this, label ?? string.Empty, $"eval-lease-{Guid.NewGuid():N}");
        _live[scope.DocumentLeaseToken] = 0;
        _scopes.Enqueue(scope);
        return scope;
    }

    public bool IsLive(string? token) => token is not null && _live.ContainsKey(token);

    internal void Release(string token) => _live.TryRemove(token, out _);
}

/// <summary>Undo scope with a lease; records mutations, abandon, and disposal.</summary>
public sealed class LeaseUndoScope : IUndoScope, IGrasshopperDocumentLease
{
    private readonly LeaseUndoScopeFactory _owner;

    internal LeaseUndoScope(LeaseUndoScopeFactory owner, string label, string token)
    {
        _owner = owner;
        Label = label;
        DocumentLeaseToken = token;
    }

    public string Label { get; }
    public string DocumentLeaseToken { get; }
    public int RecordMutationCount { get; private set; }
    public bool Abandoned { get; private set; }
    public bool Disposed { get; private set; }

    public void RecordMutation(object document) => RecordMutationCount++;
    public void Abandon() => Abandoned = true;

    public void Dispose()
    {
        Disposed = true;
        _owner.Release(DocumentLeaseToken);
    }
}

/// <summary>
/// Turn-envelope factory that decorates the product's <see cref="UndoScopeTurnEnvelopeFactory"/>
/// and records what the envelope did: how many envelopes were opened, how many dispatch copies
/// received the lease, and each keep/abandon decision. The runner asserts after every run that
/// no lease token reached history, the view, the plan, or a provider request.
/// </summary>
public sealed class RecordingTurnEnvelopeFactory : ITurnEnvelopeFactory
{
    private readonly ITurnEnvelopeFactory _inner;
    private int _begun;
    private int _leasedCalls;
    private readonly ConcurrentQueue<bool> _completions = new();

    public RecordingTurnEnvelopeFactory(ITurnEnvelopeFactory inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public int Begun => Volatile.Read(ref _begun);

    /// <summary>Dispatch copies that left the envelope carrying the lease argument.</summary>
    public int LeasedCalls => Volatile.Read(ref _leasedCalls);

    /// <summary>The <c>mutationAttempted</c> flag of every completed envelope, in order.</summary>
    public IReadOnlyList<bool> Completions => _completions.ToList();

    public ITurnEnvelope Begin(string label)
    {
        Interlocked.Increment(ref _begun);
        return new Envelope(this, _inner.Begin(label));
    }

    private sealed class Envelope : ITurnEnvelope
    {
        private readonly RecordingTurnEnvelopeFactory _owner;
        private readonly ITurnEnvelope _inner;

        public Envelope(RecordingTurnEnvelopeFactory owner, ITurnEnvelope inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public string? BindingPrefix => _inner.BindingPrefix;

        public void BeforeReversibleDispatch(AIInteractionToolCall call) => _inner.BeforeReversibleDispatch(call);

        public IReadOnlyList<AIInteractionToolCall> AttachCapabilities(IReadOnlyList<AIInteractionToolCall> materialized)
        {
            var attached = _inner.AttachCapabilities(materialized);
            Interlocked.Add(ref _owner._leasedCalls, attached.Count(c => c.Arguments?.Property(EvalToolSet.LeaseArgument) is not null));
            return attached;
        }

        public void ScrubResult(AIInteractionToolResult result) => _inner.ScrubResult(result);

        public void Complete(bool mutationAttempted)
        {
            _owner._completions.Enqueue(mutationAttempted);
            _inner.Complete(mutationAttempted);
        }

        public void Dispose() => _inner.Dispose();
    }
}

/// <summary>
/// Plan reviewer that records every plan it is shown and approves (the default) or rejects.
/// Plans are captured as JSON at review time for leak checks.
/// </summary>
public sealed class RecordingReviewer
{
    private readonly bool _approve;
    private readonly ConcurrentQueue<ToolCallPlan> _plans = new();
    private readonly ConcurrentQueue<string> _snapshots = new();

    public RecordingReviewer(bool approve) => _approve = approve;

    public IReadOnlyList<ToolCallPlan> Plans => _plans.ToList();
    public IReadOnlyList<string> PlanSnapshots => _snapshots.ToList();

    public Task<bool> ReviewAsync(ToolCallPlan plan, CancellationToken ct)
    {
        _plans.Enqueue(plan);
        _snapshots.Enqueue(JsonConvert.SerializeObject(plan));
        return Task.FromResult(_approve);
    }
}

/// <summary>
/// Transparent provider decorator used for every run: forwards both entry points unchanged,
/// snapshots each request as JSON when it arrives (so later history mutation cannot hide what the
/// provider saw), and meters each call (wall time and the usage reported on the final return).
/// </summary>
public sealed class MeteredProvider : IAIProvider
{
    private readonly IAIProvider _inner;
    private readonly object _gate = new();

    public MeteredProvider(IAIProvider inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public int Calls { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long ProviderMs { get; private set; }
    public List<string> StopReasons { get; } = new();
    public List<string> RequestSnapshots { get; } = new();

    public string Name => _inner.Name;
    public Uri DefaultServerUrl => _inner.DefaultServerUrl;
    public AIRequestCall PreCall(AIRequestCall request) => _inner.PreCall(request);
    public string CreateRequestBody(AIRequestCall request) => _inner.CreateRequestBody(request);
    public List<IAIInteraction> Decode(JObject response) => _inner.Decode(response);
    public string Encode(IAIInteraction interaction) => _inner.Encode(interaction);

    public async Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken cancellationToken = default)
    {
        Snapshot(request);
        var sw = Stopwatch.StartNew();
        AIReturn? result = null;
        try
        {
            result = await _inner.CallAsync(request, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            Record(result, sw.ElapsedMilliseconds);
        }
    }

    public async IAsyncEnumerable<AIStreamChunk> CallStreamAsync(
        AIRequestCall request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Snapshot(request);
        var sw = Stopwatch.StartNew();
        AIReturn? final = null;
        try
        {
            await foreach (var chunk in _inner.CallStreamAsync(request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (chunk.Kind == AIStreamChunkKind.Done)
                {
                    final = chunk.Final;
                }
                yield return chunk;
            }
        }
        finally
        {
            // Counted even when the stream faults or is cancelled.
            Record(final, sw.ElapsedMilliseconds);
        }
    }

    private void Snapshot(AIRequestCall request)
    {
        lock (_gate)
        {
            RequestSnapshots.Add(RequestSnapshot.Of(request));
        }
    }

    private void Record(AIReturn? result, long elapsedMs)
    {
        lock (_gate)
        {
            Calls++;
            ProviderMs += elapsedMs;
            if (result is not null)
            {
                InputTokens += result.InputTokens;
                OutputTokens += result.OutputTokens;
                StopReasons.Add(result.StopReason ?? string.Empty);
            }
        }
    }
}
