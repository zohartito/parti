// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// FrontmatterParser — tiny hand-rolled YAML frontmatter parser for SKILL.md files. The
// frontmatter shape is intentionally limited (scalar string/bool + one inline list of
// strings), so no external YAML dependency is pulled into Parti.Core.

using System;
using System.Collections.Generic;
using System.Text;

namespace Parti.Core.Prompts;

/// <summary>
/// Parses the <c>---</c>-delimited YAML frontmatter block at the top of a Markdown file.
/// The parser handles only the subset used by Parti SKILL.md files:
/// <list type="bullet">
///   <item>Scalar string values (quoted or unquoted)</item>
///   <item>Scalar boolean values (<c>true</c> / <c>false</c>)</item>
///   <item><c>null</c> scalar (returns the literal string "null")</item>
///   <item>Inline flow-style list of strings: <c>tags: [foo, bar, "baz qux"]</c></item>
/// </list>
/// Anything more elaborate (nested maps, multi-line lists, anchors) returns <c>null</c>
/// from <see cref="TryParse"/> rather than throwing.
/// </summary>
public static class FrontmatterParser
{
    private const string Delimiter = "---";

    /// <summary>
    /// Splits the frontmatter block off the top of <paramref name="text"/>, returning the
    /// raw block (without fences) and the remaining body. When no frontmatter is present,
    /// <paramref name="frontmatter"/> is <c>null</c> and <paramref name="body"/> is the full input.
    /// </summary>
    public static void SplitFrontmatter(string text, out string? frontmatter, out string body)
    {
        frontmatter = null;
        body = text ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return;

        // Frontmatter must start at the very first character (allow a BOM, nothing else).
        var start = 0;
        if (text.Length > 0 && text[0] == '\uFEFF') start = 1;

        if (!StartsWithDelimiter(text, start, out var afterOpenFence)) return;

        // Find the closing delimiter. It must appear on its own line, bounded by newlines.
        var closeIdx = FindClosingDelimiter(text, afterOpenFence);
        if (closeIdx < 0) return;

        frontmatter = text.Substring(afterOpenFence, closeIdx - afterOpenFence);

        // Body starts after the closing fence line terminator.
        var bodyStart = closeIdx + Delimiter.Length;
        if (bodyStart < text.Length && text[bodyStart] == '\r') bodyStart++;
        if (bodyStart < text.Length && text[bodyStart] == '\n') bodyStart++;
        body = text.Substring(bodyStart);
    }

    /// <summary>
    /// Parses the frontmatter at the top of <paramref name="text"/> into a dictionary.
    /// Keys are preserved case-sensitively. Values are either <see cref="string"/>,
    /// <see cref="bool"/>, or <see cref="List{T}"/> of <see cref="string"/>.
    /// </summary>
    /// <returns>
    /// The parsed dictionary, or <c>null</c> when the file has no frontmatter, the
    /// frontmatter is malformed, or a line cannot be understood. Never throws.
    /// </returns>
    public static Dictionary<string, object>? TryParse(string text)
    {
        SplitFrontmatter(text, out var block, out _);
        if (block is null) return null;

        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        var lines = block.Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#", StringComparison.Ordinal)) continue; // YAML comment

            var colon = line.IndexOf(':');
            if (colon <= 0) return null; // malformed

            var key = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim();
            if (key.Length == 0) return null;

            if (!TryParseValue(value, out var parsed)) return null;
            result[key] = parsed;
        }

        return result;
    }

    /// <summary>
    /// Convenience: parse frontmatter and materialize a <see cref="SkillManifest"/>.
    /// Returns <c>null</c> when frontmatter is missing or lacks required fields.
    /// </summary>
    public static SkillManifest? TryParseManifest(string text)
    {
        var dict = TryParse(text);
        if (dict is null) return null;

        var title = GetString(dict, "title");
        if (string.IsNullOrEmpty(title)) return null;

        var description = GetString(dict, "description") ?? string.Empty;
        var version = GetString(dict, "version") ?? "0.0.0";
        var tags = GetList(dict, "tags");
        var autoActivate = GetBool(dict, "auto_activate", defaultValue: false);
        var userInvocable = GetBool(dict, "user_invocable", defaultValue: false);
        var invocation = GetString(dict, "invocation");
        if (string.Equals(invocation, "null", StringComparison.OrdinalIgnoreCase))
        {
            invocation = null;
        }

        return new SkillManifest(
            title!,
            description,
            version,
            tags,
            autoActivate,
            userInvocable,
            invocation);
    }

    private static bool StartsWithDelimiter(string text, int start, out int afterFence)
    {
        afterFence = -1;
        if (start + Delimiter.Length > text.Length) return false;
        for (var i = 0; i < Delimiter.Length; i++)
        {
            if (text[start + i] != Delimiter[i]) return false;
        }

        // Must be followed by a line terminator or end-of-text.
        var idx = start + Delimiter.Length;
        if (idx >= text.Length) return false;
        var ch = text[idx];
        if (ch != '\r' && ch != '\n') return false;
        if (ch == '\r' && idx + 1 < text.Length && text[idx + 1] == '\n') idx++;
        afterFence = idx + 1;
        return true;
    }

    private static int FindClosingDelimiter(string text, int searchStart)
    {
        var i = searchStart;
        while (i <= text.Length - Delimiter.Length)
        {
            // The delimiter must start at a line boundary (i == searchStart, or preceded by \n).
            if (i > 0 && text[i - 1] != '\n')
            {
                i++;
                continue;
            }
            if (text[i] == '-' && text[i + 1] == '-' && text[i + 2] == '-')
            {
                var after = i + Delimiter.Length;
                if (after == text.Length || text[after] == '\r' || text[after] == '\n')
                {
                    return i;
                }
            }
            i++;
        }
        return -1;
    }

    private static bool TryParseValue(string raw, out object value)
    {
        value = string.Empty;
        if (raw.Length == 0)
        {
            value = string.Empty;
            return true;
        }

        // Inline flow list: [a, b, "c d"]
        if (raw.StartsWith("[", StringComparison.Ordinal))
        {
            return TryParseInlineList(raw, out value);
        }

        // Bool
        if (string.Equals(raw, "true", StringComparison.Ordinal))
        {
            value = true;
            return true;
        }
        if (string.Equals(raw, "false", StringComparison.Ordinal))
        {
            value = false;
            return true;
        }

        // Scalar string — strip outer quotes if present.
        value = Unquote(raw);
        return true;
    }

    private static bool TryParseInlineList(string raw, out object value)
    {
        value = new List<string>();
        if (!raw.StartsWith("[", StringComparison.Ordinal) ||
            !raw.EndsWith("]", StringComparison.Ordinal))
        {
            return false;
        }

        var inner = raw.Substring(1, raw.Length - 2);
        var items = new List<string>();
        var buf = new StringBuilder();
        var inQuote = false;
        var quoteChar = '"';

        foreach (var ch in inner)
        {
            if (inQuote)
            {
                if (ch == quoteChar)
                {
                    inQuote = false;
                    continue;
                }
                buf.Append(ch);
                continue;
            }

            if (ch == '"' || ch == '\'')
            {
                inQuote = true;
                quoteChar = ch;
                continue;
            }

            if (ch == ',')
            {
                var item = buf.ToString().Trim();
                if (item.Length > 0) items.Add(Unquote(item));
                buf.Clear();
                continue;
            }

            buf.Append(ch);
        }

        if (inQuote) return false;
        var tail = buf.ToString().Trim();
        if (tail.Length > 0) items.Add(Unquote(tail));

        value = items;
        return true;
    }

    private static string Unquote(string raw)
    {
        if (raw.Length >= 2 &&
            ((raw[0] == '"' && raw[^1] == '"') ||
             (raw[0] == '\'' && raw[^1] == '\'')))
        {
            return raw.Substring(1, raw.Length - 2);
        }
        return raw;
    }

    private static string? GetString(Dictionary<string, object> dict, string key)
    {
        return dict.TryGetValue(key, out var v) && v is string s ? s : null;
    }

    private static bool GetBool(Dictionary<string, object> dict, string key, bool defaultValue)
    {
        return dict.TryGetValue(key, out var v) && v is bool b ? b : defaultValue;
    }

    private static IReadOnlyList<string> GetList(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var v) && v is List<string> list)
        {
            return list;
        }
        return Array.Empty<string>();
    }
}
