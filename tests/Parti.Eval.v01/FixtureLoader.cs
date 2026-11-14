// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Parti.Core.Tools;

namespace Parti.Eval.V01;

/// <summary>
/// Loads eval fixtures strictly. A fixture fails to load when:
/// <list type="bullet">
///   <item>any object in it carries a key the schema does not define (a mistyped predicate, a
///   stale field) — <see cref="MissingMemberHandling.Error"/> on every fixture type;</item>
///   <item>it names a tool that is not in the product tool set (<c>ProductToolSet</c> plus
///   <c>ask_user</c> and <c>parti_record_decomposition</c>) in its provider script, its canned
///   handlers, or a tool predicate — unless it opts out with <c>expectUnknownTool</c>;</item>
///   <item>it omits the expected terminal status, or uses canvas state on a canned canvas.</item>
/// </list>
/// </summary>
public static class FixtureLoader
{
    private static readonly Lazy<JsonComponentCatalog> SharedCatalog = new(() =>
        JsonComponentCatalog.Load(Path.Combine(AppContext.BaseDirectory, "shared", "rhino8-gh-catalog.json")));

    private static readonly JsonSerializerSettings StrictSettings = new()
    {
        MissingMemberHandling = MissingMemberHandling.Error,
    };

    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.Ordinal) { "Idle", "Paused", "Error" };
    private static readonly HashSet<string> Directions = new(StringComparer.Ordinal) { "MAXIMIZE", "MINIMIZE", "KEEP" };

    /// <summary>
    /// The Rhino 8.35 component catalog snapshot (tests/shared/rhino8-gh-catalog.json) every run
    /// uses as the host catalog.
    /// </summary>
    public static IComponentCatalog Catalog => SharedCatalog.Value;

    /// <summary>Directory of the gated fixtures, copied next to the test assembly.</summary>
    public static string FixturesDirectory() => Path.Combine(AppContext.BaseDirectory, "fixtures");

    /// <summary>Directory of the live briefs (not run by the gate).</summary>
    public static string LiveFixturesDirectory() => Path.Combine(FixturesDirectory(), "live");

    /// <summary>
    /// Every gated fixture: *.json directly under fixtures/. The <c>live/</c> and
    /// <c>deferred/</c> subfolders are not gated.
    /// </summary>
    public static IReadOnlyList<string> DiscoverFixtureFiles() => Discover(FixturesDirectory());

    /// <summary>Every live brief under fixtures/live/.</summary>
    public static IReadOnlyList<string> DiscoverLiveFixtureFiles() => Discover(LiveFixturesDirectory());

    /// <summary>Repository root (the directory holding Parti.sln), found from the test assembly.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Parti.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Could not find Parti.sln above the test assembly.");
    }

    /// <summary>Parses and validates a gated (scripted) fixture file.</summary>
    public static EvalFixture Load(string path) => Load(path, live: false);

    /// <summary>Parses and validates a fixture file; <paramref name="live"/> selects the live rules.</summary>
    public static EvalFixture Load(string path, bool live)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("path is required", nameof(path));
        }
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fixture not found: {path}", path);
        }

        var fixture = Parse(File.ReadAllText(path), path);
        var stem = Path.GetFileNameWithoutExtension(path);
        if (!string.Equals(fixture.Id, stem, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Fixture '{path}' has id '{fixture.Id}'; it must equal the file name '{stem}'.");
        }
        Validate(fixture, live);
        return fixture;
    }

    /// <summary>
    /// Strictly deserializes fixture JSON. Throws on unknown keys, on keys that differ from the
    /// schema only by case (Newtonsoft would bind them), and on duplicate keys. Provider-script
    /// tool inputs and canned tool results are opaque payloads and are not key-checked.
    /// </summary>
    public static EvalFixture Parse(string json, string source = "<inline>")
    {
        try
        {
            var token = JToken.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            CheckExactKeys(token, typeof(EvalFixture), "$");
            return token.ToObject<EvalFixture>(JsonSerializer.Create(StrictSettings))
                ?? throw new InvalidOperationException($"Fixture deserialized to null: {source}");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Fixture '{source}' does not match the schema: {ex.Message}", ex);
        }
    }

    /// <summary>Recursively checks that every key in a schema object is a declared member, exact case.</summary>
    private static void CheckExactKeys(JToken token, Type type, string path)
    {
        if (token is JObject obj && type.Namespace == typeof(EvalFixture).Namespace && type.IsClass)
        {
            var members = type.GetProperties().ToDictionary(pi => char.ToLowerInvariant(pi.Name[0]) + pi.Name[1..], pi => pi.PropertyType, StringComparer.Ordinal);
            foreach (var property in obj.Properties())
            {
                if (!members.TryGetValue(property.Name, out var memberType))
                {
                    throw new JsonSerializationException($"Could not find member '{property.Name}' on {type.Name} at {path}.");
                }
                CheckExactKeys(property.Value, memberType, $"{path}.{property.Name}");
            }
            return;
        }

        var element = Nullable.GetUnderlyingType(type) ?? type;
        if (token is JArray array && element.IsGenericType && element.GetGenericTypeDefinition() == typeof(List<>))
        {
            var itemType = element.GetGenericArguments()[0];
            for (var i = 0; i < array.Count; i++)
            {
                CheckExactKeys(array[i], itemType, $"{path}[{i}]");
            }
        }
        else if (token is JObject map && element.IsGenericType && element.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var valueType = element.GetGenericArguments()[1];
            foreach (var property in map.Properties())
            {
                CheckExactKeys(property.Value, valueType, $"{path}.{property.Name}");
            }
        }
    }

    /// <summary>
    /// Validates a parsed fixture against the product tool set and the schema rules. Throws
    /// <see cref="InvalidOperationException"/> naming the first problem.
    /// </summary>
    public static void Validate(EvalFixture fixture, bool live = false)
    {
        if (fixture is null) throw new ArgumentNullException(nameof(fixture));

        void Fail(string message) => throw new InvalidOperationException($"Fixture '{fixture.Id}': {message}");

        if (string.IsNullOrWhiteSpace(fixture.Id)) throw new InvalidOperationException("Fixture is missing 'id'.");
        if (string.IsNullOrWhiteSpace(fixture.UserPrompt)) Fail("missing 'userPrompt'.");
        if (fixture.Canvas is not ("canned" or "hermetic")) Fail($"'canvas' must be \"canned\" or \"hermetic\", not '{fixture.Canvas}'.");
        if (fixture.Reviewer is not ("approve" or "reject")) Fail($"'reviewer' must be \"approve\" or \"reject\", not '{fixture.Reviewer}'.");
        if (fixture.Expected.TerminalStatus is not { } terminal ||
            terminal.Split('|').Any(s => !TerminalStatuses.Contains(s)) ||
            (!live && terminal.Contains('|')))
        {
            Fail("'expected.terminalStatus' is required and must be Idle, Paused or Error (live briefs may list alternatives with '|').");
        }

        var hermetic = fixture.Canvas == "hermetic";
        if (!hermetic && fixture.CanvasSeed is not null) Fail("'canvasSeed' needs \"canvas\": \"hermetic\".");
        var e = fixture.Expected;
        if (!hermetic && (e.CanvasHasComponent is not null || e.CanvasWireCount is not null || e.CanvasHasWire is not null || e.CanvasUnchanged is not null))
        {
            Fail("canvas predicates need \"canvas\": \"hermetic\".");
        }

        if (live)
        {
            if (fixture.ProviderScript.Count > 0) Fail("a live brief has no 'providerScript'.");
            if (fixture.ToolHandlers.Count > 0) Fail("a live brief has no canned 'toolHandlers'.");
            if (!hermetic) Fail("a live brief runs on \"canvas\": \"hermetic\".");
        }
        else if (fixture.ProviderScript.Count == 0)
        {
            Fail("empty 'providerScript'.");
        }

        foreach (var step in fixture.ProviderScript)
        {
            foreach (var block in step.Interactions)
            {
                if (block.Kind is not ("text" or "reasoning" or "tool_call")) Fail($"unknown interaction kind '{block.Kind}'.");
                if (block.Kind == "tool_call" && string.IsNullOrWhiteSpace(block.ToolName)) Fail("a tool_call block has no toolName.");
            }
        }

        foreach (var evaluation in e.EvaluationsInclude ?? new List<FixtureEvaluationExpectation>())
        {
            if (!Directions.Contains(evaluation.Direction)) Fail($"evaluationsInclude direction '{evaluation.Direction}' must be MAXIMIZE, MINIMIZE or KEEP.");
            if (string.IsNullOrWhiteSpace(evaluation.Metric)) Fail("evaluationsInclude needs a non-empty metric.");
        }

        // An empty operand would make a predicate vacuous; it is a fixture error, not a pass.
        foreach (var (name, list) in new (string, List<string>?)[]
                 {
                     ("toolCallsInclude", e.ToolCallsInclude), ("toolCallsExclude", e.ToolCallsExclude),
                     ("finalTextContainsAny", e.FinalTextContainsAny), ("finalTextContainsAll", e.FinalTextContainsAll),
                     ("planLabelsContain", e.PlanLabelsContain), ("decompositionInputsInclude", e.DecompositionInputsInclude),
                     ("rejectionMessagesContain", e.RejectionMessagesContain), ("pausedWith", e.PausedWith),
                 })
        {
            if (list is not null && (list.Count == 0 || list.Any(string.IsNullOrWhiteSpace))) Fail($"'{name}' must be a non-empty list of non-empty strings.");
        }
        foreach (var (name, text) in new[] { ("textContains", e.TextContains), ("finalTextContains", e.FinalTextContains), ("errorContains", e.ErrorContains) })
        {
            if (text is not null && string.IsNullOrWhiteSpace(text)) Fail($"'{name}' must not be empty.");
        }
        foreach (var component in e.CanvasHasComponent ?? new List<FixtureCanvasComponentExpectation>())
        {
            if (component.AnyOf is not null && (component.AnyOf.Count < 2 || !string.IsNullOrEmpty(component.ComponentGuid)))
            {
                Fail("canvasHasComponent takes either componentGuid or anyOf (two or more GUIDs), not both.");
            }
            foreach (var guid in component.AnyOf ?? new List<string> { component.ComponentGuid })
            {
                if (!Guid.TryParse(guid, out var type) || !Catalog.TryGet(type, out _))
                {
                    Fail($"canvasHasComponent '{guid}' is not a component GUID in the catalog.");
                }
            }
        }
        foreach (var wire in e.CanvasHasWire ?? new List<FixtureCanvasWireExpectation>())
        {
            if ((wire.FromAnyOf is not null && (wire.FromAnyOf.Count < 2 || !string.IsNullOrEmpty(wire.From))) ||
                (wire.ToAnyOf is not null && (wire.ToAnyOf.Count < 2 || !string.IsNullOrEmpty(wire.To))))
            {
                Fail("canvasHasWire takes from or fromAnyOf, and to or toAnyOf (two or more GUIDs), not both.");
            }
            var endpoints = (wire.FromAnyOf ?? new List<string> { wire.From }).Concat(wire.ToAnyOf ?? new List<string> { wire.To });
            if (endpoints.Any(g => !Guid.TryParse(g, out _))) Fail("canvasHasWire from/to must be component or instance GUIDs.");
        }

        ValidateToolNames(fixture, Fail);
    }

    private static void ValidateToolNames(EvalFixture fixture, Action<string> fail)
    {
        var known = EvalToolSet.ProductToolNames(Catalog);
        var unknown = new List<string>();

        void Check(string name, string where)
        {
            if (!known.Contains(name))
            {
                if (!fixture.ExpectUnknownTool)
                {
                    fail($"tool '{name}' in {where} is not registered in the product tool set.");
                }
                unknown.Add(name);
            }
        }

        foreach (var block in fixture.ProviderScript.SelectMany(s => s.Interactions).Where(b => b.Kind == "tool_call"))
        {
            Check(block.ToolName!, "providerScript");
        }

        var e = fixture.Expected;
        foreach (var name in e.ToolCallsInclude ?? new List<string>()) Check(name, "expected.toolCallsInclude");
        foreach (var name in e.ToolCallsExclude ?? new List<string>()) Check(name, "expected.toolCallsExclude");
        foreach (var name in e.ToolCallsMinCount?.Keys ?? Enumerable.Empty<string>()) Check(name, "expected.toolCallsMinCount");
        foreach (var name in e.ToolCallsMaxCount?.Keys ?? Enumerable.Empty<string>()) Check(name, "expected.toolCallsMaxCount");

        foreach (var name in fixture.ToolHandlers.Keys)
        {
            if (!known.Contains(name))
            {
                fail($"tool '{name}' in toolHandlers is not registered in the product tool set.");
            }
            if (!HermeticCanvas.PrimitiveNames.Contains(name))
            {
                fail($"toolHandlers may only can Grasshopper primitives; '{name}' always runs its real handler.");
            }
        }

        if (fixture.ExpectUnknownTool && unknown.Count == 0)
        {
            fail("'expectUnknownTool' is set but every tool name is registered.");
        }
    }

    private static IReadOnlyList<string> Discover(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
    }
}
