// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Text.Json.Nodes;
using Newtonsoft.Json.Linq;

namespace Parti.Core.Tools;

/// <summary>
/// Bridges Newtonsoft.Json (used inside the AI layer) and System.Text.Json (used at the
/// Grasshopper plugin boundary). Both libraries speak JSON text, so the bridge just
/// serializes-then-parses — no clever in-memory conversion to maintain.
/// </summary>
public static class JsonBridge
{
    /// <summary>Convert a Newtonsoft <see cref="JObject"/> to a System.Text.Json <see cref="JsonObject"/>.</summary>
    public static JsonObject ToSystemText(JObject source)
    {
        var node = JsonNode.Parse(source.ToString(Newtonsoft.Json.Formatting.None));
        return node?.AsObject() ?? new JsonObject();
    }

    /// <summary>Convert a System.Text.Json <see cref="JsonObject"/> to a Newtonsoft <see cref="JObject"/>.</summary>
    public static JObject ToNewtonsoft(JsonObject source)
    {
        return JObject.Parse(source.ToJsonString());
    }
}
