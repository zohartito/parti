// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Parti.Eval.V01;

/// <summary>
/// In-memory representation of a single eval fixture. Loaded from JSON by
/// <see cref="FixtureLoader"/> with strict deserialization: an unknown key anywhere in the
/// fixture (a mistyped predicate, a stale field) fails the load. The schema is documented in
/// fixtures/README.md next to the JSON files.
/// </summary>
public sealed class EvalFixture
{
    /// <summary>Slug identifier (e.g. "01_text_only"). Unique across the fixture set.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable one-line description. Shown in the report.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The user prompt that starts the loop through <c>ChatController.SendAsync</c>.</summary>
    public string UserPrompt { get; set; } = string.Empty;

    /// <summary>
    /// Canvas backing the Grasshopper primitives: <c>"canned"</c> (the default) answers from
    /// <see cref="ToolHandlers"/> only; <c>"hermetic"</c> runs <see cref="HermeticCanvas"/>, which
    /// tracks components, wires and values over the Rhino 8 catalog snapshot.
    /// </summary>
    public string Canvas { get; set; } = "canned";

    /// <summary>Starting state of the hermetic canvas (hermetic fixtures only).</summary>
    public FixtureCanvasSeed? CanvasSeed { get; set; }

    /// <summary>Plan reviewer behaviour: <c>"approve"</c> (the default) or <c>"reject"</c>.</summary>
    public string Reviewer { get; set; } = "approve";

    /// <summary>
    /// Answers applied through <c>ChatController.AnswerAsync</c> when the loop pauses on
    /// <c>ask_user</c>. Null or empty leaves the loop paused.
    /// </summary>
    public List<FixtureClarificationAnswer>? ClarificationAnswers { get; set; }

    /// <summary>
    /// Opt-out for the load-time tool-name check. Only a fixture that exercises an unregistered
    /// tool name (09_unknown_tool) sets this.
    /// </summary>
    public bool ExpectUnknownTool { get; set; }

    /// <summary>Optional loop budget overrides. The product budgets apply when absent.</summary>
    public FixtureSessionOptions? SessionOptions { get; set; }

    /// <summary>
    /// Ordered list of provider responses. Entry N is streamed for provider call N; the last one
    /// replays (with fresh tool ids) once the script runs out. Required for scripted fixtures,
    /// forbidden for live fixtures.
    /// </summary>
    public List<FixtureProviderStep> ProviderScript { get; set; } = new();

    /// <summary>
    /// Map of Grasshopper primitive name to canned behaviour. Overrides the hermetic canvas for
    /// that tool. <c>gh_find_component</c>, <c>gh_plugin_check</c> and <c>gh_component_lock</c>
    /// always run their real handlers and cannot be canned.
    /// </summary>
    public Dictionary<string, FixtureToolHandler> ToolHandlers { get; set; } = new();

    /// <summary>Assertions applied after the loop finishes.</summary>
    public FixtureExpected Expected { get; set; } = new();
}

/// <summary>Loop budget overrides inside a fixture.</summary>
public sealed class FixtureSessionOptions
{
    public int? MaxTurns { get; set; }
    public int? MaxToolPasses { get; set; }
}

/// <summary>One <c>ask_user</c> answer.</summary>
public sealed class FixtureClarificationAnswer
{
    public string Id { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
}

/// <summary>Starting state of a hermetic canvas.</summary>
public sealed class FixtureCanvasSeed
{
    public List<FixtureSeedComponent> Components { get; set; } = new();
    public List<FixtureSeedWire> Wires { get; set; } = new();

    /// <summary>Instance GUIDs the user has locked (through the UI) before the prompt.</summary>
    public List<string> Locked { get; set; } = new();
}

/// <summary>A seeded component instance.</summary>
public sealed class FixtureSeedComponent
{
    public string InstanceGuid { get; set; } = string.Empty;
    public string ComponentGuid { get; set; } = string.Empty;
    public string? Nickname { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public JToken? Value { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public bool Selected { get; set; }
}

/// <summary>A seeded wire between two seeded instances.</summary>
public sealed class FixtureSeedWire
{
    public string From { get; set; } = string.Empty;
    public string FromOutput { get; set; } = "0";
    public string To { get; set; } = string.Empty;
    public string ToInput { get; set; } = "0";
}

/// <summary>
/// A single step in the provider script: its stopReason ("end_turn" or "tool_use") and the
/// content blocks that come back. Text blocks are streamed as text deltas.
/// </summary>
public sealed class FixtureProviderStep
{
    public string StopReason { get; set; } = "end_turn";
    public List<FixtureInteraction> Interactions { get; set; } = new();
}

/// <summary>A content block within a provider step.</summary>
public sealed class FixtureInteraction
{
    /// <summary>"text", "reasoning" or "tool_call".</summary>
    public string Kind { get; set; } = "text";

    /// <summary>Text payload when <see cref="Kind"/> is "text" or "reasoning".</summary>
    public string? Text { get; set; }

    /// <summary>Tool name when <see cref="Kind"/> is "tool_call".</summary>
    public string? ToolName { get; set; }

    /// <summary>Tool call id (e.g. "toolu_abc"). Derived from the step position when absent.</summary>
    public string? ToolId { get; set; }

    /// <summary>Tool arguments when <see cref="Kind"/> is "tool_call".</summary>
    public JObject? Input { get; set; }
}

/// <summary>
/// Canned handler for one Grasshopper primitive. Priority is Error &gt; Sequence &gt; Result.
/// </summary>
public sealed class FixtureToolHandler
{
    /// <summary>Single canned JSON result returned every time the tool is called.</summary>
    public JObject? Result { get; set; }

    /// <summary>Sequential canned results. Call N returns entry N; the last entry repeats.</summary>
    public List<JObject>? Sequence { get; set; }

    /// <summary>If set, the handler returns <c>{"status":"error","message":…}</c>.</summary>
    public string? Error { get; set; }
}

/// <summary>A component type expected on the hermetic canvas.</summary>
public sealed class FixtureCanvasComponentExpectation
{
    public string ComponentGuid { get; set; } = string.Empty;

    /// <summary>
    /// Instead of <see cref="ComponentGuid"/>: component types that are equally correct (e.g. Circle or
    /// Circle CNR); the bounds apply to their combined count.
    /// </summary>
    public List<string>? AnyOf { get; set; }

    public int Min { get; set; } = 1;

    /// <summary>Optional upper bound; 0 asserts the type is absent.</summary>
    public int? Max { get; set; }
}

/// <summary>A wire expected on the hermetic canvas, matched by component type or by exact instance.</summary>
public sealed class FixtureCanvasWireExpectation
{
    /// <summary>Component GUID (type) or instance GUID of the source component.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Instead of <see cref="From"/>: equally correct source types or instances.</summary>
    public List<string>? FromAnyOf { get; set; }

    /// <summary>Component GUID (type) or instance GUID of the target component.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>Instead of <see cref="To"/>: equally correct target types or instances.</summary>
    public List<string>? ToAnyOf { get; set; }
}

/// <summary>An evaluation criterion expected in the recorded decomposition.</summary>
public sealed class FixtureEvaluationExpectation
{
    /// <summary>Case-insensitive substring of the metric.</summary>
    public string Metric { get; set; } = string.Empty;

    /// <summary>Exact direction: MAXIMIZE, MINIMIZE or KEEP.</summary>
    public string Direction { get; set; } = string.Empty;
}

/// <summary>
/// Expected outcome assertions. Every field except <see cref="TerminalStatus"/> is optional; a
/// null field means "do not check". The predicate set is closed: the loader rejects unknown keys.
/// </summary>
public sealed class FixtureExpected
{
    /// <summary>
    /// Required. The controller's status after the run: <c>Idle</c>, <c>Paused</c> or
    /// <c>Error</c>. A fixture passes only when this matches (A14). A live brief where more than
    /// one ending is correct may list alternatives separated by <c>|</c> (e.g. <c>Idle|Paused</c>).
    /// </summary>
    public string? TerminalStatus { get; set; }

    /// <summary>Expected final stop reason (e.g. "end_turn").</summary>
    public string? FinalStopReason { get; set; }

    /// <summary>Expected AICallStatus of the final return ("Finished" or "Error").</summary>
    public string? FinalStatus { get; set; }

    /// <summary>Minimum number of interactions in the controller history.</summary>
    public int? MinInteractions { get; set; }

    /// <summary>Substring that must appear in at least one assistant text (case-sensitive).</summary>
    public string? TextContains { get; set; }

    /// <summary>Exact number of calls that reached the tool dispatcher.</summary>
    public int? ToolCallCount { get; set; }

    /// <summary>Exact number of provider calls.</summary>
    public int? ProviderCallCount { get; set; }

    /// <summary>Substring that must appear in the final return's ErrorMessage.</summary>
    public string? ErrorContains { get; set; }

    /// <summary>Tool names that must appear among the model's tool calls.</summary>
    public List<string>? ToolCallsInclude { get; set; }

    /// <summary>Tool names that must not appear among the model's tool calls.</summary>
    public List<string>? ToolCallsExclude { get; set; }

    /// <summary>Per-tool lower bound on the model's call count.</summary>
    public Dictionary<string, int>? ToolCallsMinCount { get; set; }

    /// <summary>Per-tool upper bound on the model's call count. 0 forbids the tool.</summary>
    public Dictionary<string, int>? ToolCallsMaxCount { get; set; }

    /// <summary>Case-insensitive substring of at least one assistant text.</summary>
    public string? FinalTextContains { get; set; }

    /// <summary>At least one of these case-insensitive substrings appears in assistant text.</summary>
    public List<string>? FinalTextContainsAny { get; set; }

    /// <summary>All of these case-insensitive substrings appear in assistant text.</summary>
    public List<string>? FinalTextContainsAll { get; set; }

    // ---- product-loop predicates (D7) ----

    /// <summary>Exact number of plan reviews the reviewer saw.</summary>
    public int? PlanReviewCount { get; set; }

    /// <summary>Substrings that must each appear in some reviewed call's catalog-resolved label.</summary>
    public List<string>? PlanLabelsContain { get; set; }

    /// <summary>Whether the recorded decomposition passes the three-pillar gate at the end.</summary>
    public bool? DecompositionComplete { get; set; }

    /// <summary>Case-insensitive substrings that must each match a recorded input name.</summary>
    public List<string>? DecompositionInputsInclude { get; set; }

    /// <summary>Evaluation criteria that must be recorded (ROADMAP's fitness_clauses_include).</summary>
    public List<FixtureEvaluationExpectation>? EvaluationsInclude { get; set; }

    /// <summary>Hermetic only: component types that must be on the canvas at the end.</summary>
    public List<FixtureCanvasComponentExpectation>? CanvasHasComponent { get; set; }

    /// <summary>Hermetic only: exact number of wires on the canvas at the end.</summary>
    public int? CanvasWireCount { get; set; }

    /// <summary>Hermetic only: wires (by component type) that must exist at the end.</summary>
    public List<FixtureCanvasWireExpectation>? CanvasHasWire { get; set; }

    /// <summary>Hermetic only: true asserts the canvas equals its seed at the end.</summary>
    public bool? CanvasUnchanged { get; set; }

    /// <summary>Exact number of tool results rejected by the three-pillar gate.</summary>
    public int? GateRejections { get; set; }

    /// <summary>Exact number of tool results rejected because of a component lock.</summary>
    public int? LockRejections { get; set; }

    /// <summary>Exact number of tool results rejected because a componentGuid is not installed.</summary>
    public int? CatalogRejections { get; set; }

    /// <summary>Substrings that must each appear in some rejected tool result's message.</summary>
    public List<string>? RejectionMessagesContain { get; set; }

    /// <summary>Exact set of instance GUIDs locked at the end (the controller's lock list).</summary>
    public List<string>? LockedComponents { get; set; }

    /// <summary>Question ids that must have been shown by an <c>ask_user</c> pause.</summary>
    public List<string>? PausedWith { get; set; }
}
