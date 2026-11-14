// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Tools;

namespace Parti.Mcp;

/// <summary>
/// Standalone entry point for the Parti MCP companion server. Mirrors parti-cli's --live
/// pattern: default mode uses a stub dispatcher so the server can be exercised from Claude
/// Desktop / mcp-inspector without a running Rhino. Pass --dispatcher live to opt into the
/// real Grasshopper adapters (fails fast here — Parti.Mcp has no Rhino dependency in v0.5).
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = new PartiMcpOptions();
        var dispatcherKind = "stub";
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port":
                    if (++i >= args.Length || !int.TryParse(args[i], out var port))
                    {
                        Console.Error.WriteLine("error: --port requires an integer");
                        return 1;
                    }
                    options.Port = port;
                    break;
                case "--dispatcher":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine("error: --dispatcher requires 'stub' or 'live'");
                        return 1;
                    }
                    dispatcherKind = args[i];
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"error: unknown argument '{args[i]}'");
                    PrintUsage();
                    return 1;
            }
        }

        IToolDispatcher dispatcher;
        if (string.Equals(dispatcherKind, "live", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("error: --dispatcher live requires Rhino/Grasshopper and is unavailable in the standalone companion. Use the Parti plugin's in-process MCP server for a live canvas.");
            return 1;
        }

        dispatcher = BuildStubDispatcher();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        await using var server = new PartiMcpServer(dispatcher, options);
        try
        {
            await server.RunAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown on Ctrl+C
        }

        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("usage: parti-mcp [--port <n>] [--dispatcher stub|live]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  --port        Reserved for HTTP transport (v0.6+). Ignored for stdio.");
        Console.Error.WriteLine("  --dispatcher  'stub' (default) returns canned success payloads.");
        Console.Error.WriteLine("                'live' requires the in-process Rhino plugin; not available here.");
    }

    /// <summary>
    /// Minimal stub dispatcher. Same four tool names as <c>GrasshopperToolAdapters</c> but returns
    /// {"status":"success","stub":true,"tool":name} for every call. Kept in-file — lives outside
    /// Parti.Core.Grasshopper so this assembly has no Rhino dependency.
    /// </summary>
    private static IToolDispatcher BuildStubDispatcher()
    {
        var registry = new ToolRegistry();

        (string Name, string Description, string Schema)[] defs =
        {
            ("gh_add_component", "Add a Grasshopper component to the active canvas by its exact component GUID.",
                "{\"type\":\"object\",\"properties\":{\"componentGuid\":{\"type\":\"string\"},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"}},\"required\":[\"componentGuid\"]}"),
            ("gh_get_all", "Return a compact snapshot of every component on the active Grasshopper canvas.",
                "{\"type\":\"object\",\"properties\":{}}"),
            ("gh_get_selected", "Return a compact snapshot of only the selected components on the active canvas.",
                "{\"type\":\"object\",\"properties\":{}}"),
            ("gh_describe", "Return a detailed description of a single component identified by its stable instance GUID.",
                "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"}},\"required\":[\"target\"]}"),
        };

        foreach (var (name, description, schemaJson) in defs)
        {
            var definition = new AIToolDefinition(name, description, JObject.Parse(schemaJson));
            registry.Register(definition, (args, _) => Task.FromResult(new JObject
            {
                ["status"] = "success",
                ["stub"] = true,
                ["tool"] = name,
                ["echo"] = args.DeepClone(),
            }));
        }

        return registry;
    }
}
