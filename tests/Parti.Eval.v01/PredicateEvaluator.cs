// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Design;

namespace Parti.Eval.V01;

/// <summary>
/// Evaluates the <see cref="FixtureExpected"/> block against a finished
/// <see cref="FixtureRunResult"/> and returns every violation, plus the run's invariant failures
/// (lease containment, one result per tool_use, timeout). An empty list means the fixture passes
/// (A14: pass = expectations validate, including the expected terminal status).
/// </summary>
public static class PredicateEvaluator
{
    /// <summary>Message fragments of lock rejections (freeze-time policy and dispatch-time delete).</summary>
    private static readonly string[] LockRejectionMarkers =
    {
        "is locked by the user",
        "not allowed while any component is locked",
        "gh_component_lock must not be combined",
    };

    private const string CatalogRejectionMarker = "is not installed in this Grasshopper";

    /// <summary>Evaluates every predicate and returns the failures.</summary>
    public static List<PredicateFailure> Evaluate(FixtureRunResult result)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        var expected = result.Fixture.Expected;
        var failures = new List<PredicateFailure>(result.InvariantFailures);

        void Exact(string name, int? want, int actual)
        {
            if (want.HasValue && want.Value != actual)
            {
                failures.Add(new PredicateFailure(name, want.Value.ToString(), actual.ToString()));
            }
        }

        if (!string.IsNullOrWhiteSpace(expected.TerminalStatus) &&
            !expected.TerminalStatus!.Split('|').Contains(result.TerminalStatus.ToString(), StringComparer.Ordinal))
        {
            failures.Add(new PredicateFailure("terminalStatus", expected.TerminalStatus!,
                $"{result.TerminalStatus}{(string.IsNullOrEmpty(result.FinalReturn.ErrorMessage) ? "" : $" ({Trim(result.FinalReturn.ErrorMessage!)})")}"));
        }

        if (!string.IsNullOrWhiteSpace(expected.FinalStopReason) &&
            !string.Equals(expected.FinalStopReason, result.FinalReturn.StopReason, StringComparison.Ordinal))
        {
            failures.Add(new PredicateFailure("finalStopReason", expected.FinalStopReason!, result.FinalReturn.StopReason ?? "<null>"));
        }

        if (!string.IsNullOrWhiteSpace(expected.FinalStatus) &&
            !string.Equals(expected.FinalStatus, result.FinalReturn.Status.ToString(), StringComparison.Ordinal))
        {
            failures.Add(new PredicateFailure("finalStatus", expected.FinalStatus!, result.FinalReturn.Status.ToString()));
        }

        if (expected.MinInteractions.HasValue && result.History.Count < expected.MinInteractions.Value)
        {
            failures.Add(new PredicateFailure("minInteractions", $">= {expected.MinInteractions.Value}", result.History.Count.ToString()));
        }

        var assistantTexts = AssistantTexts(result);
        if (!string.IsNullOrWhiteSpace(expected.TextContains) &&
            !assistantTexts.Any(t => t.Contains(expected.TextContains!, StringComparison.Ordinal)))
        {
            failures.Add(new PredicateFailure("textContains", $"substring '{expected.TextContains}' in assistant text", SummariseTexts(assistantTexts)));
        }

        Exact("toolCallCount", expected.ToolCallCount, result.ToolDispatchCount);
        Exact("providerCallCount", expected.ProviderCallCount, result.ProviderCallCount);

        if (!string.IsNullOrWhiteSpace(expected.ErrorContains))
        {
            var err = result.FinalReturn.ErrorMessage ?? string.Empty;
            if (!err.Contains(expected.ErrorContains!, StringComparison.Ordinal))
            {
                failures.Add(new PredicateFailure("errorContains", $"substring '{expected.ErrorContains}' in ErrorMessage",
                    string.IsNullOrEmpty(err) ? "<null or empty>" : err));
            }
        }

        foreach (var name in expected.ToolCallsInclude ?? new List<string>())
        {
            if (!result.PerToolCallCounts.ContainsKey(name))
            {
                failures.Add(new PredicateFailure("toolCallsInclude", $"tool '{name}' is called at least once",
                    $"tool '{name}' was not called. Called tools: {SummariseTools(result)}"));
            }
        }

        foreach (var name in expected.ToolCallsExclude ?? new List<string>())
        {
            if (result.PerToolCallCounts.TryGetValue(name, out var count) && count > 0)
            {
                failures.Add(new PredicateFailure("toolCallsExclude", $"tool '{name}' is NOT called", $"tool '{name}' was called {count} time(s)"));
            }
        }

        foreach (var kv in expected.ToolCallsMinCount ?? new Dictionary<string, int>())
        {
            var actual = result.PerToolCallCounts.TryGetValue(kv.Key, out var c) ? c : 0;
            if (actual < kv.Value)
            {
                failures.Add(new PredicateFailure("toolCallsMinCount", $"tool '{kv.Key}' called >= {kv.Value} time(s)", $"tool '{kv.Key}' called {actual} time(s)"));
            }
        }

        foreach (var kv in expected.ToolCallsMaxCount ?? new Dictionary<string, int>())
        {
            var actual = result.PerToolCallCounts.TryGetValue(kv.Key, out var c) ? c : 0;
            if (actual > kv.Value)
            {
                failures.Add(new PredicateFailure("toolCallsMaxCount", $"tool '{kv.Key}' called <= {kv.Value} time(s)", $"tool '{kv.Key}' called {actual} time(s)"));
            }
        }

        if (!string.IsNullOrWhiteSpace(expected.FinalTextContains) &&
            !assistantTexts.Any(t => t.Contains(expected.FinalTextContains!, StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add(new PredicateFailure("finalTextContains", $"substring '{expected.FinalTextContains}' in assistant text (case-insensitive)", SummariseTexts(assistantTexts)));
        }

        if (expected.FinalTextContainsAny is { Count: > 0 } anyNeedles &&
            !anyNeedles.Any(n => !string.IsNullOrEmpty(n) && assistantTexts.Any(t => t.Contains(n, StringComparison.OrdinalIgnoreCase))))
        {
            failures.Add(new PredicateFailure("finalTextContainsAny", $"at least one of [{string.Join(", ", anyNeedles.Select(Quote))}] (case-insensitive)", SummariseTexts(assistantTexts)));
        }

        if (expected.FinalTextContainsAll is { Count: > 0 } allNeedles)
        {
            var missing = allNeedles
                .Where(n => !string.IsNullOrEmpty(n))
                .Where(n => !assistantTexts.Any(t => t.Contains(n, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (missing.Count > 0)
            {
                failures.Add(new PredicateFailure("finalTextContainsAll", $"all of [{string.Join(", ", allNeedles.Select(Quote))}] (case-insensitive)",
                    $"missing: [{string.Join(", ", missing.Select(Quote))}]. texts: {SummariseTexts(assistantTexts)}"));
            }
        }

        // ---- product-loop predicates (D7) ----

        Exact("planReviewCount", expected.PlanReviewCount, result.Plans.Count);

        if (expected.PlanLabelsContain is { Count: > 0 } labelNeedles)
        {
            var labels = result.Plans.SelectMany(p => p.Calls).Select(c => c.ResolvedLabel ?? string.Empty).ToList();
            foreach (var needle in labelNeedles.Where(n => !labels.Any(l => l.Contains(n, StringComparison.Ordinal))))
            {
                failures.Add(new PredicateFailure("planLabelsContain", $"a reviewed label contains '{needle}'",
                    labels.Count == 0 ? "<no reviewed calls>" : string.Join(" | ", labels)));
            }
        }

        if (expected.DecompositionComplete is { } complete)
        {
            var gate = PillarGate.Evaluate(result.Decomposition);
            var actual = gate.Outcome == PillarGateOutcome.Pass;
            if (actual != complete)
            {
                failures.Add(new PredicateFailure("decompositionComplete", complete.ToString(),
                    $"{actual} (missing: [{string.Join(", ", gate.MissingPillars)}])"));
            }
        }

        foreach (var needle in expected.DecompositionInputsInclude ?? new List<string>())
        {
            if (!result.Decomposition.Inputs.Any(i => i.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add(new PredicateFailure("decompositionInputsInclude", $"an input named like '{needle}'",
                    $"inputs: [{string.Join(", ", result.Decomposition.Inputs.Select(i => i.Name))}]"));
            }
        }

        foreach (var evaluation in expected.EvaluationsInclude ?? new List<FixtureEvaluationExpectation>())
        {
            if (!result.Decomposition.Evaluations.Any(ev =>
                    ev.Metric.Contains(evaluation.Metric, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ev.Direction.ToString(), evaluation.Direction, StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add(new PredicateFailure("evaluationsInclude", $"{evaluation.Direction} '{evaluation.Metric}'",
                    $"evaluations: [{string.Join(", ", result.Decomposition.Evaluations.Select(ev => $"{ev.Direction.ToString().ToUpperInvariant()} {ev.Metric}"))}]"));
            }
        }

        if (result.Canvas is { } canvas)
        {
            var components = canvas.Components;
            foreach (var want in expected.CanvasHasComponent ?? new List<FixtureCanvasComponentExpectation>())
            {
                var types = (want.AnyOf ?? new List<string> { want.ComponentGuid }).Select(g => Guid.Parse(g).ToString("D")).ToList();
                var have = components.Count(c => types.Contains(c.ComponentGuid, StringComparer.OrdinalIgnoreCase));
                if (have < want.Min || (want.Max is { } max && have > max))
                {
                    var bound = want.Max is { } m ? $"{want.Min}..{m}" : $">= {want.Min}";
                    failures.Add(new PredicateFailure("canvasHasComponent", $"{bound} of {string.Join(" or ", types)}", $"{have} ({CanvasSummary(canvas)})"));
                }
            }

            Exact("canvasWireCount", expected.CanvasWireCount, canvas.Wires.Count);

            foreach (var want in expected.CanvasHasWire ?? new List<FixtureCanvasWireExpectation>())
            {
                var from = (want.FromAnyOf ?? new List<string> { want.From }).Select(g => Guid.Parse(g).ToString("D")).ToList();
                var to = (want.ToAnyOf ?? new List<string> { want.To }).Select(g => Guid.Parse(g).ToString("D")).ToList();
                var typeOf = components.ToDictionary(c => c.InstanceGuid, c => c.ComponentGuid, StringComparer.Ordinal);
                // An endpoint is either one exact instance or any component of that type.
                bool Matches(string instance, List<string> wants) => wants.Any(want =>
                    string.Equals(instance, want, StringComparison.OrdinalIgnoreCase) ||
                    (typeOf.TryGetValue(instance, out var type) && string.Equals(type, want, StringComparison.OrdinalIgnoreCase)));
                if (!canvas.Wires.Any(w => Matches(w.From, from) && Matches(w.To, to)))
                {
                    failures.Add(new PredicateFailure("canvasHasWire", $"a wire {string.Join(" or ", from)} -> {string.Join(" or ", to)}", CanvasSummary(canvas)));
                }
            }

            if (expected.CanvasUnchanged is { } unchanged && canvas.Unchanged != unchanged)
            {
                failures.Add(new PredicateFailure("canvasUnchanged", unchanged.ToString(), $"{canvas.Unchanged} ({CanvasSummary(canvas)})"));
            }
        }

        var results = result.History.OfType<AIInteractionToolResult>().Select(r => r.Result ?? new JObject()).ToList();
        Exact("gateRejections", expected.GateRejections, results.Count(r => (string?)r["gate"] == "three_pillar"));
        Exact("lockRejections", expected.LockRejections, results.Count(IsLockRejection));
        Exact("catalogRejections", expected.CatalogRejections, results.Count(r => IsRejected(r) && Message(r).Contains(CatalogRejectionMarker, StringComparison.Ordinal)));

        if (expected.RejectionMessagesContain is { Count: > 0 } rejectionNeedles)
        {
            var messages = results.Where(r => IsRejected(r) || (string?)r["status"] == "locked").Select(Message).ToList();
            foreach (var needle in rejectionNeedles.Where(n => !messages.Any(m => m.Contains(n, StringComparison.Ordinal))))
            {
                failures.Add(new PredicateFailure("rejectionMessagesContain", $"a rejection message contains '{needle}'",
                    messages.Count == 0 ? "<no rejected results>" : string.Join(" | ", messages.Select(Trim))));
            }
        }

        if (expected.LockedComponents is { } wantLocked)
        {
            var want = wantLocked.Select(g => Guid.TryParse(g, out var id) ? id.ToString("D") : g).OrderBy(g => g, StringComparer.Ordinal).ToList();
            if (!want.SequenceEqual(result.LockedComponents.OrderBy(g => g, StringComparer.Ordinal), StringComparer.Ordinal))
            {
                failures.Add(new PredicateFailure("lockedComponents", $"[{string.Join(", ", want)}]", $"[{string.Join(", ", result.LockedComponents)}]"));
            }
        }

        foreach (var id in expected.PausedWith ?? new List<string>())
        {
            var shown = result.View.Questions.SelectMany(q => q).Select(q => q.Id).ToList();
            if (!shown.Contains(id, StringComparer.Ordinal))
            {
                failures.Add(new PredicateFailure("pausedWith", $"question '{id}' shown", shown.Count == 0 ? "<no questions shown>" : string.Join(", ", shown)));
            }
        }

        return failures;
    }

    private static bool IsRejected(JObject result) => result["rejected"]?.Type == JTokenType.Boolean && (bool)result["rejected"]!;

    private static bool IsLockRejection(JObject result) =>
        (string?)result["status"] == "locked" ||
        (IsRejected(result) && LockRejectionMarkers.Any(m => Message(result).Contains(m, StringComparison.Ordinal)));

    private static string Message(JObject result) => (string?)result["message"] ?? string.Empty;

    private static string CanvasSummary(HermeticCanvas canvas) =>
        $"{canvas.Components.Count} components [{string.Join(", ", canvas.Components.Select(c => c.Name))}], {canvas.Wires.Count} wires";

    /// <summary>Every assistant text in the run's history.</summary>
    public static List<string> AssistantTexts(FixtureRunResult result) =>
        result.History
            .OfType<AIInteractionText>()
            .Where(t => t.Agent == AIAgent.Assistant)
            .Select(t => t.Content ?? string.Empty)
            .ToList();

    private static string SummariseTexts(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0)
        {
            return "<no assistant text>";
        }
        return Trim(string.Join(" | ", texts));
    }

    private static string Trim(string text) => text.Length > 240 ? text[..240] + "..." : text;

    private static string SummariseTools(FixtureRunResult result)
    {
        if (result.PerToolCallCounts.Count == 0)
        {
            return "<none>";
        }
        return string.Join(", ", result.PerToolCallCounts
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static string Quote(string s) => $"'{s}'";
}

/// <summary>A single predicate violation produced by <see cref="PredicateEvaluator.Evaluate"/>.</summary>
public sealed class PredicateFailure
{
    public string PredicateName { get; }
    public string Expected { get; }
    public string Actual { get; }

    public PredicateFailure(string predicateName, string expected, string actual)
    {
        PredicateName = predicateName ?? throw new ArgumentNullException(nameof(predicateName));
        Expected = expected ?? string.Empty;
        Actual = actual ?? string.Empty;
    }

    public override string ToString() =>
        $"[{PredicateName}] expected: {Expected}; actual: {Actual}";
}
