// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// TreeHygieneAnalyzer — v0.7.5 primitive.
// Encodes the 9-op decision table from @abhinavbwj note [DWVzHYkiL3w]:
//   GRAFT, ENTWINE, FLATTEN, PATH MAPPER, TRIM, EXPLODE, FLIP MATRIX, RELATIVE ITEM, SHIFT.
// Parti audits a tool plan for tree-hygiene sins — consecutive tree ops that should be
// collapsed into a single Path Mapper — before committing components to the canvas.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Parti.Core.Design;

/// <summary>
/// Canonical names of the 9 tree-manipulation operations Parti is aware of.
/// Names match the Grasshopper component GUIDs / display names.
/// </summary>
public enum TreeOp
{
    /// <summary>Graft Tree — wraps each item in its own branch.</summary>
    Graft,
    /// <summary>Entwine — weaves multiple trees into one structured output.</summary>
    Entwine,
    /// <summary>Flatten Tree — collapses all branches into a single list.</summary>
    Flatten,
    /// <summary>Path Mapper — arbitrary tree transform via mapping expressions.</summary>
    PathMapper,
    /// <summary>Trim Tree — removes N levels from the outermost path.</summary>
    Trim,
    /// <summary>Explode Tree — splits a tree into N separate list outputs.</summary>
    Explode,
    /// <summary>Flip Matrix — swaps branches and items in a 2D tree.</summary>
    FlipMatrix,
    /// <summary>Relative Item — indexes items relative to their branch neighbors.</summary>
    RelativeItem,
    /// <summary>Shift Paths — offsets path indices by a fixed amount.</summary>
    Shift,
}

/// <summary>
/// Severity of a tree-hygiene finding. INFO is advisory; WARN recommends a refactor;
/// ERROR means the plan is definitely wrong and should be blocked.
/// </summary>
public enum HygieneSeverity
{
    Info,
    Warn,
    Error,
}

/// <summary>
/// A single finding produced by <see cref="TreeHygieneAnalyzer.Analyze"/>.
/// </summary>
/// <param name="Severity">Info / Warn / Error.</param>
/// <param name="Code">Short machine-readable code, e.g. "TH001-graft-flatten".</param>
/// <param name="Message">Human-readable explanation.</param>
/// <param name="StartIndex">Index into the input op-chain where the problem starts.</param>
/// <param name="Length">Number of consecutive ops involved.</param>
/// <param name="Suggestion">
/// Optional refactored chain. When non-null, callers can auto-apply the suggestion (or offer
/// it to the user as a preview diff).
/// </param>
public sealed record HygieneFinding(
    HygieneSeverity Severity,
    string Code,
    string Message,
    int StartIndex,
    int Length,
    IReadOnlyList<TreeOp>? Suggestion);

/// <summary>
/// Analyzes a proposed chain of tree operations for common sins and recommends
/// refactorings. Pure / stateless. Used by the planner before PlanReview emits a diff.
/// </summary>
public static class TreeHygieneAnalyzer
{
    /// <summary>
    /// Thresholds and toggles for the analyzer. Default values match the recommendations
    /// in note [DWVzHYkiL3w].
    /// </summary>
    public sealed record Options(int CollapseAfterConsecutive = 3);

    /// <summary>
    /// Examine <paramref name="chain"/> and produce all findings. Empty list means no
    /// issues detected.
    /// </summary>
    public static IReadOnlyList<HygieneFinding> Analyze(
        IReadOnlyList<TreeOp> chain,
        Options? options = null)
    {
        options ??= new Options();
        if (chain is null) throw new ArgumentNullException(nameof(chain));
        var findings = new List<HygieneFinding>();

        // Rule 1: 3+ consecutive tree ops should collapse into a single Path Mapper.
        // (Sampled on ANY consecutive run of tree ops other than a single Path Mapper.)
        for (int i = 0; i < chain.Count; i++)
        {
            int j = i;
            while (j < chain.Count && IsTreeOp(chain[j])) j++;
            int runLength = j - i;
            if (runLength >= options.CollapseAfterConsecutive &&
                !(runLength == 1 && chain[i] == TreeOp.PathMapper))
            {
                findings.Add(new HygieneFinding(
                    Severity: HygieneSeverity.Warn,
                    Code: "TH001-collapse-to-pathmapper",
                    Message: $"{runLength} consecutive tree ops — collapse into a single Path Mapper with an explicit mapping expression.",
                    StartIndex: i,
                    Length: runLength,
                    Suggestion: new[] { TreeOp.PathMapper }));
            }
            i = Math.Max(i, j - 1);
        }

        // Rule 2: Graft directly followed by Flatten is a no-op — flag as ERROR.
        for (int i = 0; i + 1 < chain.Count; i++)
        {
            if (chain[i] == TreeOp.Graft && chain[i + 1] == TreeOp.Flatten)
            {
                findings.Add(new HygieneFinding(
                    Severity: HygieneSeverity.Error,
                    Code: "TH002-graft-then-flatten",
                    Message: "Graft immediately followed by Flatten cancels out — remove both.",
                    StartIndex: i,
                    Length: 2,
                    Suggestion: Array.Empty<TreeOp>()));
            }
        }

        // Rule 3: Flatten followed by Graft throws away structure, then artificially
        // re-imposes it. Usually means the wrong tree was flattened upstream.
        for (int i = 0; i + 1 < chain.Count; i++)
        {
            if (chain[i] == TreeOp.Flatten && chain[i + 1] == TreeOp.Graft)
            {
                findings.Add(new HygieneFinding(
                    Severity: HygieneSeverity.Warn,
                    Code: "TH003-flatten-then-graft",
                    Message: "Flatten followed by Graft discards branch structure and re-wraps each item. If you need per-item branches, graft the source directly.",
                    StartIndex: i,
                    Length: 2,
                    Suggestion: new[] { TreeOp.Graft }));
            }
        }

        // Rule 4: Double Flatten is redundant.
        for (int i = 0; i + 1 < chain.Count; i++)
        {
            if (chain[i] == TreeOp.Flatten && chain[i + 1] == TreeOp.Flatten)
            {
                findings.Add(new HygieneFinding(
                    Severity: HygieneSeverity.Warn,
                    Code: "TH004-double-flatten",
                    Message: "Consecutive Flatten components are redundant.",
                    StartIndex: i,
                    Length: 2,
                    Suggestion: new[] { TreeOp.Flatten }));
            }
        }

        // Rule 5: Flip Matrix twice is a no-op.
        for (int i = 0; i + 1 < chain.Count; i++)
        {
            if (chain[i] == TreeOp.FlipMatrix && chain[i + 1] == TreeOp.FlipMatrix)
            {
                findings.Add(new HygieneFinding(
                    Severity: HygieneSeverity.Error,
                    Code: "TH005-double-flipmatrix",
                    Message: "Flip Matrix applied twice returns the original tree — remove both.",
                    StartIndex: i,
                    Length: 2,
                    Suggestion: Array.Empty<TreeOp>()));
            }
        }

        // Rule 6: Shift followed by opposite Shift — caller must track offsets for this
        // detection to fire; we conservatively flag two adjacent Shifts as potentially
        // collapsible.
        for (int i = 0; i + 1 < chain.Count; i++)
        {
            if (chain[i] == TreeOp.Shift && chain[i + 1] == TreeOp.Shift)
            {
                findings.Add(new HygieneFinding(
                    Severity: HygieneSeverity.Info,
                    Code: "TH006-double-shift",
                    Message: "Consecutive Shift components can usually be combined into a single Shift with the summed offset.",
                    StartIndex: i,
                    Length: 2,
                    Suggestion: new[] { TreeOp.Shift }));
            }
        }

        return findings;
    }

    /// <summary>
    /// Convenience: apply all findings' suggestions greedily to the chain and return the
    /// simplified result. Does not attempt to handle interacting findings — intended for
    /// preview UIs that want a "refactored" sample to show alongside the warnings.
    /// </summary>
    public static IReadOnlyList<TreeOp> ApplySuggestions(
        IReadOnlyList<TreeOp> chain,
        IReadOnlyList<HygieneFinding> findings)
    {
        // Sort descending so index-based splices don't invalidate later findings.
        var ordered = findings
            .Where(f => f.Suggestion is not null)
            .OrderByDescending(f => f.StartIndex)
            .ToList();

        var result = chain.ToList();
        foreach (var f in ordered)
        {
            if (f.StartIndex < 0 || f.StartIndex + f.Length > result.Count) continue;
            result.RemoveRange(f.StartIndex, f.Length);
            if (f.Suggestion!.Count > 0)
            {
                result.InsertRange(f.StartIndex, f.Suggestion);
            }
        }
        return result;
    }

    private static bool IsTreeOp(TreeOp _) => true;
}
