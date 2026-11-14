// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// SkillRouter — deterministic keyword router for progressive-disclosure skills.
// Given a user message, returns the ordered list of Layer 1 sub-skills the assistant
// should load. v0.9 extracted the ISkillRouter interface and renamed the concrete type
// to KeywordSkillRouter; the static SkillRouter entry-point is preserved for back-compat.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Parti.Core.Prompts;

/// <summary>
/// Result of routing a user message to sub-skills.
/// </summary>
/// <param name="SubSkills">Sub-skill names ordered by relevance — most likely first.</param>
/// <param name="Evidence">Per sub-skill, the keywords that matched (or a fallback marker).</param>
public sealed record SkillRouteResult(
    IReadOnlyList<string> SubSkills,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Evidence);

/// <summary>
/// Maps a user prompt to a list of sub-skill names. Implementations may be deterministic
/// (keyword-based) or LLM-backed; consumers program against this interface so the router
/// strategy can swap without code churn downstream.
/// </summary>
public interface ISkillRouter
{
    /// <summary>
    /// Parse the prompt and return the sub-skills it implies.
    /// </summary>
    /// <param name="prompt">The raw user message. Null/whitespace is allowed.</param>
    SkillRouteResult Route(string prompt);
}

/// <summary>
/// Deterministic keyword-based router. Substring-matches a user message against a
/// curated keyword map and returns the implied sub-skills. Stateless and side-effect-free.
/// </summary>
/// <remarks>
/// <para>
/// When no keyword matches, <see cref="Route"/> falls back to <c>parti-formalize</c>,
/// since any ambiguous brief needs parameter/constraint formalization first.
/// </para>
/// <para>
/// Also serves as the fallback for <see cref="LlmSkillRouter"/> on malformed model output.
/// </para>
/// </remarks>
public sealed class KeywordSkillRouter : ISkillRouter
{
    private const string FallbackSubSkill = "parti-formalize";
    private const string FallbackReason = "(fallback - no keyword match)";

    // Instance keyword map. When null, Route() falls back to the static curated map
    // (back-compat path used by the 18 original SkillRouterTests). When non-null, it
    // overrides — this is how the new tag-driven constructor injects routing rules.
    private readonly IReadOnlyDictionary<string, string>? _instanceMap;
    private readonly IReadOnlyCollection<string>? _instanceKnownSubSkills;

    // Keyword → sub-skill name. Case-insensitive substring match.
    // Keywords mirror the Layer 0 trigger list for each sub-skill.
    private static readonly Dictionary<string, string> s_keywordMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // parti-formalize — parameters, constraints, variables, rules.
            ["parameter"] = "parti-formalize",
            ["parameters"] = "parti-formalize",
            ["constraint"] = "parti-formalize",
            ["constraints"] = "parti-formalize",
            ["variable"] = "parti-formalize",
            ["variables"] = "parti-formalize",
            ["what varies"] = "parti-formalize",
            ["inputs"] = "parti-formalize",
            ["rules"] = "parti-formalize",

            // parti-abstract — organizing principle, design intent.
            ["principle"] = "parti-abstract",
            ["organizing"] = "parti-abstract",
            ["main idea"] = "parti-abstract",
            ["design intent"] = "parti-abstract",
            ["what's the logic"] = "parti-abstract",
            ["concept"] = "parti-abstract",

            // parti-feedback — evaluation metrics, success criteria.
            ["optimize for"] = "parti-feedback",
            ["maximize"] = "parti-feedback",
            ["minimize"] = "parti-feedback",
            ["success"] = "parti-feedback",
            ["evaluation"] = "parti-feedback",
            ["how do we know"] = "parti-feedback",
            ["metric"] = "parti-feedback",

            // parti-optimize — solver runs.
            ["galapagos"] = "parti-optimize",
            ["octopus"] = "parti-optimize",
            ["wallacei"] = "parti-optimize",
            ["solver"] = "parti-optimize",
            ["run a solver"] = "parti-optimize",
            ["best design"] = "parti-optimize",
            ["optimization"] = "parti-optimize",
            ["genome"] = "parti-optimize",
            ["fitness"] = "parti-optimize",

            // parti-ghcanvas — canvas edits.
            ["add"] = "parti-ghcanvas",
            ["connect"] = "parti-ghcanvas",
            ["wire"] = "parti-ghcanvas",
            ["place"] = "parti-ghcanvas",
            ["slider"] = "parti-ghcanvas",
            ["component"] = "parti-ghcanvas",
            ["panel"] = "parti-ghcanvas",
            ["group"] = "parti-ghcanvas",
            ["layout"] = "parti-ghcanvas",
            ["canvas"] = "parti-ghcanvas",

            // parti-fabricate — CNC and nesting.
            ["cnc"] = "parti-fabricate",
            ["nest"] = "parti-fabricate",
            ["toolpath"] = "parti-fabricate",
            ["cut list"] = "parti-fabricate",
            ["fabrication"] = "parti-fabricate",
            ["milling"] = "parti-fabricate",
            ["router"] = "parti-fabricate",
        };

    /// <summary>
    /// The raw keyword → sub-skill map. Internal-visible for test coverage only.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> KeywordMap => s_keywordMap;

    /// <summary>
    /// Every sub-skill name reachable via this router. Useful for validating external
    /// (e.g. LLM-returned) sub-skill lists against the deterministic ground truth.
    /// </summary>
    public static IReadOnlyCollection<string> KnownSubSkills { get; } =
        new HashSet<string>(s_keywordMap.Values, StringComparer.OrdinalIgnoreCase)
        {
            // Always reachable as the ambiguity fallback.
            FallbackSubSkill,
        };

    /// <summary>Default constructor — uses the static curated keyword map.</summary>
    public KeywordSkillRouter()
    {
        _instanceMap = null;
        _instanceKnownSubSkills = null;
    }

    /// <summary>
    /// Generated-router constructor — iterates the source's sub-skills, reads each
    /// <see cref="SkillManifest"/>, and builds a keyword map by concatenating every tag.
    /// </summary>
    /// <remarks>
    /// On tag collision (the same tag lists two different sub-skills), the alphabetically
    /// earliest sub-skill name wins and a warning is written via <see cref="System.Diagnostics.Trace"/>.
    /// Skills with no manifest or empty tags are ignored. When no sub-skill contributes any
    /// tag, <see cref="Route"/> falls back to <c>parti-formalize</c>.
    /// </remarks>
    public KeywordSkillRouter(ISkillSource source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var known = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var subSkills = source.ListSubSkills();

        foreach (var subSkill in subSkills.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            var manifest = source.GetManifest(subSkill);
            if (manifest is null) continue;
            known.Add(manifest.Title);

            foreach (var tag in manifest.Tags)
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;
                var normalized = tag.Trim();

                if (map.TryGetValue(normalized, out var existing))
                {
                    // Collision — keep the alphabetically earliest sub-skill name, log the loser.
                    var winner = string.CompareOrdinal(existing, manifest.Title) <= 0
                        ? existing : manifest.Title;
                    var loser = string.Equals(winner, existing, StringComparison.Ordinal)
                        ? manifest.Title : existing;
                    System.Diagnostics.Trace.WriteLine(
                        $"KeywordSkillRouter: tag collision on '{normalized}' — " +
                        $"keeping '{winner}', dropping '{loser}'.");
                    map[normalized] = winner;
                }
                else
                {
                    map[normalized] = manifest.Title;
                }
            }
        }

        // Ensure the fallback sub-skill is always reachable.
        known.Add(FallbackSubSkill);

        _instanceMap = map;
        _instanceKnownSubSkills = known;
    }

    /// <inheritdoc />
    public SkillRouteResult Route(string prompt)
    {
        var map = _instanceMap ?? s_keywordMap;
        return RouteCore(prompt, map);
    }

    /// <summary>
    /// Instance view of the sub-skills this router can reach. For the default constructor,
    /// this is <see cref="KnownSubSkills"/>; for the generated constructor, it is derived
    /// from the source's manifests.
    /// </summary>
    public IReadOnlyCollection<string> KnownSubSkillsInstance =>
        _instanceKnownSubSkills ?? KnownSubSkills;

    /// <summary>
    /// Static entry-point preserved for v0.8.5 callers. Equivalent to
    /// <c>new KeywordSkillRouter().Route(userMessage)</c>.
    /// </summary>
    internal static SkillRouteResult RouteStatic(string userMessage) =>
        RouteCore(userMessage, s_keywordMap);

    private static SkillRouteResult RouteCore(
        string userMessage,
        IReadOnlyDictionary<string, string> map)
    {
        var evidence = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(userMessage))
        {
            evidence[FallbackSubSkill] = new[] { FallbackReason };
            return new SkillRouteResult(new[] { FallbackSubSkill }, evidence);
        }

        var lower = userMessage.ToLowerInvariant();

        // Aggregate matches per sub-skill.
        var matches = new Dictionary<string, (List<string> Terms, int FirstIndex)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var kv in map)
        {
            var keyword = kv.Key;
            var subSkill = kv.Value;
            var idx = lower.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            if (!matches.TryGetValue(subSkill, out var existing))
            {
                existing = (new List<string>(), idx);
            }
            existing.Terms.Add(keyword);
            if (idx < existing.FirstIndex) existing.FirstIndex = idx;
            matches[subSkill] = existing;
        }

        if (matches.Count == 0)
        {
            evidence[FallbackSubSkill] = new[] { FallbackReason };
            return new SkillRouteResult(new[] { FallbackSubSkill }, evidence);
        }

        var ordered = matches
            .OrderByDescending(kv => kv.Value.Terms.Count)
            .ThenBy(kv => kv.Value.FirstIndex)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var kv in matches)
        {
            evidence[kv.Key] = kv.Value.Terms;
        }

        return new SkillRouteResult(ordered, evidence);
    }
}

/// <summary>
/// Back-compat static facade for the keyword router. New code should depend on
/// <see cref="ISkillRouter"/> and inject <see cref="KeywordSkillRouter"/> or
/// <see cref="LlmSkillRouter"/> directly.
/// </summary>
public static class SkillRouter
{
    /// <summary>
    /// Route a user message via the keyword router. Preserves the v0.8.5 call site.
    /// </summary>
    public static SkillRouteResult Route(string userMessage) =>
        KeywordSkillRouter.RouteStatic(userMessage);

    /// <summary>
    /// The raw keyword → sub-skill map. Internal-visible for test coverage only.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> KeywordMap => KeywordSkillRouter.KeywordMap;
}
