// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using Parti.Core.Ai;
using Parti.Core.Sessions;
using Parti.Core.Sessions.Policies;
using Parti.Core.Tools;

namespace Parti.Core.Grasshopper.Tools;

/// <summary>
/// Host services the product tool handlers share: the component catalog used for read-only
/// discovery, and the lock registry shared with the lock policy.
/// </summary>
public sealed record ProductToolContext(IComponentCatalog Catalog, ComponentLockRegistry Locks);

/// <summary>
/// The single definition of which tools the Parti product registers. The chat window, the eval
/// harness, and the prompt parity test all read the set from here.
/// <para>
/// The set is exactly the ten Grasshopper primitives (with the <c>gh_delete</c> adapter bound to
/// the context's lock registry), <c>gh_find_component</c> and <c>gh_plugin_check</c> over the
/// context's catalog, and <c>gh_component_lock</c> over the context's lock registry. No solver
/// tools and no <c>gh_undo</c>. Session tools (<c>ask_user</c>, <c>parti_record_decomposition</c>)
/// are advertised by the session, not registered here.
/// </para>
/// </summary>
public static class ProductToolSet
{
    /// <summary>Builds a registry with every product tool registered.</summary>
    public static ToolRegistry Create(ProductToolContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }
        if (context.Catalog is null || context.Locks is null)
        {
            throw new ArgumentException("The product tool context needs a catalog and a lock registry.", nameof(context));
        }

        var registry = new ToolRegistry();
        foreach (var (definition, handler) in new[]
                 {
                     GrasshopperToolAdapters.GhAddComponentAdapter(),
                     GrasshopperToolAdapters.GhGetAllAdapter(),
                     GrasshopperToolAdapters.GhGetSelectedAdapter(),
                     GrasshopperToolAdapters.GhDescribeAdapter(),
                     GrasshopperToolAdapters.GhConnectAdapter(context.Locks),
                     GrasshopperToolAdapters.GhSetInputAdapter(context.Locks),
                     GrasshopperToolAdapters.GhDeleteAdapter(context.Locks),
                     GrasshopperToolAdapters.GhGroupAdapter(),
                     GrasshopperToolAdapters.GhLayoutAdapter(),
                     GrasshopperToolAdapters.GhRelayoutAdapter(),
                 })
        {
            registry.Register(definition, handler);
        }
        registry.Register(GhFindComponentTool.Definition, GhFindComponentTool.HandlerFor(context.Catalog));
        registry.Register(GhPluginCheckTool.Definition, GhPluginCheckTool.HandlerFor(context.Catalog));
        registry.Register(GhComponentLockTool.Definition, GhComponentLockTool.HandlerFor(context.Locks));
        return registry;
    }

    /// <summary>Definitions (name, description, schema) of every product tool, in registration order.</summary>
    public static IReadOnlyList<AIToolDefinition> Definitions(ProductToolContext context) =>
        Create(context).GetDefinitions();

    /// <summary>
    /// Declared argument names per product tool, for the batch guard's strict argument check
    /// (<see cref="ToolBatchGuard(SessionOptions, ISet{string}, IReadOnlyDictionary{string, IReadOnlyCollection{string}}?)"/>).
    /// Pass <paramref name="sessionTools"/> (e.g. <c>ask_user</c>, <c>parti_record_decomposition</c>)
    /// so the session's own tools are held to their schemas too.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> DeclaredArguments(
        ProductToolContext context,
        IEnumerable<AIToolDefinition>? sessionTools = null)
    {
        var definitions = new List<AIToolDefinition>(Definitions(context));
        if (sessionTools is not null)
        {
            definitions.AddRange(sessionTools);
        }
        return ToolBatchGuard.DeclaredArgumentsFrom(definitions);
    }

    /// <summary>
    /// The deterministic freeze-time policies for the product, over the same catalog and lock
    /// registry the tools use: <see cref="ComponentLockPolicy"/> then <see cref="ComponentCatalogPolicy"/>.
    /// </summary>
    public static IReadOnlyList<IToolCallPolicy> Policies(ProductToolContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }
        return new IToolCallPolicy[]
        {
            new ComponentLockPolicy(context.Locks),
            new ComponentCatalogPolicy(context.Catalog),
        };
    }
}
