// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Sessions;
using Parti.Core.Tools;

namespace Parti.Core.Grasshopper.Chat;

/// <summary>
/// The short, bounded lines the chat transcript shows besides the model's own text: one line per
/// tool call with its outcome, and fixed notices. Pure functions, so the controller and its tests
/// share them.
/// </summary>
/// <remarks>
/// A tool line is built from the session's scrubbed result (the document lease is already
/// removed) and shows only a whitelisted subject per tool, never raw argument JSON. Any argument
/// whose name starts with <c>__parti</c> is ignored as a second guard. Control characters and line
/// breaks are flattened and every line is capped at <see cref="MaxLineLength"/>.
/// </remarks>
public static class TranscriptLines
{
    /// <summary>Maximum length of one transcript line, including the leading marker.</summary>
    public const int MaxLineLength = 160;

    /// <summary>Shown under a text reply the provider stopped at the output-token limit.</summary>
    public const string TokenLimitNotice = "(reply cut off at the token limit)";

    /// <summary>Shown when <see cref="CanvasChangedNote"/> is added to the user's message.</summary>
    public const string CanvasChangedNotice = "(the canvas changed outside this chat; Parti was asked to re-read it)";

    /// <summary>
    /// Appended to the next user message after the canvas's undo history changed outside a Parti
    /// turn (the user pressed Undo/Redo or edited the canvas), so the model does not act on a
    /// stale picture.
    /// </summary>
    public const string CanvasChangedNote =
        "[Parti automatic note] The canvas changed outside this chat (undo, redo or a manual edit); " +
        "re-read it with gh_get_all before editing.";

    private const string Marker = "· ";
    private const string Arrow = " → ";
    private const int SubjectLength = 60;
    private const int StatusLength = 70;

    /// <summary>
    /// One line for a finished tool call, e.g. <c>· gh_find_component "Number Slider" → 1 match</c>
    /// or <c>· gh_add_component Number Slider → added</c>.
    /// </summary>
    public static string ToolResult(AIInteractionToolResult result)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        var name = Flatten(result.Name, 40);
        if (name.Length == 0)
        {
            name = "(tool)";
        }

        var arguments = WithoutHostArguments(result.Arguments);
        var payload = result.Result ?? new JObject();
        var subject = Flatten(Subject(result.Name, arguments, payload), SubjectLength);
        var status = Flatten(Status(result.Name, payload), StatusLength);

        var line = new StringBuilder(Marker).Append(name);
        if (subject.Length > 0)
        {
            line.Append(' ').Append(subject);
        }
        line.Append(Arrow).Append(status.Length == 0 ? "done" : status);
        return Cap(line.ToString(), MaxLineLength);
    }

    /// <summary>True when <paramref name="result"/> is a reply the provider cut off at the token limit.</summary>
    public static bool IsCutOffAtTokenLimit(AIReturn? result) =>
        result is not null
        && !result.HasToolCalls
        && (string.Equals(result.StopReason, "max_tokens", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.StopReason, "length", StringComparison.OrdinalIgnoreCase));

    private static string Subject(string? tool, JObject arguments, JObject payload)
    {
        switch (tool)
        {
            case GhFindComponentTool.ToolName:
                var query = Text(arguments["query"]);
                return query.Length == 0 ? string.Empty : $"\"{Flatten(query, SubjectLength - 2)}\"";
            case "gh_add_component":
                // The component the host actually created, never the model's nickname.
                return Text(payload["canonicalName"]);
            case "gh_delete":
            case "gh_describe":
                return Text(arguments["target"]);
            case AskUserTool.ToolName:
            case RecordDecompositionTool.ToolName:
            case "gh_get_all":
            case "gh_get_selected":
                return string.Empty;
            default:
                return PlanReviewBuilder.SummarizeArguments(tool ?? string.Empty, arguments) ?? string.Empty;
        }
    }

    private static string Status(string? tool, JObject payload)
    {
        if (payload["cancelled"] is JValue { Type: JTokenType.Boolean } cancelled && (bool)cancelled)
        {
            return "cancelled";
        }

        var status = Text(payload["status"]);
        var failed = payload["success"] is JValue { Type: JTokenType.Boolean } success && !(bool)success;
        if (failed
            || payload["error"] is not null
            || status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("locked", StringComparison.OrdinalIgnoreCase)
            || status.Equals("rejected", StringComparison.OrdinalIgnoreCase))
        {
            var message = Text(payload["error"]);
            if (message.Length == 0)
            {
                message = Text(payload["message"]);
            }
            var label = status.Equals("locked", StringComparison.OrdinalIgnoreCase) ? "locked" : "error";
            return message.Length == 0 ? label : $"{label}: {message}";
        }

        // Success verbs only for a confirmed success. Any other status (not_found, unsupported,
        // partial, missing_plugin, ...) is shown as reported, with its message.
        if (status.Length > 0
            && !status.Equals("success", StringComparison.OrdinalIgnoreCase)
            && !status.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            var message = Text(payload["message"]);
            return message.Length == 0 ? status : $"{status}: {message}";
        }

        switch (tool)
        {
            case GhFindComponentTool.ToolName:
                var count = payload["count"] is JValue { Type: JTokenType.Integer } value ? (long)value : -1;
                return count < 0 ? "ok" : count == 1 ? "1 match" : $"{count} matches";
            case "gh_add_component":
                return "added";
            case "gh_delete":
                return "deleted";
            case "gh_connect":
                return "connected";
            case "gh_set_input":
                return "set";
            default:
                return "ok";
        }
    }

    private static JObject WithoutHostArguments(JObject? arguments)
    {
        var copy = (JObject)(arguments ?? new JObject()).DeepClone();
        foreach (var property in copy.Properties()
                     .Where(p => p.Name.StartsWith("__parti", StringComparison.Ordinal))
                     .ToList())
        {
            property.Remove();
        }
        return copy;
    }

    private static string Text(JToken? token) =>
        token is JValue { Type: JTokenType.String or JTokenType.Integer or JTokenType.Float or JTokenType.Boolean } value
            ? value.ToString()
            : string.Empty;

    /// <summary>Collapses control characters and whitespace runs to single spaces and caps the length.</summary>
    private static string Flatten(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(Math.Min(text!.Length, max + 1));
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || (c >= '\u0080' && c <= '\u009f'))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
            if (sb.Length > max)
            {
                break;
            }
        }
        return Cap(sb.ToString(), max);
    }

    private static string Cap(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, Math.Max(0, max - 1)) + "…";
}
