// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Rhino;

namespace Parti.Core.Grasshopper;

public static class GhDescribe
{
    public static Task<JsonObject> ExecuteAsync(JsonObject parameters)
    {
        var target = parameters["target"]?.GetValue<string>() ?? "";
        var doc = GrasshopperDocumentCapability.ResolveRequired(parameters);
        if (doc is null)
        {
            return Task.FromResult(new JsonObject
            {
                ["status"] = "error",
                ["message"] = "Missing or expired pinned Grasshopper document lease.",
            });
        }
        var tcs = new TaskCompletionSource<JsonObject>();

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                IGH_DocumentObject? obj = Guid.TryParse(target, out var instanceGuid)
                    ? doc.FindObject(instanceGuid, false)
                    : null;

                if (obj is null)
                {
                    tcs.SetResult(new JsonObject
                    {
                        ["status"] = "not_found",
                        ["message"] = $"No component matches '{target}'",
                        ["target"] = target,
                    });
                    return;
                }

                tcs.SetResult(BuildDescription(obj));
            }
            catch (Exception ex)
            {
                tcs.SetResult(new JsonObject
                {
                    ["status"] = "error",
                    ["message"] = ex.Message,
                });
            }
        });

        return tcs.Task;
    }

    private static JsonObject BuildDescription(IGH_DocumentObject obj)
    {
        var result = new JsonObject
        {
            ["status"] = "success",
            ["instanceGuid"] = obj.InstanceGuid.ToString(),
            ["componentGuid"] = obj.ComponentGuid.ToString(),
            ["name"] = obj.Name ?? "",
            ["nickname"] = obj.NickName ?? "",
            ["description"] = obj.Description ?? "",
            ["category"] = obj.Category ?? "",
            ["subcategory"] = obj.SubCategory ?? "",
            ["pivot"] = new JsonObject
            {
                ["x"] = obj.Attributes?.Pivot.X ?? 0f,
                ["y"] = obj.Attributes?.Pivot.Y ?? 0f,
            },
        };

        if (obj is GH_NumberSlider slider)
        {
            result["value"] = new JsonObject
            {
                ["current"] = (double)slider.CurrentValue,
                ["min"] = (double)slider.Slider.Minimum,
                ["max"] = (double)slider.Slider.Maximum,
                ["decimalPlaces"] = slider.Slider.DecimalPlaces,
            };
        }
        else if (obj is GH_Panel panel)
        {
            result["value"] = new JsonObject
            {
                ["text"] = panel.UserText ?? "",
            };
        }
        else if (obj is GH_BooleanToggle toggle)
        {
            result["value"] = toggle.Value;
        }

        if (obj is IGH_ActiveObject active)
        {
            var errors = new JsonArray();
            var warnings = new JsonArray();
            var remarks = new JsonArray();
            foreach (var msg in active.RuntimeMessages(GH_RuntimeMessageLevel.Error))
                errors.Add(msg);
            foreach (var msg in active.RuntimeMessages(GH_RuntimeMessageLevel.Warning))
                warnings.Add(msg);
            foreach (var msg in active.RuntimeMessages(GH_RuntimeMessageLevel.Remark))
                remarks.Add(msg);
            result["messages"] = new JsonObject
            {
                ["errors"] = errors,
                ["warnings"] = warnings,
                ["remarks"] = remarks,
            };
        }

        return result;
    }
}
