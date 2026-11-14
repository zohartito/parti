// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// SessionPromptBuilder — v0.9 per-turn system-prompt assembly for progressive-disclosure skills.
// Combines Layer 0 (always loaded) with the N Layer 1 sub-skills a router picks from the
// user's prompt. Returns the combined text plus the activated sub-skill names for telemetry.

using System;
using System.Collections.Generic;
using System.Text;

namespace Parti.Core.Prompts;

/// <summary>
/// Result of a single <see cref="SessionPromptBuilder.Build"/> invocation.
/// </summary>
/// <param name="SystemPrompt">
/// The fully assembled system-prompt text: Layer 0 first, then every Layer 1 sub-skill body
/// joined with a blank-line-delimited separator. Never <c>null</c>; empty only if Layer 0 is
/// empty and no sub-skills resolved.
/// </param>
/// <param name="ActivatedSubSkills">
/// The sub-skill names (in the order the router returned them) whose Layer 1 bodies were
/// loaded and included in <paramref name="SystemPrompt"/>. Unknown / unloadable names are
/// silently skipped — this list reflects what was actually appended.
/// </param>
public sealed record SessionPromptResult(
    string SystemPrompt,
    IReadOnlyList<string> ActivatedSubSkills);

/// <summary>
/// Assembles the per-turn Parti system prompt from a <see cref="SkillLoader"/> (Layer 0 + Layer 1
/// bodies) and an <see cref="ISkillRouter"/> (which sub-skills to load for a given user message).
/// </summary>
/// <remarks>
/// <para>
/// The builder is stateless beyond its injected loader / router; repeated calls with the same
/// prompt are cheap because <see cref="SkillLoader"/> caches Layer 1 reads internally.
/// </para>
/// <para>
/// Sub-skills the router returns that fail to load (missing <c>SKILL.md</c>, empty body) are
/// skipped without throwing — the builder is a best-effort composer, not a validator. The
/// router is expected to have already validated sub-skill names against its known set.
/// </para>
/// </remarks>
public sealed class SessionPromptBuilder
{
    private const string SectionSeparator = "\n\n---\n\n";

    private readonly SkillLoader _loader;
    private readonly ISkillRouter _router;

    /// <summary>
    /// Creates a builder that reads skill content from <paramref name="loader"/> and picks
    /// sub-skills via <paramref name="router"/>.
    /// </summary>
    public SessionPromptBuilder(SkillLoader loader, ISkillRouter router)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _router = router ?? throw new ArgumentNullException(nameof(router));
    }

    /// <summary>
    /// Build a system prompt for the given user message. Always reads Layer 0; routes
    /// <paramref name="userPrompt"/> through the router and appends each resolved Layer 1
    /// sub-skill body.
    /// </summary>
    /// <param name="userPrompt">
    /// The raw user message to route. <c>null</c> / whitespace is allowed — the router is
    /// responsible for choosing a fallback sub-skill in that case.
    /// </param>
    /// <returns>The combined prompt text and the list of activated sub-skill names.</returns>
    public SessionPromptResult Build(string userPrompt)
    {
        var layer0 = _loader.LoadLayer0();
        var route = _router.Route(userPrompt ?? string.Empty);

        var activated = new List<string>(route.SubSkills.Count);
        var sb = new StringBuilder();
        sb.Append(layer0);

        foreach (var subSkill in route.SubSkills)
        {
            if (string.IsNullOrWhiteSpace(subSkill))
            {
                continue;
            }

            string body;
            try
            {
                body = _loader.LoadLayer1(subSkill);
            }
            catch (KeyNotFoundException)
            {
                // Router returned a sub-skill name that has no SKILL.md on the source.
                // Skip silently — we'd rather ship a slightly thinner prompt than crash.
                continue;
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            sb.Append(SectionSeparator);
            sb.Append(body);
            activated.Add(subSkill);
        }

        return new SessionPromptResult(sb.ToString(), activated);
    }
}
