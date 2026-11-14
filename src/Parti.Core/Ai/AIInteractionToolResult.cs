// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// Ported from SmartHopper (https://github.com/architects-toolkit/SmartHopper)
// Original Copyright (c) SmartHopper contributors, LGPL-3.0-or-later

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Parti.Core.Ai;

/// <summary>
/// Result of executing a tool, sent back to the model so it can produce its next turn.
/// Maps to Anthropic's <c>tool_result</c> content block (encoded as a User message).
/// Inherits Id/Name/Arguments from <see cref="AIInteractionToolCall"/> so the pair can be kept together.
/// </summary>
public class AIInteractionToolResult : AIInteractionToolCall
{
    /// <inheritdoc />
    public override AIAgent Agent { get; set; } = AIAgent.ToolResult;

    /// <summary>
    /// Tool execution result as JSON.
    /// </summary>
    public JObject Result { get; set; } = new();

    public override string ToString()
    {
        var result = "Tool result";
        if (!string.IsNullOrEmpty(Name))
        {
            result += $" from {Name}";
        }

        if (Result != null && Result.HasValues)
        {
            result += $":\n{JsonConvert.SerializeObject(Result, Formatting.Indented)}";
        }

        return result;
    }
}
