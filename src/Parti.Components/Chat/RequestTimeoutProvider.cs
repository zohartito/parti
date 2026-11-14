// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;

namespace Parti.Components.Chat;

/// <summary>
/// Delegating provider that stamps a fixed <see cref="AIRequestCall.TimeoutSeconds"/> on every call.
/// The session builds requests with the 120 s default; local servers need longer to load a cold
/// model. Every other member forwards to the wrapped instance, which keeps its own reasoning
/// continuation state.
/// </summary>
public sealed class RequestTimeoutProvider : IAIProvider, IDisposable
{
    private readonly IAIProvider inner;

    /// <summary>Wraps <paramref name="inner"/> with a per-request timeout.</summary>
    public RequestTimeoutProvider(IAIProvider inner, int timeoutSeconds)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (timeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), timeoutSeconds, "Timeout must be positive.");
        }

        TimeoutSeconds = timeoutSeconds;
    }

    /// <summary>The timeout applied to every request.</summary>
    public int TimeoutSeconds { get; }

    /// <summary>The wrapped provider.</summary>
    public IAIProvider Inner => inner;

    /// <inheritdoc />
    public string Name => inner.Name;

    /// <inheritdoc />
    public Uri DefaultServerUrl => inner.DefaultServerUrl;

    /// <inheritdoc />
    public AIRequestCall PreCall(AIRequestCall request) => inner.PreCall(Stamp(request));

    /// <inheritdoc />
    public string CreateRequestBody(AIRequestCall request) => inner.CreateRequestBody(request);

    /// <inheritdoc />
    public List<IAIInteraction> Decode(JObject response) => inner.Decode(response);

    /// <inheritdoc />
    public string Encode(IAIInteraction interaction) => inner.Encode(interaction);

    /// <inheritdoc />
    public Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken cancellationToken = default) =>
        inner.CallAsync(Stamp(request), cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<AIStreamChunk> CallStreamAsync(
        AIRequestCall request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in inner.CallStreamAsync(Stamp(request), cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        (inner as IDisposable)?.Dispose();
    }

    private AIRequestCall Stamp(AIRequestCall request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        request.TimeoutSeconds = TimeoutSeconds;
        return request;
    }
}
