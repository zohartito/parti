// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using Parti.Core.Ai;
using Parti.Core.Grasshopper.Chat;
using Parti.Providers.Anthropic;
using Parti.Providers.Cli;
using Parti.Providers.Ollama;
using Parti.Providers.OpenAI;

namespace Parti.Components.Chat;

/// <summary>
/// Provider the chat window talks to. Claude on the user's subscription CLI is the default, with
/// local Ollama as the fallback (<see cref="ProviderSettings.DefaultProvider"/>).
/// </summary>
public enum ProviderSelection
{
    /// <summary>Local Ollama daemon through the native <c>/api/chat</c> endpoint.</summary>
    Ollama,

    /// <summary>Anthropic Messages API.</summary>
    Anthropic,

    /// <summary>OpenAI chat completions.</summary>
    OpenAI,

    /// <summary>OpenRouter chat completions.</summary>
    OpenRouter,

    /// <summary>Any OpenAI-compatible <c>/chat/completions</c> server at a custom URL.</summary>
    OpenAICompatible,

    /// <summary>The user's Claude subscription through the signed-in <c>claude</c> CLI.</summary>
    SubscriptionClaude,

    /// <summary>The user's ChatGPT subscription through the signed-in <c>codex</c> CLI.</summary>
    SubscriptionCodex,
}

/// <summary>
/// A validated provider choice from the settings dialog. <paramref name="ApiKeyOverride"/> is an
/// in-memory key typed by the user; when null the provider falls back to its environment variable.
/// <paramref name="Effort"/> and <paramref name="ExecutablePath"/> apply to the subscription (CLI)
/// providers only; null there means the default effort and a search for the executable.
/// </summary>
public sealed record ProviderConfiguration(
    ProviderSelection Provider,
    string Model,
    Uri? Endpoint,
    string? ApiKeyOverride,
    string? Effort = null,
    string? ExecutablePath = null);

/// <summary>
/// A constructed provider. <see cref="Ollama"/> is the native Ollama instance behind
/// <see cref="Provider"/> (for model listing and warm-up), or null for other providers.
/// </summary>
public sealed record ProviderHandle(IAIProvider Provider, OllamaProvider? Ollama);

/// <summary>
/// Non-UI rules behind the chat window's provider settings: defaults, endpoint and key checks,
/// Ollama model preselection, and provider construction. Kept out of <see cref="ChatForm"/> so it
/// can be tested without Eto.
/// </summary>
public static class ProviderSettings
{
    /// <summary>
    /// Provider selected when the window first opens: Claude on the user's subscription CLI, which
    /// passed 30 of 30 live briefs (docs/EVAL-LIVE.md, 2026-09-27). Without a <c>claude</c> CLI the
    /// window falls back to a local Ollama model (<see cref="DefaultSubscription"/>).
    /// </summary>
    public const ProviderSelection DefaultProvider = ProviderSelection.SubscriptionClaude;

    /// <summary>Model preselected for <see cref="DefaultProvider"/>.</summary>
    public const string DefaultSubscriptionModel = "claude-opus-5-5";

    /// <summary>
    /// The first-open configuration when <paramref name="resolveExecutable"/> finds the
    /// <c>claude</c> CLI: <see cref="DefaultSubscriptionModel"/> at <see cref="DefaultEffort"/>.
    /// Null otherwise, and the window then looks for a local Ollama model.
    /// </summary>
    public static ProviderConfiguration? DefaultSubscription(Func<string, string?> resolveExecutable)
    {
        if (resolveExecutable is null) throw new ArgumentNullException(nameof(resolveExecutable));
        return resolveExecutable(SubscriptionCliOptions.ExecutableName(CliBackendKind.Claude)) is null
            ? null
            : new ProviderConfiguration(ProviderSelection.SubscriptionClaude, DefaultSubscriptionModel, null, null, DefaultEffort);
    }

    /// <summary>
    /// Output-token budget for product turns (spec D6): <see cref="PartiComposition.DefaultMaxTokens"/>,
    /// the one product constant.
    /// </summary>
    public const int DefaultMaxTokens = PartiComposition.DefaultMaxTokens;

    /// <summary>
    /// Request timeout for local servers. It bounds the wait for response headers and each idle gap
    /// between stream lines, so it must cover a cold model load (46.6 s measured for a cold
    /// qwen3.6:27b; larger models take longer).
    /// </summary>
    public const int LocalTimeoutSeconds = 600;

    /// <summary>Request timeout for hosted APIs (the <see cref="AIRequestCall"/> default).</summary>
    public const int HostedTimeoutSeconds = 120;

    /// <summary>
    /// Per-call limit for the subscription CLIs (<see cref="SubscriptionCliOptions.DefaultTimeout"/>);
    /// the CLI provider enforces it by killing the process tree.
    /// </summary>
    public const int SubscriptionTimeoutSeconds = 180;

    /// <summary>Effort preselected for the subscription CLIs.</summary>
    public const string DefaultEffort = SubscriptionCliOptions.DefaultEffort;

    /// <summary>Ollama models preselected in this order when installed (spec D6).</summary>
    public static IReadOnlyList<string> PreferredOllamaModels { get; } = new[] { "qwen3.6:27b", "gemma4:31b-65k" };

    /// <summary>Environment variable holding Anthropic's key.</summary>
    public const string AnthropicKeyEnvironmentVariable = "ANTHROPIC_API_KEY";

    /// <summary>Human-readable name for the dropdown.</summary>
    public static string DisplayName(ProviderSelection provider) => provider switch
    {
        ProviderSelection.Ollama => "Ollama (local)",
        ProviderSelection.Anthropic => "Anthropic",
        ProviderSelection.OpenAI => "OpenAI",
        ProviderSelection.OpenRouter => "OpenRouter",
        ProviderSelection.OpenAICompatible => "OpenAI-compatible (custom URL)",
        ProviderSelection.SubscriptionClaude => "Subscription (Claude CLI)",
        ProviderSelection.SubscriptionCodex => "Subscription (Codex CLI)",
        _ => provider.ToString(),
    };

    /// <summary>True for the providers that run a signed-in vendor CLI on the user's subscription.</summary>
    public static bool IsSubscription(ProviderSelection provider) =>
        provider is ProviderSelection.SubscriptionClaude or ProviderSelection.SubscriptionCodex;

    /// <summary>The CLI behind a subscription provider.</summary>
    public static CliBackendKind CliBackend(ProviderSelection provider) => provider switch
    {
        ProviderSelection.SubscriptionClaude => CliBackendKind.Claude,
        ProviderSelection.SubscriptionCodex => CliBackendKind.Codex,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Not a subscription provider."),
    };

    /// <summary>Effort levels offered for <paramref name="provider"/>; empty when it takes none.</summary>
    public static IReadOnlyList<string> EffortLevels(ProviderSelection provider) =>
        IsSubscription(provider) ? SubscriptionCliOptions.EffortLevels(CliBackend(provider)) : Array.Empty<string>();

    /// <summary>
    /// The status-line description of <paramref name="configuration"/>, e.g.
    /// "Subscription: claude-opus-5-5 via claude CLI" or "Ollama (local) / qwen3.6:27b".
    /// </summary>
    public static string Describe(ProviderConfiguration configuration)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        return IsSubscription(configuration.Provider)
            ? $"Subscription: {configuration.Model} via {SubscriptionCliOptions.ExecutableName(CliBackend(configuration.Provider))} CLI"
            : $"{DisplayName(configuration.Provider)} / {configuration.Model}";
    }

    /// <summary>True when the provider reads the endpoint box.</summary>
    public static bool UsesEndpoint(ProviderSelection provider) =>
        provider is ProviderSelection.Ollama or ProviderSelection.OpenAICompatible;

    /// <summary>True for servers the user runs (local or LAN), which get the long timeout.</summary>
    public static bool IsLocal(ProviderSelection provider) =>
        provider is ProviderSelection.Ollama or ProviderSelection.OpenAICompatible;

    /// <summary>True when the provider cannot work without an API key.</summary>
    public static bool RequiresApiKey(ProviderSelection provider) =>
        provider is ProviderSelection.Anthropic or ProviderSelection.OpenAI or ProviderSelection.OpenRouter;

    /// <summary>
    /// True when the API key box applies (a custom server may take an optional key). The
    /// subscription CLIs use their own sign-in, so they take no key.
    /// </summary>
    public static bool AcceptsApiKey(ProviderSelection provider) =>
        provider != ProviderSelection.Ollama && !IsSubscription(provider);

    /// <summary>Request timeout applied to every call for this provider.</summary>
    public static int TimeoutSecondsFor(ProviderSelection provider) =>
        IsSubscription(provider) ? SubscriptionTimeoutSeconds
        : IsLocal(provider) ? LocalTimeoutSeconds
        : HostedTimeoutSeconds;

    /// <summary>Endpoint used when the box is left blank, or null when one must be typed.</summary>
    public static Uri? DefaultEndpoint(ProviderSelection provider) =>
        provider == ProviderSelection.Ollama ? OllamaProviderOptions.DefaultBaseUrl : null;

    /// <summary>Environment variable the key falls back to, or null when there is none.</summary>
    public static string? ApiKeyEnvironmentVariable(ProviderSelection provider) => provider switch
    {
        ProviderSelection.Anthropic => AnthropicKeyEnvironmentVariable,
        ProviderSelection.OpenAI => OpenAICompatibleProviderOptions.OpenAIKeyEnvironmentVariable,
        ProviderSelection.OpenRouter => OpenAICompatibleProviderOptions.OpenRouterKeyEnvironmentVariable,
        _ => null,
    };

    /// <summary>
    /// Model suggestions shown before any listing (Anthropic's known IDs, the subscription CLIs'
    /// models; others none).
    /// </summary>
    public static IReadOnlyList<string> SuggestedModels(ProviderSelection provider) =>
        provider == ProviderSelection.Anthropic ? AnthropicProvider.KnownModels
        : IsSubscription(provider) ? SubscriptionCliOptions.KnownModels(CliBackend(provider))
        : Array.Empty<string>();

    /// <summary>
    /// Picks the first installed model from <see cref="PreferredOllamaModels"/>, else the first
    /// installed tag, else null.
    /// </summary>
    public static string? PreselectModel(IReadOnlyList<string>? installed)
    {
        if (installed is null || installed.Count == 0)
        {
            return null;
        }

        foreach (var preferred in PreferredOllamaModels)
        {
            if (installed.Contains(preferred, StringComparer.Ordinal))
            {
                return preferred;
            }
        }

        return installed.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    /// <summary>
    /// The key the provider will use: the in-memory override when set, else the provider's
    /// environment variable, else null.
    /// </summary>
    public static string? ResolveApiKey(
        ProviderSelection provider,
        string? apiKeyOverride,
        Func<string, string?>? environment = null)
    {
        if (!string.IsNullOrWhiteSpace(apiKeyOverride))
        {
            return apiKeyOverride!.Trim();
        }

        var variable = ApiKeyEnvironmentVariable(provider);
        if (variable is null)
        {
            return null;
        }

        var value = (environment ?? Environment.GetEnvironmentVariable)(variable);
        return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
    }

    /// <summary>
    /// Parses and checks an endpoint the way the providers do: absolute <c>http</c>/<c>https</c>, a
    /// host, no user-info, query, or fragment. A blank box takes <see cref="DefaultEndpoint"/>.
    /// Providers that do not use an endpoint get null.
    /// </summary>
    public static bool TryParseEndpoint(
        ProviderSelection provider,
        string? endpointText,
        out Uri? endpoint,
        out string error)
    {
        endpoint = null;
        error = string.Empty;
        if (!UsesEndpoint(provider))
        {
            return true;
        }

        var text = endpointText?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            endpoint = DefaultEndpoint(provider);
            if (endpoint is null)
            {
                error = "Enter the server URL, for example http://localhost:11434/v1.";
                return false;
            }
            return true;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
        {
            error = $"\"{text}\" is not an absolute URL.";
            return false;
        }

        if (!BoundedHttp.TryValidateEndpoint(parsed, out var validation))
        {
            error = validation;
            return false;
        }

        endpoint = parsed;
        return true;
    }

    /// <summary>
    /// Validates the whole settings form. On success <paramref name="configuration"/> holds the
    /// trimmed model, the parsed endpoint, and the key override (null when blank).
    /// </summary>
    public static bool TryValidate(
        ProviderSelection provider,
        string? model,
        string? endpointText,
        string? apiKeyOverride,
        out ProviderConfiguration? configuration,
        out string error) =>
        TryValidate(provider, model, endpointText, apiKeyOverride, effort: null, executablePath: null, out configuration, out error);

    /// <summary>
    /// Validates the whole settings form, including the subscription CLIs' effort (blank takes
    /// <see cref="DefaultEffort"/>) and optional executable path. The model and effort go on the
    /// CLI's command line, so they must be plain ids.
    /// </summary>
    public static bool TryValidate(
        ProviderSelection provider,
        string? model,
        string? endpointText,
        string? apiKeyOverride,
        string? effort,
        string? executablePath,
        out ProviderConfiguration? configuration,
        out string error)
    {
        configuration = null;
        var trimmedModel = model?.Trim();
        if (string.IsNullOrEmpty(trimmedModel))
        {
            error = "Choose or type a model.";
            return false;
        }

        if (IsSubscription(provider))
        {
            var backend = CliBackend(provider);
            if (!SubscriptionCliOptions.IsSafeModelName(trimmedModel))
            {
                error = $"\"{trimmedModel}\" is not a model id (letters, digits and . _ : - only).";
                return false;
            }

            var chosenEffort = string.IsNullOrWhiteSpace(effort) ? DefaultEffort : effort!.Trim();
            if (!SubscriptionCliOptions.IsKnownEffort(backend, chosenEffort))
            {
                error = $"Effort must be one of {string.Join(", ", SubscriptionCliOptions.EffortLevels(backend))}.";
                return false;
            }

            var path = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath!.Trim();
            error = string.Empty;
            configuration = new ProviderConfiguration(provider, trimmedModel!, null, null, chosenEffort, path);
            return true;
        }

        if (!TryParseEndpoint(provider, endpointText, out var endpoint, out error))
        {
            return false;
        }

        var keyOverride = AcceptsApiKey(provider) && !string.IsNullOrWhiteSpace(apiKeyOverride)
            ? apiKeyOverride!.Trim()
            : null;

        // Mirror OpenAICompatibleProvider: a bearer key never travels over plain http to a remote host.
        if (keyOverride is not null
            && endpoint is not null
            && string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !BoundedHttp.IsLoopback(endpoint))
        {
            error = $"Refusing to send an API key over plain http to {endpoint.Host}; use https.";
            return false;
        }

        configuration = new ProviderConfiguration(provider, trimmedModel!, endpoint, keyOverride);
        return true;
    }

    /// <summary>
    /// Builds the provider for <paramref name="configuration"/>, wrapped so every request carries
    /// <see cref="TimeoutSecondsFor"/>. Throws <see cref="ArgumentException"/> when the provider
    /// rejects its options.
    /// </summary>
    public static ProviderHandle CreateProvider(ProviderConfiguration configuration)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        OllamaProvider? ollama = null;
        IAIProvider inner;
        switch (configuration.Provider)
        {
            case ProviderSelection.Ollama:
                ollama = new OllamaProvider(new OllamaProviderOptions
                {
                    BaseUrl = configuration.Endpoint ?? OllamaProviderOptions.DefaultBaseUrl,
                });
                inner = ollama;
                break;
            case ProviderSelection.Anthropic:
                inner = new AnthropicProvider();
                break;
            case ProviderSelection.OpenAI:
                inner = new OpenAICompatibleProvider(WithKey(OpenAICompatibleProviderOptions.OpenAI(), configuration));
                break;
            case ProviderSelection.OpenRouter:
                inner = new OpenAICompatibleProvider(WithKey(OpenAICompatibleProviderOptions.OpenRouter(), configuration));
                break;
            case ProviderSelection.SubscriptionClaude:
            case ProviderSelection.SubscriptionCodex:
                inner = new SubscriptionCliProvider(new SubscriptionCliOptions
                {
                    Backend = CliBackend(configuration.Provider),
                    Model = configuration.Model,
                    Effort = string.IsNullOrWhiteSpace(configuration.Effort) ? DefaultEffort : configuration.Effort!,
                    ExecutablePath = configuration.ExecutablePath,
                    Timeout = TimeSpan.FromSeconds(SubscriptionTimeoutSeconds),
                });
                break;
            case ProviderSelection.OpenAICompatible:
                var options = new OpenAICompatibleProviderOptions
                {
                    Name = "OpenAI-compatible",
                    BaseUrl = configuration.Endpoint
                        ?? throw new ArgumentException("An endpoint is required.", nameof(configuration)),
                    TokenLimitField = OpenAICompatibleProviderOptions.MaxTokensField,
                };
                inner = new OpenAICompatibleProvider(WithKey(options, configuration));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(configuration), configuration.Provider, "Unknown provider.");
        }

        return new ProviderHandle(
            new RequestTimeoutProvider(inner, TimeoutSecondsFor(configuration.Provider)),
            ollama);
    }

    /// <summary>
    /// The key the session sends with each request. Anthropic reads the request key; the
    /// OpenAI-compatible providers resolve theirs from options and environment, so the session
    /// sends none.
    /// </summary>
    public static string SessionApiKey(ProviderConfiguration configuration, Func<string, string?>? environment = null)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        return configuration.Provider == ProviderSelection.Anthropic
            ? ResolveApiKey(ProviderSelection.Anthropic, configuration.ApiKeyOverride, environment) ?? string.Empty
            : string.Empty;
    }

    private static OpenAICompatibleProviderOptions WithKey(
        OpenAICompatibleProviderOptions options,
        ProviderConfiguration configuration)
    {
        options.ApiKey = configuration.ApiKeyOverride;
        return options;
    }
}
