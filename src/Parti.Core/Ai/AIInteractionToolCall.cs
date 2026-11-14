// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// Ported from SmartHopper (https://github.com/architects-toolkit/SmartHopper)
// Original Copyright (c) SmartHopper contributors, LGPL-3.0-or-later

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Parti.Core.Ai;

/// <summary>
/// Assistant decision to invoke a tool. Maps to Anthropic's <c>tool_use</c> content block.
/// </summary>
public class AIInteractionToolCall : AIInteractionBase
{
    /// <inheritdoc />
    public override AIAgent Agent { get; set; } = AIAgent.ToolCall;

    /// <summary>
    /// Provider-assigned tool call id (e.g. "toolu_...").
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Tool name (e.g. "gh_add_component").
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Tool arguments as a JSON object (Anthropic's <c>input</c>).
    /// </summary>
    public JObject Arguments { get; set; } = new();

    /// <summary>
    /// Optional reasoning content emitted alongside the tool call (e.g. DeepSeek reasoning_content).
    /// </summary>
    public string? Reasoning { get; set; }

    public override string ToString()
    {
        var result = "Calling tool";
        if (!string.IsNullOrEmpty(Id))
        {
            result += $" ({Id})";
        }

        if (!string.IsNullOrEmpty(Name))
        {
            result += $" {Name}";
        }

        if (Arguments != null && Arguments.HasValues)
        {
            result += $" with arguments:\n{JsonConvert.SerializeObject(Arguments, Formatting.Indented)}";
        }

        return result;
    }
}
