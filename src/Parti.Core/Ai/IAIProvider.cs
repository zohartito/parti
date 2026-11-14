// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// Ported from SmartHopper (https://github.com/architects-toolkit/SmartHopper)
// Original Copyright (c) SmartHopper contributors, LGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Parti.Core.Ai;

/// <summary>
/// Lean provider interface for Parti v0.4. A provider converts between Parti's in-memory
/// interaction model and a specific LLM vendor's HTTP protocol.
///
/// Compared to SmartHopper's IAIProvider this is stripped of: streaming, model discovery,
/// settings persistence, reflection-based tool discovery, and icon handling. Those live
/// above the provider (session / UI) in Parti.
/// </summary>
public interface IAIProvider
{
    /// <summary>
    /// Provider display name (e.g. "Anthropic").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Default base URL for this provider (e.g. "https://api.anthropic.com/v1").
    /// </summary>
    Uri DefaultServerUrl { get; }

    /// <summary>
    /// Fills provider-specific defaults on <paramref name="request"/>: HTTP method, auth scheme,
    /// endpoint, and required headers (e.g. anthropic-version). Called before every HTTP send.
    /// </summary>
    AIRequestCall PreCall(AIRequestCall request);

    /// <summary>
    /// Builds the final JSON request body that will be POSTed to the provider.
    /// Combines <see cref="AIRequestCall.Interactions"/>, <see cref="AIRequestCall.Tools"/>,
    /// <see cref="AIRequestCall.SystemPrompt"/>, and sampling parameters into the provider's wire format.
    /// </summary>
    string CreateRequestBody(AIRequestCall request);

    /// <summary>
    /// Decodes the raw JSON response from the provider into a flat list of
    /// <see cref="IAIInteraction"/>s (one text interaction plus zero or more tool_use interactions for Anthropic).
    /// </summary>
    List<IAIInteraction> Decode(JObject response);

    /// <summary>
    /// Encodes a single interaction as the provider's wire-level JSON (primarily for debugging).
    /// </summary>
    string Encode(IAIInteraction interaction);

    /// <summary>
    /// Executes a single non-streaming HTTP call and returns the decoded result.
    /// </summary>
    Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams an incremental provider response as a sequence of <see cref="AIStreamChunk"/>s.
    /// A stream yields zero or more non-terminal chunks (text and tool fragments) followed by
    /// exactly one <see cref="AIStreamChunkKind.Done"/> chunk with the cumulative
    /// <see cref="AIReturn"/>. Errors (including cancellation except for an externally-cancelled
    /// token the caller already observed) are delivered as a Done chunk with
    /// <see cref="AICallStatus.Error"/>.
    ///
    /// The default implementation falls back to <see cref="CallAsync"/> and yields a single Done
    /// chunk. Providers that support SSE (e.g. Anthropic) override this.
    /// </summary>
    async IAsyncEnumerable<AIStreamChunk> CallStreamAsync(
        AIRequestCall request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var ret = await CallAsync(request, cancellationToken).ConfigureAwait(false);
        yield return new AIStreamChunk(
            AIStreamChunkKind.Done,
            Final: ret,
            StopReason: ret.StopReason);
    }
}
