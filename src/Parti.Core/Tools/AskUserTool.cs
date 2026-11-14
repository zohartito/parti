// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Sessions;

namespace Parti.Core.Tools;

/// <summary>
/// Built-in session-level tool that lets the model pause and ask the user for clarification
/// instead of guessing. Unlike canvas tools, <c>ask_user</c> has no traditional dispatch handler —
/// the <see cref="Parti.Core.Sessions.ConversationSession"/> intercepts calls and transitions
/// to a paused state; <see cref="Parti.Core.Sessions.ConversationSession.ResumeAsync"/> supplies
/// the answers and continues the turn loop.
/// <para>
/// Marked <see cref="AIToolDefinition.IsReversible"/> = false so pausing does not create an
/// undo entry on the Grasshopper canvas.
/// </para>
/// </summary>
public static class AskUserTool
{
    /// <summary>Canonical tool name surfaced to providers.</summary>
    public const string ToolName = "ask_user";

    /// <summary>
    /// Tool definition passed to the provider in every request's <c>tools</c> array. The
    /// session itself dispatches this tool (there is no handler in the registry).
    /// </summary>
    public static AIToolDefinition Definition { get; } = new AIToolDefinition(
        Name: ToolName,
        Description: "Pause and ask the user for clarification when the request is ambiguous "
            + "or a parameter cannot be inferred from the canvas. Prefer this over guessing. "
            + "The session will surface the questions to the UI and resume with the user's answers.",
        InputSchema: JObject.Parse(@"
{
  ""type"": ""object"",
  ""properties"": {
    ""questions"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""id"": {""type"": ""string""},
          ""prompt"": {""type"": ""string""},
          ""kind"": {""type"": ""string"", ""enum"": [""text"", ""radio"", ""component_picker""]},
          ""options"": {""type"": ""array"", ""items"": {""type"": ""string""}, ""description"": ""radio only: 2 or more choices""}
        },
        ""required"": [""id"", ""prompt"", ""kind""],
        ""additionalProperties"": false
      }
    }
  },
  ""required"": [""questions""]
}
"))
    {
        IsReversible = false,
    };

    /// <summary>Most questions in one call.</summary>
    public const int MaxQuestions = 8;

    /// <summary>Most options on one radio question.</summary>
    public const int MaxOptions = 12;

    private const int MaxIdLength = 64;
    private const int MaxPromptLength = 300;
    private const int MaxOptionLength = 120;

    /// <summary>The expected argument shape, appended to every parse error.</summary>
    public const string ExpectedShape =
        "Expected: {questions:[{id, prompt, kind: text|radio|component_picker, options?: [string]}]}; " +
        "1 to 8 questions; id unique and non-blank; prompt non-blank; options only for radio (2 to 12); no other fields.";

    /// <summary>
    /// Strictly parses the <c>questions</c> of an <c>ask_user</c> call. Every problem is listed in
    /// one <paramref name="error"/> (path and expectation, then <see cref="ExpectedShape"/>) so a
    /// model can repair the call in one retry; a call that fails never pauses the session, so the
    /// user is never shown a blank or unanswerable question. Prompts and options are cleaned with
    /// <see cref="CatalogText.Clean"/>.
    /// </summary>
    public static bool TryParseQuestions(JObject? arguments, out IReadOnlyList<Question> questions, out string error)
    {
        questions = Array.Empty<Question>();
        error = string.Empty;
        var errors = new List<string>();
        var parsed = new List<Question>();

        if (arguments is null)
        {
            error = "arguments are missing. " + ExpectedShape;
            return false;
        }

        foreach (var property in arguments.Properties().Where(p => p.Name != "questions"))
        {
            errors.Add($"arguments has an unknown field '{CatalogText.Clean(property.Name, 40)}'");
        }

        if (arguments["questions"] is not JArray array)
        {
            errors.Add("'questions' is required and must be an array");
            array = new JArray();
        }
        else if (array.Count == 0 || array.Count > MaxQuestions)
        {
            errors.Add($"'questions' has {array.Count} items; ask 1 to {MaxQuestions}");
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(array.Count, MaxQuestions); i++)
        {
            var path = $"questions[{i}]";
            if (array[i] is not JObject item)
            {
                errors.Add($"{path} must be an object");
                continue;
            }

            var before = errors.Count;
            foreach (var property in item.Properties())
            {
                if (property.Name is not ("id" or "prompt" or "kind" or "options"))
                {
                    errors.Add($"{path} has an unknown field '{CatalogText.Clean(property.Name, 40)}' (allowed: id, prompt, kind, options)");
                }
            }

            var id = ReadString(item, "id", MaxIdLength, path, errors);
            if (id is not null && !seenIds.Add(id))
            {
                errors.Add($"{path}.id '{id}' repeats an earlier id; ids must be unique");
            }

            var prompt = ReadString(item, "prompt", MaxPromptLength, path, errors);

            QuestionKind? kind = null;
            var kindToken = item["kind"];
            switch (kindToken?.Type == JTokenType.String ? (string?)kindToken : null)
            {
                case "text":
                    kind = QuestionKind.Text;
                    break;
                case "radio":
                    kind = QuestionKind.Radio;
                    break;
                case "component_picker":
                    kind = QuestionKind.ComponentPicker;
                    break;
                default:
                    errors.Add($"'{path}.kind' is required and must be text, radio or component_picker");
                    break;
            }

            List<string>? options = null;
            var optionsToken = item["options"];
            var hasOptions = optionsToken is not null && optionsToken.Type != JTokenType.Null;
            if (kind == QuestionKind.Radio)
            {
                options = ReadOptions(optionsToken, path, errors);
            }
            else if (hasOptions && kind is not null)
            {
                errors.Add($"'{path}.options' is only allowed for kind radio");
            }

            if (errors.Count == before)
            {
                parsed.Add(new Question(id!, prompt!, kind!.Value, options));
            }
        }

        if (errors.Count > 0)
        {
            error = $"{errors.Count} problem(s): " + string.Join("; ", errors.Take(24))
                + (errors.Count > 24 ? $"; and {errors.Count - 24} more" : string.Empty)
                + ". " + ExpectedShape;
            return false;
        }

        questions = parsed;
        return true;
    }

    private static string? ReadString(JObject item, string key, int maxLength, string path, List<string> errors)
    {
        var token = item[key];
        if (token is null || token.Type != JTokenType.String)
        {
            errors.Add($"'{path}.{key}' is required and must be a non-blank string");
            return null;
        }

        var raw = (string?)token ?? string.Empty;
        if (raw.Length > maxLength)
        {
            errors.Add($"'{path}.{key}' is longer than {maxLength} characters");
            return null;
        }

        var cleaned = CatalogText.Clean(raw, maxLength);
        if (cleaned.Length == 0)
        {
            errors.Add($"'{path}.{key}' must not be blank");
            return null;
        }

        return cleaned;
    }

    private static List<string>? ReadOptions(JToken? token, string path, List<string> errors)
    {
        if (token is not JArray array || array.Count < 2 || array.Count > MaxOptions)
        {
            errors.Add($"'{path}.options' is required for kind radio: an array of 2 to {MaxOptions} non-blank strings");
            return null;
        }

        var options = new List<string>(array.Count);
        for (var j = 0; j < array.Count; j++)
        {
            var raw = array[j]?.Type == JTokenType.String ? (string?)array[j] : null;
            var cleaned = raw is null || raw.Length > MaxOptionLength ? string.Empty : CatalogText.Clean(raw, MaxOptionLength);
            if (cleaned.Length == 0)
            {
                errors.Add($"'{path}.options[{j}]' must be a non-blank string of at most {MaxOptionLength} characters");
                return null;
            }

            if (options.Contains(cleaned, StringComparer.Ordinal))
            {
                errors.Add($"'{path}.options[{j}]' repeats an earlier option");
                return null;
            }

            options.Add(cleaned);
        }

        return options;
    }
}
