// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Parti.Core.Sessions;
using Rhino;

namespace Parti.Core.Grasshopper;

/// <summary>
/// Sets the persistent value of a parameter on the active canvas. v0.6 supports
/// <c>GH_NumberSlider</c>, <c>GH_Panel</c>, and the generic <c>Param_Number</c> /
/// <c>Param_Integer</c> / <c>Param_String</c> parameters. Anything else is rejected
/// with <c>status=unsupported</c> so the model learns to pick a supported target.
/// <para>
/// The result always reports what Grasshopper holds after the assignment (read back), never the
/// requested value alone. A number slider rejects a value outside its current range with
/// <c>status=error</c> and the range, unless the call supplies explicit new bounds
/// (<c>min</c> and/or <c>max</c>), in which case the bounds are changed first. <c>min</c>/<c>max</c>
/// are accepted only for number sliders.
/// </para>
/// </summary>
public static class GhSetInput
{
    public static Task<JsonObject> ExecuteAsync(JsonObject parameters) => ExecuteAsync(parameters, locks: null);

    /// <summary>
    /// Sets the value unless the target is a parameter nested in a component (always refused) or,
    /// with <paramref name="locks"/>, a locked component. Both checks run on the UI thread against
    /// the leased document, together with the edit.
    /// </summary>
    public static Task<JsonObject> ExecuteAsync(JsonObject parameters, ComponentLockRegistry? locks)
    {
        var targetKey = parameters["target"]?.GetValue<string>() ?? "";
        var inputSelector = parameters["input"];
        var valueNode = parameters["value"];
        if (!TryReadBound(parameters["min"], "min", out var newMin, out var boundError) ||
            !TryReadBound(parameters["max"], "max", out var newMax, out boundError))
        {
            return Task.FromResult(ErrorResult(boundError));
        }
        var doc = GrasshopperDocumentCapability.ResolveRequired(parameters);
        if (doc is null)
        {
            return Task.FromResult(ErrorResult("Missing or expired pinned Grasshopper document lease."));
        }
        var undo = GrasshopperDocumentCapability.ResolveUndoLog(parameters);

        var tcs = new TaskCompletionSource<JsonObject>();

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                if (string.IsNullOrEmpty(targetKey))
                {
                    tcs.SetResult(ErrorResult("'target' is required."));
                    return;
                }

                if (valueNode is null)
                {
                    tcs.SetResult(ErrorResult("'value' is required."));
                    return;
                }

                var obj = GhTargets.Resolve(doc, targetKey, out var nestedRejection);
                if (nestedRejection is not null)
                {
                    tcs.SetResult(nestedRejection);
                    return;
                }

                if (obj is null)
                {
                    tcs.SetResult(new JsonObject
                    {
                        ["status"] = "not_found",
                        ["message"] = $"No component matches '{targetKey}'.",
                        ["target"] = targetKey,
                    });
                    return;
                }

                if (GhTargets.LockedResult(obj, targetKey, "gh_set_input", locks) is { } locked)
                {
                    tcs.SetResult(locked);
                    return;
                }

                // Sliders and panels treat their own surface as the "input" — no parameter
                // traversal needed. For plain Param_Number/Integer/String, use input[0]
                // or a named selector.
                JsonObject? result;
                if (obj is GH_NumberSlider slider)
                {
                    result = ApplyToSlider(slider, valueNode, newMin, newMax, undo);
                }
                else if (newMin.HasValue || newMax.HasValue)
                {
                    tcs.SetResult(ErrorResult("'min'/'max' apply only to number sliders; nothing was changed."));
                    return;
                }
                else if (obj is GH_Panel panel)
                {
                    result = ApplyToPanel(panel, valueNode, undo);
                }
                else
                {
                    var inputParam = FindInputParam(obj, inputSelector, out var label);
                    if (inputParam is null)
                    {
                        tcs.SetResult(ErrorResult(
                            $"Input '{FormatParamSelector(inputSelector)}' not found on '{targetKey}'."));
                        return;
                    }

                    result = ApplyToParam(inputParam, valueNode, label, undo);
                }

                if (result is null)
                {
                    tcs.SetResult(new JsonObject
                    {
                        ["status"] = "unsupported",
                        ["message"] = "v0.6 only supports sliders, panels, and basic number/string/integer parameters",
                        ["target"] = targetKey,
                    });
                    return;
                }

                if (result["status"]?.GetValue<string>() == "success")
                {
                    doc.NewSolution(false);
                }

                result["target"] = targetKey;
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetResult(ErrorResult(ex.Message));
            }
        });

        return tcs.Task;
    }

    private static JsonObject? ApplyToSlider(GH_NumberSlider slider, JsonNode valueNode, double? newMin, double? newMax, TurnUndoLog? undo)
    {
        if (!TryReadDouble(valueNode, out var number))
        {
            return null;
        }

        var currentMin = slider.Slider.Minimum;
        var currentMax = slider.Slider.Maximum;
        if (!TryPlanSliderAssignment(number, currentMin, currentMax, newMin, newMax,
                out var min, out var max, out var error))
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["message"] = error,
                ["range"] = new JsonObject { ["min"] = (double)currentMin, ["max"] = (double)currentMax },
            };
        }

        // Captured before any write: undo restores the slider's range and value together.
        GhTurnUndo.BeforeEdit(undo, slider);
        if (min != currentMin || max != currentMax)
        {
            // Order the writes so the domain is never inverted in between.
            if (min > currentMax)
            {
                slider.Slider.Maximum = max;
                slider.Slider.Minimum = min;
            }
            else
            {
                slider.Slider.Minimum = min;
                slider.Slider.Maximum = max;
            }
        }
        slider.SetSliderValue((decimal)number);
        slider.ExpireSolution(false);

        // Report what Grasshopper holds (sliders round to their precision), not what was asked for.
        var held = (double)slider.CurrentValue;
        var result = new JsonObject
        {
            ["status"] = "success",
            ["input"] = "value",
            ["value"] = held,
            ["requested"] = number,
            ["range"] = new JsonObject { ["min"] = (double)slider.Slider.Minimum, ["max"] = (double)slider.Slider.Maximum },
        };
        if (held != number)
        {
            result["adjusted"] = true;
            result["message"] = $"Grasshopper holds {held.ToString(System.Globalization.CultureInfo.InvariantCulture)} (slider precision), not the requested value.";
        }
        return result;
    }

    /// <summary>
    /// Decides the bounds a slider assignment uses. Without explicit bounds the value must lie in
    /// the current range. With <paramref name="newMin"/> and/or <paramref name="newMax"/>, the
    /// missing side keeps its current bound, the new domain must be non-empty (min &lt; max), and
    /// the value must lie inside it. Non-finite numbers are rejected.
    /// </summary>
    internal static bool TryPlanSliderAssignment(
        double requested,
        decimal currentMin,
        decimal currentMax,
        double? newMin,
        double? newMax,
        out decimal min,
        out decimal max,
        out string error)
    {
        min = currentMin;
        max = currentMax;
        error = string.Empty;
        if (!IsFinite(requested) || (newMin is double a && !IsFinite(a)) || (newMax is double b && !IsFinite(b)))
        {
            error = "Slider values and bounds must be finite numbers; nothing was changed.";
            return false;
        }

        decimal value;
        try
        {
            value = (decimal)requested;
            if (newMin.HasValue) min = (decimal)newMin.Value;
            if (newMax.HasValue) max = (decimal)newMax.Value;
        }
        catch (OverflowException)
        {
            error = "Slider value or bounds are too large; nothing was changed.";
            return false;
        }

        if (min >= max)
        {
            error = $"Slider bounds must satisfy min < max (got [{Format(min)}, {Format(max)}]); nothing was changed.";
            return false;
        }
        if (value < min || value > max)
        {
            error = newMin.HasValue || newMax.HasValue
                ? $"Value {Format(value)} is outside the requested slider range [{Format(min)}, {Format(max)}]; nothing was changed."
                : $"Value {Format(value)} is outside the slider range [{Format(min)}, {Format(max)}]; nothing was changed. Pass explicit 'min'/'max' to change the range.";
            return false;
        }
        return true;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static string Format(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static bool TryReadBound(JsonNode? node, string name, out double? bound, out string error)
    {
        bound = null;
        error = string.Empty;
        if (node is null)
        {
            return true;
        }
        if (node is JsonValue v && (v.TryGetValue<double>(out var d) || TryGetInt(v, out d)))
        {
            bound = d;
            return true;
        }
        error = $"'{name}' must be a number.";
        return false;
    }

    private static bool TryGetInt(JsonValue value, out double number)
    {
        if (value.TryGetValue<int>(out var i))
        {
            number = i;
            return true;
        }
        number = 0;
        return false;
    }

    private static JsonObject ApplyToPanel(GH_Panel panel, JsonNode valueNode, TurnUndoLog? undo)
    {
        string text;
        if (valueNode is JsonValue v && v.TryGetValue<string>(out var s) && s is not null)
        {
            text = s;
        }
        else
        {
            text = valueNode.ToJsonString();
        }

        GhTurnUndo.BeforeEdit(undo, panel);
        panel.UserText = text;
        panel.ExpireSolution(false);

        return new JsonObject
        {
            ["status"] = "success",
            ["input"] = "text",
            ["value"] = panel.UserText,
        };
    }

    private static JsonObject? ApplyToParam(IGH_Param param, JsonNode valueNode, string label, TurnUndoLog? undo)
    {
        switch (param)
        {
            case Param_Number number:
            {
                if (!TryReadDouble(valueNode, out var d)) return null;
                GhTurnUndo.BeforePersistentData(undo, number);
                number.PersistentData.Clear();
                number.PersistentData.Append(new GH_Number(d));
                number.ExpireSolution(false);
                return SuccessEcho(label, number.PersistentData.AllData(true).OfType<GH_Number>().FirstOrDefault()?.Value ?? d);
            }
            case Param_Integer integer:
            {
                if (!TryReadDouble(valueNode, out var d)) return null;
                var i = (int)Math.Round(d);
                GhTurnUndo.BeforePersistentData(undo, integer);
                integer.PersistentData.Clear();
                integer.PersistentData.Append(new GH_Integer(i));
                integer.ExpireSolution(false);
                var heldInt = integer.PersistentData.AllData(true).OfType<GH_Integer>().FirstOrDefault()?.Value ?? i;
                var echo = SuccessEcho(label, heldInt);
                if (heldInt != d)
                {
                    echo["requested"] = d;
                    echo["adjusted"] = true;
                }
                return echo;
            }
            case Param_String str:
            {
                string text;
                if (valueNode is JsonValue sv && sv.TryGetValue<string>(out var ss) && ss is not null)
                {
                    text = ss;
                }
                else
                {
                    text = valueNode.ToJsonString();
                }
                GhTurnUndo.BeforePersistentData(undo, str);
                str.PersistentData.Clear();
                str.PersistentData.Append(new GH_String(text));
                str.ExpireSolution(false);
                return SuccessEcho(label, str.PersistentData.AllData(true).OfType<GH_String>().FirstOrDefault()?.Value ?? text);
            }
            default:
                return null;
        }
    }

    private static JsonObject SuccessEcho(string label, double value) => new()
    {
        ["status"] = "success",
        ["input"] = label,
        ["value"] = value,
    };

    private static JsonObject SuccessEcho(string label, int value) => new()
    {
        ["status"] = "success",
        ["input"] = label,
        ["value"] = value,
    };

    private static JsonObject SuccessEcho(string label, string value) => new()
    {
        ["status"] = "success",
        ["input"] = label,
        ["value"] = value,
    };

    private static bool TryReadDouble(JsonNode node, out double value)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out value))
            {
                return true;
            }
            if (v.TryGetValue<int>(out var asInt))
            {
                value = asInt;
                return true;
            }
            if (v.TryGetValue<bool>(out var asBool))
            {
                value = asBool ? 1 : 0;
                return true;
            }
            if (v.TryGetValue<string>(out var asString) && asString is not null
                && double.TryParse(asString, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static IGH_Param? FindInputParam(IGH_DocumentObject obj, JsonNode? selector, out string label)
    {
        label = "0";
        if (obj is IGH_Component comp)
        {
            var list = comp.Params.Input;
            int index = 0;
            if (selector is JsonValue value)
            {
                if (value.TryGetValue<int>(out var asInt))
                {
                    index = asInt;
                }
                else if (value.TryGetValue<double>(out var asDouble))
                {
                    index = (int)asDouble;
                }
                else if (value.TryGetValue<string>(out var name) && name is not null)
                {
                    if (int.TryParse(name, out var parsed))
                    {
                        index = parsed;
                    }
                    else
                    {
                        var byName = list.FirstOrDefault(p =>
                            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.NickName, name, StringComparison.OrdinalIgnoreCase));
                        if (byName is not null)
                        {
                            label = byName.Name ?? name;
                            return byName;
                        }
                        label = name;
                        return null;
                    }
                }
            }
            if (index < 0 || index >= list.Count)
            {
                label = index.ToString();
                return null;
            }
            var found = list[index];
            label = found.Name ?? index.ToString();
            return found;
        }

        if (obj is IGH_Param standalone)
        {
            label = standalone.Name ?? "0";
            return standalone;
        }

        return null;
    }

    private static string FormatParamSelector(JsonNode? node)
    {
        if (node is null)
        {
            return "0";
        }
        return node.ToJsonString().Trim('"');
    }

    private static JsonObject ErrorResult(string message) => new()
    {
        ["status"] = "error",
        ["message"] = message,
    };
}
