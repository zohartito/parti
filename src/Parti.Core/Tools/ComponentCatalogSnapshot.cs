// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Parti.Core.Tools;

/// <summary>
/// Immutable, cleaned, GUID-indexed <see cref="IComponentCatalog"/> built from raw entries. Both the
/// live Grasshopper catalog and the JSON test catalog go through it, so every catalog string that
/// reaches a prompt, a tool result, or a plan label has been cleaned the same way.
/// </summary>
public sealed class ComponentCatalogSnapshot : IComponentCatalog
{
    private readonly IReadOnlyList<ComponentCatalogEntry> _all;
    private readonly Dictionary<Guid, ComponentCatalogEntry> _byGuid;
    private readonly IReadOnlyList<LibraryEntry> _libraries;

    /// <summary>
    /// Cleans every string (control characters stripped, lengths bounded; see <see cref="CatalogText"/>)
    /// and indexes the entries by component GUID. When a GUID repeats, the first entry wins.
    /// Empty GUIDs are dropped.
    /// </summary>
    public ComponentCatalogSnapshot(IEnumerable<ComponentCatalogEntry> entries, IEnumerable<LibraryEntry> libraries)
    {
        if (entries is null)
        {
            throw new ArgumentNullException(nameof(entries));
        }
        if (libraries is null)
        {
            throw new ArgumentNullException(nameof(libraries));
        }

        var all = new List<ComponentCatalogEntry>();
        _byGuid = new Dictionary<Guid, ComponentCatalogEntry>();
        foreach (var raw in entries)
        {
            if (raw is null || raw.ComponentGuid == Guid.Empty || _byGuid.ContainsKey(raw.ComponentGuid))
            {
                continue;
            }
            var clean = new ComponentCatalogEntry(
                raw.ComponentGuid,
                CatalogText.Clean(raw.Name, CatalogText.NameLength),
                CatalogText.Clean(raw.NickName, CatalogText.ShortLength),
                CatalogText.Clean(raw.Category, CatalogText.ShortLength),
                CatalogText.Clean(raw.SubCategory, CatalogText.ShortLength),
                CatalogText.Clean(raw.Library, CatalogText.NameLength),
                raw.Obsolete,
                CatalogText.Clean(raw.Exposure, CatalogText.ShortLength),
                CatalogText.Clean(raw.Description, CatalogText.DescriptionLength));
            _byGuid.Add(clean.ComponentGuid, clean);
            all.Add(clean);
        }
        _all = all;
        _libraries = libraries
            .Where(l => l is not null)
            .Select(l => new LibraryEntry(
                CatalogText.Clean(l.Name, CatalogText.NameLength),
                CatalogText.Clean(l.Version, CatalogText.ShortLength),
                CatalogText.Clean(l.Author, CatalogText.NameLength),
                l.IsCore))
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<ComponentCatalogEntry> All => _all;

    /// <inheritdoc />
    public bool TryGet(Guid componentGuid, [MaybeNullWhen(false)] out ComponentCatalogEntry entry) =>
        _byGuid.TryGetValue(componentGuid, out entry);

    /// <inheritdoc />
    public IReadOnlyList<LibraryEntry> Libraries() => _libraries;
}

/// <summary>
/// Cleaning for catalog strings. They come from third-party plugins, so they are untrusted: C0/C1
/// control characters are removed, whitespace runs collapse to one space, and length is bounded.
/// </summary>
public static class CatalogText
{
    /// <summary>Bound for names, library names, and authors.</summary>
    public const int NameLength = 80;

    /// <summary>Bound for nicknames, categories, subcategories, exposure, and versions.</summary>
    public const int ShortLength = 40;

    /// <summary>Bound for stored descriptions (search input). Tool output truncates further.</summary>
    public const int DescriptionLength = 500;

    /// <summary>
    /// Removes control characters, collapses whitespace, trims, and truncates to
    /// <paramref name="maxLength"/> characters (the last one becomes an ellipsis when cut).
    /// Null becomes the empty string.
    /// </summary>
    public static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            // Format characters (bidi overrides, zero-width joiners) could make a label render as
            // something else, so they are dropped outright.
            if (char.GetUnicodeCategory(c) == UnicodeCategory.Format)
            {
                continue;
            }
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                // Tabs/newlines are both control and whitespace; either way they separate words.
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(c);
        }
        var cleaned = builder.ToString();
        if (maxLength <= 0)
        {
            return string.Empty;
        }
        if (cleaned.Length <= maxLength)
        {
            return cleaned;
        }
        var cut = maxLength - 1;
        if (cut > 0 && char.IsHighSurrogate(cleaned[cut - 1]))
        {
            cut--; // never split a surrogate pair
        }
        return cleaned.Substring(0, cut) + "…";
    }
}
