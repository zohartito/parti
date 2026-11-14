// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Tools;

namespace Parti.Core.Sessions.Policies;

/// <summary>
/// Rejects a frozen response whose <c>gh_add_component.componentGuid</c> does not resolve in the
/// host component catalog. It runs after freeze and before review, so the human is only ever asked
/// to approve a component type the host can name from its own catalog.
/// </summary>
public sealed class ComponentCatalogPolicy : IToolCallPolicy
{
    private readonly IComponentCatalog _catalog;

    /// <summary>Creates a policy over the host component catalog.</summary>
    public ComponentCatalogPolicy(IComponentCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <summary>The catalog this policy consults.</summary>
    public IComponentCatalog Catalog => _catalog;

    /// <inheritdoc />
    public bool TryValidate(
        IReadOnlyList<AIInteractionToolCall> frozen,
        IReadOnlyDictionary<string, bool> reversibilityByName,
        out string error)
    {
        error = string.Empty;
        if (frozen is null)
        {
            return true;
        }
        foreach (var call in frozen)
        {
            if (call.Name != "gh_add_component")
            {
                continue;
            }
            var raw = call.Arguments?["componentGuid"] is JValue { Type: JTokenType.String } value ? (string?)value : null;
            if (raw is null || !Guid.TryParse(raw, out var componentGuid))
            {
                error = "Tool response rejected: 'gh_add_component.componentGuid' must be an exact Grasshopper component GUID.";
                return false;
            }
            if (!_catalog.TryGet(componentGuid, out _))
            {
                error = $"Tool response rejected: component type {componentGuid:D} is not installed in this Grasshopper. Use gh_find_component to get an installed componentGuid.";
                return false;
            }
        }
        return true;
    }
}
