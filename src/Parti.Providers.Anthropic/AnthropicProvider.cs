// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// Ported from SmartHopper (https://github.com/architects-toolkit/SmartHopper)
// Original Copyright (c) SmartHopper contributors, LGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;

namespace Parti.Providers.Anthropic;

/// <summary>
/// Provider for Anthropic's Messages API. Translates Parti's <see cref="IAIInteraction"/> stream
/// into Anthropic's role/content_block shape and back.
///
/// Lean port of SmartHopper.Providers.Anthropic.AnthropicProvider: keeps PreCall / Encode / Decode /
/// CreateRequestBody / SortContentBlocks. v0.5 wires SSE streaming via
/// <see cref="CallStreamAsync"/>; the non-streaming <see cref="CallAsync"/> remains intact.
/// Model discovery (hardcoded IDs) and the structured-outputs beta header (disabled by default per
/// PORT-PLAN-v0.4) are still out of scope.
/// </summary>
public sealed class AnthropicProvider : IAIProvider
{
    // These are deliberately local transport budgets. Provider token limits and request timeouts
    // do not bound a malformed, fragmented, or non-success HTTP response.
    public const int MaxNonStreamingResponseBytes = 1_048_576;
    public const int MaxStreamingResponseBytes = 1_048_576;
    public const int MaxErrorResponseBytes = 16_384;
    public const int MaxSseLineCharacters = 65_536;
    public const int MaxSseEvents = 2_048;
    public const int MaxStreamTextCharacters = 524_288;
    public const int MaxToolArgumentCharacters = 262_144;

    /// <summary>
    /// Stable provider identifier.
    /// </summary>
    public const string ProviderName = "Anthropic";

    /// <summary>
    /// API version header value ("anthropic-version"). Current as of April 2026.
    /// </summary>
    public const string AnthropicVersion = "2023-06-01";

    /// <summary>
    /// Beta header value for structured outputs. Kept for v0.7 enablement; not applied by default.
    /// </summary>
    public const string StructuredOutputsBetaValue = "structured-outputs-2025-11-13";

    /// <summary>
    /// The three model IDs this port supports. Full model catalog is deferred (see PORT-PLAN-v0.4).
    /// </summary>
    public static readonly IReadOnlyList<string> KnownModels = new[]
    {
        "claude-opus-4-7",
        "claude-sonnet-4-6",
        "claude-haiku-4-5-20251001",
    };

    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;

    /// <summary>
    /// Creates a provider with a shared <see cref="HttpClient"/>. Caller retains ownership.
    /// </summary>
    public AnthropicProvider(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.ownsHttpClient = false;
    }

    /// <summary>
    /// Creates a provider with an internally-managed <see cref="HttpClient"/>. Convenient for tests and CLI harnesses.
    /// </summary>
    public AnthropicProvider()
    {
        this.httpClient = new HttpClient();
        this.ownsHttpClient = true;
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public Uri DefaultServerUrl => new("https://api.anthropic.com/v1");

    /// <inheritdoc />
    public AIRequestCall PreCall(AIRequestCall request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        request.BaseUrl ??= DefaultServerUrl;

        switch (request.Endpoint)
        {
            case "/models":
                request.HttpMethod = "GET";
                request.RequestKind = AIRequestKind.Backoffice;
                break;
            default:
                request.HttpMethod = "POST";
                request.Endpoint = "/messages";
                break;
        }

        request.ContentType = "application/json";
        request.Authentication = "x-api-key";

        if (!request.Headers.ContainsKey("anthropic-version"))
        {
            request.Headers["anthropic-version"] = AnthropicVersion;
        }

        ApplyStructuredOutputsBetaHeader(request);

        return request;
    }

    /// <inheritdoc />
    public string Encode(IAIInteraction interaction)
    {
        if (interaction == null)
        {
            return string.Empty;
        }

        try
        {
            string? role = GetRoleForAgent(interaction.Agent);
            if (string.IsNullOrEmpty(role))
            {
                return string.Empty;
            }

            var contentBlock = CreateContentBlock(interaction);
            if (contentBlock == null)
            {
                return string.Empty;
            }

            var message = new JObject
            {
                ["role"] = role,
                ["content"] = new JArray { contentBlock },
            };

            return message.ToString();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Anthropic] Encode(IAIInteraction) error: {ex.Message}");
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public string CreateRequestBody(AIRequestCall request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.HttpMethod == "GET" || request.HttpMethod == "DELETE")
        {
            return string.Empty;
        }

        int maxTokens = request.MaxTokens > 0 ? request.MaxTokens : 4096;

        Debug.WriteLine($"[Anthropic] CreateRequestBody - Model: {request.Model}, MaxTokens: {maxTokens}");

        var systemTexts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            systemTexts.Add(request.SystemPrompt!);
        }

        var messages = new JArray();
        string? currentRole = null;
        JArray? currentContentBlocks = null;

        foreach (var interaction in request.Interactions ?? Enumerable.Empty<IAIInteraction>())
        {
            try
            {
                if (interaction.Agent == AIAgent.System || interaction.Agent == AIAgent.Context)
                {
                    if (interaction is AIInteractionText sysText && !string.IsNullOrEmpty(sysText.Content))
                    {
                        systemTexts.Add(sysText.Content!);
                    }

                    continue;
                }

                string? role = GetRoleForAgent(interaction.Agent);
                if (string.IsNullOrEmpty(role))
                {
                    continue;
                }

                var contentBlock = CreateContentBlock(interaction);
                if (contentBlock == null)
                {
                    continue;
                }

                if (currentRole != role)
                {
                    if (currentRole != null && currentContentBlocks != null && currentContentBlocks.Count > 0)
                    {
                        var sortedContent = SortContentBlocks(currentContentBlocks);
                        messages.Add(new JObject
                        {
                            ["role"] = currentRole,
                            ["content"] = sortedContent,
                        });
                    }

                    currentRole = role;
                    currentContentBlocks = new JArray { contentBlock };
                }
                else
                {
                    currentContentBlocks?.Add(contentBlock);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Anthropic] Warning: could not encode interaction: {ex.Message}");
            }
        }

        if (currentRole != null && currentContentBlocks != null && currentContentBlocks.Count > 0)
        {
            var sortedContent = SortContentBlocks(currentContentBlocks);
            messages.Add(new JObject
            {
                ["role"] = currentRole,
                ["content"] = sortedContent,
            });
        }

        var requestBody = new JObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
        };

        if (request.Temperature is double t && !double.IsNaN(t) && t > 0)
        {
            requestBody["temperature"] = t;
        }

        // Structured outputs beta path. Kept for v0.7; only active when the caller has opted in.
        if (request.EnableStructuredOutputsBeta && !string.IsNullOrWhiteSpace(request.JsonOutputSchema))
        {
            try
            {
                var schemaObj = JObject.Parse(request.JsonOutputSchema);
                systemTexts.Add("The response must be a valid JSON object that strictly follows this schema: "
                                 + schemaObj.ToString(Formatting.None));
                requestBody["output_format"] = new JObject
                {
                    ["type"] = "json_schema",
                    ["schema"] = schemaObj,
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Anthropic] Failed to parse JSON schema: {ex.Message}");
            }
        }

        if (systemTexts.Count > 0)
        {
            var combined = string.Join("\n---\n", systemTexts.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (!string.IsNullOrWhiteSpace(combined))
            {
                requestBody["system"] = combined;
            }
        }

        if (request.Tools != null && request.Tools.Count > 0)
        {
            var toolsArr = new JArray();
            foreach (var tool in request.Tools)
            {
                toolsArr.Add(new JObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = tool.InputSchema ?? new JObject(),
                });
            }

            if (toolsArr.Count > 0)
            {
                requestBody["tools"] = toolsArr;
                requestBody["tool_choice"] = new JObject { ["type"] = request.ToolChoice ?? "auto" };
            }
        }

        return requestBody.ToString();
    }

    /// <inheritdoc />
    public List<IAIInteraction> Decode(JObject response)
    {
        var interactions = new List<IAIInteraction>();
        if (response == null)
        {
            return interactions;
        }

        try
        {
            var content = response["content"] as JArray;
            string contentText = string.Empty;
            var toolCalls = new List<AIInteractionToolCall>();
            var toolResults = new List<AIInteractionToolResult>();

            if (content != null)
            {
                var textParts = new List<string>();
                foreach (var block in content.OfType<JObject>())
                {
                    var type = block["type"]?.ToString();
                    if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                    {
                        var t = block["text"]?.ToString();
                        if (!string.IsNullOrEmpty(t))
                        {
                            textParts.Add(t!);
                        }
                    }
                    else if (string.Equals(type, "thinking", StringComparison.OrdinalIgnoreCase))
                    {
                        // Extended thinking content: keep out of final text but don't fail the decode.
                        // Future: surface as AIInteractionText.Reasoning on the same turn.
                    }
                    else if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
                    {
                        var toolCall = new AIInteractionToolCall
                        {
                            Id = block["id"]?.ToString(),
                            Name = block["name"]?.ToString(),
                            Arguments = block["input"] as JObject ?? new JObject(),
                            Agent = AIAgent.ToolCall,
                        };

                        Debug.WriteLine($"[Anthropic] Decoded tool_use: id={toolCall.Id}, name={toolCall.Name}");
                        toolCalls.Add(toolCall);
                    }
                    else if (string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase))
                    {
                        var tr = new AIInteractionToolResult
                        {
                            Id = block["tool_use_id"]?.ToString() ?? block["id"]?.ToString(),
                            Agent = AIAgent.ToolResult,
                        };

                        if (!string.IsNullOrEmpty(tr.Id))
                        {
                            var matchingCall = toolCalls.FirstOrDefault(c => string.Equals(c.Id, tr.Id, StringComparison.Ordinal));
                            if (matchingCall != null)
                            {
                                tr.Name = matchingCall.Name;
                                tr.Arguments = matchingCall.Arguments;
                            }
                        }

                        var resultText = ExtractToolResultText(block["content"]);
                        try
                        {
                            tr.Result = string.IsNullOrWhiteSpace(resultText)
                                ? new JObject()
                                : JObject.Parse(resultText);
                        }
                        catch
                        {
                            tr.Result = new JObject { ["value"] = resultText ?? string.Empty };
                        }

                        toolResults.Add(tr);
                    }
                }

                contentText = string.Join(string.Empty, textParts);
            }

            var textInteraction = new AIInteractionText
            {
                Agent = AIAgent.Assistant,
                Content = contentText,
            };

            interactions.Add(textInteraction);

            if (toolCalls.Count > 0)
            {
                interactions.AddRange(toolCalls.Cast<IAIInteraction>());
            }

            if (toolResults.Count > 0)
            {
                interactions.AddRange(toolResults.Cast<IAIInteraction>());
            }

            Debug.WriteLine($"[Anthropic] Decode returning {interactions.Count} interactions "
                            + $"(text chars={contentText.Length}, toolCalls={toolCalls.Count}, toolResults={toolResults.Count})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Anthropic] Decode error: {ex.Message}");
        }

        return interactions;
    }

    /// <inheritdoc />
    public async Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var ret = new AIReturn { Request = request, Status = AICallStatus.Pending };
        request = this.PreCall(request);

        var baseUrl = request.BaseUrl ?? this.DefaultServerUrl;
        var url = new Uri(baseUrl.ToString().TrimEnd('/') + request.Endpoint);

        using var httpRequest = new HttpRequestMessage(new HttpMethod(request.HttpMethod), url);

        // Auth: Anthropic uses x-api-key
        if (string.Equals(request.Authentication, "x-api-key", StringComparison.OrdinalIgnoreCase))
        {
            httpRequest.Headers.Add("x-api-key", request.ApiKey);
        }
        else if (string.Equals(request.Authentication, "bearer", StringComparison.OrdinalIgnoreCase))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        }

        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "x-api-key", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.HttpMethod != "GET" && request.HttpMethod != "DELETE")
        {
            var bodyJson = CreateRequestBody(request);
            httpRequest.Content = new StringContent(bodyJson, Encoding.UTF8, request.ContentType);
        }

        request.Headers.TryGetValue("Content-Type", out _); // touch to avoid unused warning path

        try
        {
            ret.Status = AICallStatus.Processing;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (request.TimeoutSeconds > 0)
            {
                cts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
            }

            using var response = await httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await ReadErrorBodyAsync(response.Content, cts.Token).ConfigureAwait(false);
                ret.CreateError($"HTTP {(int)response.StatusCode}: {errorBody}", request);
                return ret;
            }

            var body = await ReadBoundedBodyAsync(response.Content, MaxNonStreamingResponseBytes, cts.Token)
                .ConfigureAwait(false);

            JObject parsed;
            try
            {
                parsed = JObject.Parse(body);
            }
            catch (Exception ex)
            {
                ret.CreateError($"Failed to parse provider response: {ex.Message}", request);
                return ret;
            }

            ret.Interactions = Decode(parsed);
            ret.StopReason = parsed["stop_reason"]?.ToString();

            if (parsed["usage"] is JObject usage)
            {
                ret.InputTokens = usage["input_tokens"]?.Value<int>() ?? 0;
                ret.OutputTokens = usage["output_tokens"]?.Value<int>() ?? 0;
            }

            ret.Status = string.Equals(ret.StopReason, "tool_use", StringComparison.OrdinalIgnoreCase)
                ? AICallStatus.CallingTools
                : AICallStatus.Finished;

            return ret;
        }
        catch (OperationCanceledException)
        {
            ret.CreateError("Call cancelled or timed out", request);
            return ret;
        }
        catch (HttpRequestException ex)
        {
            ret.CreateError($"Network error: {ex.InnerException?.Message ?? ex.Message}", request);
            return ret;
        }
        catch (Exception ex)
        {
            ret.CreateError($"Provider error: {ex.InnerException?.Message ?? ex.Message}", request);
            return ret;
        }
    }

    /// <summary>
    /// Streams a Messages API response using Server-Sent Events. Yields one
    /// <see cref="AIStreamChunk"/> per incremental event and exactly one terminal
    /// <see cref="AIStreamChunkKind.Done"/> chunk with the cumulative <see cref="AIReturn"/>.
    ///
    /// Adapted from SmartHopper's AnthropicStreamingAdapter (SSE framing + per-block buffering).
    /// External cancellation is surfaced as an <see cref="OperationCanceledException"/> so callers
    /// can distinguish deliberate abort from provider errors. Other exceptions (HTTP, malformed
    /// events, decode failures) are converted to a terminal Done chunk with
    /// <see cref="AICallStatus.Error"/>.
    /// </summary>
    public async IAsyncEnumerable<AIStreamChunk> CallStreamAsync(
        AIRequestCall request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        request = this.PreCall(request);

        // Streaming only makes sense on /messages. Other endpoints (e.g. /models) fall back to
        // the non-streaming path and are surfaced as a single Done chunk.
        if (!string.Equals(request.Endpoint, "/messages", StringComparison.Ordinal))
        {
            var fallback = await CallAsync(request, cancellationToken).ConfigureAwait(false);
            yield return new AIStreamChunk(
                AIStreamChunkKind.Done,
                Final: fallback,
                StopReason: fallback.StopReason);
            yield break;
        }

        AIReturn? prepareError = null;
        HttpRequestMessage? httpRequest = null;
        CancellationTokenSource? cts = null;
        HttpResponseMessage? response = null;

        try
        {
            try
            {
                var baseUrl = request.BaseUrl ?? this.DefaultServerUrl;
                var url = new Uri(baseUrl.ToString().TrimEnd('/') + request.Endpoint);

                var bodyJson = CreateRequestBody(request);
                JObject bodyObj;
                try
                {
                    bodyObj = string.IsNullOrWhiteSpace(bodyJson) ? new JObject() : JObject.Parse(bodyJson);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to prepare streaming body: {ex.Message}", ex);
                }

                bodyObj["stream"] = true;

                httpRequest = new HttpRequestMessage(new HttpMethod(request.HttpMethod), url)
                {
                    Content = new StringContent(bodyObj.ToString(), Encoding.UTF8, request.ContentType),
                };

                if (string.Equals(request.Authentication, "x-api-key", StringComparison.OrdinalIgnoreCase))
                {
                    httpRequest.Headers.Add("x-api-key", request.ApiKey);
                }
                else if (string.Equals(request.Authentication, "bearer", StringComparison.OrdinalIgnoreCase))
                {
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
                }

                foreach (var header in request.Headers)
                {
                    if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(header.Key, "x-api-key", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(header.Key, "Accept", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                httpRequest.Headers.Accept.Clear();
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (request.TimeoutSeconds > 0)
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
                }

                response = await httpClient
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await ReadErrorBodyAsync(response.Content, cts.Token).ConfigureAwait(false);
                    var err = new AIReturn { Request = request };
                    err.CreateError($"HTTP {(int)response.StatusCode}: {body}", request);
                    prepareError = err;
                }
            }
            catch (OperationCanceledException)
            {
                // External cancellation propagates — callers expect OperationCanceledException.
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                var err = new AIReturn { Request = request };
                err.CreateError("Call timed out", request);
                prepareError = err;
            }
            catch (HttpRequestException ex)
            {
                var err = new AIReturn { Request = request };
                err.CreateError($"Network error: {ex.InnerException?.Message ?? ex.Message}", request);
                prepareError = err;
            }
            catch (Exception ex)
            {
                var err = new AIReturn { Request = request };
                err.CreateError($"Provider error: {ex.InnerException?.Message ?? ex.Message}", request);
                prepareError = err;
            }

            if (prepareError != null)
            {
                yield return new AIStreamChunk(
                    AIStreamChunkKind.Done,
                    Final: prepareError,
                    StopReason: prepareError.StopReason);
                yield break;
            }

            // At this point response is non-null and successful.
            await foreach (var chunk in ReadSseChunksAsync(response!, request, cts!.Token)
                .ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            response?.Dispose();
            httpRequest?.Dispose();
            cts?.Dispose();
        }
    }

    private async IAsyncEnumerable<AIStreamChunk> ReadSseChunksAsync(
        HttpResponseMessage response,
        AIRequestCall request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var textBuffer = new StringBuilder();
        var toolCalls = new List<AIInteractionToolCall>();
        AIInteractionToolCall? currentToolCall = null;
        var toolArgsBuffer = new StringBuilder();
        string? lastStopReason = null;
        int inputTokens = 0;
        int outputTokens = 0;
        int eventCount = 0;

        Stream? responseStream = null;
        StreamReader? reader = null;
        AIReturn? terminalError = null;

        if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaxStreamingResponseBytes)
        {
            yield return new AIStreamChunk(AIStreamChunkKind.Done, Final: CreateStreamError(request,
                $"Streaming response exceeds the {MaxStreamingResponseBytes}-byte transport budget."));
            yield break;
        }

        try
        {
            responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            responseStream = new BoundedReadStream(responseStream, MaxStreamingResponseBytes);
            reader = new StreamReader(responseStream, new UTF8Encoding(false, true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            responseStream?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            terminalError = new AIReturn { Request = request };
            terminalError.CreateError($"Failed to open SSE stream: {ex.Message}", request);
        }

        if (terminalError != null)
        {
            yield return new AIStreamChunk(AIStreamChunkKind.Done, Final: terminalError);
            yield break;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                AIReturn? readError = null;
                try
                {
                    line = await ReadSseLineAsync(reader!, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    line = null;
                    readError = new AIReturn { Request = request };
                    readError.CreateError($"Stream read error: {ex.Message}", request);
                }

                if (readError != null)
                {
                    yield return BuildFinalChunk(readError, textBuffer, toolCalls, lastStopReason, inputTokens, outputTokens, request, fromError: true);
                    yield break;
                }

                if (line == null)
                {
                    // EOF without message_stop — treat as a successful close.
                    break;
                }

                if (line.Length == 0 || line.StartsWith(":", StringComparison.Ordinal))
                {
                    // Blank line (event delimiter) or SSE comment — ignore.
                    continue;
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    // "event:" or other SSE fields — Anthropic repeats the type inside data, so we
                    // can safely ignore these.
                    continue;
                }

                var payload = line.Substring(5).TrimStart();
                if (payload == "[DONE]")
                {
                    break;
                }

                eventCount++;
                if (eventCount > MaxSseEvents)
                {
                    yield return new AIStreamChunk(AIStreamChunkKind.Done, Final: CreateStreamError(request,
                        $"Streaming response exceeded the {MaxSseEvents}-event budget."));
                    yield break;
                }

                JObject parsed;
                try
                {
                    parsed = JObject.Parse(payload);
                }
                catch
                {
                    // Malformed JSON in a single event: skip and keep streaming rather than failing
                    // the whole turn. If the very first event is malformed we still emit Done at
                    // end-of-stream below. The MalformedEventProducesErrorDone test exercises a
                    // stream where *every* event is garbage, in which case we surface Error.
                    continue;
                }

                var type = parsed["type"]?.ToString();

                if (string.Equals(type, "message_start", StringComparison.OrdinalIgnoreCase))
                {
                    if (parsed["message"] is JObject startMsg && startMsg["usage"] is JObject startUsage)
                    {
                        inputTokens = startUsage["input_tokens"]?.Value<int>() ?? inputTokens;
                        outputTokens = startUsage["output_tokens"]?.Value<int>() ?? outputTokens;
                    }
                }
                else if (string.Equals(type, "content_block_start", StringComparison.OrdinalIgnoreCase))
                {
                    var contentBlock = parsed["content_block"] as JObject;
                    var blockType = contentBlock?["type"]?.ToString();

                    if (contentBlock != null && string.Equals(blockType, "tool_use", StringComparison.OrdinalIgnoreCase))
                    {
                        currentToolCall = new AIInteractionToolCall
                        {
                            Id = contentBlock["id"]?.ToString(),
                            Name = contentBlock["name"]?.ToString(),
                            Arguments = new JObject(),
                            Agent = AIAgent.ToolCall,
                        };
                        toolArgsBuffer.Clear();

                        yield return new AIStreamChunk(
                            AIStreamChunkKind.ToolCallStart,
                            ToolName: currentToolCall.Name,
                            ToolUseId: currentToolCall.Id,
                            ToolArgsPartial: string.Empty);
                    }
                }
                else if (string.Equals(type, "content_block_delta", StringComparison.OrdinalIgnoreCase))
                {
                    var delta = parsed["delta"] as JObject;
                    var deltaType = delta?["type"]?.ToString();

                    if (string.Equals(deltaType, "text_delta", StringComparison.OrdinalIgnoreCase))
                    {
                        var t = delta?["text"]?.ToString();
                        if (!string.IsNullOrEmpty(t))
                        {
                            if (t.Length > MaxStreamTextCharacters - textBuffer.Length)
                            {
                                yield return new AIStreamChunk(AIStreamChunkKind.Done, Final: CreateStreamError(request,
                                    $"Streaming response exceeded the {MaxStreamTextCharacters}-character text budget."));
                                yield break;
                            }
                            textBuffer.Append(t);
                            yield return new AIStreamChunk(AIStreamChunkKind.TextDelta, Text: t);
                        }
                    }
                    else if (string.Equals(deltaType, "input_json_delta", StringComparison.OrdinalIgnoreCase))
                    {
                        var partial = delta?["partial_json"]?.ToString();
                        if (!string.IsNullOrEmpty(partial) && currentToolCall != null)
                        {
                            if (partial.Length > MaxToolArgumentCharacters - toolArgsBuffer.Length)
                            {
                                yield return new AIStreamChunk(AIStreamChunkKind.Done, Final: CreateStreamError(request,
                                    $"Streaming response exceeded the {MaxToolArgumentCharacters}-character tool-argument budget."));
                                yield break;
                            }
                            toolArgsBuffer.Append(partial);
                            yield return new AIStreamChunk(
                                AIStreamChunkKind.ToolArgDelta,
                                ToolName: currentToolCall.Name,
                                ToolUseId: currentToolCall.Id,
                                ToolArgsPartial: partial);
                        }
                    }
                }
                else if (string.Equals(type, "content_block_stop", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentToolCall != null)
                    {
                        var argsJson = toolArgsBuffer.ToString();
                        try
                        {
                            currentToolCall.Arguments = string.IsNullOrEmpty(argsJson)
                                ? new JObject()
                                : JObject.Parse(argsJson);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Anthropic] Failed to parse tool arguments on stream close: {ex.Message}");
                            currentToolCall.Arguments = new JObject();
                        }

                        toolCalls.Add(currentToolCall);

                        yield return new AIStreamChunk(
                            AIStreamChunkKind.ToolCallComplete,
                            ToolName: currentToolCall.Name,
                            ToolUseId: currentToolCall.Id,
                            ToolArgsPartial: argsJson);

                        currentToolCall = null;
                        toolArgsBuffer.Clear();
                    }
                }
                else if (string.Equals(type, "message_delta", StringComparison.OrdinalIgnoreCase))
                {
                    if (parsed["usage"] is JObject usage)
                    {
                        inputTokens = usage["input_tokens"]?.Value<int>() ?? inputTokens;
                        outputTokens = usage["output_tokens"]?.Value<int>() ?? outputTokens;
                    }

                    var stopReason = (parsed["delta"] as JObject)?["stop_reason"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(stopReason))
                    {
                        lastStopReason = stopReason;
                    }
                }
                else if (string.Equals(type, "message_stop", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                // "ping" and any unknown event type are intentionally ignored.
            }
        }
        finally
        {
            reader?.Dispose();
            responseStream?.Dispose();
        }

        // Honor cooperative cancellation: if the token was tripped externally, rethrow so callers
        // see an OperationCanceledException instead of a synthesized Done.
        cancellationToken.ThrowIfCancellationRequested();

        yield return BuildFinalChunk(
            finalError: null,
            textBuffer,
            toolCalls,
            lastStopReason,
            inputTokens,
            outputTokens,
            request,
            fromError: false);
    }

    private static AIReturn CreateStreamError(AIRequestCall request, string message)
    {
        var error = new AIReturn { Request = request };
        error.CreateError(message, request);
        return error;
    }

    private static async Task<string?> ReadSseLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var oneCharacter = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(oneCharacter.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return line.Length == 0 ? null : line.ToString();
            }

            var character = oneCharacter[0];
            if (character == '\n')
            {
                if (line.Length > 0 && line[^1] == '\r')
                {
                    line.Length--;
                }
                return line.ToString();
            }

            if (line.Length >= MaxSseLineCharacters)
            {
                throw new InvalidOperationException(
                    $"SSE line exceeds the {MaxSseLineCharacters}-character budget.");
            }
            line.Append(character);
        }
    }

    private static async Task<string> ReadBoundedBodyAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
        {
            throw new InvalidOperationException($"Provider response exceeds the {maxBytes}-byte transport budget.");
        }

        using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bounded = new BoundedReadStream(source, maxBytes);
        using var reader = new StreamReader(bounded, new UTF8Encoding(false, true));
        var body = new StringBuilder();
        var buffer = new char[8_192];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return body.ToString();
            }
            body.Append(buffer, 0, read);
        }
    }

    private static async Task<string> ReadErrorBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var contentLength = content.Headers.ContentLength;
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxErrorResponseBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
        }

        var truncated = contentLength is long declared && declared > MaxErrorResponseBytes;
        if (!truncated && total == buffer.Length)
        {
            var probe = new byte[1];
            truncated = await stream.ReadAsync(probe.AsMemory(), cancellationToken).ConfigureAwait(false) != 0;
        }

        var body = Encoding.UTF8.GetString(buffer, 0, total);
        return truncated ? body + " [diagnostic truncated]" : body;
    }

    /// <summary>
    /// Counts bytes accepted from an HTTP response and fails before response material can grow
    /// beyond the caller's budget. A one-byte EOF probe distinguishes an exact-size body from an
    /// over-limit body without buffering the excess payload.
    /// </summary>
    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _readBytes;

        public BoundedReadStream(Stream inner, long maxBytes)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _maxBytes = maxBytes > 0 ? maxBytes : throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, AllowedCount(count));
            ValidateRead(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await _inner.ReadAsync(buffer, offset, AllowedCount(count), cancellationToken).ConfigureAwait(false);
            ValidateRead(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer[..AllowedCount(buffer.Length)], cancellationToken).ConfigureAwait(false);
            ValidateRead(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private int AllowedCount(int requested)
        {
            if (_readBytes < _maxBytes)
            {
                return (int)Math.Min(requested, _maxBytes - _readBytes);
            }

            // Do not trust an absent Content-Length: request only one byte to tell EOF from an
            // over-limit response. That byte is never returned to a parser or buffer.
            return Math.Min(requested, 1);
        }

        private void ValidateRead(int read)
        {
            if (read == 0)
            {
                return;
            }
            if (_readBytes >= _maxBytes || read > _maxBytes - _readBytes)
            {
                throw new InvalidOperationException($"Provider response exceeds the {_maxBytes}-byte transport budget.");
            }
            _readBytes += read;
        }
    }

    private static AIStreamChunk BuildFinalChunk(
        AIReturn? finalError,
        StringBuilder textBuffer,
        List<AIInteractionToolCall> toolCalls,
        string? stopReason,
        int inputTokens,
        int outputTokens,
        AIRequestCall request,
        bool fromError)
    {
        if (fromError && finalError != null)
        {
            return new AIStreamChunk(AIStreamChunkKind.Done, Final: finalError, StopReason: stopReason);
        }

        var interactions = new List<IAIInteraction>();
        if (textBuffer.Length > 0)
        {
            interactions.Add(new AIInteractionText
            {
                Agent = AIAgent.Assistant,
                Content = textBuffer.ToString(),
            });
        }

        foreach (var call in toolCalls)
        {
            interactions.Add(call);
        }

        var anyContent = interactions.Count > 0;
        AIReturn final;

        if (!anyContent && stopReason == null)
        {
            // Nothing parseable came through — surface as Error so callers can distinguish
            // an empty-but-successful stream from a bad one.
            final = new AIReturn { Request = request };
            final.CreateError("Streaming response contained no parseable events", request);
            return new AIStreamChunk(AIStreamChunkKind.Done, Final: final);
        }

        final = new AIReturn
        {
            Request = request,
            Interactions = interactions,
            StopReason = stopReason,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Status = string.Equals(stopReason, "tool_use", StringComparison.OrdinalIgnoreCase)
                ? AICallStatus.CallingTools
                : AICallStatus.Finished,
        };

        return new AIStreamChunk(AIStreamChunkKind.Done, Final: final, StopReason: stopReason);
    }

    /// <summary>
    /// Sorts content blocks so that tool_result comes first and text comes before tool_use within
    /// a single message. Required by Anthropic: a user message that carries tool results must
    /// start with them (a session note or the next prompt merged into it goes after), and an
    /// assistant message puts its text before its tool_use blocks. The API rejects either reverse order.
    /// </summary>
    private static JArray SortContentBlocks(JArray contentBlocks)
    {
        if (contentBlocks == null || contentBlocks.Count <= 1)
        {
            return contentBlocks!;
        }

        var sorted = new JArray();
        var toolResultBlocks = new List<JToken>();
        var textBlocks = new List<JToken>();
        var toolUseBlocks = new List<JToken>();
        var otherBlocks = new List<JToken>();

        foreach (var block in contentBlocks)
        {
            if (block is JObject obj)
            {
                var type = obj["type"]?.ToString();
                if (string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase))
                {
                    toolResultBlocks.Add(block);
                }
                else if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    textBlocks.Add(block);
                }
                else if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
                {
                    toolUseBlocks.Add(block);
                }
                else
                {
                    otherBlocks.Add(block);
                }
            }
            else
            {
                otherBlocks.Add(block);
            }
        }

        foreach (var block in toolResultBlocks)
        {
            sorted.Add(block);
        }

        foreach (var block in textBlocks)
        {
            sorted.Add(block);
        }

        foreach (var block in toolUseBlocks)
        {
            sorted.Add(block);
        }

        foreach (var block in otherBlocks)
        {
            sorted.Add(block);
        }

        return sorted;
    }

    private static string? GetRoleForAgent(AIAgent agent)
    {
        return agent switch
        {
            AIAgent.System or AIAgent.Context => null,
            AIAgent.User => "user",
            AIAgent.Assistant or AIAgent.ToolCall => "assistant",
            AIAgent.ToolResult => "user",
            _ => null,
        };
    }

    private static JObject? CreateContentBlock(IAIInteraction interaction)
    {
        if (interaction == null)
        {
            return null;
        }

        switch (interaction)
        {
            case AIInteractionText textInteraction:
            {
                var text = textInteraction.Content ?? string.Empty;
                if (string.IsNullOrEmpty(text))
                {
                    return null;
                }

                return new JObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                };
            }

            case AIInteractionToolResult toolResultInteraction:
            {
                var resultText = toolResultInteraction.Result?.ToString(Formatting.None) ?? string.Empty;
                return new JObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = toolResultInteraction.Id ?? string.Empty,
                    ["content"] = resultText,
                };
            }

            case AIInteractionToolCall toolCallInteraction:
            {
                return new JObject
                {
                    ["type"] = "tool_use",
                    ["id"] = toolCallInteraction.Id ?? string.Empty,
                    ["name"] = toolCallInteraction.Name ?? string.Empty,
                    ["input"] = toolCallInteraction.Arguments ?? new JObject(),
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Recovers a tool_result's textual content whether the provider returned a bare string,
    /// an array of text blocks, or nested JSON.
    /// </summary>
    private static string ExtractToolResultText(JToken? resultContent)
    {
        if (resultContent == null)
        {
            return string.Empty;
        }

        if (resultContent is JArray rcArr)
        {
            var parts = new List<string>();
            foreach (var item in rcArr)
            {
                if (item is JObject o)
                {
                    var ttype = o["type"]?.ToString();
                    if (string.Equals(ttype, "text", StringComparison.OrdinalIgnoreCase))
                    {
                        var t = o["text"]?.ToString();
                        if (!string.IsNullOrEmpty(t))
                        {
                            parts.Add(t!);
                        }
                    }
                    else
                    {
                        parts.Add(o.ToString(Formatting.None));
                    }
                }
                else
                {
                    parts.Add(item?.ToString(Formatting.None) ?? string.Empty);
                }
            }

            return string.Join(string.Empty, parts);
        }

        return resultContent.Type == JTokenType.Object || resultContent.Type == JTokenType.Array
            ? resultContent.ToString(Formatting.None)
            : resultContent.ToString();
    }

    /// <summary>
    /// Applies the "anthropic-beta: structured-outputs-2025-11-13" header when the caller opts in
    /// and provides a schema. Per PORT-PLAN-v0.4 this stays disabled by default.
    /// </summary>
    private static void ApplyStructuredOutputsBetaHeader(AIRequestCall request)
    {
        if (!request.EnableStructuredOutputsBeta || string.IsNullOrWhiteSpace(request.JsonOutputSchema))
        {
            return;
        }

        const string betaHeaderName = "anthropic-beta";
        if (!request.Headers.TryGetValue(betaHeaderName, out var existing) || string.IsNullOrWhiteSpace(existing))
        {
            request.Headers[betaHeaderName] = StructuredOutputsBetaValue;
            return;
        }

        if (!existing.Contains(StructuredOutputsBetaValue, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers[betaHeaderName] = existing.EndsWith(",", StringComparison.Ordinal)
                ? existing + StructuredOutputsBetaValue
                : string.Concat(existing, ",", StructuredOutputsBetaValue);
        }
    }

    /// <summary>
    /// Disposes the owned <see cref="HttpClient"/> if this provider created one.
    /// </summary>
    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }
}
