// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;

namespace Parti.Providers.OpenAI;

/// <summary>
/// Configuration for <see cref="OpenAICompatibleProvider"/>. One provider class serves OpenAI,
/// OpenRouter and Ollama's OpenAI-compatible <c>/v1</c>; the presets below fill the defaults.
/// The provider snapshots and validates these values at construction, so later changes to an
/// options instance never redirect an existing provider or its credentials.
/// </summary>
public sealed class OpenAICompatibleProviderOptions
{
    /// <summary>The <c>max_completion_tokens</c> token-limit field (OpenAI).</summary>
    public const string MaxCompletionTokensField = "max_completion_tokens";

    /// <summary>The <c>max_tokens</c> token-limit field (OpenRouter, Ollama, most compatibles).</summary>
    public const string MaxTokensField = "max_tokens";

    /// <summary>Environment variable holding the OpenAI key.</summary>
    public const string OpenAIKeyEnvironmentVariable = "OPENAI_API_KEY";

    /// <summary>Environment variable holding the OpenRouter key.</summary>
    public const string OpenRouterKeyEnvironmentVariable = "OPENROUTER_API_KEY";

    /// <summary>
    /// The only extra headers a configuration may add (OpenRouter attribution headers).
    /// </summary>
    public static readonly IReadOnlyCollection<string> AllowedExtraHeaders = new[] { "HTTP-Referer", "X-Title" };

    /// <summary>Display name reported as <see cref="OpenAICompatibleProvider.Name"/>.</summary>
    public string Name { get; set; } = "OpenAI-compatible";

    /// <summary>
    /// API root including the version segment (e.g. <c>https://api.openai.com/v1</c>); requests go
    /// to <c>{BaseUrl}/chat/completions</c>. Validated: http/https, no user-info, query or fragment.
    /// </summary>
    public Uri BaseUrl { get; set; } = new("https://api.openai.com/v1");

    /// <summary>
    /// In-memory API key override. When null or empty, <see cref="ApiKeyEnvironmentVariable"/> is read.
    /// A key set on the request itself takes precedence over both.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Environment variable consulted when no key is supplied. Null disables the lookup.</summary>
    public string? ApiKeyEnvironmentVariable { get; set; }

    /// <summary>
    /// Body field that carries the output token limit: <see cref="MaxCompletionTokensField"/> or
    /// <see cref="MaxTokensField"/>.
    /// </summary>
    public string TokenLimitField { get; set; } = MaxCompletionTokensField;

    /// <summary>
    /// Extra request headers. Only names in <see cref="AllowedExtraHeaders"/> are accepted; any other
    /// name makes the provider constructor throw.
    /// </summary>
    public IDictionary<string, string> ExtraHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>OpenAI preset: <c>https://api.openai.com/v1</c>, <c>max_completion_tokens</c>, <c>OPENAI_API_KEY</c>.</summary>
    public static OpenAICompatibleProviderOptions OpenAI() => new()
    {
        Name = "OpenAI",
        BaseUrl = new Uri("https://api.openai.com/v1"),
        TokenLimitField = MaxCompletionTokensField,
        ApiKeyEnvironmentVariable = OpenAIKeyEnvironmentVariable,
    };

    /// <summary>OpenRouter preset: <c>https://openrouter.ai/api/v1</c>, <c>max_tokens</c>, <c>OPENROUTER_API_KEY</c>.</summary>
    public static OpenAICompatibleProviderOptions OpenRouter() => new()
    {
        Name = "OpenRouter",
        BaseUrl = new Uri("https://openrouter.ai/api/v1"),
        TokenLimitField = MaxTokensField,
        ApiKeyEnvironmentVariable = OpenRouterKeyEnvironmentVariable,
    };

    /// <summary>
    /// Ollama's OpenAI-compatible endpoint: <c>http://{host}:11434/v1</c>, <c>max_tokens</c>, no key.
    /// </summary>
    public static OpenAICompatibleProviderOptions OllamaOpenAI(string host = "localhost") => new()
    {
        Name = "Ollama-OpenAI",
        BaseUrl = new UriBuilder(Uri.UriSchemeHttp, host, 11434, "v1").Uri,
        TokenLimitField = MaxTokensField,
        ApiKeyEnvironmentVariable = null,
    };

    /// <summary>
    /// Returns a detached copy.
    /// </summary>
    public OpenAICompatibleProviderOptions Clone()
    {
        var copy = new OpenAICompatibleProviderOptions
        {
            Name = Name,
            BaseUrl = BaseUrl,
            ApiKey = ApiKey,
            ApiKeyEnvironmentVariable = ApiKeyEnvironmentVariable,
            TokenLimitField = TokenLimitField,
        };

        foreach (var header in ExtraHeaders)
        {
            copy.ExtraHeaders[header.Key] = header.Value;
        }

        return copy;
    }
}
