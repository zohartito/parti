// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;

namespace Parti.Integration.Tests;

/// <summary>
/// Hermetic in-memory <see cref="IAIProvider"/> for integration tests. Never touches the network.
/// Can be constructed from a fixed sequence of <see cref="AIReturn"/> responses or from a custom
/// <see cref="Func{AIRequestCall, AIReturn}"/> responder for request-aware shaping.
/// </summary>
public sealed class FakeAIProvider : IAIProvider
{
    private readonly Func<AIRequestCall, AIReturn> _responder;

    /// <summary>Every request observed by <see cref="CallAsync"/>, in call order.</summary>
    public List<AIRequestCall> ReceivedRequests { get; } = new();

    /// <summary>Number of times <see cref="CallAsync"/> was invoked.</summary>
    public int CallCount { get; private set; }

    /// <summary>
    /// Constructs a fake provider that plays back <paramref name="script"/> in order. Once the
    /// script is exhausted the last entry is replayed indefinitely.
    /// </summary>
    public FakeAIProvider(IEnumerable<AIReturn> script)
    {
        if (script is null)
        {
            throw new ArgumentNullException(nameof(script));
        }

        var list = script.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("script must contain at least one AIReturn", nameof(script));
        }

        var callIndex = 0;
        _responder = _ =>
        {
            var index = Math.Min(callIndex, list.Count - 1);
            callIndex++;
            return CloneReturn(list[index]);
        };
    }

    /// <summary>
    /// Constructs a fake provider driven by a custom function. Use for request-aware responses.
    /// </summary>
    public FakeAIProvider(Func<AIRequestCall, AIReturn> responder)
    {
        _responder = responder ?? throw new ArgumentNullException(nameof(responder));
    }

    /// <inheritdoc />
    public string Name => "fake-integration";

    /// <inheritdoc />
    public Uri DefaultServerUrl { get; } = new("https://example.invalid");

    /// <inheritdoc />
    public AIRequestCall PreCall(AIRequestCall request) => request;

    /// <inheritdoc />
    public string CreateRequestBody(AIRequestCall request) => "{}";

    /// <inheritdoc />
    public List<IAIInteraction> Decode(JObject response) => new();

    /// <inheritdoc />
    public string Encode(IAIInteraction interaction) => "{}";

    /// <inheritdoc />
    public Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        ReceivedRequests.Add(request);
        var ret = _responder(request);
        ret.Request = request;
        return Task.FromResult(ret);
    }

    private static AIReturn CloneReturn(AIReturn template)
    {
        // Shallow clone so the session can mutate TurnIds without affecting the script.
        return new AIReturn
        {
            Status = template.Status,
            StopReason = template.StopReason,
            Interactions = new List<IAIInteraction>(template.Interactions),
            ErrorMessage = template.ErrorMessage,
            InputTokens = template.InputTokens,
            OutputTokens = template.OutputTokens,
        };
    }
}
