// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// SkillManifest — parsed YAML frontmatter block for a SKILL.md file. The manifest is the
// machine-readable routing contract (tags, auto_activate, invocation). Layer 0 / Layer 1
// prose bodies are loaded separately via ISkillSource.ReadText, which strips this frontmatter.

using System.Collections.Generic;

namespace Parti.Core.Prompts;

/// <summary>
/// Parsed YAML frontmatter block from the top of a SKILL.md file.
/// </summary>
/// <param name="Title">The <c>title</c> field — canonical sub-skill name.</param>
/// <param name="Description">The <c>description</c> field — one-sentence blurb.</param>
/// <param name="Version">The <c>version</c> field — semver string.</param>
/// <param name="Tags">Routing keywords the skill should match on.</param>
/// <param name="AutoActivate">Whether the router can auto-pick this skill without explicit invocation.</param>
/// <param name="UserInvocable">Whether the user can invoke this skill by slash command.</param>
/// <param name="Invocation">Slash-command name, or <c>null</c> when not user-invocable.</param>
public sealed record SkillManifest(
    string Title,
    string Description,
    string Version,
    IReadOnlyList<string> Tags,
    bool AutoActivate,
    bool UserInvocable,
    string? Invocation);
