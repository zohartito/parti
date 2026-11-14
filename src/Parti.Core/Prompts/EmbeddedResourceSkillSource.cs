// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// EmbeddedResourceSkillSource — resolves Parti skill files from an assembly's
// embedded resources. Used by the shipped .gha so the plugin is self-contained
// and does not depend on filesystem layout at runtime.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Parti.Core.Prompts;

/// <summary>
/// <see cref="ISkillSource"/> backed by the embedded resources of a .NET assembly.
/// </summary>
/// <remarks>
/// Resource names are expected to follow the convention
/// <c>{resourcePrefix}.{relative-path-with-dots}</c>, e.g.
/// <c>Parti.Prompts.parti-foundations.SKILL.md</c> or
/// <c>Parti.Prompts.parti-foundations.parti-ghcanvas.SKILL.md</c>.
/// <para>
/// On Windows, MSBuild's <c>%(RecursiveDir)</c> token emits backslashes, so some
/// resource names may contain literal <c>\</c> characters (e.g.
/// <c>Parti.Prompts.parti-foundations\parti-abstract\SKILL.md</c>). This source
/// transparently falls back to backslash variants when the dotted lookup misses.
/// </para>
/// </remarks>
public sealed class EmbeddedResourceSkillSource : ISkillSource
{
    private const string RootFolder = "parti-foundations";
    private const string SkillFile = "SKILL.md";

    private readonly Assembly _assembly;
    private readonly string _resourcePrefix;
    private readonly string[] _resourceNames;

    /// <summary>
    /// Creates a new source that reads from <paramref name="assembly"/>'s embedded resources
    /// whose names start with <paramref name="resourcePrefix"/>.
    /// </summary>
    /// <param name="assembly">Assembly hosting the embedded resources (typically the shipped .gha).</param>
    /// <param name="resourcePrefix">
    /// Logical-name prefix up to but not including the foundations-relative path, e.g.
    /// <c>Parti.Prompts.parti-foundations</c>.
    /// </param>
    public EmbeddedResourceSkillSource(
        Assembly assembly,
        string resourcePrefix = "Parti.Prompts.parti-foundations")
    {
        _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        if (string.IsNullOrWhiteSpace(resourcePrefix))
        {
            throw new ArgumentException("Resource prefix must be provided.", nameof(resourcePrefix));
        }
        _resourcePrefix = resourcePrefix;
        _resourceNames = assembly.GetManifestResourceNames();
    }

    /// <inheritdoc />
    /// <exception cref="FileNotFoundException">
    /// Thrown when no resource matches the requested <paramref name="relativePath"/>.
    /// </exception>
    public string? ReadText(string relativePath)
    {
        var raw = ReadRawText(relativePath);
        if (raw is null) return null;
        FrontmatterParser.SplitFrontmatter(raw, out _, out var body);
        return body;
    }

    /// <inheritdoc />
    public SkillManifest? GetManifest(string subSkillName)
    {
        if (string.IsNullOrWhiteSpace(subSkillName)) return null;
        var path = $"{RootFolder}/{subSkillName}/{SkillFile}";
        try
        {
            var raw = ReadRawText(path);
            return raw is null ? null : FrontmatterParser.TryParseManifest(raw);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private string? ReadRawText(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;

        // Drop any leading foundations-folder prefix. SkillLoader passes paths like
        // "parti-foundations/SKILL.md" or "parti-foundations/parti-abstract/SKILL.md",
        // but the constructor prefix already encodes the foundations folder.
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var rootPrefix = RootFolder + "/";
        if (normalized.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(rootPrefix.Length);
        }
        else if (normalized.Equals(RootFolder, StringComparison.OrdinalIgnoreCase))
        {
            normalized = string.Empty;
        }

        // Primary lookup: dotted logical name.
        var dotted = normalized.Replace('/', '.');
        var candidate = string.IsNullOrEmpty(dotted)
            ? _resourcePrefix
            : _resourcePrefix + "." + dotted;

        var stream = _assembly.GetManifestResourceStream(candidate);
        if (stream is null)
        {
            // Fallback: MSBuild on Windows may have embedded the resource with backslashes
            // from %(RecursiveDir). Scan the manifest for any name that matches the logical
            // path when both sides are normalized.
            var resolved = ResolveBackslashVariant(normalized);
            if (resolved is not null)
            {
                stream = _assembly.GetManifestResourceStream(resolved);
            }
        }

        if (stream is null)
        {
            throw new FileNotFoundException(
                $"Embedded skill resource not found for relative path '{relativePath}' " +
                $"(tried '{candidate}').");
        }

        using (stream)
        using (var reader = new StreamReader(stream))
        {
            return reader.ReadToEnd();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListSubSkills()
    {
        var subSkills = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefixWithDot = _resourcePrefix + ".";

        foreach (var name in _resourceNames)
        {
            // Normalize the full resource name up front so that the %(RecursiveDir)
            // backslash artifact on Windows doesn't cause StartsWith to miss names whose
            // first separator after the prefix is "\" instead of ".".
            var canonical = name.Replace('\\', '.').Replace('/', '.');
            if (!canonical.StartsWith(prefixWithDot, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = canonical.Substring(prefixWithDot.Length);

            if (!rest.EndsWith("." + SkillFile, StringComparison.OrdinalIgnoreCase)
                && !rest.Equals(SkillFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Strip the trailing ".SKILL.md" — what remains is the sub-skill folder chain
            // (e.g. "parti-abstract") or empty for the Layer 0 SKILL.md itself.
            var suffixLength = SkillFile.Length + 1;
            if (rest.Length <= suffixLength) continue;
            var trimmed = rest.Substring(0, rest.Length - suffixLength);
            if (trimmed.Length == 0) continue;

            // Only the first segment is the sub-skill; deeper SKILL.md files (if any) belong
            // to nested sub-skills we don't surface here.
            var firstDot = trimmed.IndexOf('.');
            var first = firstDot < 0 ? trimmed : trimmed.Substring(0, firstDot);
            if (firstDot < 0)
            {
                subSkills.Add(first);
            }
        }

        return new List<string>(subSkills);
    }

    /// <summary>
    /// Scans the manifest for a resource whose name matches the requested dotted path
    /// after backslashes and forward slashes are normalized to dots.
    /// </summary>
    private string? ResolveBackslashVariant(string normalizedRelativePath)
    {
        var target = string.IsNullOrEmpty(normalizedRelativePath)
            ? _resourcePrefix
            : _resourcePrefix + "." + normalizedRelativePath.Replace('/', '.');

        foreach (var name in _resourceNames)
        {
            var canonical = name.Replace('\\', '.').Replace('/', '.');
            if (string.Equals(canonical, target, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }
        return null;
    }
}
