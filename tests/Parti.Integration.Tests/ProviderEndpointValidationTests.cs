// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Providers.Ollama;
using Parti.Providers.OpenAI;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// S19: provider endpoints are validated, requests only ever go to the configured endpoint, and
/// credentials only reach the configured host.
/// </summary>
public class ProviderEndpointValidationTests
{
    public static IEnumerable<object[]> InvalidEndpoints => new[]
    {
        new object[] { "ftp://localhost:11434" },
        new object[] { "file:///etc/passwd" },
        new object[] { "http://user:secret@localhost:11434" },
        new object[] { "http://localhost:11434/?redirect=evil" },
        new object[] { "http://localhost:11434/#frag" },
    };

    private static AIRequestCall Request() => new()
    {
        Model = "m",
        Interactions = new List<IAIInteraction> { new AIInteractionText { Agent = AIAgent.User, Content = "hi" } },
    };

    private static string OkOllama => ProviderHttp.Ndjson(ProviderHttp.OllamaLine(content: "ok"), ProviderHttp.OllamaLine(done: true, doneReason: "stop"));

    private static string OkSse => ProviderHttp.Sse(
        new JObject { ["choices"] = new JArray { new JObject { ["index"] = 0, ["delta"] = new JObject { ["content"] = "ok" }, ["finish_reason"] = "stop" } } })
        + ProviderHttp.SseDone;

    [Theory]
    [MemberData(nameof(InvalidEndpoints))]
    public void InvalidEndpoints_AreRejectedAtConstruction(string endpoint)
    {
        var uri = new Uri(endpoint);
        Assert.False(BoundedHttp.TryValidateEndpoint(uri, out var error));
        Assert.False(string.IsNullOrEmpty(error));

        Assert.Throws<ArgumentException>(() => new OllamaProvider(new OllamaProviderOptions { BaseUrl = uri }));
        var options = OpenAICompatibleProviderOptions.OpenAI();
        options.BaseUrl = uri;
        Assert.Throws<ArgumentException>(() => new OpenAICompatibleProvider(options));
    }

    [Fact]
    public void RelativeEndpoint_IsRejected()
    {
        Assert.False(BoundedHttp.TryValidateEndpoint(new Uri("/api", UriKind.Relative), out _));
        Assert.False(BoundedHttp.TryValidateEndpoint(null, out _));
    }

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://ollama-host:11434")]
    [InlineData("https://openrouter.ai/api/v1")]
    [InlineData("http://[::1]:11434")]
    public void ValidEndpoints_AreAccepted(string endpoint)
    {
        Assert.True(BoundedHttp.TryValidateEndpoint(new Uri(endpoint), out var error), error);
    }

    [Fact]
    public async Task BearerNeverSentWhenAuthenticationNone()
    {
        var server = ProviderHttp.Serve(OkOllama, OkOllama);
        using var provider = new OllamaProvider(server.Client);
        var request = Request();
        request.ApiKey = "sk-should-never-leave";

        await ProviderHttp.CollectAsync(provider.CallStreamAsync(request));
        await provider.CallAsync(Request());

        Assert.Equal("none", request.Authentication);
        Assert.All(server.Requests, r =>
        {
            Assert.Null(r.Authorization);
            Assert.DoesNotContain("sk-should-never-leave", r.Body);
        });
    }

    [Fact]
    public async Task Ollama_RequestForAnotherEndpoint_IsRejectedBeforeSend()
    {
        var server = ProviderHttp.Serve(OkOllama);
        using var provider = new OllamaProvider(server.Client, new OllamaProviderOptions { BaseUrl = new Uri("http://ollama-host:11434") });
        var request = Request();
        request.BaseUrl = new Uri("http://evil.example:11434");

        var final = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(request)))[^1].Final!;
        var nonStreaming = await provider.CallAsync(request);

        Assert.False(final.Success);
        Assert.False(nonStreaming.Success);
        Assert.Contains("does not match the configured", final.ErrorMessage);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task OpenAICompat_BearerSentOnlyToConfiguredHost()
    {
        var server = ProviderHttp.Serve(OkSse);
        var options = OpenAICompatibleProviderOptions.OpenRouter();
        options.ApiKey = "sk-or-test";
        using var provider = new OpenAICompatibleProvider(server.Client, options);

        var foreign = Request();
        foreign.BaseUrl = new Uri("https://attacker.example/api/v1");
        var rejected = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(foreign)))[^1].Final!;
        Assert.False(rejected.Success);
        Assert.Empty(server.Requests);

        var ok = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request())))[^1].Final!;
        Assert.True(ok.Success, ok.ErrorMessage);
        var sent = Assert.Single(server.Requests);
        Assert.Equal("openrouter.ai", sent.Uri.Host);
        Assert.Equal("Bearer sk-or-test", sent.Authorization);
    }

    [Fact]
    public async Task OpenAICompat_ApiKeyNeverSentOverPlainHttpToRemoteHost()
    {
        var server = ProviderHttp.Serve(OkSse);
        var options = OpenAICompatibleProviderOptions.OllamaOpenAI("ollama-host");
        options.ApiKey = "sk-plain";
        using var provider = new OpenAICompatibleProvider(server.Client, options);

        var final = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request())))[^1].Final!;

        Assert.False(final.Success);
        Assert.Contains("plain http", final.ErrorMessage);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task OpenAICompat_NoKey_SendsNoAuthorization()
    {
        var server = ProviderHttp.Serve(OkSse);
        using var provider = new OpenAICompatibleProvider(server.Client, OpenAICompatibleProviderOptions.OllamaOpenAI("ollama-host"));

        var final = (await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request())))[^1].Final!;

        Assert.True(final.Success, final.ErrorMessage);
        Assert.Null(Assert.Single(server.Requests).Authorization);
        Assert.Equal("http://ollama-host:11434/v1/chat/completions", server.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task OpenAICompat_OnlyAllowlistedExtraHeadersAreSent()
    {
        var server = ProviderHttp.Serve(OkSse);
        var options = OpenAICompatibleProviderOptions.OllamaOpenAI();
        options.ExtraHeaders["HTTP-Referer"] = "https://github.com/zohartito/parti";
        options.ExtraHeaders["X-Title"] = "Parti";
        using var provider = new OpenAICompatibleProvider(server.Client, options);
        var request = Request();
        request.Headers["X-Forwarded-Host"] = "evil";

        await ProviderHttp.CollectAsync(provider.CallStreamAsync(request));

        var headers = Assert.Single(server.Requests).Headers;
        Assert.Equal("https://github.com/zohartito/parti", headers["HTTP-Referer"]);
        Assert.Equal("Parti", headers["X-Title"]);
        Assert.False(headers.ContainsKey("X-Forwarded-Host"));
    }

    [Theory]
    [InlineData("Authorization", "Bearer x")]
    [InlineData("X-Forwarded-Host", "evil")]
    [InlineData("X-Title", "bad\r\nInjected: 1")]
    public void OpenAICompat_DisallowedExtraHeaders_AreRejectedAtConstruction(string name, string value)
    {
        var options = OpenAICompatibleProviderOptions.OpenRouter();
        options.ExtraHeaders[name] = value;

        Assert.Throws<ArgumentException>(() => new OpenAICompatibleProvider(options));
    }

    [Fact]
    public void OpenAICompat_UnknownTokenLimitField_IsRejected()
    {
        var options = OpenAICompatibleProviderOptions.OpenAI();
        options.TokenLimitField = "max_output_tokens";

        Assert.Throws<ArgumentException>(() => new OpenAICompatibleProvider(options));
    }

    [Fact]
    public async Task OptionsAreSnapshottedAtConstruction()
    {
        var server = ProviderHttp.Serve(OkSse, OkOllama);
        var options = OpenAICompatibleProviderOptions.OllamaOpenAI("ollama-host");
        using var provider = new OpenAICompatibleProvider(server.Client, options);
        options.BaseUrl = new Uri("https://attacker.example/v1");
        options.ApiKey = "sk-late";

        await ProviderHttp.CollectAsync(provider.CallStreamAsync(Request()));

        var ollamaOptions = new OllamaProviderOptions { BaseUrl = new Uri("http://ollama-host:11434") };
        using var ollama = new OllamaProvider(server.Client, ollamaOptions);
        ollamaOptions.BaseUrl = new Uri("http://attacker.example:11434");
        await ProviderHttp.CollectAsync(ollama.CallStreamAsync(Request()));

        Assert.All(server.Requests, r => Assert.Equal("ollama-host", r.Uri.Host));
        Assert.All(server.Requests, r => Assert.Null(r.Authorization));
    }

    [Fact]
    public void Ollama_InvalidThinkValue_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new OllamaProvider(new OllamaProviderOptions { Think = "maximum" }));
        using var ok = new OllamaProvider(new OllamaProviderOptions { Think = true });
        Assert.Equal(JTokenType.Boolean, ok.Options.Think!.Type);
    }
}
