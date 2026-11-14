// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Grasshopper.Chat;
using Parti.Providers.Anthropic;
using Parti.Providers.Cli;
using Parti.Providers.Ollama;
using Parti.Providers.OpenAI;
using Xunit;
using Xunit.Abstractions;

namespace Parti.Eval.V01;

/// <summary>
/// A fact that is skipped unless <c>PARTI_LIVE_EVAL=1</c>, so the gate never calls a model.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PARTI_LIVE_EVAL") != "1")
        {
            Skip = "Live-model eval: set PARTI_LIVE_EVAL=1 (see docs/EVAL-LIVE.md).";
        }
    }
}

/// <summary>Live runner settings, read from the environment.</summary>
public sealed record LiveConfig(
    string Provider,
    Uri Endpoint,
    IReadOnlyList<string> Models,
    int Repeats,
    IReadOnlyList<string> FixtureFilter,
    TimeSpan RunTimeout,
    string? ApiKey,
    string? ProgressLog = null,
    int MaxTokens = LiveConfig.ProductMaxTokens,
    string CliEffort = SubscriptionCliOptions.DefaultEffort)
{
    /// <summary>Sampling temperature for every live call (D7: temperature 0).</summary>
    public const double Temperature = 0;

    /// <summary>
    /// Output budget the chat window sends (<c>ProviderSettings.DefaultMaxTokens</c> in
    /// Parti.Components, which the eval cannot reference). <c>PartiComposition.DefaultMaxTokens</c>
    /// is 4096; the product passes 8192.
    /// </summary>
    public const int ProductMaxTokens = 8192;

    public static LiveConfig FromEnvironment()
    {
        static string Env(string name, string fallback) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

        var provider = Env("PARTI_LIVE_PROVIDER", "ollama").ToLowerInvariant();
        var endpoint = new Uri(Env("PARTI_LIVE_ENDPOINT", provider switch
        {
            "anthropic" => "https://api.anthropic.com",
            "openai-compat" => "http://localhost:11434/v1",
            // The CLI provider dials nothing from this process; the placeholder is never contacted.
            "cli" => "cli://local",
            _ => "http://localhost:11434",
        }));
        var models = Env("PARTI_LIVE_MODELS", "qwen3.6:27b,gpt-oss:20b")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var repeats = int.Parse(Env("PARTI_LIVE_REPEATS", "3"), CultureInfo.InvariantCulture);
        var filter = Env("PARTI_LIVE_FIXTURES", "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var timeout = TimeSpan.FromSeconds(int.Parse(Env("PARTI_LIVE_RUN_TIMEOUT_S", "900"), CultureInfo.InvariantCulture));
        var apiKey = Environment.GetEnvironmentVariable(provider == "anthropic" ? "ANTHROPIC_API_KEY" : "PARTI_LIVE_API_KEY");
        var progress = Environment.GetEnvironmentVariable("PARTI_LIVE_PROGRESS_LOG");
        var maxTokens = int.Parse(Env("PARTI_LIVE_MAX_TOKENS", ProductMaxTokens.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        var cliEffort = Env("PARTI_LIVE_CLI_EFFORT", SubscriptionCliOptions.DefaultEffort);
        return new LiveConfig(provider, endpoint, models, Math.Max(1, repeats), filter, timeout, apiKey,
            string.IsNullOrWhiteSpace(progress) ? null : progress, maxTokens, cliEffort);
    }

    /// <summary>
    /// Splits a <c>PARTI_LIVE_PROVIDER=cli</c> model entry, <c>backend:model</c> (for example
    /// <c>claude:claude-opus-5-5</c> or <c>codex:gpt-6-astra</c>), into the CLI and the model id.
    /// </summary>
    public static (CliBackendKind Backend, string Model) SplitCliModel(string entry)
    {
        var colon = entry.IndexOf(':', StringComparison.Ordinal);
        var backend = colon > 0 ? entry.Substring(0, colon).Trim().ToLowerInvariant() : string.Empty;
        var model = colon > 0 ? entry.Substring(colon + 1).Trim() : string.Empty;
        return backend switch
        {
            "claude" when model.Length > 0 => (CliBackendKind.Claude, model),
            "codex" when model.Length > 0 => (CliBackendKind.Codex, model),
            _ => throw new InvalidOperationException($"PARTI_LIVE_MODELS entry '{entry}' is not claude:<model> or codex:<model>."),
        };
    }

    /// <summary>
    /// The model id sent to the provider (and used to name the results file) and the provider label
    /// recorded in the results: the CLI entry's model part and <c>cli:claude</c> / <c>cli:codex</c>
    /// for <c>PARTI_LIVE_PROVIDER=cli</c>, else the entry and the provider unchanged.
    /// </summary>
    public (string ModelId, string ProviderLabel) Describe(string modelEntry)
    {
        if (Provider != "cli")
        {
            return (modelEntry, Provider);
        }
        var (backend, model) = SplitCliModel(modelEntry);
        return (model, $"cli:{SubscriptionCliOptions.ExecutableName(backend)}");
    }
}

/// <summary>
/// Live-model runner (D7): the same briefs format, predicates and product composition as the
/// hermetic gate, with a real provider and the <see cref="HermeticCanvas"/>. Each brief under
/// fixtures/live/ runs <c>PARTI_LIVE_REPEATS</c> times per model at temperature 0 with the
/// chat window's output budget. Results go to
/// <c>bench/results/&lt;date&gt;/&lt;model&gt;.json</c> (with compact transcripts) and the tables in
/// <c>docs/EVAL-LIVE.md</c> are regenerated from every result file of that date. The run fails
/// only if the <see cref="EgressRecorder"/> saw a host other than the configured endpoint; low pass
/// rates are results, not test failures.
/// <para>Environment: <c>PARTI_LIVE_EVAL=1</c>, <c>PARTI_LIVE_PROVIDER=ollama|openai-compat|anthropic</c>,
/// <c>PARTI_LIVE_ENDPOINT</c>, <c>PARTI_LIVE_MODELS</c>, <c>PARTI_LIVE_REPEATS</c>, and optionally
/// <c>PARTI_LIVE_FIXTURES</c> (comma-separated ids; a subset rerun replaces only those briefs in
/// the day's file), <c>PARTI_LIVE_RUN_TIMEOUT_S</c>, and
/// <c>PARTI_LIVE_PROGRESS_LOG</c> (a file that gets one line per finished run), and
/// <c>PARTI_LIVE_MAX_TOKENS</c> (default 8192, the chat window's budget). With
/// <c>PARTI_LIVE_PROVIDER=cli</c>, <c>PARTI_LIVE_MODELS</c> entries are <c>claude:&lt;model&gt;</c> or
/// <c>codex:&lt;model&gt;</c> and <c>PARTI_LIVE_CLI_EFFORT</c> (default medium) sets the effort; the
/// signed-in CLI does the network calls, so the egress recorder sees none from Parti.</para>
/// </summary>
[Trait("lane", "live")]
public class LiveModelRunner
{
    private readonly ITestOutputHelper _output;

    public LiveModelRunner(ITestOutputHelper output) => _output = output;

    [LiveFact]
    public async Task RunLiveBriefs()
    {
        var config = LiveConfig.FromEnvironment();
        var briefs = FixtureLoader.DiscoverLiveFixtureFiles()
            .Select(p => FixtureLoader.Load(p, live: true))
            .Where(f => config.FixtureFilter.Count == 0 || config.FixtureFilter.Contains(f.Id, StringComparer.Ordinal))
            .ToList();
        Assert.NotEmpty(briefs);

        var date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); // local date of the run
        var gitSha = GitSha();
        var resultsDir = Path.Combine(FixtureLoader.RepoRoot(), "bench", "results", date);
        Directory.CreateDirectory(resultsDir);
        var foreign = new List<Uri>();

        foreach (var model in config.Models)
        {
            var runs = new JArray();
            await Warm(config, model, foreign).ConfigureAwait(false);
            foreach (var brief in briefs)
            {
                for (var repeat = 1; repeat <= config.Repeats; repeat++)
                {
                    var record = await RunOnce(config, model, brief, repeat, foreign).ConfigureAwait(false);
                    record["gitSha"] = gitSha;
                    record["maxTokens"] = config.MaxTokens;
                    if (config.Provider == "cli")
                    {
                        record["cliEffort"] = config.CliEffort; // recorded per run: a subset rerun may use another effort
                    }
                    runs.Add(record);
                    Progress(config, $"{DateTime.Now:HH:mm:ss} {model} {brief.Id} #{repeat}: {(record.Value<bool>("pass") ? "pass" : "FAIL")} " +
                        $"{record.Value<string>("terminalStatus")} {record.Value<long?>("latencyMs")} ms {record["failures"]?.ToString(Formatting.None)}");
                }
                // Written after every brief so an interrupted run keeps what it measured.
                WriteResults(config, resultsDir, date, gitSha, config.Describe(model), briefs, runs);
            }
        }

        Assert.True(foreign.Count == 0, $"Egress to a foreign host: {string.Join(", ", foreign)}");
    }

    private void Progress(LiveConfig config, string line)
    {
        _output.WriteLine(line);
        if (config.ProgressLog is { } path)
        {
            File.AppendAllText(path, line + "\n");
        }
    }

    private static void WriteResults(LiveConfig config, string resultsDir, string date, string gitSha, (string ModelId, string ProviderLabel) described,
        IReadOnlyList<EvalFixture> briefs, JArray runs)
    {
        var model = described.ModelId;
        var path = Path.Combine(resultsDir, $"{LiveReport.SafeName(model)}.json");
        var allRuns = new JArray(runs.Select(r => r.DeepClone()));
        var allBriefs = briefs.Select(b => new JObject { ["id"] = b.Id, ["description"] = b.Description, ["userPrompt"] = b.UserPrompt }).ToList();

        // A subset rerun (PARTI_LIVE_FIXTURES) replaces only those briefs in the day's file for
        // this model; the other briefs' runs, and the SHA each run was measured at, are kept.
        if (config.FixtureFilter.Count > 0 && File.Exists(path))
        {
            var existing = JObject.Parse(File.ReadAllText(path));
            var rerun = briefs.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
            var kept = (existing["runs"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(r => !rerun.Contains(r.Value<string>("fixture")!)).ToList();
            allRuns = new JArray(kept.Concat(allRuns.OfType<JObject>())
                .OrderBy(r => r.Value<string>("fixture"), StringComparer.Ordinal).ThenBy(r => r.Value<int>("repeat")));
            allBriefs = (existing["briefs"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(b => !rerun.Contains(b.Value<string>("id")!))
                .Concat(allBriefs).OrderBy(b => b.Value<string>("id"), StringComparer.Ordinal).ToList();
        }

        var shas = allRuns.OfType<JObject>().Select(r => r.Value<string>("gitSha") ?? gitSha).Distinct(StringComparer.Ordinal).ToList();
        var budgets = allRuns.OfType<JObject>().Select(r => r.Value<int?>("maxTokens") ?? PartiComposition.DefaultMaxTokens).Distinct().OrderBy(b => b).ToList();
        var file = new JObject
        {
            ["schema"] = "parti.eval.live.v1",
            ["date"] = date,
            ["gitSha"] = string.Join(",", shas.Count == 0 ? new List<string> { gitSha } : shas),
            ["provider"] = described.ProviderLabel,
            ["endpoint"] = config.Endpoint.GetLeftPart(UriPartial.Authority),
            ["model"] = model,
            ["repeats"] = config.Repeats,
            ["temperature"] = LiveConfig.Temperature,
            ["maxTokens"] = string.Join(",", budgets),
            ["cliEffort"] = string.Join(",", allRuns.OfType<JObject>().Select(r => r.Value<string>("cliEffort")).Where(e => e is not null).Distinct()),
            ["briefs"] = new JArray(allBriefs),
            ["summary"] = LiveReport.Summarize(allRuns),
            ["runs"] = allRuns,
        };
        File.WriteAllText(path, file.ToString(Formatting.Indented) + "\n");
        LiveReport.WriteDoc(FixtureLoader.RepoRoot(), resultsDir);
    }

    private async Task Warm(LiveConfig config, string model, List<Uri> foreign)
    {
        if (config.Provider != "ollama")
        {
            return;
        }
        var (provider, egress, client) = CreateProvider(config);
        try
        {
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var ok = await ((OllamaProvider)provider).WarmAsync(model, cts.Token).ConfigureAwait(false);
            _output.WriteLine($"warm {model}: {ok} in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"warm {model} failed: {ex.Message}");
        }
        finally
        {
            foreign.AddRange(egress.ForeignRequests);
            client.Dispose();
        }
    }

    private static async Task<JObject> RunOnce(LiveConfig config, string model, EvalFixture brief, int repeat, List<Uri> foreign)
    {
        var (provider, egress, client) = CreateProvider(config, model);
        try
        {
            FixtureRunResult run;
            try
            {
                run = await FixtureRunner.RunAsync(brief, new ProviderSetup(provider, config.Describe(model).ModelId, LiveConfig.Temperature,
                    config.MaxTokens, config.RunTimeout, config.ApiKey)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["fixture"] = brief.Id,
                    ["repeat"] = repeat,
                    ["pass"] = false,
                    ["terminalStatus"] = "crash",
                    ["failures"] = new JArray($"[crash] {ex.GetType().Name}: {ex.Message}"),
                    ["latencyMs"] = null,
                    ["tokens"] = null,
                };
            }

            var failures = PredicateEvaluator.Evaluate(run);
            return new JObject
            {
                ["fixture"] = brief.Id,
                ["repeat"] = repeat,
                ["pass"] = failures.Count == 0,
                ["terminalStatus"] = run.TerminalStatus.ToString(),
                ["finalCallStatus"] = run.FinalReturn.Status.ToString(),
                ["error"] = run.FinalReturn.ErrorMessage,
                ["failures"] = new JArray(failures.Select(f => f.ToString())),
                ["providerCalls"] = run.ProviderCallCount,
                ["inputTokens"] = run.Provider.InputTokens,
                ["outputTokens"] = run.Provider.OutputTokens,
                ["tokens"] = run.Provider.InputTokens + run.Provider.OutputTokens,
                ["latencyMs"] = run.DurationMs,
                ["providerMs"] = run.Provider.ProviderMs,
                ["stopReasons"] = new JArray(run.Provider.StopReasons),
                ["toolSequence"] = new JArray(FixtureRunner.ToolUses(run.History).Select(c => c.Name)),
                ["dispatched"] = new JArray(run.Tools.Dispatched),
                ["planReviews"] = run.Plans.Count,
                ["planLabels"] = new JArray(run.Plans.SelectMany(p => p.Calls).Select(c => c.ResolvedLabel ?? c.Name)),
                ["decompositionComplete"] = Parti.Core.Design.PillarGate.Evaluate(run.Decomposition).Outcome == Parti.Core.Design.PillarGateOutcome.Pass,
                ["canvas"] = run.Canvas is { } canvas
                    ? new JObject
                    {
                        ["components"] = new JArray(canvas.Components.Select(c => c.Name)),
                        ["wires"] = canvas.Wires.Count,
                        ["unchanged"] = canvas.Unchanged,
                    }
                    : null,
                ["egressHosts"] = new JArray(egress.Hosts),
                ["transcript"] = LiveReport.Transcript(run.History),
            };
        }
        finally
        {
            foreign.AddRange(egress.ForeignRequests);
            client.Dispose();
        }
    }

    /// <summary>
    /// Builds the configured provider over a fresh <see cref="HttpClient"/> whose handler chain
    /// starts with an <see cref="EgressRecorder"/> pinned to the configured endpoint. The CLI
    /// provider (<c>cli</c>, which needs <paramref name="modelEntry"/> to pick claude or codex) never
    /// uses the client: the recorder then stays empty because Parti itself sends nothing.
    /// </summary>
    public static (IAIProvider Provider, EgressRecorder Egress, HttpClient Client) CreateProvider(LiveConfig config, string? modelEntry = null)
    {
        // Redirects are not followed: a redirect would leave the handler chain without passing the guard.
        var egress = new EgressRecorder(config.Endpoint, new HttpClientHandler { AllowAutoRedirect = false });
        var client = new HttpClient(egress) { Timeout = Timeout.InfiniteTimeSpan };
        IAIProvider provider = config.Provider switch
        {
            "ollama" => new OllamaProvider(client, new OllamaProviderOptions { BaseUrl = config.Endpoint }),
            "openai-compat" => new OpenAICompatibleProvider(client, new OpenAICompatibleProviderOptions
            {
                Name = "OpenAI-compatible (live eval)",
                BaseUrl = config.Endpoint,
                TokenLimitField = OpenAICompatibleProviderOptions.MaxTokensField,
                ApiKey = config.ApiKey,
                ApiKeyEnvironmentVariable = null,
            }),
            "anthropic" => config.Endpoint.Host == "api.anthropic.com"
                ? new AnthropicProvider(client)
                : throw new InvalidOperationException("The Anthropic provider has no endpoint option; PARTI_LIVE_ENDPOINT must be https://api.anthropic.com."),
            "cli" => new SubscriptionCliProvider(new SubscriptionCliOptions
            {
                Backend = LiveConfig.SplitCliModel(modelEntry ?? throw new InvalidOperationException("The cli provider needs a model entry.")).Backend,
                Model = LiveConfig.SplitCliModel(modelEntry).Model,
                Effort = config.CliEffort,
            }),
            _ => throw new InvalidOperationException($"PARTI_LIVE_PROVIDER '{config.Provider}' is not ollama, openai-compat, anthropic or cli."),
        };
        return (provider, egress, client);
    }

    private static string GitSha()
    {
        try
        {
            var root = FixtureLoader.RepoRoot();
            var sha = Git(root, "rev-parse --short HEAD");
            var dirty = Git(root, "status --porcelain --untracked-files=no");
            return string.IsNullOrWhiteSpace(sha) ? "unknown" : sha + (string.IsNullOrWhiteSpace(dirty) ? "" : "-dirty");
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string Git(string root, string args)
    {
        using var process = Process.Start(new ProcessStartInfo("git", args)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);
        return output.Trim();
    }
}

/// <summary>Live result files and the docs/EVAL-LIVE.md tables.</summary>
public static class LiveReport
{
    public const string StartMarker = "<!-- live-results:start -->";
    public const string EndMarker = "<!-- live-results:end -->";

    /// <summary>File-name-safe model id (e.g. <c>qwen3.6:27b</c> → <c>qwen3.6_27b</c>).</summary>
    public static string SafeName(string model) => Regex.Replace(model, "[^A-Za-z0-9._-]", "_");

    /// <summary>Per-brief pass counts, median tokens and median latency over <paramref name="runs"/>.</summary>
    public static JObject Summarize(JArray runs)
    {
        var summary = new JObject();
        foreach (var group in runs.OfType<JObject>().GroupBy(r => r.Value<string>("fixture")!).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = group.ToList();
            summary[group.Key] = new JObject
            {
                ["runs"] = list.Count,
                ["passes"] = list.Count(r => r.Value<bool>("pass")),
                ["medianTokens"] = Median(Known(list, "tokens")),
                ["medianLatencyMs"] = Median(Known(list, "latencyMs")),
                ["terminalStatuses"] = new JArray(list.Select(r => r.Value<string>("terminalStatus"))),
                ["failingPredicates"] = new JObject(list
                    .SelectMany(r => (r["failures"] as JArray ?? new JArray()).Select(f => PredicateName((string?)f)))
                    .GroupBy(n => n).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new JProperty(g.Key, g.Count()))),
            };
        }
        return summary;
    }

    /// <summary>
    /// Regenerates the tables in docs/EVAL-LIVE.md from every result file in
    /// <paramref name="resultsDir"/>. Text outside the markers (the analysis) is kept.
    /// </summary>
    public static void WriteDoc(string repoRoot, string resultsDir)
    {
        var files = Directory.GetFiles(resultsDir, "*.json").OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => JObject.Parse(File.ReadAllText(f))).ToList();
        var relative = Path.GetRelativePath(repoRoot, resultsDir).Replace('\\', '/');
        var tables = BuildTables(files, relative);

        var docPath = Path.Combine(repoRoot, "docs", "EVAL-LIVE.md");
        var existing = File.Exists(docPath) ? File.ReadAllText(docPath) : null;
        string doc;
        if (existing is not null && existing.Contains(StartMarker, StringComparison.Ordinal) && existing.Contains(EndMarker, StringComparison.Ordinal))
        {
            var start = existing.IndexOf(StartMarker, StringComparison.Ordinal);
            var end = existing.IndexOf(EndMarker, StringComparison.Ordinal) + EndMarker.Length;
            doc = existing[..start] + tables + existing[end..];
        }
        else
        {
            doc = "# Parti live-model eval\n\n" + tables + "\n\n## Analysis\n\n(not written yet)\n";
        }
        File.WriteAllText(docPath, doc);
    }

    /// <summary>Renders the marked tables block for a set of per-model result files.</summary>
    public static string BuildTables(IReadOnlyList<JObject> files, string resultsPath)
    {
        var sb = new StringBuilder();
        sb.Append(StartMarker).Append('\n');
        sb.Append($"_Generated by `Parti.Eval.V01.LiveModelRunner` from `{resultsPath}/*.json`; edit the analysis outside these markers only._\n\n");
        sb.Append("| model | provider | endpoint | git SHA | date | repeats | temperature | max tokens | pass rate | median tokens / run | median latency / run |\n");
        sb.Append("|-------|----------|----------|---------|------|---------|-------------|------------|-----------|---------------------|----------------------|\n");
        foreach (var f in files)
        {
            var runs = (f["runs"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var passes = runs.Count(r => r.Value<bool>("pass"));
            sb.Append($"| {f["model"]} | {f["provider"]} | {f["endpoint"]} | {f["gitSha"]} | {f["date"]} | {f["repeats"]} | {f["temperature"]} | {f["maxTokens"]} | ");
            sb.Append($"{passes}/{runs.Count} ({Percent(passes, runs.Count)}) | {Median(Known(runs, "tokens")):0} | {Seconds(Median(Known(runs, "latencyMs")))} |\n");
        }

        var briefIds = files.SelectMany(f => (f["summary"] as JObject ?? new JObject()).Properties().Select(p => p.Name))
            .Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal).ToList();

        sb.Append("\n### Pass rate per brief\n\n");
        sb.Append("| brief | ").Append(string.Join(" | ", files.Select(f => (string?)f["model"]))).Append(" |\n");
        sb.Append("|-------|").Append(string.Concat(files.Select(_ => "------|"))).Append('\n');
        foreach (var id in briefIds)
        {
            sb.Append($"| {id} | ");
            sb.Append(string.Join(" | ", files.Select(f => f["summary"]?[id] is JObject s
                ? $"{s["passes"]}/{s["runs"]}"
                : "—")));
            sb.Append(" |\n");
        }

        sb.Append("\n### Tokens, latency and failing predicates per brief (medians per run)\n\n");
        sb.Append("| brief | model | median tokens | median latency | terminal statuses | failing predicates (count over repeats) |\n");
        sb.Append("|-------|-------|---------------|----------------|-------------------|------------------------------------------|\n");
        foreach (var id in briefIds)
        {
            foreach (var f in files)
            {
                if (f["summary"]?[id] is not JObject s)
                {
                    continue;
                }
                var statuses = string.Join(", ", (s["terminalStatuses"] as JArray ?? new JArray()).Select(t => (string?)t));
                var failing = string.Join(", ", (s["failingPredicates"] as JObject ?? new JObject()).Properties().Select(p => $"{p.Name}×{p.Value}"));
                sb.Append($"| {id} | {f["model"]} | {s.Value<double>("medianTokens"):0} | {Seconds(s.Value<double>("medianLatencyMs"))} | {statuses} | {(failing.Length == 0 ? "—" : failing)} |\n");
            }
        }
        sb.Append(EndMarker);
        return sb.ToString();
    }

    /// <summary>Compact, truncated transcript of a run's history for the result file.</summary>
    public static JArray Transcript(IEnumerable<AIInteractionBase> history)
    {
        var transcript = new JArray();
        foreach (var entry in history)
        {
            switch (entry)
            {
                case AIInteractionToolResult r:
                    transcript.Add(new JObject { ["t"] = "result", ["name"] = r.Name, ["result"] = Trunc(r.Result?.ToString(Formatting.None), 500) });
                    break;
                case AIInteractionToolCall c:
                    transcript.Add(new JObject { ["t"] = "call", ["name"] = c.Name, ["args"] = Trunc(c.Arguments?.ToString(Formatting.None), 700) });
                    break;
                case AIInteractionText t:
                    var item = new JObject { ["t"] = t.Agent.ToString().ToLowerInvariant(), ["content"] = Trunc(t.Content, 900) };
                    if (!string.IsNullOrEmpty(t.Reasoning))
                    {
                        item["reasoning"] = Trunc(t.Reasoning, 700);
                    }
                    transcript.Add(item);
                    break;
            }
        }
        return transcript;
    }

    private static string PredicateName(string? failure)
    {
        if (string.IsNullOrEmpty(failure) || failure[0] != '[')
        {
            return "other";
        }
        var close = failure.IndexOf(']');
        return close > 1 ? failure[1..close] : "other";
    }

    private static string? Trunc(string? text, int max) =>
        text is null ? null : text.Length <= max ? text : text[..max] + $"… (+{text.Length - max} chars)";

    /// <summary>Values of <paramref name="key"/> that are known (a crashed run has none).</summary>
    private static IEnumerable<double> Known(IEnumerable<JObject> runs, string key) =>
        runs.Select(r => r[key]).Where(t => t is { Type: JTokenType.Integer or JTokenType.Float }).Select(t => t!.Value<double>());

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static string Percent(int passes, int runs) =>
        runs == 0 ? "—" : (100.0 * passes / runs).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Seconds(double ms) => (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
