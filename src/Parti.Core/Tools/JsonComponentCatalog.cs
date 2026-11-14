// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Parti.Core.Tools;

/// <summary>
/// <see cref="IComponentCatalog"/> loaded from a JSON export of a live Grasshopper catalog
/// (e.g. <c>tests/shared/rhino8-gh-catalog.json</c>). Used by hermetic tests and evals.
/// <para>
/// Shape: <c>{"source":"…","libraries":[{"name","version","author","core"}],
/// "components":[{"componentGuid","name","nickname","category","subcategory","library",
/// "obsolete","exposure","description"}]}</c>. Strings are cleaned exactly as the live catalog
/// cleans them (<see cref="ComponentCatalogSnapshot"/>).
/// </para>
/// </summary>
public sealed class JsonComponentCatalog : IComponentCatalog
{
    private readonly ComponentCatalogSnapshot _snapshot;

    private JsonComponentCatalog(ComponentCatalogSnapshot snapshot, string source)
    {
        _snapshot = snapshot;
        Source = source;
    }

    /// <summary>The export's free-text provenance line (cleaned), or empty.</summary>
    public string Source { get; }

    /// <summary>
    /// Loads a catalog export. An absolute path is used as given. A relative path is tried against
    /// the current directory first and then against each ancestor of the application base
    /// directory, so a repo-relative path such as <c>tests/shared/rhino8-gh-catalog.json</c> works
    /// from any test output folder.
    /// </summary>
    /// <exception cref="ArgumentException">The path is blank.</exception>
    /// <exception cref="FileNotFoundException">No candidate file exists.</exception>
    /// <exception cref="InvalidDataException">The file does not have the expected shape, a GUID is
    /// invalid, or a component GUID repeats.</exception>
    public static JsonComponentCatalog Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Catalog path must be provided.", nameof(path));
        }
        var resolved = Resolve(path)
            ?? throw new FileNotFoundException($"Component catalog '{path}' was not found.", path);

        JObject root;
        try
        {
            using var reader = new JsonTextReader(File.OpenText(resolved)) { DateParseHandling = DateParseHandling.None };
            root = JObject.Load(reader);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Component catalog '{path}' is not valid JSON: {ex.Message}", ex);
        }
        return Parse(root);
    }

    /// <summary>Builds a catalog from an already-parsed export object.</summary>
    public static JsonComponentCatalog Parse(JObject root)
    {
        if (root is null)
        {
            throw new ArgumentNullException(nameof(root));
        }
        if (root["components"] is not JArray components)
        {
            throw new InvalidDataException("Component catalog has no 'components' array.");
        }

        var entries = new List<ComponentCatalogEntry>(components.Count);
        var seen = new HashSet<Guid>();
        foreach (var token in components)
        {
            if (token is not JObject item)
            {
                throw new InvalidDataException("Component catalog entries must be objects.");
            }
            var rawGuid = item["componentGuid"]?.Type == JTokenType.String ? (string?)item["componentGuid"] : null;
            if (rawGuid is null || !Guid.TryParse(rawGuid, out var componentGuid) || componentGuid == Guid.Empty)
            {
                throw new InvalidDataException($"Component catalog entry has an invalid componentGuid '{rawGuid}'.");
            }
            if (!seen.Add(componentGuid))
            {
                throw new InvalidDataException($"Component catalog repeats componentGuid {componentGuid:D}.");
            }
            entries.Add(new ComponentCatalogEntry(
                componentGuid,
                Text(item, "name"),
                Text(item, "nickname"),
                Text(item, "category"),
                Text(item, "subcategory"),
                Text(item, "library"),
                item["obsolete"]?.Type == JTokenType.Boolean && (bool)item["obsolete"]!,
                Text(item, "exposure"),
                Text(item, "description")));
        }

        var libraries = new List<LibraryEntry>();
        if (root["libraries"] is JArray libraryArray)
        {
            foreach (var token in libraryArray)
            {
                if (token is not JObject item)
                {
                    throw new InvalidDataException("Component catalog libraries must be objects.");
                }
                libraries.Add(new LibraryEntry(
                    Text(item, "name"),
                    Text(item, "version"),
                    Text(item, "author"),
                    item["core"]?.Type == JTokenType.Boolean && (bool)item["core"]!));
            }
        }

        var source = CatalogText.Clean(root["source"]?.Type == JTokenType.String ? (string?)root["source"] : null, 200);
        return new JsonComponentCatalog(new ComponentCatalogSnapshot(entries, libraries), source);
    }

    /// <inheritdoc />
    public IReadOnlyList<ComponentCatalogEntry> All => _snapshot.All;

    /// <inheritdoc />
    public bool TryGet(Guid componentGuid, [MaybeNullWhen(false)] out ComponentCatalogEntry entry) =>
        _snapshot.TryGet(componentGuid, out entry);

    /// <inheritdoc />
    public IReadOnlyList<LibraryEntry> Libraries() => _snapshot.Libraries();

    private static string Text(JObject item, string name) =>
        item[name]?.Type == JTokenType.String ? (string?)item[name] ?? string.Empty : string.Empty;

    private static string? Resolve(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return File.Exists(path) ? path : null;
        }
        var fromCurrent = Path.GetFullPath(path);
        if (File.Exists(fromCurrent))
        {
            return fromCurrent;
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, path);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
