// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// SkillLoader — v0.8.5 progressive-disclosure skill bundle loader.
// Resolves the Parti 3-layer skill files from an ISkillSource (filesystem in dev,
// embedded resources in shipped .gha). Caches Layer 1 bundles once read.

using System;
using System.Collections.Generic;
using System.IO;

namespace Parti.Core.Prompts;

/// <summary>
/// Loads the Parti 3-layer skill bundle from either the filesystem (development)
/// or an embedded resource provider (production / shipped .gha).
/// </summary>
/// <remarks>
/// The three layers are:
/// <list type="bullet">
///   <item>Layer 0 — <c>parti-foundations/SKILL.md</c>, always loaded at session start</item>
///   <item>Layer 1 — <c>parti-foundations/&lt;sub-skill&gt;/SKILL.md</c>, loaded lazily per sub-skill</item>
///   <item>Layer 2 — references and templates under each sub-skill, loaded on explicit reference</item>
/// </list>
/// </remarks>
public sealed class SkillLoader
{
    private const string RootFolder = "parti-foundations";
    private const string SkillFile = "SKILL.md";

    private readonly ISkillSource _source;
    private readonly Dictionary<string, string> _layer1Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string>? _knownSubSkills;

    /// <summary>
    /// Creates a new loader backed by the given <paramref name="source"/>.
    /// </summary>
    public SkillLoader(ISkillSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>
    /// Returns the cached list of sub-skill directory names reported by the source.
    /// </summary>
    public IReadOnlyList<string> KnownSubSkills
    {
        get
        {
            _knownSubSkills ??= _source.ListSubSkills();
            return _knownSubSkills;
        }
    }

    /// <summary>
    /// Reads Layer 0 — the always-loaded foundation skill.
    /// </summary>
    /// <exception cref="FileNotFoundException">Thrown when the Layer 0 SKILL.md is missing.</exception>
    public string LoadLayer0()
    {
        var path = $"{RootFolder}/{SkillFile}";
        var text = _source.ReadText(path);
        if (text is null)
        {
            throw new FileNotFoundException(
                $"Layer 0 skill file not found at '{path}'. The parti-foundations bundle is required.");
        }
        return text;
    }

    /// <summary>
    /// Reads Layer 1 — the SKILL.md for the named sub-skill. Results are cached per
    /// sub-skill name; subsequent calls return the cached copy without hitting the source.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="subSkillName"/> is null or whitespace.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when no SKILL.md exists under the given sub-skill folder.</exception>
    public string LoadLayer1(string subSkillName)
    {
        if (string.IsNullOrWhiteSpace(subSkillName))
        {
            throw new ArgumentException("Sub-skill name must be provided.", nameof(subSkillName));
        }

        if (_layer1Cache.TryGetValue(subSkillName, out var cached))
        {
            return cached;
        }

        var path = $"{RootFolder}/{subSkillName}/{SkillFile}";
        var text = _source.ReadText(path);
        if (text is null)
        {
            throw new KeyNotFoundException(
                $"Layer 1 skill '{subSkillName}' not found at '{path}'.");
        }

        _layer1Cache[subSkillName] = text;
        return text;
    }

    /// <summary>
    /// Reads a Layer 2 reference or template for the given sub-skill. Returns
    /// <c>null</c> when the file does not exist, since Layer 2 references are optional.
    /// </summary>
    /// <param name="subSkillName">The sub-skill folder name, e.g. <c>parti-ghcanvas</c>.</param>
    /// <param name="referenceRelativePath">Path relative to the sub-skill folder, e.g. <c>references/tree-ops-decision-table.md</c>.</param>
    public string? LoadLayer2(string subSkillName, string referenceRelativePath)
    {
        if (string.IsNullOrWhiteSpace(subSkillName))
        {
            throw new ArgumentException("Sub-skill name must be provided.", nameof(subSkillName));
        }
        if (string.IsNullOrWhiteSpace(referenceRelativePath))
        {
            throw new ArgumentException("Reference path must be provided.", nameof(referenceRelativePath));
        }

        var normalized = referenceRelativePath.Replace('\\', '/').TrimStart('/');
        var path = $"{RootFolder}/{subSkillName}/{normalized}";
        return _source.ReadText(path);
    }
}

/// <summary>
/// Abstraction over "where the skill files live" — filesystem, embedded resources,
/// or an in-memory dictionary for tests.
/// </summary>
public interface ISkillSource
{
    /// <summary>
    /// Returns the text contents of the file at <paramref name="relativePath"/>, or
    /// <c>null</c> when no such file exists. Paths use forward slashes. Implementations
    /// are responsible for stripping any YAML frontmatter — callers receive clean body prose.
    /// </summary>
    string? ReadText(string relativePath);

    /// <summary>
    /// Returns the directory names under <c>parti-foundations/</c> — one per sub-skill.
    /// </summary>
    IReadOnlyList<string> ListSubSkills();

    /// <summary>
    /// Returns the parsed <see cref="SkillManifest"/> for the named sub-skill (the one whose
    /// frontmatter was stripped from its Layer 1 SKILL.md body by <see cref="ReadText"/>).
    /// Returns <c>null</c> when the skill file has no frontmatter or the frontmatter is
    /// malformed. Default implementation returns <c>null</c> for back-compat.
    /// </summary>
    /// <param name="subSkillName">Sub-skill folder name, e.g. <c>parti-ghcanvas</c>.</param>
    SkillManifest? GetManifest(string subSkillName) => null;
}

/// <summary>
/// <see cref="ISkillSource"/> backed by a directory on the local filesystem.
/// </summary>
public sealed class FileSystemSkillSource : ISkillSource
{
    private const string RootFolder = "parti-foundations";
    private readonly string _rootDirectory;

    /// <summary>
    /// Creates a new source rooted at <paramref name="rootDirectory"/>. The
    /// <c>parti-foundations</c> folder is expected to sit directly inside this root.
    /// </summary>
    public FileSystemSkillSource(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Root directory must be provided.", nameof(rootDirectory));
        }
        _rootDirectory = rootDirectory;
    }

    /// <inheritdoc />
    public string? ReadText(string relativePath)
    {
        var raw = ReadRawText(relativePath);
        if (raw is null) return null;
        FrontmatterParser.SplitFrontmatter(raw, out _, out var body);
        return body;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListSubSkills()
    {
        var foundationsDir = Path.Combine(_rootDirectory, RootFolder);
        if (!Directory.Exists(foundationsDir)) return Array.Empty<string>();

        var results = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(foundationsDir))
        {
            results.Add(Path.GetFileName(dir));
        }
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    /// <inheritdoc />
    public SkillManifest? GetManifest(string subSkillName)
    {
        if (string.IsNullOrWhiteSpace(subSkillName)) return null;
        var path = $"{RootFolder}/{subSkillName}/SKILL.md";
        var raw = ReadRawText(path);
        return raw is null ? null : FrontmatterParser.TryParseManifest(raw);
    }

    private string? ReadRawText(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.Combine(_rootDirectory, normalized);
        if (!File.Exists(full)) return null;
        return File.ReadAllText(full);
    }
}

/// <summary>
/// <see cref="ISkillSource"/> backed by an in-memory dictionary keyed by relative path.
/// Useful for unit tests and for wrapping embedded-resource blobs.
/// </summary>
public sealed class InMemorySkillSource : ISkillSource
{
    private const string RootFolder = "parti-foundations";
    private readonly IReadOnlyDictionary<string, string> _files;
    private readonly IReadOnlyDictionary<string, SkillManifest>? _manifests;

    /// <summary>
    /// Creates a new in-memory source from the given <paramref name="files"/> map.
    /// Keys use forward slashes, e.g. <c>parti-foundations/parti-ghcanvas/SKILL.md</c>.
    /// </summary>
    public InMemorySkillSource(IReadOnlyDictionary<string, string> files)
        : this(files, manifests: null)
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets callers supply manifests directly (keyed by
    /// sub-skill folder name) without having to embed YAML frontmatter in each file blob.
    /// When a manifest key is not provided, <see cref="GetManifest"/> falls back to parsing
    /// frontmatter from the corresponding <c>SKILL.md</c> file body.
    /// </summary>
    public InMemorySkillSource(
        IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, SkillManifest>? manifests)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _manifests = manifests;
    }

    /// <inheritdoc />
    public string? ReadText(string relativePath)
    {
        var raw = ReadRawText(relativePath);
        if (raw is null) return null;
        FrontmatterParser.SplitFrontmatter(raw, out _, out var body);
        return body;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListSubSkills()
    {
        var prefix = $"{RootFolder}/";
        var subSkills = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in _files.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = key.Substring(prefix.Length);
            var slash = rest.IndexOf('/');
            if (slash <= 0) continue; // Layer 0 SKILL.md itself, not a sub-skill.
            subSkills.Add(rest.Substring(0, slash));
        }

        return new List<string>(subSkills);
    }

    /// <inheritdoc />
    public SkillManifest? GetManifest(string subSkillName)
    {
        if (string.IsNullOrWhiteSpace(subSkillName)) return null;
        if (_manifests is not null && _manifests.TryGetValue(subSkillName, out var m))
        {
            return m;
        }
        var path = $"{RootFolder}/{subSkillName}/SKILL.md";
        var raw = ReadRawText(path);
        return raw is null ? null : FrontmatterParser.TryParseManifest(raw);
    }

    private string? ReadRawText(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return _files.TryGetValue(normalized, out var text) ? text : null;
    }
}
