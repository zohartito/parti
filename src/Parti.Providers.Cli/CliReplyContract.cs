// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Parti.Providers.Cli;

/// <summary>One tool call as the model wrote it in a reply.</summary>
/// <param name="Id">The model's call id (may be blank; the provider assigns one then).</param>
/// <param name="Name">Tool name.</param>
/// <param name="Arguments">Arguments parsed from <c>arguments_json</c>.</param>
public sealed record CliToolCall(string Id, string Name, JObject Arguments);

/// <summary>A reply that satisfied <see cref="CliReplyContract"/>.</summary>
public sealed record CliReply(string Text, IReadOnlyList<CliToolCall> ToolCalls);

/// <summary>
/// The one reply contract both CLIs are held to by their schema flag:
/// <c>{"text": string, "tool_calls": [{"id": string, "name": string, "arguments_json": string}]}</c>,
/// no extra keys, every key required. Arguments travel as a JSON <em>string</em> because Codex's
/// strict schema mode rejects free-form objects. Parti's <c>ToolBatchGuard</c> still validates
/// every call downstream; this only checks the shape.
/// </summary>
public static class CliReplyContract
{
    /// <summary>Most tool calls accepted in one reply (the session's guards apply lower limits).</summary>
    public const int MaxToolCallsPerReply = 64;

    /// <summary>The JSON schema passed to <c>claude --json-schema</c> and <c>codex --output-schema</c>.</summary>
    public const string SchemaJson =
        "{\"type\":\"object\",\"additionalProperties\":false," +
        "\"properties\":{\"text\":{\"type\":\"string\"}," +
        "\"tool_calls\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":false," +
        "\"properties\":{\"id\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"arguments_json\":{\"type\":\"string\"}}," +
        "\"required\":[\"id\",\"name\",\"arguments_json\"]}}}," +
        "\"required\":[\"text\",\"tool_calls\"]}";

    /// <summary>
    /// Checks <paramref name="reply"/> against the contract. On failure <paramref name="problem"/>
    /// is a short, model-facing description of what was wrong.
    /// </summary>
    public static bool TryParse(JObject? reply, out CliReply? parsed, out string problem)
    {
        parsed = null;
        problem = string.Empty;
        if (reply is null)
        {
            problem = "the reply was not a JSON object";
            return false;
        }

        if (reply["text"] is not JValue { Type: JTokenType.String } textToken)
        {
            problem = "\"text\" must be a string";
            return false;
        }
        if (reply["tool_calls"] is not JArray callsToken)
        {
            problem = "\"tool_calls\" must be an array";
            return false;
        }
        if (callsToken.Count > MaxToolCallsPerReply)
        {
            problem = $"at most {MaxToolCallsPerReply} tool calls fit in one reply";
            return false;
        }

        var calls = new List<CliToolCall>(callsToken.Count);
        for (var index = 0; index < callsToken.Count; index++)
        {
            if (callsToken[index] is not JObject call)
            {
                problem = $"tool_calls[{index}] must be an object";
                return false;
            }
            var id = call["id"] is JValue { Type: JTokenType.String } idToken ? (string)idToken! : null;
            var name = call["name"] is JValue { Type: JTokenType.String } nameToken ? (string)nameToken! : null;
            var argumentsJson = call["arguments_json"] is JValue { Type: JTokenType.String } argsToken ? (string)argsToken! : null;
            if (id is null || argumentsJson is null)
            {
                problem = $"tool_calls[{index}] needs string \"id\" and \"arguments_json\"";
                return false;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                problem = $"tool_calls[{index}] has no tool name";
                return false;
            }
            if (!TryParseArguments(argumentsJson, out var arguments))
            {
                problem = $"tool_calls[{index}].arguments_json is not a JSON object encoded as a string";
                return false;
            }
            calls.Add(new CliToolCall(id.Trim(), name!.Trim(), arguments!));
        }

        parsed = new CliReply((string)textToken!, calls);
        return true;
    }

    /// <summary>Parses the text of a reply file or field into a JSON object, or null.</summary>
    public static JObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            // Strings stay strings (no date coercion); one value only, nothing after it.
            using var reader = new JsonTextReader(new System.IO.StringReader(json!))
            {
                DateParseHandling = DateParseHandling.None,
                MaxDepth = 64,
            };
            var token = JToken.ReadFrom(reader);
            return reader.Read() ? null : token as JObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseArguments(string json, out JObject? arguments)
    {
        arguments = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            arguments = new JObject();
            return true;
        }
        arguments = ParseObject(json);
        return arguments is not null;
    }
}
