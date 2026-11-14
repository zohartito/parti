// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Parti.Core.Ai;
using Parti.Core.Grasshopper.Chat;
using Parti.Core.Prompts;
using Parti.Core.Sessions;
using Parti.Core.Tools;

namespace Parti.Eval.V01;

/// <summary>
/// Runs a fixture through the product loop: <see cref="PartiComposition.CreateController"/> builds
/// the same <see cref="ChatController"/> the chat window uses (skill-bundle prompt, streaming, the
/// three-pillar gate, the lock and catalog policies, strict declared arguments, the product
/// repair cap), with test doubles only at the edges:
/// <list type="bullet">
///   <item>the provider (<see cref="ScriptedProvider"/>, or a real one for live runs), behind a
///   transparent <see cref="MeteredProvider"/>;</item>
///   <item>the view (<see cref="RecordingChatView"/>) and the reviewer (<see cref="RecordingReviewer"/>);</item>
///   <item>the host: <see cref="LeaseUndoScopeFactory"/> under the product's
///   <see cref="UndoScopeTurnEnvelopeFactory"/> (decorated by <see cref="RecordingTurnEnvelopeFactory"/>),
///   the Rhino 8 catalog snapshot, and <see cref="EvalToolSet"/> in place of the Grasshopper
///   primitives.</item>
/// </list>
/// After every run it checks two loop invariants whatever the fixture expects: no document lease
/// reached history, the view, a plan, or a provider request; and (unless paused) every tool_use
/// has exactly one tool_result.
/// </summary>
public static class FixtureRunner
{
    /// <summary>Model id sent by scripted runs.</summary>
    public const string ScriptedModel = "eval-model";

    /// <summary>Upper bound on one scripted run; a hang fails the fixture instead of the suite.</summary>
    public static readonly TimeSpan ScriptedTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long a timed-out run may take to honour cancellation.</summary>
    public static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(60);

    /// <summary>Runs a scripted fixture against its provider script.</summary>
    public static Task<FixtureRunResult> RunAsync(EvalFixture fixture)
    {
        if (fixture is null) throw new ArgumentNullException(nameof(fixture));
        return RunAsync(fixture, new ProviderSetup(new ScriptedProvider(fixture.ProviderScript), ScriptedModel, null, PartiComposition.DefaultMaxTokens, ScriptedTimeout));
    }

    /// <summary>Runs a fixture against the provider in <paramref name="setup"/>.</summary>
    public static async Task<FixtureRunResult> RunAsync(EvalFixture fixture, ProviderSetup setup)
    {
        if (fixture is null) throw new ArgumentNullException(nameof(fixture));
        if (setup is null) throw new ArgumentNullException(nameof(setup));

        var catalog = FixtureLoader.Catalog;
        var leases = new LeaseUndoScopeFactory();
        var envelopes = new RecordingTurnEnvelopeFactory(new UndoScopeTurnEnvelopeFactory(leases));
        var tools = new EvalToolSet(catalog, leases, fixture.ToolHandlers, fixture.CanvasSeed, fixture.Canvas == "hermetic");
        var reviewer = new RecordingReviewer(approve: fixture.Reviewer != "reject");
        var provider = new MeteredProvider(setup.Provider);
        var view = new RecordingChatView();

        var options = new PartiCompositionOptions(
            Provider: provider,
            UndoFactory: leases,
            // The source root is the folder that CONTAINS parti-foundations. Pointing it at
            // parti-foundations itself silently fell back to FallbackSystemPrompt (fixed 2026-09-27).
            Skills: new FileSystemSkillSource(Path.Combine(FixtureLoader.RepoRoot(), "prompts")),
            Catalog: catalog,
            Reviewer: reviewer.ReviewAsync,
            Model: setup.Model,
            MaxTokens: setup.MaxTokens,
            ToolsOverride: tools.Dispatcher,
            ApiKey: setup.ApiKey,
            Temperature: setup.Temperature,
            SessionOverrides: Overrides(fixture.SessionOptions),
            EnvelopeFactory: envelopes);

        using var controller = PartiComposition.CreateController(options, view);
        var locks = tools.BindTo(controller);
        foreach (var locked in fixture.CanvasSeed?.Locked ?? new List<string>())
        {
            locks.Lock(locked);
        }

        var sw = Stopwatch.StartNew();
        var timedOut = false;
        var loop = Drive(controller, fixture);
        if (await Task.WhenAny(loop, Task.Delay(setup.Timeout)).ConfigureAwait(false) != loop)
        {
            timedOut = true;
            controller.Cancel();
            // Cancellation is cooperative; a provider that ignores it must not hang the suite.
            if (await Task.WhenAny(loop, Task.Delay(CancelGrace)).ConfigureAwait(false) != loop)
            {
                throw new TimeoutException($"Fixture '{fixture.Id}' did not stop within {CancelGrace.TotalSeconds:0} s of cancellation.");
            }
        }
        await loop.ConfigureAwait(false);
        sw.Stop();

        var history = controller.History.ToList();
        var result = new FixtureRunResult
        {
            Fixture = fixture,
            FinalReturn = controller.LastReturn ?? new AIReturn { Status = AICallStatus.Error, ErrorMessage = "No loop ran." },
            TerminalStatus = controller.Status,
            History = history,
            Decomposition = controller.CurrentDecomposition,
            LockedComponents = controller.LockedComponents,
            View = view,
            Plans = reviewer.Plans,
            Envelopes = envelopes,
            Tools = tools,
            Canvas = tools.Canvas,
            Provider = provider,
            ProviderCallCount = provider.Calls,
            ToolDispatchCount = tools.DispatchCount,
            PerToolCallCounts = CountToolUses(history),
            DurationMs = sw.ElapsedMilliseconds,
        };

        if (timedOut)
        {
            result.InvariantFailures.Add(new PredicateFailure("timeout", $"run finishes within {setup.Timeout.TotalSeconds:0} s", "cancelled after the timeout"));
        }
        CheckLeaseNeverLeaves(result, leases, reviewer);
        CheckOneResultPerToolUse(result);
        return result;
    }

    private static async Task Drive(ChatController controller, EvalFixture fixture)
    {
        await controller.SendAsync(fixture.UserPrompt).ConfigureAwait(false);
        if (controller.Status == SessionStatus.Paused && fixture.ClarificationAnswers is { Count: > 0 } answers)
        {
            await controller.AnswerAsync(answers.Select(a => new ClarificationAnswer(a.Id, a.Answer)).ToList()).ConfigureAwait(false);
        }
    }

    private static SessionOptions? Overrides(FixtureSessionOptions? fixtureOptions)
    {
        if (fixtureOptions is null)
        {
            return null;
        }
        return new SessionOptions
        {
            MaxTurns = fixtureOptions.MaxTurns ?? PartiComposition.ProductMaxTurns,
            MaxToolPasses = fixtureOptions.MaxToolPasses ?? PartiComposition.ProductMaxToolPasses,
        };
    }

    private static Dictionary<string, int> CountToolUses(IEnumerable<AIInteractionBase> history)
    {
        var perTool = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var call in ToolUses(history))
        {
            perTool[call.Name!] = perTool.TryGetValue(call.Name!, out var n) ? n + 1 : 1;
        }
        return perTool;
    }

    /// <summary>The model's tool_use entries in history (results excluded).</summary>
    public static IEnumerable<AIInteractionToolCall> ToolUses(IEnumerable<AIInteractionBase> history) =>
        history.OfType<AIInteractionToolCall>()
            .Where(c => c is not AIInteractionToolResult && c.Agent == AIAgent.ToolCall && !string.IsNullOrEmpty(c.Name));

    private static void CheckLeaseNeverLeaves(FixtureRunResult result, LeaseUndoScopeFactory leases, RecordingReviewer reviewer)
    {
        var surfaces = new List<(string Where, string Text)>
        {
            ("history", JsonConvert.SerializeObject(RequestSnapshot.Interactions(result.History.Cast<IAIInteraction>()))),
            // Streams are concatenated exactly as displayed, so a token split across deltas is found.
            ("assistant stream", string.Concat(result.View.AssistantDeltas)),
            ("reasoning stream", string.Concat(result.View.ReasoningDeltas)),
            ("errors", string.Join("\n", result.View.Errors)),
            ("questions", JsonConvert.SerializeObject(result.View.Questions)),
            ("decomposition card", JsonConvert.SerializeObject(result.View.Decompositions.Select(d => d.Decomposition))),
            ("final return", JsonConvert.SerializeObject(RequestSnapshot.Interactions(result.FinalReturn.Interactions ?? new List<IAIInteraction>()))
                + result.FinalReturn.ErrorMessage),
        };
        surfaces.AddRange(reviewer.PlanSnapshots.Select(p => ("plan", p)));
        surfaces.AddRange(result.Provider.RequestSnapshots.Select(r => ("provider request", r)));

        var needles = leases.IssuedTokens.Append(EvalToolSet.LeaseArgument).ToList();
        foreach (var (where, text) in surfaces)
        {
            if (needles.Any(n => text.Contains(n, StringComparison.Ordinal)))
            {
                result.InvariantFailures.Add(new PredicateFailure("leaseNeverLeavesDispatch", "no document lease outside dispatch copies", $"lease found in {where}"));
                return;
            }
        }
    }

    private static void CheckOneResultPerToolUse(FixtureRunResult result)
    {
        var useList = ToolUses(result.History).ToList();
        if (result.TerminalStatus == SessionStatus.Paused &&
            useList.LastOrDefault(c => c.Name == AskUserTool.ToolName) is { } pending)
        {
            // Only the pending ask_user is exempt: it gets its single result when the loop resumes.
            useList.Remove(pending);
        }
        var uses = useList.Select(c => c.Id ?? string.Empty).OrderBy(i => i, StringComparer.Ordinal).ToList();
        var results = result.History.OfType<AIInteractionToolResult>().Select(r => r.Id ?? string.Empty).OrderBy(i => i, StringComparer.Ordinal).ToList();
        if (!uses.SequenceEqual(results, StringComparer.Ordinal))
        {
            result.InvariantFailures.Add(new PredicateFailure(
                "oneResultPerToolUse",
                $"one tool_result per tool_use ({uses.Count})",
                $"{results.Count} results for ids [{string.Join(", ", uses)}]"));
        }
    }
}

/// <summary>Provider-side settings for one run.</summary>
public sealed record ProviderSetup(IAIProvider Provider, string Model, double? Temperature, int MaxTokens, TimeSpan Timeout, string? ApiKey = null);

/// <summary>Everything a predicate, the reporter, or the live runner needs from one run.</summary>
public sealed class FixtureRunResult
{
    public EvalFixture Fixture { get; set; } = default!;
    public AIReturn FinalReturn { get; set; } = default!;

    /// <summary>The controller's status after the run (Idle, Paused or Error).</summary>
    public SessionStatus TerminalStatus { get; set; }

    public IReadOnlyList<AIInteractionBase> History { get; set; } = Array.Empty<AIInteractionBase>();
    public Parti.Core.Design.Decomposition Decomposition { get; set; } = default!;
    public IReadOnlyList<string> LockedComponents { get; set; } = Array.Empty<string>();
    public RecordingChatView View { get; set; } = default!;
    public IReadOnlyList<ToolCallPlan> Plans { get; set; } = Array.Empty<ToolCallPlan>();
    public RecordingTurnEnvelopeFactory Envelopes { get; set; } = default!;
    public EvalToolSet Tools { get; set; } = default!;
    public HermeticCanvas? Canvas { get; set; }
    public MeteredProvider Provider { get; set; } = default!;
    public int ProviderCallCount { get; set; }

    /// <summary>Calls that reached the tool dispatcher (after guard, policies, gate and review).</summary>
    public int ToolDispatchCount { get; set; }

    /// <summary>The model's tool_use count per tool name (rejected calls included).</summary>
    public Dictionary<string, int> PerToolCallCounts { get; set; } = new();

    /// <summary>Loop invariants checked on every run, whatever the fixture expects.</summary>
    public List<PredicateFailure> InvariantFailures { get; } = new();

    /// <summary>Wall time of the run. Never written to the committed hermetic report.</summary>
    public long DurationMs { get; set; }
}
