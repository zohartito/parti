// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Rhino;

namespace Parti.Core.Grasshopper;

public static class GhAddComponent
{
    public static Task<JsonObject> ExecuteAsync(JsonObject parameters)
    {
        var type = parameters["type"]?.GetValue<string>() ?? "";
        var x = parameters["x"]?.GetValue<double>() ?? 0.0;
        var y = parameters["y"]?.GetValue<double>() ?? 0.0;
        var nickname = parameters["nickname"]?.GetValue<string?>();

        if (!Guid.TryParse(type, out var componentGuid))
        {
            return Task.FromResult(ErrorResult("'type' must be an exact Grasshopper component GUID."));
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
                var proxy = Instances.ComponentServer.ObjectProxies
                    .FirstOrDefault(p => p.Guid == componentGuid);
                if (proxy is null)
                {
                    tcs.SetResult(ErrorResult($"Unknown component type GUID {componentGuid}"));
                    return;
                }

                var obj = proxy.CreateInstance();
                if (obj is null)
                {
                    tcs.SetResult(ErrorResult($"CreateInstance returned null for {proxy.Desc.Name}"));
                    return;
                }

                obj.CreateAttributes();
                if (!string.IsNullOrEmpty(nickname))
                {
                    obj.NickName = nickname!;
                }
                obj.Attributes.Pivot = new PointF((float)x, (float)y);

                ConfigurePrimitive(obj);

                doc.AddObject(obj, false);
                GhTurnUndo.Added(undo, obj);
                obj.ExpireSolution(false);
                doc.NewSolution(false);

                tcs.SetResult(new JsonObject
                {
                    ["status"] = "success",
                    ["instanceGuid"] = obj.InstanceGuid.ToString(),
                    ["componentGuid"] = proxy.Guid.ToString(),
                    ["canonicalName"] = proxy.Desc.Name,
                    ["nickname"] = obj.NickName,
                    ["pivot"] = new JsonObject
                    {
                        ["x"] = obj.Attributes.Pivot.X,
                        ["y"] = obj.Attributes.Pivot.Y,
                    },
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(ErrorResult(ex.Message));
            }
        });

        return tcs.Task;
    }

    private static void ConfigurePrimitive(IGH_DocumentObject obj)
    {
        if (obj is GH_NumberSlider slider)
        {
            slider.SetInitCode("0.0 < 0.5 < 1.0");
        }
    }

    private static JsonObject ErrorResult(string message) => new()
    {
        ["status"] = "error",
        ["message"] = message,
    };

}
