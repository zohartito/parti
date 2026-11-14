// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// LlmSkillRouter — v0.9 LLM-backed router for progressive-disclosure skills.
// Asks the model which sub-skills a user prompt implies; falls back to the deterministic
// keyword router on any failure (exception, malformed JSON, unknown sub-skill name).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Parti.Core.Prompts;

/// <summary>
/// LLM-backed <see cref="ISkillRouter"/>. Builds a system prompt listing the available
/// sub-skills with short descriptions, sends the user prompt, and parses a small JSON
/// response of the form
/// <c>{"sub_skills": ["..."], "evidence": {"...": ["..."]}}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Falls back to <see cref="KeywordSkillRouter"/> on any of:
/// (a) the invoker throws, (b) JSON fails to parse, (c) the returned list is empty,
/// (d) any returned sub-skill name is not a <see cref="KeywordSkillRouter.KnownSubSkills"/>
/// member. The fallback is deterministic, so calls are always guaranteed to return a
/// well-formed result.
/// </para>
/// <para>
/// Sub-skill descriptions are duplicated from <c>SubAgentRegistry</c> for now. A follow-up
/// should consolidate the two into a single source of truth — they are kept apart to
/// avoid pulling a Design.SubAgents dependency into Prompts at the router layer.
/// </para>
/// </remarks>
public sealed class LlmSkillRouter : ISkillRouter
{
    private readonly ILlmInvoker _invoker;
    private readonly KeywordSkillRouter _fallback;
    private readonly string _systemPrompt;
    private readonly IReadOnlyCollection<string> _knownSubSkills;

    /// <summary>
    /// Creates a new router that calls <paramref name="invoker"/> and falls back to a
    /// fresh <see cref="KeywordSkillRouter"/> on any failure.
    /// </summary>
    public LlmSkillRouter(ILlmInvoker invoker)
        : this(invoker, new KeywordSkillRouter())
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets callers inject the fallback router instance.
    /// </summary>
    public LlmSkillRouter(ILlmInvoker invoker, KeywordSkillRouter fallback)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _knownSubSkills = KeywordSkillRouter.KnownSubSkills;
        _systemPrompt = BuildSystemPrompt();
    }

    /// <summary>
    /// Generated-router constructor: derives <see cref="KnownSubSkills"/> (the closed set of
    /// accepted sub-skill names) from <paramref name="source"/>'s manifests, and builds its
    /// fallback from the same source. Keeps LLM routing in sync with whatever sub-skills the
    /// source actually ships — no hardcoded catalog to keep up to date.
    /// </summary>
    public LlmSkillRouter(ILlmInvoker invoker, ISkillSource source)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        if (source is null) throw new ArgumentNullException(nameof(source));

        _fallback = new KeywordSkillRouter(source);

        var known = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in source.ListSubSkills())
        {
            var m = source.GetManifest(sub);
            if (m is not null && !string.IsNullOrEmpty(m.Title))
            {
                known.Add(m.Title);
            }
        }
        known.Add("parti-formalize"); // Fallback sub-skill is always reachable.
        _knownSubSkills = known;
        _systemPrompt = BuildSystemPrompt();
    }

    /// <summary>The system prompt sent to the LLM on every call. Exposed for test coverage.</summary>
    public string SystemPrompt => _systemPrompt;

    /// <summary>
    /// Closed set of sub-skill names this router accepts in LLM responses. Populated from
    /// either the static <see cref="KeywordSkillRouter.KnownSubSkills"/> (default) or the
    /// injected <see cref="ISkillSource"/> manifests (generated).
    /// </summary>
    public IReadOnlyCollection<string> KnownSubSkills => _knownSubSkills;

    /// <inheritdoc />
    public SkillRouteResult Route(string prompt)
    {
        var safePrompt = prompt ?? string.Empty;
        string raw;
        try
        {
            raw = _invoker
                .InvokeAsync(_systemPrompt, safePrompt, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            return _fallback.Route(safePrompt);
        }

        if (!TryParseResponse(raw, out var subSkills, out var evidence))
        {
            return _fallback.Route(safePrompt);
        }

        // Reject any unknown sub-skill — the router is a closed set.
        foreach (var name in subSkills)
        {
            if (!_knownSubSkills.Contains(name))
            {
                return _fallback.Route(safePrompt);
            }
        }

        return new SkillRouteResult(subSkills, evidence);
    }

    private static bool TryParseResponse(
        string raw,
        out IReadOnlyList<string> subSkills,
        out IReadOnlyDictionary<string, IReadOnlyList<string>> evidence)
    {
        subSkills = Array.Empty<string>();
        evidence = new Dictionary<string, IReadOnlyList<string>>();

        if (string.IsNullOrWhiteSpace(raw)) return false;

        JObject root;
        try
        {
            root = JObject.Parse(raw);
        }
        catch
        {
            return false;
        }

        if (root["sub_skills"] is not JArray subSkillsArr || subSkillsArr.Count == 0)
        {
            return false;
        }

        var list = new List<string>(subSkillsArr.Count);
        foreach (var token in subSkillsArr)
        {
            var name = token?.ToString();
            if (string.IsNullOrWhiteSpace(name)) return false;
            list.Add(name);
        }

        var evidenceMap = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (root["evidence"] is JObject evidenceObj)
        {
            foreach (var prop in evidenceObj.Properties())
            {
                if (prop.Value is JArray terms)
                {
                    var termList = terms
                        .Select(t => t?.ToString() ?? string.Empty)
                        .Where(t => !string.IsNullOrEmpty(t))
                        .ToList();
                    evidenceMap[prop.Name] = termList;
                }
            }
        }

        subSkills = list;
        evidence = evidenceMap;
        return true;
    }

    private static string BuildSystemPrompt()
    {
        // Sub-skill catalog — name + one-line description. These six names are the
        // closed set the keyword router also produces; LlmSkillRouter rejects anything
        // outside this set at parse time.
        var catalog = new (string Name, string Description)[]
        {
            ("parti-formalize",
                "Resolve parameters, constraints, variables, and rules — anything that turns a fuzzy brief into inputs + rules."),
            ("parti-abstract",
                "Name the organizing principle, main idea, or design intent behind the project."),
            ("parti-feedback",
                "Define evaluation metrics and success criteria — how do we know the design is any good."),
            ("parti-optimize",
                "Run or set up a solver (Galapagos, Octopus, Wallacei) to search for better designs."),
            ("parti-ghcanvas",
                "Make Grasshopper canvas edits — add/connect/place components, sliders, panels, groups."),
            ("parti-substrate",
                "Pick the right medium or substrate for the task (pen, spreadsheet, GH, Houdini, code, ML)."),
        };

        var lines = new List<string>
        {
            "You route a user's message to the Parti sub-skills that should be loaded.",
            "Respond with ONLY a JSON object — no prose, no markdown fences.",
            "",
            "Shape: {\"sub_skills\": [\"name\", ...], \"evidence\": {\"name\": [\"phrase\", ...]}}",
            "- sub_skills: most-relevant first. Use only the names listed below.",
            "- evidence: per selected sub-skill, the phrases from the user message that justify picking it.",
            "",
            "Available sub-skills:",
        };
        foreach (var (name, description) in catalog)
        {
            lines.Add($"- {name}: {description}");
        }

        return string.Join("\n", lines);
    }
}
