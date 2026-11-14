// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Parti.Core.Sessions;

/// <summary>
/// Single mention record: a component identity (stable instance GUID, "D" format, + canonical
/// name) and when it was last referenced in the conversation.
/// </summary>
public sealed record MemoryEntry(string InstanceGuid, string ComponentName, DateTime LastMentionedUtc);

/// <summary>
/// Conversation memory for component references. Populated opportunistically from canvas
/// tool_results (see <see cref="ConversationSession.Memory"/>); exposes simple reference
/// resolution so system prompts or downstream tools can map fuzzy user references like
/// "the slider I just added" to a concrete instance GUID. Only instance GUIDs are stored; the
/// retired SimpleId handles (e.g. <c>sli-3</c>) are neither recorded nor passed through.
/// <para>
/// v0.7 scope: BUILD the memory and expose <see cref="ResolveReference"/> /
/// <see cref="RecentByType"/> for callers that want to use it. Actually rewriting user prompts
/// to resolve references is v0.8.
/// </para>
/// </summary>
public sealed class MemoryResolver
{
    private readonly Dictionary<string, MemoryEntry> _mentions = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    // Retired SimpleId handles (e.g. "sli-3"). They are refused outright: fuzzy matching would map
    // "sli-3" to whatever slider was mentioned last.
    private static readonly Regex RetiredSimpleId = new(
        @"^[a-z]+-?\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Words we strip when extracting a component-type hint from a fuzzy reference.
    // Kept intentionally small; resolution is best-effort, not NLP.
    private static readonly HashSet<string> FillerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "that", "a", "an", "my", "our", "this", "those", "these",
        "i", "just", "added", "created", "made", "inserted", "placed",
        "most", "recent", "recently", "latest", "last", "previous", "new",
    };

    /// <summary>
    /// Record (or refresh) a mention of the component with <paramref name="instanceGuid"/>.
    /// Overwrites the existing timestamp if the id has been seen before. Anything that is not a
    /// GUID is ignored.
    /// </summary>
    public void Mention(string instanceGuid, string componentName, DateTime timestamp)
    {
        if (!Guid.TryParse(instanceGuid, out var parsed))
        {
            return;
        }

        var id = parsed.ToString("D");
        lock (_lock)
        {
            _mentions[id] = new MemoryEntry(id, componentName ?? string.Empty, timestamp);
        }
    }

    /// <summary>
    /// Resolve a user-facing reference (e.g. "the slider I just added", "that panel") to an
    /// instance GUID, or null when nothing matches.
    /// <list type="bullet">
    ///   <item>A literal GUID passes through unchanged; a retired SimpleId handle resolves to null.</item>
    ///   <item>Otherwise strip articles / fillers, extract the longest letter-only token as the
    ///   component-type hint, and return the most-recently-mentioned instance GUID whose
    ///   <see cref="MemoryEntry.ComponentName"/> contains that hint (case-insensitive).</item>
    /// </list>
    /// </summary>
    public string? ResolveReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var trimmed = reference.Trim();

        // Canvas snapshots now expose stable instance GUIDs rather than recomputed ordinals.
        // Preserve a literal GUID unchanged so downstream mutation tools can enforce identity.
        if (Guid.TryParse(trimmed, out _))
        {
            return trimmed;
        }

        if (RetiredSimpleId.IsMatch(trimmed))
        {
            return null;
        }

        var typeHint = ExtractTypeHint(trimmed);
        if (typeHint is null)
        {
            return null;
        }

        lock (_lock)
        {
            var match = _mentions.Values
                .Where(e => ComponentMatches(e.ComponentName, typeHint))
                .OrderByDescending(e => e.LastMentionedUtc)
                .FirstOrDefault();
            return match?.InstanceGuid;
        }
    }

    /// <summary>
    /// Return the most-recent mentions whose component name contains <paramref name="componentName"/>
    /// (case-insensitive), newest first, up to <paramref name="limit"/>. Useful for building
    /// system-prompt context like "recent sliders: <guid>, <guid>".
    /// </summary>
    public IReadOnlyList<MemoryEntry> RecentByType(string componentName, int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(componentName) || limit <= 0)
        {
            return Array.Empty<MemoryEntry>();
        }

        lock (_lock)
        {
            return _mentions.Values
                .Where(e => ComponentMatches(e.ComponentName, componentName))
                .OrderByDescending(e => e.LastMentionedUtc)
                .Take(limit)
                .ToList();
        }
    }

    /// <summary>
    /// Strip filler words from <paramref name="reference"/> and return the longest remaining
    /// letter-only token as the component-type hint, or null when nothing useful is left.
    /// </summary>
    private static string? ExtractTypeHint(string reference)
    {
        // Split on whitespace and non-letter runs so "I just-added" or "panel," still tokenise.
        var rawTokens = Regex.Split(reference, @"[^A-Za-z]+");
        var candidates = new List<string>();
        foreach (var token in rawTokens)
        {
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            if (FillerWords.Contains(token))
            {
                continue;
            }

            candidates.Add(token);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Prefer the longest remaining token — in "the slider I just added" that's "slider".
        return candidates.OrderByDescending(c => c.Length).First();
    }

    private static bool ComponentMatches(string componentName, string typeHint)
    {
        if (string.IsNullOrEmpty(componentName))
        {
            return false;
        }

        return componentName.IndexOf(typeHint, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
