// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using Parti.Components.Chat;
using Parti.Providers.Cli;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// Settings rules for the subscription (CLI) providers: names, models, effort, no API key, the
/// status line, and provider construction.
/// </summary>
public class SubscriptionSettingsTests
{
    [Theory]
    [InlineData(ProviderSelection.SubscriptionClaude, "Subscription (Claude CLI)", "claude-opus-5-5,claude-fable-5-1,claude-sonnet-5")]
    [InlineData(ProviderSelection.SubscriptionCodex, "Subscription (Codex CLI)", "gpt-6-astra,gpt-6-sol")]
    public void SubscriptionProviders_NameModelsAndNoKey(ProviderSelection provider, string name, string models)
    {
        Assert.Equal(name, ProviderSettings.DisplayName(provider));
        Assert.Equal(models.Split(','), ProviderSettings.SuggestedModels(provider));
        Assert.True(ProviderSettings.IsSubscription(provider));
        Assert.False(ProviderSettings.AcceptsApiKey(provider));
        Assert.False(ProviderSettings.RequiresApiKey(provider));
        Assert.False(ProviderSettings.UsesEndpoint(provider));
        Assert.Null(ProviderSettings.ApiKeyEnvironmentVariable(provider));
        Assert.Contains("medium", ProviderSettings.EffortLevels(provider));
        Assert.Equal(180, ProviderSettings.TimeoutSecondsFor(provider));
    }

    [Fact]
    public void TryValidate_Subscription_DefaultsEffort_DropsKeyAndEndpoint()
    {
        Assert.True(ProviderSettings.TryValidate(ProviderSelection.SubscriptionClaude, " claude-opus-5-5 ", "http://x", "typed-key",
            effort: null, executablePath: "  ", out var configuration, out var error), error);

        Assert.Equal(new ProviderConfiguration(ProviderSelection.SubscriptionClaude, "claude-opus-5-5", null, null, "medium", null), configuration);
    }

    [Theory]
    [InlineData("claude-opus-5-5 --dangerously-skip-permissions", "high")]
    [InlineData("-p", "high")]
    [InlineData("claude-opus-5-5", "turbo")]
    [InlineData("claude-opus-5-5", "ultra")] // codex-only level
    public void TryValidate_Subscription_RejectsUnsafeModelOrUnknownEffort(string model, string effort)
    {
        Assert.False(ProviderSettings.TryValidate(ProviderSelection.SubscriptionClaude, model, null, null, effort, null, out var configuration, out var error));
        Assert.Null(configuration);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Describe_Subscription_ShowsModelAndCli()
    {
        Assert.Equal("Subscription: claude-opus-5-5 via claude CLI",
            ProviderSettings.Describe(new ProviderConfiguration(ProviderSelection.SubscriptionClaude, "claude-opus-5-5", null, null, "medium")));
        Assert.Equal("Subscription: gpt-6-astra via codex CLI",
            ProviderSettings.Describe(new ProviderConfiguration(ProviderSelection.SubscriptionCodex, "gpt-6-astra", null, null)));
        Assert.Equal("Ollama (local) / qwen3.6:27b",
            ProviderSettings.Describe(new ProviderConfiguration(ProviderSelection.Ollama, "qwen3.6:27b", null, null)));
    }

    [Fact]
    public void CreateProvider_Subscription_BuildsTheCliProvider()
    {
        var handle = ProviderSettings.CreateProvider(new ProviderConfiguration(
            ProviderSelection.SubscriptionCodex, "gpt-6-sol", null, null, "high", "/opt/homebrew/bin/codex"));

        var wrapper = Assert.IsType<RequestTimeoutProvider>(handle.Provider);
        var provider = Assert.IsType<SubscriptionCliProvider>(wrapper.Inner);
        Assert.Null(handle.Ollama);
        Assert.Equal("Subscription (Codex CLI)", provider.Name);
        var options = provider.Options;
        Assert.Equal(CliBackendKind.Codex, options.Backend);
        Assert.Equal("gpt-6-sol", options.Model);
        Assert.Equal("high", options.Effort);
        Assert.Equal("/opt/homebrew/bin/codex", options.ExecutablePath);
        Assert.Equal(TimeSpan.FromSeconds(180), options.Timeout);
        Assert.Equal(string.Empty, ProviderSettings.SessionApiKey(new ProviderConfiguration(ProviderSelection.SubscriptionCodex, "gpt-6-sol", null, null)));
    }
}
