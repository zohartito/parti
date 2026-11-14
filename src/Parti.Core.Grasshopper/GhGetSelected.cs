// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Rhino;

namespace Parti.Core.Grasshopper;

public static class GhGetSelected
{
    public static Task<JsonObject> ExecuteAsync(JsonObject parameters)
    {
        var doc = GrasshopperDocumentCapability.ResolveRequired(parameters);
        if (doc is null)
        {
            return Task.FromResult(CanvasSnapshot.Build(doc: null, selectedOnly: true));
        }
        var tcs = new TaskCompletionSource<JsonObject>();
        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                tcs.SetResult(CanvasSnapshot.Build(doc, selectedOnly: true));
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
}
