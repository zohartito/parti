// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Design;

namespace Parti.Core.Tools;

/// <summary>
/// Session-level tool that records the model's three-pillar <see cref="Decomposition"/>
/// (inputs, rules, principle, evaluations, feedback plan). Like <see cref="AskUserTool"/>, it is
/// intercepted by <see cref="Sessions.ConversationSession"/> rather than dispatched to a
/// registry handler, and it must be the only call in its response.
/// <para>
/// Parsing is strict and bounded: unknown keys, bad enums, over-long strings, too many items,
/// and blank required strings are rejected, and a rejected record leaves the session's
/// decomposition unchanged.
/// </para>
/// </summary>
public static class RecordDecompositionTool
{
    /// <summary>Wire name of the tool.</summary>
    public const string ToolName = "parti_record_decomposition";

    private const int MaxInputs = 16;
    private const int MaxRules = 16;
    private const int MaxEvaluations = 8;

    /// <summary>
    /// JSON schema advertised to the model: the product-wiring spec (D4) plus the optional evaluation
    /// <c>comparator</c>, added after the live R3 run where a stated bound ("keep the area under
    /// 5 m2") could not be expressed with MAXIMIZE/MINIMIZE/KEEP alone and the model stalled.
    /// </summary>
    public static JObject Schema => JObject.Parse(SchemaJson);

    private const string SchemaJson = @"{""type"":""object"",""additionalProperties"":false,
 ""required"":[""inputs"",""rules"",""principle"",""evaluations""],
 ""properties"":{
  ""inputs"":{""type"":""array"",""maxItems"":16,""items"":{""type"":""object"",""additionalProperties"":false,
    ""required"":[""name"",""domain"",""source""],""properties"":{
      ""name"":{""type"":""string"",""maxLength"":80},""domain"":{""type"":""string"",""maxLength"":120},
      ""unit"":{""type"":""string"",""maxLength"":16},""source"":{""type"":""string"",""enum"":[""user_stated"",""assumed""]}}}},
  ""rules"":{""type"":""array"",""maxItems"":16,""items"":{""type"":""object"",""additionalProperties"":false,
    ""required"":[""statement"",""source""],""properties"":{
      ""statement"":{""type"":""string"",""maxLength"":200},""source"":{""type"":""string"",""enum"":[""user_stated"",""assumed""]}}}},
  ""principle"":{""type"":""string"",""maxLength"":200},
  ""evaluations"":{""type"":""array"",""maxItems"":8,""items"":{""type"":""object"",""additionalProperties"":false,
    ""required"":[""metric"",""direction"",""source""],""properties"":{
      ""metric"":{""type"":""string"",""maxLength"":80},
      ""direction"":{""type"":""string"",""enum"":[""MAXIMIZE"",""MINIMIZE"",""KEEP""]},
      ""target"":{""type"":""number""},""comparator"":{""type"":""string"",""enum"":[""<="","">="",""==""]},
      ""source"":{""type"":""string"",""enum"":[""user_stated"",""assumed""]}}}},
  ""feedbackPlan"":{""type"":""string"",""maxLength"":300}}}";

    /// <summary>Tool definition surfaced in every session request. Not reversible.</summary>
    public static AIToolDefinition Definition { get; } = new AIToolDefinition(
        Name: ToolName,
        Description: "Record the design decomposition before any canvas write: inputs that vary (name, domain, unit), "
            + "rules that must hold, the one-sentence principle, and evaluations (metric, MAXIMIZE/MINIMIZE/KEEP, optional target, optional comparator <=, >= or == that makes the target a hard bound: 'keep area under 5' is KEEP <= 5). "
            + "Mark each item user_stated or assumed. Must be the only call in its response. The host blocks canvas writes "
            + "until the decomposition has inputs, a principle or rule, and an evaluation.",
        InputSchema: Schema)
    {
        IsReversible = false,
    };

    /// <summary>
    /// The expected argument shape, appended to every parse error so a model can repair the whole
    /// call at once.
    /// </summary>
    public const string ExpectedShape =
        "Expected: {inputs:[{name, domain, unit?, source}], rules:[{statement, source}], principle, " +
        "evaluations:[{metric, direction: MAXIMIZE|MINIMIZE|KEEP, target?: number, comparator?: <=|>=|== (needs target), source}], " +
        "feedbackPlan?}; source is user_stated or assumed; no other fields.";

    /// <summary>Most problems listed in one error; the rest are counted.</summary>
    internal const int MaxListedErrors = 24;

    /// <summary>
    /// Parses <paramref name="arguments"/> into a <see cref="Decomposition"/>. Strings are
    /// cleaned with <see cref="CatalogText.Clean"/> (control and format characters removed,
    /// whitespace runs, including newlines, collapsed to one space, trimmed). Returns false with a
    /// human-readable <paramref name="error"/> on any violation of the schema, including blank
    /// required strings. The error lists EVERY problem found (path and what was expected, up to
    /// <see cref="MaxListedErrors"/>) followed by <see cref="ExpectedShape"/>, so one retry can
    /// fix them all; strictness is unchanged (unknown fields are still rejected).
    /// </summary>
    public static bool TryParse(JObject arguments, out Decomposition decomposition, out string error)
    {
        decomposition = Decomposition.Empty;
        error = string.Empty;

        if (arguments is null)
        {
            error = "arguments are missing. " + ExpectedShape;
            return false;
        }

        var errors = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok)
            {
                errors.Add(message);
            }
        }

        UnknownKeys(arguments, "arguments", errors, "inputs", "rules", "principle", "evaluations", "feedbackPlan");

        Check(TryArray(arguments, "inputs", MaxInputs, out var inputsArray, out var e), e);
        Check(TryArray(arguments, "rules", MaxRules, out var rulesArray, out e), e);
        Check(TryArray(arguments, "evaluations", MaxEvaluations, out var evaluationsArray, out e), e);
        Check(TryString(arguments, "principle", 200, required: true, "principle", out var principle, out e), e);
        Check(TryString(arguments, "feedbackPlan", 300, required: false, "feedbackPlan", out var feedbackPlan, out e), e);

        var inputs = new List<Input>(inputsArray.Count);
        for (var i = 0; i < inputsArray.Count; i++)
        {
            var path = $"inputs[{i}]";
            if (inputsArray[i] is not JObject item)
            {
                errors.Add($"{path} must be an object");
                continue;
            }
            var before = errors.Count;
            UnknownKeys(item, path, errors, "name", "domain", "unit", "source");
            Check(TryString(item, "name", 80, required: true, path + ".name", out var name, out e), e);
            Check(TryString(item, "domain", 120, required: true, path + ".domain", out var domain, out e), e);
            Check(TryString(item, "unit", 16, required: false, path + ".unit", out var unit, out e), e);
            Check(TrySource(item, path, out var source, out e), e);
            if (errors.Count == before)
            {
                inputs.Add(new Input(name!, domain!, unit, source));
            }
        }

        var rules = new List<Rule>(rulesArray.Count);
        for (var i = 0; i < rulesArray.Count; i++)
        {
            var path = $"rules[{i}]";
            if (rulesArray[i] is not JObject item)
            {
                errors.Add($"{path} must be an object");
                continue;
            }
            var before = errors.Count;
            UnknownKeys(item, path, errors, "statement", "source");
            Check(TryString(item, "statement", 200, required: true, path + ".statement", out var statement, out e), e);
            Check(TrySource(item, path, out var source, out e), e);
            if (errors.Count == before)
            {
                rules.Add(new Rule(statement!, source));
            }
        }

        var evaluations = new List<Evaluation>(evaluationsArray.Count);
        for (var i = 0; i < evaluationsArray.Count; i++)
        {
            var path = $"evaluations[{i}]";
            if (evaluationsArray[i] is not JObject item)
            {
                errors.Add($"{path} must be an object");
                continue;
            }
            var before = errors.Count;
            UnknownKeys(item, path, errors, "metric", "direction", "target", "comparator", "source");
            Check(TryString(item, "metric", 80, required: true, path + ".metric", out var metric, out e), e);
            Check(TryDirection(item, path, out var direction, out e), e);
            var targetOk = TryTarget(item, path, out var target, out e);
            Check(targetOk, e);
            EvalComparator? comparator = null;
            if (targetOk)
            {
                // The comparator is judged against a valid target only; a bad target is reported once.
                Check(TryComparator(item, path, target, out comparator, out e), e);
            }
            Check(TrySource(item, path, out var source, out e), e);
            if (errors.Count == before)
            {
                evaluations.Add(new Evaluation(metric!, direction, target, source, comparator));
            }
        }

        if (errors.Count > 0)
        {
            var listed = errors.Take(MaxListedErrors).ToList();
            var more = errors.Count - listed.Count;
            error = $"{errors.Count} problem(s): " + string.Join("; ", listed)
                + (more > 0 ? $"; and {more} more" : string.Empty)
                + ". " + ExpectedShape;
            return false;
        }

        decomposition = new Decomposition(inputs, rules, evaluations, principle, feedbackPlan);
        return true;
    }

    private static void UnknownKeys(JObject obj, string path, List<string> errors, params string[] allowed)
    {
        foreach (var property in obj.Properties())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                errors.Add($"{path} has an unknown field '{Truncate(property.Name)}' (allowed: {string.Join(", ", allowed)})");
            }
        }
    }

    private static bool TryArray(JObject obj, string key, int maxItems, out JArray array, out string error)
    {
        array = new JArray();
        if (obj[key] is not JArray value)
        {
            error = $"'{key}' is required and must be an array";
            return false;
        }
        if (value.Count > maxItems)
        {
            error = $"'{key}' has {value.Count} items; the limit is {maxItems}";
            return false;
        }
        array = value;
        error = string.Empty;
        return true;
    }

    private static bool TryString(JObject obj, string key, int maxLength, bool required, string path, out string? value, out string error)
    {
        value = null;
        error = string.Empty;
        var token = obj[key];
        if (token is null || token.Type == JTokenType.Null)
        {
            if (required)
            {
                error = $"'{path}' is required";
                return false;
            }
            return true;
        }
        if (token.Type != JTokenType.String)
        {
            error = $"'{path}' must be a string";
            return false;
        }
        var raw = (string?)token ?? string.Empty;
        if (raw.Length > maxLength)
        {
            error = $"'{path}' is longer than {maxLength} characters";
            return false;
        }
        // The recorded strings are shown to the user in every plan review; clean them like catalog
        // text so a newline or bidi override cannot reshape the plan's "Why" block.
        var cleaned = CatalogText.Clean(raw, maxLength);
        if (cleaned.Length == 0)
        {
            if (required)
            {
                error = $"'{path}' must not be empty";
                return false;
            }
            return true;
        }
        value = cleaned;
        return true;
    }

    private static bool TrySource(JObject item, string path, out PillarSource source, out string error)
    {
        source = PillarSource.UserStated;
        error = string.Empty;
        var token = item["source"];
        if (token is null || token.Type != JTokenType.String)
        {
            error = $"'{path}.source' is required and must be \"user_stated\" or \"assumed\"";
            return false;
        }
        switch ((string?)token)
        {
            case "user_stated":
                source = PillarSource.UserStated;
                return true;
            case "assumed":
                source = PillarSource.Assumed;
                return true;
            default:
                error = $"'{path}.source' must be \"user_stated\" or \"assumed\"";
                return false;
        }
    }

    private static bool TryDirection(JObject item, string path, out EvalDirection direction, out string error)
    {
        direction = EvalDirection.Keep;
        error = string.Empty;
        var token = item["direction"];
        if (token is null || token.Type != JTokenType.String)
        {
            error = $"'{path}.direction' is required and must be MAXIMIZE, MINIMIZE or KEEP";
            return false;
        }
        switch ((string?)token)
        {
            case "MAXIMIZE":
                direction = EvalDirection.Maximize;
                return true;
            case "MINIMIZE":
                direction = EvalDirection.Minimize;
                return true;
            case "KEEP":
                direction = EvalDirection.Keep;
                return true;
            default:
                error = $"'{path}.direction' must be MAXIMIZE, MINIMIZE or KEEP";
                return false;
        }
    }

    private static bool TryTarget(JObject item, string path, out double? target, out string error)
    {
        target = null;
        error = string.Empty;
        var token = item["target"];
        if (token is null || token.Type == JTokenType.Null)
        {
            return true;
        }
        if (token.Type is not (JTokenType.Integer or JTokenType.Float))
        {
            error = $"'{path}.target' must be a number";
            return false;
        }
        var value = token.Value<double>();
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            error = $"'{path}.target' must be a finite number";
            return false;
        }
        target = value;
        return true;
    }

    private static bool TryComparator(JObject item, string path, double? target, out EvalComparator? comparator, out string error)
    {
        comparator = null;
        error = string.Empty;
        var token = item["comparator"];
        if (token is null || token.Type == JTokenType.Null)
        {
            return true;
        }
        const string allowed = "must be \"<=\", \">=\" or \"==\"";
        if (token.Type != JTokenType.String)
        {
            error = $"'{path}.comparator' {allowed}";
            return false;
        }
        switch ((string?)token)
        {
            case "<=":
                comparator = EvalComparator.AtMost;
                break;
            case ">=":
                comparator = EvalComparator.AtLeast;
                break;
            case "==":
                comparator = EvalComparator.Equal;
                break;
            default:
                error = $"'{path}.comparator' {allowed}";
                return false;
        }
        if (target is null)
        {
            comparator = null;
            error = $"'{path}.comparator' needs a numeric 'target' to compare against";
            return false;
        }
        return true;
    }

    private static string Truncate(string value) => value.Length <= 40 ? value : value[..40];
}
