// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Parti.Eval.V01;

/// <summary>
/// Network-free tests for the live runner's plumbing: the briefs load under the live rules, the
/// egress guard blocks foreign hosts, and the EVAL-LIVE tables render. The live run itself is
/// <see cref="LiveModelRunner"/>, skipped unless <c>PARTI_LIVE_EVAL=1</c>.
/// </summary>
public class LiveRunnerTests
{
    [Fact]
    public void LiveBriefs_LoadUnderLiveRules()
    {
        var briefs = FixtureLoader.DiscoverLiveFixtureFiles();
        Assert.True(briefs.Count >= 10, $"expected about 10 live briefs, found {briefs.Count}");
        foreach (var path in briefs)
        {
            var brief = FixtureLoader.Load(path, live: true);
            Assert.Empty(brief.ProviderScript);
            Assert.Equal("hermetic", brief.Canvas);
        }
    }

    // ---- live runner plumbing (no network) ----

    [Fact]
    public async Task EgressRecorder_FlagsForeignHost()
    {
        var egress = new EgressRecorder(new Uri("http://ollama-host:11434"), new StubHandler());
        using var client = new HttpClient(egress);

        using var allowed = await client.GetAsync("http://ollama-host:11434/api/tags");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.example.com/v1/chat"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://ollama-host:8080/")); // same host, other port

        Assert.Equal(2, egress.ForeignRequests.Count);
        Assert.Equal("api.example.com", egress.ForeignRequests[0].Host);
        Assert.Equal(3, egress.Requests.Count);
    }

    [Fact]
    public void LiveReport_RendersPassRatesAndMedians()
    {
        var runs = new JArray(
            new JObject { ["fixture"] = "L01", ["pass"] = true, ["tokens"] = 100, ["latencyMs"] = 1000, ["terminalStatus"] = "Idle", ["failures"] = new JArray() },
            new JObject { ["fixture"] = "L01", ["pass"] = false, ["tokens"] = 300, ["latencyMs"] = 3000, ["terminalStatus"] = "Error", ["failures"] = new JArray("[terminalStatus] expected: Idle; actual: Error") });
        var file = new JObject
        {
            ["model"] = "m:1", ["provider"] = "ollama", ["endpoint"] = "http://h:1", ["gitSha"] = "abc", ["date"] = "2026-09-27",
            ["repeats"] = 2, ["temperature"] = 0, ["maxTokens"] = 4096, ["summary"] = LiveReport.Summarize(runs), ["runs"] = runs,
        };

        var tables = LiveReport.BuildTables(new[] { file }, "bench/results/2026-09-27");
        Assert.Contains("| L01 | 1/2 |", tables);
        Assert.Contains("1/2 (50%)", tables);
        Assert.Contains("terminalStatus×1", tables);
        Assert.Contains("| 200 | 2.0 s |", tables);
        Assert.Equal("qwen3.6_27b", LiveReport.SafeName("qwen3.6:27b"));
    }

    [Fact]
    public void CliProvider_SplitsBackendAndModel_AndNamesResultsByModel()
    {
        var config = new LiveConfig("cli", new Uri("cli://local"), new[] { "claude:claude-opus-5-5", "codex:gpt-6-astra" },
            1, Array.Empty<string>(), TimeSpan.FromMinutes(1), null, CliEffort: "low");

        Assert.Equal((Parti.Providers.Cli.CliBackendKind.Claude, "claude-opus-5-5"), LiveConfig.SplitCliModel("claude:claude-opus-5-5"));
        Assert.Equal(("gpt-6-astra", "cli:codex"), config.Describe("codex:gpt-6-astra"));
        Assert.Throws<InvalidOperationException>(() => LiveConfig.SplitCliModel("gpt-6-astra"));
        Assert.Throws<InvalidOperationException>(() => LiveConfig.SplitCliModel("copilot:gpt-6"));

        var (provider, egress, client) = LiveModelRunner.CreateProvider(config, "codex:gpt-6-astra");
        using (client)
        {
            var cli = Assert.IsType<Parti.Providers.Cli.SubscriptionCliProvider>(provider);
            Assert.Equal(Parti.Providers.Cli.CliBackendKind.Codex, cli.Options.Backend);
            Assert.Equal("gpt-6-astra", cli.Options.Model);
            Assert.Equal("low", cli.Options.Effort);
            Assert.Empty(egress.Requests);
        }

        var ollama = new LiveConfig("ollama", new Uri("http://h:1"), new[] { "qwen3.6:27b" }, 1, Array.Empty<string>(), TimeSpan.FromMinutes(1), null);
        Assert.Equal(("qwen3.6:27b", "ollama"), ollama.Describe("qwen3.6:27b"));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }
}
