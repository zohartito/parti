// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Diagnostics;
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

namespace Parti.Providers.Ollama;

/// <summary>
/// Provider for an Ollama server's native <c>/api/chat</c> endpoint. Streams NDJSON: each
/// <c>message.content</c> fragment becomes a <see cref="AIStreamChunkKind.TextDelta"/>, each
/// <c>message.thinking</c> fragment a <see cref="AIStreamChunkKind.ReasoningDelta"/>, and tool calls
/// are buffered and released only when the stream completes normally (atomic batch finalization).
/// <para>
/// Reasoning is never folded into visible content. It is kept as bounded continuation metadata
/// on the assistant interaction and replayed as <c>thinking</c> only on a tool-loop continuation
/// sent through this same provider instance, as Ollama's tool-calling documentation requires.
/// Reasoning produced by any other provider is never sent.
/// </para>
/// <para>
/// All response material is bounded (see the <c>Max*</c> constants) and every budget fails closed
/// with an error; none truncates.
/// </para>
/// </summary>
public sealed class OllamaProvider : IAIProvider, IDisposable
{
    /// <summary>
    /// Stable provider identifier.
    /// </summary>
    public const string ProviderName = "Ollama";

    /// <summary>Lower clamp for <c>options.num_predict</c>.</summary>
    public const int MinNumPredict = 256;

    /// <summary>Upper clamp for <c>options.num_predict</c>.</summary>
    public const int MaxNumPredict = 16_384;

    /// <summary>Budget for a non-streaming response body.</summary>
    public const int MaxNonStreamingResponseBytes = 1_048_576;

    /// <summary>Budget for a non-success body used as an error diagnostic.</summary>
    public const int MaxErrorResponseBytes = BoundedHttp.DefaultErrorDiagnosticBytes;

    /// <summary>Allowance for terminal, usage and error events beyond the per-token lines.</summary>
    public const int TerminalEventMargin = 256;

    /// <summary>
    /// NDJSON object budget. Ollama emits one object per generated token (content or thinking),
    /// so the budget is twice the <see cref="MaxNumPredict"/> clamp plus the terminal margin.
    /// </summary>
    public const int MaxStreamEvents = (2 * MaxNumPredict) + TerminalEventMargin;

    /// <summary>Physical-line budget (NDJSON objects plus any blank lines).</summary>
    public const int MaxPhysicalLines = 2 * MaxStreamEvents;

    /// <summary>Per-line budget. Ollama sends a whole tool call in one line, so this exceeds
    /// <see cref="MaxToolArgumentCharacters"/> plus the JSON envelope.</summary>
    public const int MaxStreamLineCharacters = 300_000;

    /// <summary>Allowance per NDJSON line used to size <see cref="MaxStreamingResponseBytes"/>.</summary>
    public const int StreamLineByteAllowance = 480;

    /// <summary>Byte budget for one streamed response (16 MiB).</summary>
    public const int MaxStreamingResponseBytes = 16 * 1_048_576;

    /// <summary>Budget for visible assistant text in one response.</summary>
    public const int MaxStreamTextCharacters = 524_288;

    /// <summary>Budget for reasoning (thinking) text in one response.</summary>
    public const int MaxReasoningCharacters = 524_288;

    /// <summary>Budget for one tool call's serialized arguments.</summary>
    public const int MaxToolArgumentCharacters = 262_144;

    /// <summary>Maximum tool calls accepted in one response; the session applies tighter limits.</summary>
    public const int MaxToolCallsPerResponse = 64;

    /// <summary>Maximum model names returned by <see cref="ListModelsAsync"/>.</summary>
    public const int MaxListedModels = 1_024;

    /// <summary>Guidance returned when a thinking model spends its whole budget on reasoning.</summary>
    public const string ReasoningBudgetExhaustedMessage =
        "model used its whole output budget on reasoning; raise num_predict";

    private const int MaxToolNameCharacters = 256;
    private const int MaxModelNameCharacters = 256;
    private const int MaxInlineErrorCharacters = 2_048;

    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly OllamaProviderOptions options;
    private readonly ContinuationStore continuation = new();

    /// <summary>
    /// Creates a provider with a shared <see cref="HttpClient"/> and default options. Caller retains
    /// ownership of the client.
    /// </summary>
    public OllamaProvider(HttpClient httpClient)
        : this(httpClient, new OllamaProviderOptions())
    {
    }

    /// <summary>
    /// Creates a provider with an internally-managed <see cref="HttpClient"/> and default options.
    /// </summary>
    public OllamaProvider()
        : this(new OllamaProviderOptions())
    {
    }

    /// <summary>
    /// Creates a provider with an internally-managed <see cref="HttpClient"/>.
    /// </summary>
    public OllamaProvider(OllamaProviderOptions options)
        : this(CreateOwnedClient(), options, ownsClient: true)
    {
    }

    /// <summary>
    /// Creates a provider with a shared <see cref="HttpClient"/>. Caller retains ownership.
    /// </summary>
    public OllamaProvider(HttpClient httpClient, OllamaProviderOptions options)
        : this(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), options, ownsClient: false)
    {
    }

    private OllamaProvider(HttpClient httpClient, OllamaProviderOptions options, bool ownsClient)
    {
        this.httpClient = httpClient;
        this.ownsHttpClient = ownsClient;
        this.options = ValidateOptions(options);
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <summary>
    /// The configured, validated endpoint (<see cref="OllamaProviderOptions.BaseUrl"/>).
    /// </summary>
    public Uri DefaultServerUrl => options.BaseUrl;

    /// <summary>
    /// A detached copy of the options this provider was built with.
    /// </summary>
    public OllamaProviderOptions Options => options.Clone();

    /// <summary>
    /// Clamps a requested output budget to [<see cref="MinNumPredict"/>, <see cref="MaxNumPredict"/>].
    /// </summary>
    public static int ClampNumPredict(int maxTokens) => Math.Clamp(maxTokens, MinNumPredict, MaxNumPredict);

    /// <inheritdoc />
    public AIRequestCall PreCall(AIRequestCall request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        request.BaseUrl ??= DefaultServerUrl;
        request.HttpMethod = "POST";
        request.Endpoint = "/api/chat";
        request.ContentType = "application/json";
        request.Authentication = "none";

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
        var interactions = new List<IAIInteraction>();
        if (response == null)
        {
            return interactions;
        }

        try
        {
            if (response["message"] is not JObject message)
            {
                return interactions;
            }

            var contentText = message["content"]?.Type == JTokenType.String ? (string?)message["content"] : null;
            if (!string.IsNullOrEmpty(contentText))
            {
                interactions.Add(new AIInteractionText
                {
                    Agent = AIAgent.Assistant,
                    Content = contentText,
                });
            }

            if (message["tool_calls"] is JArray toolCalls)
            {
                foreach (var call in toolCalls.OfType<JObject>())
                {
                    if (call["function"] is not JObject fn)
                    {
                        continue;
                    }

                    interactions.Add(new AIInteractionToolCall
                    {
                        Id = NewToolCallId(),
                        Name = fn["name"]?.ToString(),
                        Arguments = LenientArguments(fn["arguments"]),
                        Agent = AIAgent.ToolCall,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Ollama] Decode error: {ex.Message}");
        }

        return interactions;
    }

    /// <inheritdoc />
    public string Encode(IAIInteraction interaction)
    {
        if (interaction == null)
        {
            return string.Empty;
        }

        switch (interaction)
        {
            case AIInteractionText text:
            {
                var role = text.Agent switch
                {
                    AIAgent.System or AIAgent.Context => "system",
                    AIAgent.User => "user",
                    AIAgent.Assistant => "assistant",
                    _ => "user",
                };
                return new JObject
                {
                    ["role"] = role,
                    ["content"] = text.Content ?? string.Empty,
                }.ToString();
            }

            case AIInteractionToolResult toolResult:
                return EncodeToolResult(toolResult).ToString();

            case AIInteractionToolCall toolCall:
                return new JObject
                {
                    ["role"] = "assistant",
                    ["content"] = string.Empty,
                    ["tool_calls"] = new JArray { EncodeToolCall(toolCall) },
                }.ToString();
        }

        return string.Empty;
    }

    /// <inheritdoc />
    public async Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        request = PreCall(request);
        if (!TryResolveUrl(request, request.Endpoint, out var url, out var endpointError))
        {
            return Error(request, endpointError);
        }

        var ret = new AIReturn { Request = request, Status = AICallStatus.Processing };
        try
        {
            using var httpRequest = CreateHttpRequest(request, url, BuildBody(request, stream: false));
            using var cts = CreateTimeout(request, cancellationToken);
            using var response = await httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token)
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
            catch (Exception ex)
            {
                return Error(request, $"Failed to parse provider response: {ex.Message}");
            }

            var accumulator = new ResponseAccumulator();
            var lineError = accumulator.Apply(parsed, deltas: null);
            if (lineError != null)
            {
                return Error(request, lineError);
            }

            return Finish(accumulator, request, toolChunks: null);
        }
        catch (OperationCanceledException ex) when (BoundedHttp.IsConnectTimeout(ex))
        {
            ret.CreateError(BoundedHttp.ConnectTimeoutMessage, request);
            return ret;
        }
        catch (OperationCanceledException)
        {
            ret.CreateError("Call cancelled or timed out", request);
            return ret;
        }
        catch (BoundedHttpException ex)
        {
            ret.CreateError(ex.Message, request);
            return ret;
        }
        catch (HttpRequestException ex)
        {
            ret.CreateError(
                BoundedHttp.IsConnectTimeout(ex) ? BoundedHttp.ConnectTimeoutMessage : $"Network error: {ex.InnerException?.Message ?? ex.Message}",
                request);
            return ret;
        }
        catch (Exception ex)
        {
            ret.CreateError($"Provider error: {ex.InnerException?.Message ?? ex.Message}", request);
            return ret;
        }
    }

    /// <summary>
    /// Streams <c>/api/chat</c> as NDJSON. Yields text and reasoning deltas as they arrive, then —
    /// only if the stream completed normally — one <see cref="AIStreamChunkKind.ToolCallStart"/> and
    /// <see cref="AIStreamChunkKind.ToolCallComplete"/> pair per buffered tool call, then exactly one
    /// <see cref="AIStreamChunkKind.Done"/>. External cancellation surfaces as an
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
        if (!TryResolveUrl(request, request.Endpoint, out var url, out var endpointError))
        {
            yield return DoneChunk(Error(request, endpointError));
            yield break;
        }

        using var cts = CreateTimeout(request, cancellationToken);
        HttpRequestMessage? httpRequest = null;
        HttpResponseMessage? response = null;
        BoundedLineReader? reader = null;
        AIReturn? failure = null;
        var accumulator = new ResponseAccumulator();

        try
        {
            try
            {
                httpRequest = CreateHttpRequest(request, url, BuildBody(request, stream: true));
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-ndjson"));
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
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var lineError = accumulator.ApplyLine(line, deltas);
                foreach (var delta in deltas)
                {
                    yield return delta;
                }

                deltas.Clear();
                if (lineError != null)
                {
                    yield return DoneChunk(Error(request, lineError));
                    yield break;
                }

                if (accumulator.Done)
                {
                    break;
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
        foreach (var chunk in toolChunks)
        {
            yield return chunk;
        }

        yield return DoneChunk(final);
    }

    /// <summary>
    /// Lists installed model names from a bounded <c>GET /api/tags</c>. Throws
    /// <see cref="InvalidOperationException"/> with a diagnostic when the server cannot be read.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var url = BoundedHttp.Combine(options.BaseUrl, "/api/tags");
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var diagnostic = await BoundedHttp
                    .ReadErrorDiagnosticAsync(response.Content, MaxErrorResponseBytes, cts.Token)
                    .ConfigureAwait(false);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {diagnostic}");
            }

            var body = await BoundedHttp.ReadBodyAsync(response.Content, MaxNonStreamingResponseBytes, cts.Token)
                .ConfigureAwait(false);
            var names = new List<string>();
            if (JObject.Parse(body)["models"] is JArray models)
            {
                foreach (var model in models.OfType<JObject>())
                {
                    var name = model["name"]?.Type == JTokenType.String ? (string?)model["name"] : null;
                    if (string.IsNullOrWhiteSpace(name) || name!.Length > MaxModelNameCharacters)
                    {
                        continue;
                    }

                    names.Add(name);
                    if (names.Count >= MaxListedModels)
                    {
                        break;
                    }
                }
            }

            return names;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not list Ollama models: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Optional warm-up: <c>POST /api/generate {model, prompt:"", keep_alive}</c> so the first chat
    /// call does not pay the model-load time. Reads a bounded response and never throws for
    /// transport or server errors; returns true when the server answered with success.
    /// </summary>
    public async Task<bool> WarmAsync(string model, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model is required.", nameof(model));
        }

        var body = new JObject
        {
            ["model"] = model,
            ["prompt"] = string.Empty,
            ["stream"] = false,
        };
        if (!string.IsNullOrWhiteSpace(options.KeepAlive))
        {
            body["keep_alive"] = options.KeepAlive;
        }

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BoundedHttp.Combine(options.BaseUrl, "/api/generate"))
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
            };
            using var response = await httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await BoundedHttp.ReadErrorDiagnosticAsync(response.Content, MaxErrorResponseBytes, cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            await BoundedHttp.ReadBodyAsync(response.Content, MaxNonStreamingResponseBytes, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Ollama] Warm-up failed: {ex.Message}");
            return false;
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

    // --- request building ------------------------------------------------------------------

    private JObject BuildBody(AIRequestCall request, bool stream)
    {
        var messages = new JArray();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new JObject
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt,
            });
        }

        var interactions = request.Interactions ?? new List<IAIInteraction>();

        // Reasoning is replayed only inside the current tool loop: assistant messages after the
        // last user message.
        var lastUserIndex = -1;
        for (var i = 0; i < interactions.Count; i++)
        {
            if (interactions[i] is AIInteractionText { Agent: AIAgent.User })
            {
                lastUserIndex = i;
            }
        }

        // Ollama groups an assistant text reply and its tool_calls into ONE message.
        JObject? pendingAssistant = null;

        void FlushAssistant()
        {
            if (pendingAssistant != null)
            {
                messages.Add(pendingAssistant);
                pendingAssistant = null;
            }
        }

        void ReplayThinking(IAIInteraction carrier, int index)
        {
            if (index > lastUserIndex && pendingAssistant != null && pendingAssistant["thinking"] == null)
            {
                var thinking = continuation.Lookup(carrier);
                if (!string.IsNullOrEmpty(thinking))
                {
                    pendingAssistant["thinking"] = thinking;
                }
            }
        }

        for (var index = 0; index < interactions.Count; index++)
        {
            switch (interactions[index])
            {
                case AIInteractionText text when text.Agent == AIAgent.System || text.Agent == AIAgent.Context:
                    FlushAssistant();
                    if (!string.IsNullOrEmpty(text.Content))
                    {
                        messages.Add(new JObject
                        {
                            ["role"] = "system",
                            ["content"] = text.Content,
                        });
                    }

                    break;

                case AIInteractionText text when text.Agent == AIAgent.User:
                    FlushAssistant();
                    messages.Add(new JObject
                    {
                        ["role"] = "user",
                        ["content"] = text.Content ?? string.Empty,
                    });
                    break;

                case AIInteractionText text when text.Agent == AIAgent.Assistant:
                    FlushAssistant();
                    pendingAssistant = new JObject
                    {
                        ["role"] = "assistant",
                        ["content"] = text.Content ?? string.Empty,
                    };
                    ReplayThinking(text, index);
                    break;

                case AIInteractionToolResult toolResult:
                    FlushAssistant();
                    messages.Add(EncodeToolResult(toolResult));
                    break;

                case AIInteractionToolCall toolCall:
                    pendingAssistant ??= new JObject
                    {
                        ["role"] = "assistant",
                        ["content"] = string.Empty,
                    };

                    if (pendingAssistant["tool_calls"] is not JArray toolCallsArr)
                    {
                        toolCallsArr = new JArray();
                        pendingAssistant["tool_calls"] = toolCallsArr;
                    }

                    toolCallsArr.Add(EncodeToolCall(toolCall));
                    ReplayThinking(toolCall, index);
                    break;
            }
        }

        FlushAssistant();

        var modelOptions = new JObject
        {
            ["num_ctx"] = options.NumCtx,
            ["num_predict"] = ClampNumPredict(request.MaxTokens),
        };

        if (request.Temperature is double t && !double.IsNaN(t))
        {
            modelOptions["temperature"] = t;
        }

        var body = new JObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = stream,
            ["options"] = modelOptions,
        };

        if (!string.IsNullOrWhiteSpace(options.KeepAlive))
        {
            body["keep_alive"] = options.KeepAlive;
        }

        if (options.Think != null)
        {
            body["think"] = options.Think.DeepClone();
        }

        if (request.Tools != null && request.Tools.Count > 0)
        {
            var toolsArr = new JArray();
            foreach (var tool in request.Tools)
            {
                toolsArr.Add(new JObject
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

            body["tools"] = toolsArr;
        }

        return body;
    }

    private static JObject EncodeToolCall(AIInteractionToolCall toolCall)
    {
        return new JObject
        {
            ["function"] = new JObject
            {
                ["name"] = toolCall.Name ?? string.Empty,
                ["arguments"] = toolCall.Arguments ?? new JObject(),
            },
        };
    }

    private static JObject EncodeToolResult(AIInteractionToolResult toolResult)
    {
        var message = new JObject
        {
            ["role"] = "tool",
            ["content"] = toolResult.Result?.ToString(Formatting.None) ?? "{}",
        };

        if (!string.IsNullOrEmpty(toolResult.Name))
        {
            message["tool_name"] = toolResult.Name;
        }

        return message;
    }

    private HttpRequestMessage CreateHttpRequest(AIRequestCall request, Uri url, JObject body)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
        };

        // Ollama is unauthenticated. PreCall sets Authentication to "none", so no bearer is sent
        // unless a caller deliberately re-enables it after PreCall; even then it only goes to the
        // configured endpoint, because the URL was resolved against the configured BaseUrl.
        if (string.Equals(request.Authentication, "bearer", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(request.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        }

        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "x-api-key", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return httpRequest;
    }

    private bool TryResolveUrl(AIRequestCall request, string path, out Uri url, out string error)
    {
        url = options.BaseUrl;
        var requested = request.BaseUrl ?? options.BaseUrl;
        if (!BoundedHttp.TryValidateEndpoint(requested, out error))
        {
            error = $"Invalid Ollama endpoint: {error}";
            return false;
        }

        if (!BoundedHttp.IsSameEndpoint(requested, options.BaseUrl))
        {
            error = "Request endpoint does not match the configured Ollama endpoint.";
            return false;
        }

        url = BoundedHttp.Combine(options.BaseUrl, path);
        return true;
    }

    private static OllamaProviderOptions ValidateOptions(OllamaProviderOptions? options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var copy = options.Clone();
        if (!BoundedHttp.TryValidateEndpoint(copy.BaseUrl, out var error))
        {
            throw new ArgumentException($"Invalid Ollama endpoint: {error}", nameof(options));
        }

        if (copy.NumCtx <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "NumCtx must be positive.");
        }

        if (copy.Think != null && copy.Think.Type != JTokenType.Boolean)
        {
            var level = copy.Think.Type == JTokenType.String ? (string?)copy.Think : null;
            if (level is not ("low" or "medium" or "high"))
            {
                throw new ArgumentException("Think must be a boolean or one of \"low\", \"medium\", \"high\".", nameof(options));
            }
        }

        return copy;
    }

    private static HttpClient CreateOwnedClient()
    {
        // The provider's own cancellation sources govern timeouts; HttpClient's 100 s default would
        // otherwise abort a model that is still loading. The connect itself is bounded.
        return BoundedHttp.CreateProviderClient();
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
        if (!accumulator.Done)
        {
            return Error(request, accumulator.ToolCalls.Count > 0
                ? "Ollama stream ended before completion; no tool calls were run."
                : "Ollama stream ended before completion.");
        }

        var content = accumulator.Content.ToString();
        var reasoning = accumulator.Reasoning.ToString();
        var doneReason = accumulator.DoneReason;

        if (string.Equals(doneReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            if (accumulator.ToolCalls.Count > 0)
            {
                return Error(request,
                    "Ollama stopped at the output limit while emitting tool calls; no tool calls were run. Raise num_predict.");
            }

            if (content.Length == 0)
            {
                return Error(request, reasoning.Length > 0
                    ? ReasoningBudgetExhaustedMessage
                    : "Ollama stopped at the output limit before producing any output; raise num_predict.");
            }
        }
        else if (doneReason != null && !string.Equals(doneReason, "stop", StringComparison.OrdinalIgnoreCase)
            && accumulator.ToolCalls.Count > 0)
        {
            return Error(request, $"Ollama finished with done_reason \"{doneReason}\"; no tool calls were run.");
        }

        var ret = new AIReturn
        {
            Request = request,
            InputTokens = accumulator.InputTokens,
            OutputTokens = accumulator.OutputTokens,
        };

        IAIInteraction? reasoningCarrier = null;
        if (content.Length > 0)
        {
            var text = new AIInteractionText
            {
                Agent = AIAgent.Assistant,
                Content = content,
            };
            ret.Interactions.Add(text);
            reasoningCarrier = text;
        }

        foreach (var (name, arguments) in accumulator.ToolCalls)
        {
            var call = new AIInteractionToolCall
            {
                Id = NewToolCallId(),
                Name = name,
                Arguments = arguments,
                Agent = AIAgent.ToolCall,
            };
            ret.Interactions.Add(call);
            reasoningCarrier ??= call;

            if (toolChunks != null)
            {
                var argsJson = arguments.ToString(Formatting.None);
                toolChunks.Add(new AIStreamChunk(AIStreamChunkKind.ToolCallStart, ToolName: name, ToolUseId: call.Id, ToolArgsPartial: string.Empty));
                toolChunks.Add(new AIStreamChunk(AIStreamChunkKind.ToolCallComplete, ToolName: name, ToolUseId: call.Id, ToolArgsPartial: argsJson));
            }
        }

        if (reasoning.Length > 0 && reasoningCarrier != null)
        {
            switch (reasoningCarrier)
            {
                case AIInteractionText text:
                    text.Reasoning = reasoning;
                    break;
                case AIInteractionToolCall call:
                    call.Reasoning = reasoning;
                    break;
            }

            continuation.Register(reasoningCarrier, reasoning);
        }

        var hasToolCalls = accumulator.ToolCalls.Count > 0;
        ret.StopReason = hasToolCalls
            ? "tool_use"
            : string.Equals(doneReason, "length", StringComparison.OrdinalIgnoreCase) ? "max_tokens" : "end_turn";
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

    private static string NewToolCallId() => $"ollama_{Guid.NewGuid():N}";

    /// <summary>
    /// Lenient argument decoding kept for the public <see cref="Decode"/> helper. The call paths
    /// use the strict parser in <see cref="ResponseAccumulator"/>.
    /// </summary>
    private static JObject LenientArguments(JToken? argsToken)
    {
        if (argsToken == null || argsToken.Type == JTokenType.Null)
        {
            return new JObject();
        }

        if (argsToken is JObject obj)
        {
            return obj;
        }

        if (argsToken.Type == JTokenType.String)
        {
            var s = argsToken.ToString();
            if (string.IsNullOrWhiteSpace(s))
            {
                return new JObject();
            }

            try
            {
                return JObject.Parse(s);
            }
            catch (JsonException)
            {
                return new JObject { ["value"] = s };
            }
        }

        return new JObject { ["value"] = argsToken };
    }

    /// <summary>
    /// Accumulates one response (NDJSON lines or a single non-streaming object) under the
    /// provider's budgets. Every method returns an error string instead of throwing.
    /// </summary>
    private sealed class ResponseAccumulator
    {
        public StringBuilder Content { get; } = new();

        public StringBuilder Reasoning { get; } = new();

        public List<(string Name, JObject Arguments)> ToolCalls { get; } = new();

        public bool Done { get; private set; }

        public string? DoneReason { get; private set; }

        public int InputTokens { get; private set; }

        public int OutputTokens { get; private set; }

        public int Events { get; private set; }

        public string? ApplyLine(string line, List<AIStreamChunk> deltas)
        {
            JObject parsed;
            try
            {
                parsed = JObject.Parse(line);
            }
            catch (JsonException)
            {
                return "Malformed stream line from Ollama.";
            }

            return Apply(parsed, deltas);
        }

        public string? Apply(JObject parsed, List<AIStreamChunk>? deltas)
        {
            Events++;
            if (Events > MaxStreamEvents)
            {
                return $"Streaming response exceeded the {MaxStreamEvents}-event budget.";
            }

            if (parsed["error"] is JToken errorToken && errorToken.Type != JTokenType.Null)
            {
                var message = errorToken.Type == JTokenType.String ? (string?)errorToken : errorToken.ToString(Formatting.None);
                message ??= "unknown error";
                if (message.Length > MaxInlineErrorCharacters)
                {
                    message = message.Substring(0, MaxInlineErrorCharacters) + BoundedHttp.DiagnosticTruncatedMarker;
                }

                return $"Ollama error: {message}";
            }

            if (parsed["message"] is JObject messageObj)
            {
                var content = messageObj["content"]?.Type == JTokenType.String ? (string?)messageObj["content"] : null;
                if (!string.IsNullOrEmpty(content))
                {
                    if (content!.Length > MaxStreamTextCharacters - Content.Length)
                    {
                        return $"Streaming response exceeded the {MaxStreamTextCharacters}-character text budget.";
                    }

                    Content.Append(content);
                    deltas?.Add(new AIStreamChunk(AIStreamChunkKind.TextDelta, Text: content));
                }

                var thinking = messageObj["thinking"]?.Type == JTokenType.String ? (string?)messageObj["thinking"] : null;
                if (!string.IsNullOrEmpty(thinking))
                {
                    if (thinking!.Length > MaxReasoningCharacters - Reasoning.Length)
                    {
                        return $"Streaming response exceeded the {MaxReasoningCharacters}-character reasoning budget.";
                    }

                    Reasoning.Append(thinking);
                    deltas?.Add(new AIStreamChunk(AIStreamChunkKind.ReasoningDelta, Text: thinking));
                }

                if (messageObj["tool_calls"] is JArray toolCalls)
                {
                    foreach (var call in toolCalls)
                    {
                        var error = AddToolCall(call);
                        if (error != null)
                        {
                            return error;
                        }
                    }
                }
                else if (messageObj["tool_calls"] is JToken other && other.Type != JTokenType.Null)
                {
                    return "Malformed tool_calls from Ollama.";
                }
            }

            if (parsed["done"]?.Type == JTokenType.Boolean && (bool)parsed["done"]!)
            {
                Done = true;
                DoneReason = parsed["done_reason"]?.Type == JTokenType.String ? (string?)parsed["done_reason"] : null;
                InputTokens = ReadCount(parsed["prompt_eval_count"]);
                OutputTokens = ReadCount(parsed["eval_count"]);
            }

            return null;
        }

        private string? AddToolCall(JToken call)
        {
            if (ToolCalls.Count >= MaxToolCallsPerResponse)
            {
                return $"Ollama returned more than {MaxToolCallsPerResponse} tool calls.";
            }

            if (call is not JObject callObj || callObj["function"] is not JObject fn)
            {
                return "Malformed tool call from Ollama.";
            }

            var name = fn["name"]?.Type == JTokenType.String ? (string?)fn["name"] : null;
            if (string.IsNullOrWhiteSpace(name) || name!.Length > MaxToolNameCharacters)
            {
                return "Ollama returned a tool call without a valid name.";
            }

            var argsToken = fn["arguments"];
            JObject arguments;
            if (argsToken == null || argsToken.Type == JTokenType.Null)
            {
                arguments = new JObject();
            }
            else if (argsToken is JObject obj)
            {
                if (obj.ToString(Formatting.None).Length > MaxToolArgumentCharacters)
                {
                    return $"Tool call arguments exceeded the {MaxToolArgumentCharacters}-character budget.";
                }

                arguments = obj;
            }
            else if (argsToken.Type == JTokenType.String)
            {
                var raw = (string)argsToken!;
                if (raw.Length > MaxToolArgumentCharacters)
                {
                    return $"Tool call arguments exceeded the {MaxToolArgumentCharacters}-character budget.";
                }

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
                        return $"Tool call \"{name}\" has arguments that are not a JSON object.";
                    }
                }
            }
            else
            {
                return $"Tool call \"{name}\" has arguments that are not a JSON object.";
            }

            ToolCalls.Add((name, arguments));
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

    /// <summary>
    /// Reasoning produced by this provider instance, keyed both by the interaction that carries it
    /// and by the reasoning string instance (so a shallow copy that keeps the same string still
    /// resolves). Weak keys: entries die with the conversation history.
    /// </summary>
    private sealed class ContinuationStore
    {
        private readonly ConditionalWeakTable<IAIInteraction, string> byCarrier = new();
        private readonly ConditionalWeakTable<string, object> byReasoning = new();

        public void Register(IAIInteraction carrier, string reasoning)
        {
            byCarrier.AddOrUpdate(carrier, reasoning);
            byReasoning.AddOrUpdate(reasoning, carrier);
        }

        public string? Lookup(IAIInteraction interaction)
        {
            if (byCarrier.TryGetValue(interaction, out var reasoning))
            {
                return reasoning;
            }

            var candidate = interaction switch
            {
                AIInteractionText text => text.Reasoning,
                AIInteractionToolCall call => call.Reasoning,
                _ => null,
            };

            return candidate != null && byReasoning.TryGetValue(candidate, out _) ? candidate : null;
        }
    }
}
