// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;
using System.Linq;

namespace Parti.Core;

public enum ResolveKind
{
    Success,
    NotFound,
    Ambiguous,
}

public sealed record ComponentCandidate(string Name, string? Guid, string? Category);

public sealed record ResolveResult(
    ResolveKind Kind,
    ComponentCandidate? Match,
    IReadOnlyList<ComponentCandidate> Candidates);

public sealed class NameResolver
{
    private const int MaxCandidates = 10;

    private static readonly Dictionary<string, string> Aliases = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["slider"] = "Number Slider",
        ["numberslider"] = "Number Slider",
        ["number slider"] = "Number Slider",
        ["num"] = "Number Slider",
        ["panel"] = "Panel",
        ["xy"] = "XY Plane",
        ["xyplane"] = "XY Plane",
        ["xy plane"] = "XY Plane",
        ["plane"] = "XY Plane",
        ["addition"] = "Addition",
        ["add"] = "Addition",
        ["sub"] = "Subtraction",
        ["subtraction"] = "Subtraction",
        ["mul"] = "Multiplication",
        ["multiply"] = "Multiplication",
        ["div"] = "Division",
        ["line"] = "Line",
        ["point"] = "Point",
        ["curve"] = "Curve",
        ["circle"] = "Circle",
        ["rectangle"] = "Rectangle",
        ["rect"] = "Rectangle",
        ["box"] = "Box",
        ["series"] = "Series",
        ["range"] = "Range",
        ["graft"] = "Graft Tree",
        ["flatten"] = "Flatten Tree",
        ["merge"] = "Merge",
    };

    public ResolveResult Resolve(string query, IReadOnlyList<ComponentCandidate> proxies)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ResolveResult(ResolveKind.NotFound, null, System.Array.Empty<ComponentCandidate>());
        }

        var normalized = System.Text.RegularExpressions.Regex.Replace(query.Trim(), @"\s+", " ");

        if (System.Guid.TryParse(normalized, out _))
        {
            var byGuid = proxies.FirstOrDefault(p => p.Guid is not null &&
                string.Equals(p.Guid, normalized, System.StringComparison.OrdinalIgnoreCase));
            return byGuid is not null
                ? new ResolveResult(ResolveKind.Success, byGuid, System.Array.Empty<ComponentCandidate>())
                : new ResolveResult(ResolveKind.NotFound, null, System.Array.Empty<ComponentCandidate>());
        }

        if (Aliases.TryGetValue(normalized, out var canonical))
        {
            var aliased = proxies.FirstOrDefault(p =>
                string.Equals(p.Name, canonical, System.StringComparison.OrdinalIgnoreCase));
            if (aliased is not null)
            {
                return new ResolveResult(ResolveKind.Success, aliased, System.Array.Empty<ComponentCandidate>());
            }
        }

        var exact = proxies
            .Where(p => string.Equals(p.Name, normalized, System.StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exact.Count == 1)
        {
            return new ResolveResult(ResolveKind.Success, exact[0], System.Array.Empty<ComponentCandidate>());
        }

        if (exact.Count > 1)
        {
            return new ResolveResult(ResolveKind.Ambiguous, null, exact.Take(MaxCandidates).ToList());
        }

        var partial = proxies
            .Where(p => p.Name.IndexOf(normalized, System.StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(p => p.Name.Length)
            .ThenBy(p => p.Name, System.StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidates)
            .ToList();

        return partial.Count switch
        {
            0 => new ResolveResult(ResolveKind.NotFound, null, System.Array.Empty<ComponentCandidate>()),
            1 => new ResolveResult(ResolveKind.Success, partial[0], System.Array.Empty<ComponentCandidate>()),
            _ => new ResolveResult(ResolveKind.Ambiguous, null, partial),
        };
    }
}
