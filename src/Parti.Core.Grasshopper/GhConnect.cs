// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Parti.Core.Sessions;
using Rhino;

namespace Parti.Core.Grasshopper;

/// <summary>
/// Connects an output parameter of one component to an input parameter of another on the
/// pinned Grasshopper document. Targets are resolved only by stable instance GUID. Parameters may
/// be selected by zero-based index or by name.
/// </summary>
public static class GhConnect
{
    public static Task<JsonObject> ExecuteAsync(JsonObject parameters) => ExecuteAsync(parameters, locks: null);

    /// <summary>
    /// Wires the two objects unless either names a parameter nested in a component (always
    /// refused) or, with <paramref name="locks"/>, a locked component. Both checks run on the UI
    /// thread against the leased document, together with the edit.
    /// </summary>
    public static Task<JsonObject> ExecuteAsync(JsonObject parameters, ComponentLockRegistry? locks)
    {
        var fromTarget = parameters["from"]?.GetValue<string>() ?? "";
        var toTarget = parameters["to"]?.GetValue<string>() ?? "";
        var fromOutputNode = parameters["fromOutput"];
        var toInputNode = parameters["toInput"];
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
                if (string.IsNullOrEmpty(fromTarget) || string.IsNullOrEmpty(toTarget))
                {
                    tcs.SetResult(ErrorResult("Both 'from' and 'to' are required."));
                    return;
                }

                var source = GhTargets.Resolve(doc, fromTarget, out var sourceRejection);
                if (sourceRejection is not null)
                {
                    tcs.SetResult(sourceRejection);
                    return;
                }

                if (source is null)
                {
                    tcs.SetResult(NotFoundResult(fromTarget, "source"));
                    return;
                }

                var target = GhTargets.Resolve(doc, toTarget, out var targetRejection);
                if (targetRejection is not null)
                {
                    tcs.SetResult(targetRejection);
                    return;
                }

                if (target is null)
                {
                    tcs.SetResult(NotFoundResult(toTarget, "target"));
                    return;
                }

                if (GhTargets.LockedResult(source, fromTarget, "gh_connect", locks) is { } lockedSource)
                {
                    tcs.SetResult(lockedSource);
                    return;
                }

                if (GhTargets.LockedResult(target, toTarget, "gh_connect", locks) is { } lockedTarget)
                {
                    tcs.SetResult(lockedTarget);
                    return;
                }

                var sourceOutput = ResolveOutputParam(source, fromOutputNode, out var sourceLabel);
                if (sourceOutput is null)
                {
                    tcs.SetResult(ErrorResult(
                        $"Output '{FormatParamSelector(fromOutputNode)}' not found on '{fromTarget}'."));
                    return;
                }

                var targetInput = ResolveInputParam(target, toInputNode, out var targetLabel);
                if (targetInput is null)
                {
                    tcs.SetResult(ErrorResult(
                        $"Input '{FormatParamSelector(toInputNode)}' not found on '{toTarget}'."));
                    return;
                }

                // Tree access / type mismatches are handled silently by Grasshopper at solve time,
                // so we don't pre-validate: just wire it up and let NewSolution report runtime errors.
                GhTurnUndo.BeforeWire(undo, targetInput);
                targetInput.AddSource(sourceOutput);
                doc.NewSolution(false);

                tcs.SetResult(new JsonObject
                {
                    ["status"] = "success",
                    ["from"] = fromTarget,
                    ["to"] = toTarget,
                    ["fromOutput"] = sourceLabel,
                    ["toInput"] = targetLabel,
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(ErrorResult(ex.Message));
            }
        });

        return tcs.Task;
    }

    private static IGH_Param? ResolveOutputParam(IGH_DocumentObject obj, JsonNode? selector, out string label)
    {
        if (obj is IGH_Component comp)
        {
            return ResolveParamInList(comp.Params.Output, selector, fallbackIndex: 0, out label);
        }

        // Standalone param (slider, panel, etc.) — the object itself is the output.
        if (obj is IGH_Param standalone)
        {
            label = standalone.Name ?? "0";
            return standalone;
        }

        label = "";
        return null;
    }

    private static IGH_Param? ResolveInputParam(IGH_DocumentObject obj, JsonNode? selector, out string label)
    {
        if (obj is IGH_Component comp)
        {
            return ResolveParamInList(comp.Params.Input, selector, fallbackIndex: 0, out label);
        }

        // Standalone param can act as an input sink too (rare but legal).
        if (obj is IGH_Param standalone)
        {
            label = standalone.Name ?? "0";
            return standalone;
        }

        label = "";
        return null;
    }

    private static IGH_Param? ResolveParamInList(
        System.Collections.Generic.IList<IGH_Param> list,
        JsonNode? selector,
        int fallbackIndex,
        out string label)
    {
        int index = fallbackIndex;

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
                if (int.TryParse(name, out var parsedIndex))
                {
                    index = parsedIndex;
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

    private static JsonObject NotFoundResult(string target, string role) => new()
    {
        ["status"] = "error",
        ["message"] = $"No component matches '{target}' ({role}).",
        ["target"] = target,
    };
}
