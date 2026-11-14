// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using Newtonsoft.Json.Linq;

namespace Parti.Core.Ai;

/// <summary>
/// Minimal tool definition passed to a provider. The provider decides how to render this on the wire
/// (Anthropic: <c>name</c>/<c>description</c>/<c>input_schema</c>; Ollama: OpenAI-compat
/// <c>function.name</c>/<c>function.parameters</c>).
/// </summary>
public sealed record AIToolDefinition(string Name, string Description, JObject InputSchema)
{
    /// <summary>
    /// True when this tool mutates the Grasshopper canvas and its effect can be
    /// rolled back through the undo system (<see cref="Parti.Core.Grasshopper.IUndoScope"/>).
    /// The session wraps every reversible tool call for a single assistant turn
    /// in one undo scope so a single Ctrl+Z reverts the whole turn.
    ///
    /// Read-only tools (e.g. <c>gh_get_all</c>, <c>gh_describe</c>) leave this at
    /// the default of <c>false</c>.
    ///
    /// Added in v0.6 — explicit declaration avoids reflection-based
    /// heuristics and lets third-party tool packs opt in to undo batching.
    /// </summary>
    public bool IsReversible { get; init; }
}
