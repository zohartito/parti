// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Grasshopper.Chat;
using Parti.Core.Grasshopper.Tools;
using Parti.Core.Sessions;
using Parti.Core.Tools;

namespace Parti.Eval.V01;

/// <summary>
/// The tool dispatcher the eval hands to <see cref="PartiComposition"/> as <c>ToolsOverride</c>.
/// Definitions (names, descriptions, schemas, reversibility) are exactly
/// <see cref="ProductToolSet.Definitions"/>, so the composition derives the same strict declared
/// argument map as the product. Handlers:
/// <list type="bullet">
///   <item><c>gh_find_component</c>, <c>gh_plugin_check</c>: the real Core handlers over the catalog.</item>
///   <item><c>gh_component_lock</c>: the real handler over the controller's own lock registry
///   (bound after the controller is built, see <see cref="BindTo"/>).</item>
///   <item>The ten Grasshopper primitives: a canned handler when the fixture has one, otherwise the
///   <see cref="HermeticCanvas"/> (hermetic fixtures) or a "no canned handler" error (canned fixtures).
///   Every primitive call must carry a live document lease, as the real primitives require.</item>
/// </list>
/// </summary>
public sealed class EvalToolSet
{
    /// <summary>
    /// Host-reserved argument the product's turn envelope attaches to approved dispatch copies
    /// (<c>GrasshopperDocumentCapability.LeaseArgument</c>, which is internal to Core.Grasshopper).
    /// </summary>
    public const string LeaseArgument = "__parti_document_lease";

    private static readonly FieldInfo? ControllerLocksField =
        typeof(ChatController).GetField("_locks", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly IReadOnlyDictionary<string, FixtureToolHandler> _canned;
    private readonly Dictionary<string, int> _cannedCounters = new(StringComparer.Ordinal);
    private readonly LeaseUndoScopeFactory _leases;
    private readonly List<string> _dispatched = new();
    private readonly object _gate = new();
    private ComponentLockRegistry? _locks;

    public EvalToolSet(
        IComponentCatalog catalog,
        LeaseUndoScopeFactory leases,
        IReadOnlyDictionary<string, FixtureToolHandler>? canned,
        FixtureCanvasSeed? seed,
        bool hermetic)
    {
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _canned = canned ?? new Dictionary<string, FixtureToolHandler>();
        Canvas = hermetic ? new HermeticCanvas(catalog, seed, _leases.IsLive, () => _locks) : null;

        var registry = new ToolRegistry();
        foreach (var definition in ProductDefinitions(catalog))
        {
            registry.Register(definition, HandlerFor(definition.Name, catalog));
        }
        Dispatcher = new CountingDispatcher(registry, this);
    }

    /// <summary>The dispatcher to pass as <c>PartiCompositionOptions.ToolsOverride</c>.</summary>
    public IToolDispatcher Dispatcher { get; }

    /// <summary>The hermetic canvas, or null for canned fixtures.</summary>
    public HermeticCanvas? Canvas { get; }

    /// <summary>Tool names that reached the dispatcher, in order.</summary>
    public IReadOnlyList<string> Dispatched { get { lock (_gate) return _dispatched.ToList(); } }

    /// <summary>Number of calls that reached the dispatcher.</summary>
    public int DispatchCount { get { lock (_gate) return _dispatched.Count; } }

    /// <summary>The lock registry the tools use (the controller's, once bound).</summary>
    public ComponentLockRegistry? Locks => _locks;

    /// <summary>Product tool definitions over <paramref name="catalog"/>, in registration order.</summary>
    public static IReadOnlyList<AIToolDefinition> ProductDefinitions(IComponentCatalog catalog) =>
        ProductToolSet.Definitions(new ProductToolContext(catalog, new ComponentLockRegistry()));

    /// <summary>
    /// Every tool name the product loop can see: the product set plus the session's tools.
    /// </summary>
    public static IReadOnlySet<string> ProductToolNames(IComponentCatalog catalog)
    {
        var names = new HashSet<string>(ProductDefinitions(catalog).Select(d => d.Name), StringComparer.Ordinal)
        {
            AskUserTool.ToolName,
            RecordDecompositionTool.ToolName,
        };
        return names;
    }

    /// <summary>
    /// Binds <c>gh_component_lock</c> and the canvas's dispatch-time lock check to the lock
    /// registry the composition built for <paramref name="controller"/>, which is the same one the
    /// freeze-time <c>ComponentLockPolicy</c> and the human-only unlock use. The composition keeps
    /// that registry private when <c>ToolsOverride</c> is set, so it is read by reflection here
    /// (hand-off: a composition seam would remove this).
    /// </summary>
    public ComponentLockRegistry BindTo(ChatController controller)
    {
        if (controller is null) throw new ArgumentNullException(nameof(controller));
        var registry = ControllerLocksField?.GetValue(controller) as ComponentLockRegistry
            ?? throw new InvalidOperationException("ChatController no longer exposes its lock registry as '_locks'; update EvalToolSet.BindTo.");
        _locks = registry;
        return registry;
    }

    private Func<JObject, CancellationToken, Task<JObject>> HandlerFor(string name, IComponentCatalog catalog)
    {
        switch (name)
        {
            case GhFindComponentTool.ToolName:
                return GhFindComponentTool.HandlerFor(catalog);
            case "gh_plugin_check":
                return GhPluginCheckTool.HandlerFor(catalog);
            case "gh_component_lock":
                return (args, ct) =>
                {
                    var locks = _locks ?? throw new InvalidOperationException("gh_component_lock dispatched before the lock registry was bound.");
                    return GhComponentLockTool.HandlerFor(locks)(args, ct);
                };
            default:
                return (args, ct) => Task.FromResult(Primitive(name, args));
        }
    }

    private JObject Primitive(string name, JObject args)
    {
        var lease = args[LeaseArgument]?.Type == JTokenType.String ? (string?)args[LeaseArgument] : null;
        if (!_leases.IsLive(lease))
        {
            return new JObject { ["status"] = "error", ["message"] = "Missing or expired pinned Grasshopper document lease." };
        }

        if (_canned.TryGetValue(name, out var handler))
        {
            if (!string.IsNullOrEmpty(handler.Error))
            {
                return new JObject { ["status"] = "error", ["message"] = handler.Error };
            }
            if (handler.Sequence is { Count: > 0 } sequence)
            {
                int index;
                lock (_gate)
                {
                    index = _cannedCounters.TryGetValue(name, out var n) ? n : 0;
                    _cannedCounters[name] = index + 1;
                }
                return (JObject)(sequence[Math.Min(index, sequence.Count - 1)] ?? new JObject()).DeepClone();
            }
            return (JObject)(handler.Result ?? new JObject()).DeepClone();
        }

        if (Canvas is not null)
        {
            return Canvas.Execute(name, args);
        }

        return new JObject { ["status"] = "error", ["message"] = $"The fixture has no canned handler for '{name}'." };
    }

    private sealed class CountingDispatcher : IToolDispatcher
    {
        private readonly ToolRegistry _inner;
        private readonly EvalToolSet _owner;

        public CountingDispatcher(ToolRegistry inner, EvalToolSet owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public IReadOnlyList<AIToolDefinition> GetDefinitions() => _inner.GetDefinitions();

        public bool HasTool(string name) => _inner.HasTool(name);

        public Task<AIInteractionToolResult> DispatchAsync(AIInteractionToolCall call, CancellationToken ct = default)
        {
            lock (_owner._gate)
            {
                _owner._dispatched.Add(call?.Name ?? string.Empty);
            }
            return _inner.DispatchAsync(call!, ct);
        }
    }
}
