// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
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

namespace Parti.Providers.OpenAI;

/// <summary>
/// Provider for OpenAI-compatible <c>/chat/completions</c> endpoints (OpenAI, OpenRouter, Ollama's
/// <c>/v1</c>). Streams Server-Sent Events up to <c>[DONE]</c>: <c>delta.content</c> becomes a
/// <see cref="AIStreamChunkKind.TextDelta"/>; <c>delta.reasoning</c> or
/// <c>delta.reasoning_content</c> a <see cref="AIStreamChunkKind.ReasoningDelta"/>;
/// <c>delta.tool_calls[index]</c> fragments are accumulated by index and released only when the
/// stream completes normally (finish_reason <c>tool_calls</c> or <c>stop</c>, then <c>[DONE]</c>).
/// <para>
/// Reasoning (<c>reasoning</c>/<c>reasoning_content</c> text and OpenRouter
/// <c>reasoning_details</c>) is bounded continuation metadata: never visible content, and replayed
/// under its original field name only on a tool-loop continuation sent through this same provider
/// instance. Reasoning produced by another provider is never sent.
/// </para>
/// <para>
/// The bearer key is sent only to the configured endpoint, never over plain http to a
/// non-loopback host, and only the allowlisted extra headers are added.
/// </para>
/// </summary>
public sealed class OpenAICompatibleProvider : IAIProvider, IDisposable
{
    /// <summary>Lower clamp for the output token limit.</summary>
    public const int MinOutputTokens = 256;

    /// <summary>Upper clamp for the output token limit (same as the Ollama num_predict clamp).</summary>
    public const int MaxOutputTokens = 16_384;

    /// <summary>Budget for a non-streaming response body.</summary>
    public const int MaxNonStreamingResponseBytes = 1_048_576;

    /// <summary>Budget for a non-success body used as an error diagnostic.</summary>
    public const int MaxErrorResponseBytes = BoundedHttp.DefaultErrorDiagnosticBytes;

    /// <summary>Allowance for the finish, usage and error events beyond the per-token events.</summary>
    public const int TerminalEventMargin = 256;

    /// <summary>
    /// SSE <c>data:</c> event budget: twice the <see cref="MaxOutputTokens"/> clamp (reasoning and
    /// content tokens may each arrive as their own event) plus the terminal margin.
    /// </summary>
    public const int MaxStreamEvents = (2 * MaxOutputTokens) + TerminalEventMargin;

    /// <summary>Physical-line budget: every event is followed by a blank separator line, plus comments.</summary>
    public const int MaxPhysicalLines = 4 * MaxStreamEvents;

    /// <summary>Per-line budget; at least one whole tool call's arguments plus the JSON envelope.</summary>
    public const int MaxStreamLineCharacters = 300_000;

    /// <summary>Allowance per SSE event used to size <see cref="MaxStreamingResponseBytes"/>.</summary>
    public const int StreamEventByteAllowance = 480;

    /// <summary>Byte budget for one streamed response (16 MiB).</summary>
    public const int MaxStreamingResponseBytes = 16 * 1_048_576;

    /// <summary>Budget for visible assistant text in one response.</summary>
    public const int MaxStreamTextCharacters = 524_288;

    /// <summary>Budget for reasoning text in one response.</summary>
    public const int MaxReasoningCharacters = 524_288;

    /// <summary>Budget for accumulated <c>reasoning_details</c> material in one response.</summary>
    public const int MaxReasoningDetailsCharacters = 524_288;

    /// <summary>Budget for one tool call's arguments.</summary>
    public const int MaxToolArgumentCharacters = 262_144;

    /// <summary>Maximum tool calls (distinct indices) accepted in one response.</summary>
    public const int MaxToolCallsPerResponse = 64;

    /// <summary>Guidance returned when a reasoning model spends its whole budget on reasoning.</summary>
    public const string ReasoningBudgetExhaustedMessage =
        "model used its whole output budget on reasoning; raise the output token limit";

    private const int MaxToolNameCharacters = 256;
    private const int MaxToolCallIdCharacters = 256;
    private const int MaxReasoningDetailEntries = 256;
    private const int MaxInlineErrorCharacters = 2_048;

    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly OpenAICompatibleProviderOptions options;
    private readonly ContinuationStore continuation = new();

    /// <summary>
    /// Creates a provider with an internally-managed <see cref="HttpClient"/>.
    /// </summary>
    public OpenAICompatibleProvider(OpenAICompatibleProviderOptions options)
        : this(BoundedHttp.CreateProviderClient(), options, ownsClient: true)
    {
    }

    /// <summary>
    /// Creates a provider with a shared <see cref="HttpClient"/>. Caller retains ownership.
    /// </summary>
    public OpenAICompatibleProvider(HttpClient httpClient, OpenAICompatibleProviderOptions options)
        : this(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), options, ownsClient: false)
    {
    }

    private OpenAICompatibleProvider(HttpClient httpClient, OpenAICompatibleProviderOptions options, bool ownsClient)
    {
        this.httpClient = httpClient;
        this.ownsHttpClient = ownsClient;
        this.options = ValidateOptions(options);
    }

    /// <inheritdoc />
    public string Name => options.Name;

    /// <summary>
    /// The configured, validated endpoint (<see cref="OpenAICompatibleProviderOptions.BaseUrl"/>).
    /// </summary>
    public Uri DefaultServerUrl => options.BaseUrl;

    /// <summary>
    /// The configured token-limit field name.
    /// </summary>
    public string TokenLimitField => options.TokenLimitField;

    /// <summary>
    /// Clamps a requested output budget to [<see cref="MinOutputTokens"/>, <see cref="MaxOutputTokens"/>].
    /// </summary>
    public static int ClampOutputTokens(int maxTokens) => Math.Clamp(maxTokens, MinOutputTokens, MaxOutputTokens);

    /// <inheritdoc />
    public AIRequestCall PreCall(AIRequestCall request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        request.BaseUrl ??= DefaultServerUrl;
        request.HttpMethod = "POST";
        request.Endpoint = "/chat/completions";
        request.ContentType = "application/json";
        request.Authentication = "bearer";
        return request;
    }

    /// <inheritdoc />
    public string CreateRequestBody(AIRequestCall request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return BuildBody(request, stream: false).ToString(Formatting.None);
    }

    /// <inheritdoc />
    public List<IAIInteraction> Decode(JObject response)
    {
        if (response == null)
        {
            return new List<IAIInteraction>();
        }

        var accumulator = new ResponseAccumulator(streaming: false);
        if (accumulator.ApplyEvent(response, deltas: null) != null)
        {
            return new List<IAIInteraction>();
        }

        var ret = Finish(accumulator, new AIRequestCall(), toolChunks: null);
        return ret.Success ? ret.Interactions : new List<IAIInteraction>();
    }

    /// <inheritdoc />
    public string Encode(IAIInteraction interaction)
    {
        switch (interaction)
        {
            case AIInteractionText text:
                return new JObject
                {
                    ["role"] = RoleFor(text.Agent),
                    ["content"] = text.Content ?? string.Empty,
                }.ToString();
            case AIInteractionToolResult toolResult:
                return EncodeToolResult(toolResult).ToString();
            case AIInteractionToolCall toolCall:
                return new JObject
                {
                    ["role"] = "assistant",
                    ["content"] = string.Empty,
                    ["tool_calls"] = new JArray { EncodeToolCall(toolCall) },
                }.ToString();
            default:
                return string.Empty;
        }
    }

    /// <inheritdoc />
    public async Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        request = PreCall(request);
        if (!TryCreateHttpRequest(request, stream: false, out var httpRequest, out var prepareError))
        {
            return Error(request, prepareError);
        }

        try
        {
            using (httpRequest)
            using (var cts = CreateTimeout(request, cancellationToken))
            {
                using var response = await httpClient
                    .SendAsync(httpRequest!, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var diagnostic = await BoundedHttp
                        .ReadErrorDiagnosticAsync(response.Content, MaxErrorResponseBytes, cts.Token)
                        .ConfigureAwait(false);
                    return Error(request, $"HTTP {(int)response.StatusCode}: {diagnostic}");
                }

                var body = await BoundedHttp.ReadBodyAsync(response.Content, MaxNonStreamingResponseBytes, cts.Token)
                    .ConfigureAwait(false);

                JObject parsed;
                try
                {
                    parsed = JObject.Parse(body);
                }
                catch (JsonException ex)
                {
                    return Error(request, $"Failed to parse provider response: {ex.Message}");
                }

                var accumulator = new ResponseAccumulator(streaming: false);
                var applyError = accumulator.ApplyEvent(parsed, deltas: null);
                return applyError != null ? Error(request, applyError) : Finish(accumulator, request, toolChunks: null);
            }
        }
        catch (OperationCanceledException ex) when (!BoundedHttp.IsConnectTimeout(ex))
        {
            return Error(request, "Call cancelled or timed out");
        }
        catch (Exception ex)
        {
            return Error(request, DescribeTransportFailure(ex));
        }
    }

    /// <summary>
    /// Streams <c>/chat/completions</c> with <c>stream:true</c> and
    /// <c>stream_options.include_usage:true</c>. Yields text and reasoning deltas as they arrive,
    /// then — only after a normal finish and <c>[DONE]</c> — one ToolCallStart/ToolCallComplete pair
    /// per accumulated tool call, then exactly one Done. External cancellation surfaces as an
    /// <see cref="OperationCanceledException"/>; every other failure is a Done chunk with an error.
    /// <see cref="AIRequestCall.TimeoutSeconds"/> bounds the wait for response headers and each idle
    /// gap between lines, not the total generation time.
    /// </summary>
    public async IAsyncEnumerable<AIStreamChunk> CallStreamAsync(
        AIRequestCall request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        request = PreCall(request);
        if (!TryCreateHttpRequest(request, stream: true, out var httpRequest, out var prepareError))
        {
            yield return DoneChunk(Error(request, prepareError));
            yield break;
        }

        using var cts = CreateTimeout(request, cancellationToken);
        HttpResponseMessage? response = null;
        BoundedLineReader? reader = null;
        AIReturn? failure = null;
        var accumulator = new ResponseAccumulator(streaming: true);

        try
        {
            try
            {
                httpRequest!.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                response = await httpClient
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var diagnostic = await BoundedHttp
                        .ReadErrorDiagnosticAsync(response.Content, MaxErrorResponseBytes, cts.Token)
                        .ConfigureAwait(false);
                    failure = Error(request, $"HTTP {(int)response.StatusCode}: {diagnostic}");
                }
                else
                {
                    var stream = await BoundedHttp
                        .OpenBoundedStreamAsync(response.Content, MaxStreamingResponseBytes, cts.Token)
                        .ConfigureAwait(false);
                    reader = new BoundedLineReader(stream, MaxStreamLineCharacters, MaxPhysicalLines);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = Error(request, DescribeTransportFailure(ex));
            }

            if (failure != null)
            {
                yield return DoneChunk(failure);
                yield break;
            }

            var deltas = new List<AIStreamChunk>();
            while (true)
            {
                string? line;
                string? readError = null;
                try
                {
                    line = await reader!.ReadLineAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    line = null;
                    readError = DescribeTransportFailure(ex);
                }

                if (readError != null)
                {
                    yield return DoneChunk(Error(request, readError));
                    yield break;
                }

                if (line == null)
                {
                    break;
                }

                ResetIdleTimeout(cts, request);

                // Blank lines separate events; ':' lines are comments (OpenRouter keep-alives);
                // "event:", "id:" and "retry:" fields carry nothing this provider uses.
                if (line.Length == 0 || line[0] == ':' || !line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var payload = line.Substring(5).Trim();
                if (payload == "[DONE]")
                {
                    accumulator.SawDone = true;
                    break;
                }

                string? eventError;
                JObject? parsed = null;
                try
                {
                    parsed = JObject.Parse(payload);
                    eventError = null;
                }
                catch (JsonException)
                {
                    eventError = "Malformed SSE event from provider.";
                }

                if (eventError == null)
                {
                    eventError = accumulator.ApplyEvent(parsed!, deltas);
                }

                foreach (var delta in deltas)
                {
                    yield return delta;
                }

                deltas.Clear();
                if (eventError != null)
                {
                    yield return DoneChunk(Error(request, eventError));
                    yield break;
                }
            }
        }
        finally
        {
            reader?.Dispose();
            response?.Dispose();
            httpRequest?.Dispose();
        }

        cancellationToken.ThrowIfCancellationRequested();

        var toolChunks = new List<AIStreamChunk>();
        var final = Finish(accumulator, request, toolChunks);
        if (final.Success)
        {
            foreach (var chunk in toolChunks)
            {
                yield return chunk;
            }
        }

        yield return DoneChunk(final);
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

    // --- request building ------------------------------------------------------------------

    private JObject BuildBody(AIRequestCall request, bool stream)
    {
        var messages = new JArray();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new JObject { ["role"] = "system", ["content"] = request.SystemPrompt });
        }

        var interactions = request.Interactions ?? new List<IAIInteraction>();
        var lastUserIndex = -1;
        for (var i = 0; i < interactions.Count; i++)
        {
            if (interactions[i] is AIInteractionText { Agent: AIAgent.User })
            {
                lastUserIndex = i;
            }
        }

        JObject? pendingAssistant = null;
        var replayed = new HashSet<JObject>();

        void FlushAssistant()
        {
            if (pendingAssistant != null)
            {
                messages.Add(pendingAssistant);
                pendingAssistant = null;
            }
        }

        void ReplayReasoning(IAIInteraction carrier, int index)
        {
            if (index <= lastUserIndex || pendingAssistant == null || replayed.Contains(pendingAssistant))
            {
                return;
            }

            var metadata = continuation.Lookup(carrier);
            if (metadata == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(metadata.Reasoning))
            {
                pendingAssistant[metadata.ReasoningField] = metadata.Reasoning;
            }

            if (metadata.ReasoningDetails != null)
            {
                pendingAssistant["reasoning_details"] = metadata.ReasoningDetails.DeepClone();
            }

            replayed.Add(pendingAssistant);
        }

        for (var index = 0; index < interactions.Count; index++)
        {
            switch (interactions[index])
            {
                case AIInteractionText text when text.Agent == AIAgent.System || text.Agent == AIAgent.Context:
                    FlushAssistant();
                    if (!string.IsNullOrEmpty(text.Content))
                    {
                        messages.Add(new JObject { ["role"] = "system", ["content"] = text.Content });
                    }

                    break;

                case AIInteractionText text when text.Agent == AIAgent.User:
                    FlushAssistant();
                    messages.Add(new JObject { ["role"] = "user", ["content"] = text.Content ?? string.Empty });
                    break;

                case AIInteractionText text when text.Agent == AIAgent.Assistant:
                    FlushAssistant();
                    pendingAssistant = new JObject { ["role"] = "assistant", ["content"] = text.Content ?? string.Empty };
                    ReplayReasoning(text, index);
                    break;

                case AIInteractionToolResult toolResult:
                    FlushAssistant();
                    messages.Add(EncodeToolResult(toolResult));
                    break;

                case AIInteractionToolCall toolCall:
                    pendingAssistant ??= new JObject { ["role"] = "assistant", ["content"] = string.Empty };
                    if (pendingAssistant["tool_calls"] is not JArray calls)
                    {
                        calls = new JArray();
                        pendingAssistant["tool_calls"] = calls;
                    }

                    calls.Add(EncodeToolCall(toolCall));
                    ReplayReasoning(toolCall, index);
                    break;
            }
        }

        FlushAssistant();

        var body = new JObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = stream,
            [options.TokenLimitField] = ClampOutputTokens(request.MaxTokens),
        };

        if (stream)
        {
            body["stream_options"] = new JObject { ["include_usage"] = true };
        }

        if (request.Temperature is double t && !double.IsNaN(t))
        {
            body["temperature"] = t;
        }

        if (request.Tools != null && request.Tools.Count > 0)
        {
            var tools = new JArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = tool.InputSchema ?? new JObject(),
                    },
                });
            }

            body["tools"] = tools;
            body["tool_choice"] = request.ToolChoice switch
            {
                null or "" or "auto" => "auto",
                "none" => "none",
                "any" => "required",
                var name => new JObject { ["type"] = "function", ["function"] = new JObject { ["name"] = name } },
            };
        }

        return body;
    }

    private static JObject EncodeToolCall(AIInteractionToolCall toolCall)
    {
        return new JObject
        {
            ["id"] = toolCall.Id ?? string.Empty,
            ["type"] = "function",
            ["function"] = new JObject
            {
                ["name"] = toolCall.Name ?? string.Empty,
                ["arguments"] = (toolCall.Arguments ?? new JObject()).ToString(Formatting.None),
            },
        };
    }

    private static JObject EncodeToolResult(AIInteractionToolResult toolResult)
    {
        return new JObject
        {
            ["role"] = "tool",
            ["tool_call_id"] = toolResult.Id ?? string.Empty,
            ["content"] = toolResult.Result?.ToString(Formatting.None) ?? "{}",
        };
    }

    private static string RoleFor(AIAgent agent) => agent switch
    {
        AIAgent.System or AIAgent.Context => "system",
        AIAgent.Assistant => "assistant",
        _ => "user",
    };

    private bool TryCreateHttpRequest(AIRequestCall request, bool stream, out HttpRequestMessage? httpRequest, out string error)
    {
        httpRequest = null;
        var requested = request.BaseUrl ?? options.BaseUrl;
        if (!BoundedHttp.TryValidateEndpoint(requested, out error))
        {
            error = $"Invalid {options.Name} endpoint: {error}";
            return false;
        }

        if (!BoundedHttp.IsSameEndpoint(requested, options.BaseUrl))
        {
            error = $"Request endpoint does not match the configured {options.Name} endpoint.";
            return false;
        }

        var apiKey = ResolveApiKey(request);
        if (!string.IsNullOrEmpty(apiKey)
            && !string.Equals(options.BaseUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !BoundedHttp.IsLoopback(options.BaseUrl))
        {
            error = $"Refusing to send an API key over plain http to {options.BaseUrl.Host}; use https.";
            return false;
        }

        var message = new HttpRequestMessage(HttpMethod.Post, BoundedHttp.Combine(options.BaseUrl, "/chat/completions"))
        {
            Content = new StringContent(BuildBody(request, stream).ToString(Formatting.None), Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrEmpty(apiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        // Only the validated allowlisted headers are added; request.Headers is not forwarded.
        foreach (var header in options.ExtraHeaders)
        {
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        httpRequest = message;
        error = string.Empty;
        return true;
    }

    private string? ResolveApiKey(AIRequestCall request)
    {
        if (!string.IsNullOrEmpty(request.ApiKey))
        {
            return request.ApiKey;
        }

        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            return options.ApiKey;
        }

        return string.IsNullOrWhiteSpace(options.ApiKeyEnvironmentVariable)
            ? null
            : Environment.GetEnvironmentVariable(options.ApiKeyEnvironmentVariable!);
    }

    private static OpenAICompatibleProviderOptions ValidateOptions(OpenAICompatibleProviderOptions? options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var copy = options.Clone();
        if (!BoundedHttp.TryValidateEndpoint(copy.BaseUrl, out var error))
        {
            throw new ArgumentException($"Invalid endpoint: {error}", nameof(options));
        }

        if (copy.TokenLimitField is not (OpenAICompatibleProviderOptions.MaxCompletionTokensField
            or OpenAICompatibleProviderOptions.MaxTokensField))
        {
            throw new ArgumentException(
                $"TokenLimitField must be \"{OpenAICompatibleProviderOptions.MaxCompletionTokensField}\" or \"{OpenAICompatibleProviderOptions.MaxTokensField}\".",
                nameof(options));
        }

        foreach (var header in copy.ExtraHeaders)
        {
            if (!OpenAICompatibleProviderOptions.AllowedExtraHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Extra header \"{header.Key}\" is not allowed.", nameof(options));
            }

            if (header.Value == null || header.Value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            {
                throw new ArgumentException($"Extra header \"{header.Key}\" has an invalid value.", nameof(options));
            }
        }

        if (string.IsNullOrWhiteSpace(copy.Name))
        {
            copy.Name = "OpenAI-compatible";
        }

        return copy;
    }

    private static CancellationTokenSource CreateTimeout(AIRequestCall request, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ResetIdleTimeout(cts, request);
        return cts;
    }

    private static void ResetIdleTimeout(CancellationTokenSource cts, AIRequestCall request)
    {
        if (request.TimeoutSeconds > 0)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        }
    }

    private static string DescribeTransportFailure(Exception ex)
    {
        if (BoundedHttp.IsConnectTimeout(ex))
        {
            return BoundedHttp.ConnectTimeoutMessage;
        }

        return ex switch
        {
            OperationCanceledException => "Call timed out",
            BoundedHttpException bounded => bounded.Message,
            DecoderFallbackException => "Provider stream is not valid UTF-8.",
            HttpRequestException http => $"Network error: {http.InnerException?.Message ?? http.Message}",
            _ => $"Provider error: {ex.InnerException?.Message ?? ex.Message}",
        };
    }

    // --- response finalization ------------------------------------------------------------

    private AIReturn Finish(ResponseAccumulator accumulator, AIRequestCall request, List<AIStreamChunk>? toolChunks)
    {
        var hasToolCalls = accumulator.ToolCalls.Count > 0;
        if (accumulator.Streaming && !accumulator.SawDone)
        {
            return Error(request, hasToolCalls
                ? "Provider stream ended before [DONE]; no tool calls were run."
                : "Provider stream ended before [DONE].");
        }

        var finish = accumulator.FinishReason;
        if (finish == null)
        {
            return Error(request, hasToolCalls
                ? "Provider did not report a finish_reason; no tool calls were run."
                : "Provider did not report a finish_reason.");
        }

        var content = accumulator.Content.ToString();
        var reasoning = accumulator.Reasoning.ToString();
        var isLength = string.Equals(finish, "length", StringComparison.Ordinal);

        if (isLength)
        {
            if (hasToolCalls)
            {
                return Error(request,
                    "Provider stopped at the output limit while emitting tool calls; no tool calls were run.");
            }

            if (content.Length == 0)
            {
                return Error(request, reasoning.Length > 0
                    ? ReasoningBudgetExhaustedMessage
                    : "Provider stopped at the output limit before producing any output.");
            }
        }
        else if (string.Equals(finish, "content_filter", StringComparison.Ordinal))
        {
            return Error(request, "Provider stopped the response (content_filter); no tool calls were run.");
        }
        else if (hasToolCalls && finish is not ("tool_calls" or "stop"))
        {
            return Error(request, $"Provider finished with \"{finish}\"; no tool calls were run.");
        }

        var ret = new AIReturn
        {
            Request = request,
            InputTokens = accumulator.InputTokens,
            OutputTokens = accumulator.OutputTokens,
        };

        var calls = new List<AIInteractionToolCall>();
        foreach (var builder in accumulator.ToolCalls.Values)
        {
            if (string.IsNullOrWhiteSpace(builder.Name))
            {
                return Error(request, "Provider returned a tool call without a name; no tool calls were run.");
            }

            JObject arguments;
            var raw = builder.Arguments.ToString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                arguments = new JObject();
            }
            else
            {
                try
                {
                    arguments = JObject.Parse(raw);
                }
                catch (JsonException)
                {
                    return Error(request,
                        $"Tool call \"{builder.Name}\" has arguments that are not a JSON object; no tool calls were run.");
                }
            }

            calls.Add(new AIInteractionToolCall
            {
                Id = string.IsNullOrEmpty(builder.Id) ? $"call_{Guid.NewGuid():N}" : builder.Id,
                Name = builder.Name,
                Arguments = arguments,
                Agent = AIAgent.ToolCall,
            });
        }

        IAIInteraction? carrier = null;
        if (content.Length > 0)
        {
            var text = new AIInteractionText { Agent = AIAgent.Assistant, Content = content };
            ret.Interactions.Add(text);
            carrier = text;
        }

        foreach (var call in calls)
        {
            ret.Interactions.Add(call);
            carrier ??= call;
            toolChunks?.Add(new AIStreamChunk(AIStreamChunkKind.ToolCallStart, ToolName: call.Name, ToolUseId: call.Id, ToolArgsPartial: string.Empty));
            toolChunks?.Add(new AIStreamChunk(AIStreamChunkKind.ToolCallComplete, ToolName: call.Name, ToolUseId: call.Id, ToolArgsPartial: call.Arguments.ToString(Formatting.None)));
        }

        var details = accumulator.BuildReasoningDetails();
        if (carrier != null && (reasoning.Length > 0 || details != null))
        {
            if (reasoning.Length > 0)
            {
                switch (carrier)
                {
                    case AIInteractionText text:
                        text.Reasoning = reasoning;
                        break;
                    case AIInteractionToolCall call:
                        call.Reasoning = reasoning;
                        break;
                }
            }

            continuation.Register(carrier, new ContinuationMetadata(
                reasoning.Length > 0 ? reasoning : null,
                accumulator.ReasoningField ?? "reasoning",
                details));
        }

        ret.StopReason = hasToolCalls ? "tool_use" : isLength ? "max_tokens" : "end_turn";
        ret.Status = hasToolCalls ? AICallStatus.CallingTools : AICallStatus.Finished;
        return ret;
    }

    private static AIReturn Error(AIRequestCall request, string message)
    {
        var ret = new AIReturn { Request = request };
        ret.CreateError(message, request);
        return ret;
    }

    private static AIStreamChunk DoneChunk(AIReturn final) => new(AIStreamChunkKind.Done, Final: final, StopReason: final.StopReason);

    private static string? AsString(JToken? token) => token?.Type == JTokenType.String ? (string?)token : null;

    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }

    /// <summary>
    /// Accumulates one response (SSE events or a single non-streaming object) under the provider's
    /// budgets. Methods return an error string instead of throwing.
    /// </summary>
    private sealed class ResponseAccumulator
    {
        private readonly List<JObject> reasoningDetails = new();
        private readonly Dictionary<long, JObject> reasoningDetailsByIndex = new();
        private int reasoningDetailCharacters;

        public ResponseAccumulator(bool streaming)
        {
            Streaming = streaming;
        }

        public bool Streaming { get; }

        public bool SawDone { get; set; }

        public StringBuilder Content { get; } = new();

        public StringBuilder Reasoning { get; } = new();

        public string? ReasoningField { get; private set; }

        public SortedDictionary<int, ToolCallBuilder> ToolCalls { get; } = new();

        public string? FinishReason { get; private set; }

        public int InputTokens { get; private set; }

        public int OutputTokens { get; private set; }

        public int Events { get; private set; }

        public string? ApplyEvent(JObject evt, List<AIStreamChunk>? deltas)
        {
            Events++;
            if (Events > MaxStreamEvents)
            {
                return $"Streaming response exceeded the {MaxStreamEvents}-event budget.";
            }

            if (evt["error"] is JToken errorToken && errorToken.Type != JTokenType.Null)
            {
                var message = errorToken is JObject errorObj
                    ? AsString(errorObj["message"]) ?? errorObj.ToString(Formatting.None)
                    : AsString(errorToken) ?? errorToken.ToString(Formatting.None);
                if (message.Length > MaxInlineErrorCharacters)
                {
                    message = message.Substring(0, MaxInlineErrorCharacters) + BoundedHttp.DiagnosticTruncatedMarker;
                }

                return $"Provider error: {message}";
            }

            if (evt["usage"] is JObject usage)
            {
                InputTokens = ReadCount(usage["prompt_tokens"]);
                OutputTokens = ReadCount(usage["completion_tokens"]);
            }

            if (evt["choices"] is JArray choices)
            {
                foreach (var choice in choices.OfType<JObject>())
                {
                    // Only one completion is requested; ignore any other choice index.
                    if (choice["index"]?.Type == JTokenType.Integer && (long)choice["index"]! != 0)
                    {
                        continue;
                    }

                    var body = Streaming ? choice["delta"] : choice["message"];
                    var finish = AsString(choice["finish_reason"]);

                    // Termination is sticky: once a finish_reason arrived, the choice may not carry
                    // more data or a different reason (a later "tool_calls" must never overwrite an
                    // earlier "length"). Usage-only chunks and empty deltas are still accepted.
                    if (FinishReason != null)
                    {
                        if (!string.IsNullOrEmpty(finish) && !string.Equals(finish, FinishReason, StringComparison.Ordinal))
                        {
                            return "Provider sent a conflicting finish_reason; no tool calls were run.";
                        }

                        if (body is JObject late && HasChoiceData(late))
                        {
                            return "Provider sent data after finish_reason; no tool calls were run.";
                        }

                        continue;
                    }

                    if (body is JObject bodyObj)
                    {
                        var error = ApplyDelta(bodyObj, deltas);
                        if (error != null)
                        {
                            return error;
                        }
                    }

                    if (!string.IsNullOrEmpty(finish))
                    {
                        FinishReason = finish;
                    }
                }
            }

            return null;
        }

        public JArray? BuildReasoningDetails()
        {
            return reasoningDetails.Count == 0 ? null : new JArray(reasoningDetails.Select(d => d.DeepClone()));
        }

        private static bool HasChoiceData(JObject delta)
        {
            // Any non-empty value counts, whatever its type: a malformed shape after the finish is
            // rejected too rather than silently ignored.
            return new[] { "content", "reasoning", "reasoning_content", "reasoning_details", "tool_calls" }
                .Any(field => IsNonEmpty(delta[field]));
        }

        private static bool IsNonEmpty(JToken? token) => token switch
        {
            null => false,
            JValue { Type: JTokenType.Null or JTokenType.Undefined } => false,
            JValue { Type: JTokenType.String } value => !string.IsNullOrEmpty((string?)value),
            JArray array => array.Count > 0,
            JObject obj => obj.Count > 0,
            _ => true,
        };

        private string? ApplyDelta(JObject delta, List<AIStreamChunk>? deltas)
        {
            var content = AsString(delta["content"]);
            if (!string.IsNullOrEmpty(content))
            {
                if (content!.Length > MaxStreamTextCharacters - Content.Length)
                {
                    return $"Streaming response exceeded the {MaxStreamTextCharacters}-character text budget.";
                }

                Content.Append(content);
                deltas?.Add(new AIStreamChunk(AIStreamChunkKind.TextDelta, Text: content));
            }

            string? reasoningField = null;
            var reasoning = AsString(delta["reasoning"]);
            if (!string.IsNullOrEmpty(reasoning))
            {
                reasoningField = "reasoning";
            }
            else
            {
                reasoning = AsString(delta["reasoning_content"]);
                if (!string.IsNullOrEmpty(reasoning))
                {
                    reasoningField = "reasoning_content";
                }
            }

            if (reasoningField != null)
            {
                if (reasoning!.Length > MaxReasoningCharacters - Reasoning.Length)
                {
                    return $"Streaming response exceeded the {MaxReasoningCharacters}-character reasoning budget.";
                }

                ReasoningField ??= reasoningField;
                Reasoning.Append(reasoning);
                deltas?.Add(new AIStreamChunk(AIStreamChunkKind.ReasoningDelta, Text: reasoning));
            }

            if (delta["reasoning_details"] is JArray details)
            {
                var error = MergeReasoningDetails(details);
                if (error != null)
                {
                    return error;
                }
            }

            if (delta["tool_calls"] is JArray toolCalls)
            {
                var position = 0;
                foreach (var toolCall in toolCalls)
                {
                    var error = ApplyToolCallDelta(toolCall, position++);
                    if (error != null)
                    {
                        return error;
                    }
                }
            }
            else if (delta["tool_calls"] is JToken other && other.Type != JTokenType.Null)
            {
                return "Malformed tool_calls from provider.";
            }

            return null;
        }

        private string? ApplyToolCallDelta(JToken token, int position)
        {
            if (token is not JObject call)
            {
                return "Malformed tool call from provider.";
            }

            long index = position;
            if (call["index"] is JToken indexToken && indexToken.Type != JTokenType.Null)
            {
                if (indexToken.Type != JTokenType.Integer)
                {
                    return "Tool call index is not an integer.";
                }

                index = (long)indexToken;
            }

            if (index < 0 || index >= MaxToolCallsPerResponse)
            {
                return $"Tool call index {index} is outside the {MaxToolCallsPerResponse}-call budget.";
            }

            if (!ToolCalls.TryGetValue((int)index, out var builder))
            {
                builder = new ToolCallBuilder();
                ToolCalls[(int)index] = builder;
            }

            var id = AsString(call["id"]);
            if (!string.IsNullOrEmpty(id))
            {
                if (id!.Length > MaxToolCallIdCharacters)
                {
                    return "Tool call id is too long.";
                }

                if (builder.Id != null && !string.Equals(builder.Id, id, StringComparison.Ordinal))
                {
                    return $"Tool call index {index} changed its id mid-stream.";
                }

                builder.Id = id;
            }

            if (call["function"] is JObject function)
            {
                var name = AsString(function["name"]);
                if (!string.IsNullOrEmpty(name))
                {
                    if (name!.Length > MaxToolNameCharacters)
                    {
                        return "Tool call name is too long.";
                    }

                    if (builder.Name != null && !string.Equals(builder.Name, name, StringComparison.Ordinal))
                    {
                        return $"Tool call index {index} changed its name mid-stream.";
                    }

                    builder.Name = name;
                }

                var argsToken = function["arguments"];
                string? fragment;
                if (argsToken == null || argsToken.Type == JTokenType.Null)
                {
                    fragment = null;
                }
                else if (argsToken.Type == JTokenType.String)
                {
                    fragment = (string?)argsToken;
                }
                else if (argsToken is JObject obj && builder.Arguments.Length == 0)
                {
                    // Some compatible servers send a whole object instead of a JSON string.
                    fragment = obj.ToString(Formatting.None);
                }
                else
                {
                    return $"Tool call index {index} has malformed arguments.";
                }

                if (!string.IsNullOrEmpty(fragment))
                {
                    if (fragment!.Length > MaxToolArgumentCharacters - builder.Arguments.Length)
                    {
                        return $"Tool call arguments exceeded the {MaxToolArgumentCharacters}-character budget.";
                    }

                    builder.Arguments.Append(fragment);
                }
            }

            return null;
        }

        private string? MergeReasoningDetails(JArray details)
        {
            foreach (var token in details)
            {
                if (token is not JObject detail)
                {
                    return "Malformed reasoning_details from provider.";
                }

                var size = detail.ToString(Formatting.None).Length;
                if (size > MaxReasoningDetailsCharacters - reasoningDetailCharacters)
                {
                    return $"Streaming response exceeded the {MaxReasoningDetailsCharacters}-character reasoning_details budget.";
                }

                reasoningDetailCharacters += size;

                if (Streaming && detail["index"]?.Type == JTokenType.Integer
                    && reasoningDetailsByIndex.TryGetValue((long)detail["index"]!, out var existing))
                {
                    // Streamed fragments of one detail: concatenate the text-bearing fields, keep
                    // the latest value of every other field.
                    foreach (var property in detail.Properties())
                    {
                        if (property.Name is "text" or "summary" or "data" or "signature"
                            && property.Value.Type == JTokenType.String
                            && existing[property.Name]?.Type == JTokenType.String)
                        {
                            existing[property.Name] = (string)existing[property.Name]! + (string)property.Value!;
                        }
                        else if (property.Value.Type != JTokenType.Null)
                        {
                            existing[property.Name] = property.Value.DeepClone();
                        }
                    }

                    continue;
                }

                if (reasoningDetails.Count >= MaxReasoningDetailEntries)
                {
                    return $"Provider returned more than {MaxReasoningDetailEntries} reasoning_details entries.";
                }

                var copy = (JObject)detail.DeepClone();
                reasoningDetails.Add(copy);
                if (Streaming && detail["index"]?.Type == JTokenType.Integer)
                {
                    reasoningDetailsByIndex[(long)detail["index"]!] = copy;
                }
            }

            return null;
        }

        private static int ReadCount(JToken? token)
        {
            if (token?.Type != JTokenType.Integer)
            {
                return 0;
            }

            var value = (long)token;
            return value < 0 ? 0 : (int)Math.Min(value, int.MaxValue);
        }
    }

    private sealed record ContinuationMetadata(string? Reasoning, string ReasoningField, JArray? ReasoningDetails);

    /// <summary>
    /// Continuation metadata produced by this provider instance, keyed by the carrying interaction
    /// and by the reasoning string instance (so a shallow copy that keeps the same string still
    /// resolves). Weak keys: entries die with the conversation history.
    /// </summary>
    private sealed class ContinuationStore
    {
        private readonly ConditionalWeakTable<IAIInteraction, ContinuationMetadata> byCarrier = new();
        private readonly ConditionalWeakTable<string, ContinuationMetadata> byReasoning = new();

        public void Register(IAIInteraction carrier, ContinuationMetadata metadata)
        {
            byCarrier.AddOrUpdate(carrier, metadata);
            if (metadata.Reasoning != null)
            {
                byReasoning.AddOrUpdate(metadata.Reasoning, metadata);
            }
        }

        public ContinuationMetadata? Lookup(IAIInteraction interaction)
        {
            if (byCarrier.TryGetValue(interaction, out var metadata))
            {
                return metadata;
            }

            var candidate = interaction switch
            {
                AIInteractionText text => text.Reasoning,
                AIInteractionToolCall call => call.Reasoning,
                _ => null,
            };

            return candidate != null && byReasoning.TryGetValue(candidate, out metadata) ? metadata : null;
        }
    }
}
