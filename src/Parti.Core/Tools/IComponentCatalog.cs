// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Parti.Core.Tools;

/// <summary>
/// Read-only view of the component types the host can instantiate, keyed by exact component GUID.
/// Backs read-only discovery (<c>gh_find_component</c>, <c>gh_plugin_check</c>) and the
/// catalog-resolved identity shown in plan review.
/// </summary>
public interface IComponentCatalog
{
    /// <summary>Every catalogued component type.</summary>
    IReadOnlyList<ComponentCatalogEntry> All { get; }

    /// <summary>Looks up a component type by its exact component GUID.</summary>
    bool TryGet(Guid componentGuid, [MaybeNullWhen(false)] out ComponentCatalogEntry entry);

    /// <summary>Loaded component libraries (no file paths).</summary>
    IReadOnlyList<LibraryEntry> Libraries();
}

/// <summary>One component type in an <see cref="IComponentCatalog"/>.</summary>
public sealed record ComponentCatalogEntry(
    Guid ComponentGuid,
    string Name,
    string NickName,
    string Category,
    string SubCategory,
    string Library,
    bool Obsolete,
    string Exposure,
    string Description);

/// <summary>One loaded component library. Deliberately carries no filesystem location.</summary>
public sealed record LibraryEntry(string Name, string Version, string Author, bool IsCore);

/// <summary>Catalog with no components and no libraries.</summary>
public sealed class EmptyComponentCatalog : IComponentCatalog
{
    /// <summary>Shared instance; the catalog is immutable.</summary>
    public static EmptyComponentCatalog Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<ComponentCatalogEntry> All => Array.Empty<ComponentCatalogEntry>();

    /// <inheritdoc />
    public bool TryGet(Guid componentGuid, [MaybeNullWhen(false)] out ComponentCatalogEntry entry)
    {
        entry = null;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<LibraryEntry> Libraries() => Array.Empty<LibraryEntry>();
}
