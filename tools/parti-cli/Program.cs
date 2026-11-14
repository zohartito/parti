// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Grasshopper.Tools;
using Parti.Core.Sessions;
using Parti.Core.Tools;
using Parti.Providers.Anthropic;

namespace Parti.Cli;

/// <summary>
/// Minimal CLI harness for Parti v0.4: wires <see cref="AnthropicProvider"/>,
/// <see cref="ConversationSession"/>, and an <see cref="IToolDispatcher"/> from a single prompt.
/// Default mode uses a stub dispatcher. The standalone CLI deliberately refuses <c>--live</c>:
/// it cannot own the reviewed document lease, consent UI, and undo lifecycle required for a real
/// Grasshopper mutation turn.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 1;
    private const int ExitMaxTurns = 2;
    private const int ExitApiError = 3;

    private static async Task<int> Main(string[] args)
    {
        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {TerminalTextSanitizer.Sanitize(ex.Message)}");
            Console.Error.WriteLine(CliOptions.Usage);
            return ExitUsage;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(CliOptions.Usage);
            return ExitOk;
        }

        if (string.IsNullOrWhiteSpace(options.Prompt))
        {
            Console.Error.WriteLine("error: --prompt is required");
            Console.Error.WriteLine(CliOptions.Usage);
            return ExitUsage;
        }

        if (options.Live)
        {
            Console.Error.WriteLine("error: --live is disabled in parti-cli because standalone execution cannot pin a reviewed Grasshopper document lease.");
            Console.Error.WriteLine("Use the in-Rhino Parti chat controller for live canvas mutations.");
            return ExitUsage;
        }

        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("error: ANTHROPIC_API_KEY is not set. Export it in your shell and try again.");
            return ExitUsage;
        }

        string? systemPrompt = null;
        if (!string.IsNullOrWhiteSpace(options.System))
        {
            systemPrompt = ResolveSystem(options.System!);
        }

        var provider = new AnthropicProvider();
        IToolDispatcher dispatcher = BuildStubDispatcher();

        var sessionOptions = new SessionOptions
        {
            Model = options.Model,
            ApiKey = apiKey!,
            SystemPrompt = systemPrompt,
        };

        IConversationObserver? observer = options.Verbose ? new VerboseObserver() : null;
        var session = new ConversationSession(provider, dispatcher, sessionOptions, observer);

        AIReturn result;
        try
        {
            result = await session.RunAsync(options.Prompt!, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: unhandled exception: {TerminalTextSanitizer.Sanitize(ex.Message)}");
            return ExitApiError;
        }

        PrintFinalSummary(result);

        if (result.Success && options.Interactive)
        {
            while (true)
            {
                Console.Write("\n> ");
                var line = Console.ReadLine();
                if (line is null || string.Equals(line.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                try
                {
                    result = await session.ContinueAsync(line, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"error: unhandled exception: {TerminalTextSanitizer.Sanitize(ex.Message)}");
                    return ExitApiError;
                }
                PrintFinalSummary(result);
                if (!result.Success)
                {
                    break;
                }
            }
        }

        if (!result.Success)
        {
            if (result.ErrorMessage is not null &&
                result.ErrorMessage.Contains("Max turns", StringComparison.Ordinal))
            {
                return ExitMaxTurns;
            }

            return ExitApiError;
        }

        return ExitOk;
    }

    private static string ResolveSystem(string systemArg)
    {
        // Treat the value as a file path first; fall back to inline text.
        try
        {
            if (File.Exists(systemArg))
            {
                return File.ReadAllText(systemArg);
            }
        }
        catch (IOException)
        {
            // fall through: treat as inline
        }

        return systemArg;
    }

    private static void PrintFinalSummary(AIReturn result)
    {
        Console.WriteLine();
        Console.WriteLine("=== final ===");
        Console.WriteLine($"status      : {result.Status}");
        Console.WriteLine($"stop_reason : {TerminalTextSanitizer.Sanitize(result.StopReason ?? "(none)")}");
        Console.WriteLine($"tokens_in   : {result.InputTokens}");
        Console.WriteLine($"tokens_out  : {result.OutputTokens}");

        // A cap stop is an error that still carries the model's closing summary, so text prints first.
        var finalTexts = result.Interactions
            .OfType<AIInteractionText>()
            .Where(t => t.Agent == AIAgent.Assistant && !string.IsNullOrEmpty(t.Content))
            .Select(t => t.Content!)
            .ToList();

        if (finalTexts.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(TerminalTextSanitizer.Sanitize(string.Join("\n\n", finalTexts)));
        }

        if (!result.Success && !string.IsNullOrEmpty(result.ErrorMessage))
        {
            Console.Error.WriteLine($"error       : {TerminalTextSanitizer.Sanitize(result.ErrorMessage)}");
        }
    }

    /// <summary>
    /// Builds a stub dispatcher exposing the same four Grasshopper tool definitions as
    /// <see cref="GrasshopperToolAdapters.CreateDefault"/>, but returning plausible success payloads
    /// instead of calling into <c>Instances.ActiveCanvas</c> (which is null outside Rhino).
    /// </summary>
    private static ToolRegistry BuildStubDispatcher()
    {
        var registry = new ToolRegistry();

        var addDef = GrasshopperToolAdapters.GhAddComponentAdapter().Definition;
        registry.Register(addDef, (args, _) =>
        {
            var name = args["name"]?.ToString() ?? "component";
            var x = args["x"]?.ToObject<double>() ?? 0.0;
            var y = args["y"]?.ToObject<double>() ?? 0.0;
            var result = new JObject
            {
                ["status"] = "success",
                ["stub"] = true,
                ["simple_id"] = $"stub_{Guid.NewGuid().ToString("N")[..8]}",
                ["name"] = name,
                ["x"] = x,
                ["y"] = y,
            };
            return Task.FromResult(result);
        });

        var getAllDef = GrasshopperToolAdapters.GhGetAllAdapter().Definition;
        registry.Register(getAllDef, (_, _) =>
        {
            var result = new JObject
            {
                ["status"] = "success",
                ["stub"] = true,
                ["count"] = 0,
                ["components"] = new JArray(),
            };
            return Task.FromResult(result);
        });

        var getSelectedDef = GrasshopperToolAdapters.GhGetSelectedAdapter().Definition;
        registry.Register(getSelectedDef, (_, _) =>
        {
            var result = new JObject
            {
                ["status"] = "success",
                ["stub"] = true,
                ["count"] = 0,
                ["components"] = new JArray(),
            };
            return Task.FromResult(result);
        });

        var describeDef = GrasshopperToolAdapters.GhDescribeAdapter().Definition;
        registry.Register(describeDef, (args, _) =>
        {
            var target = args["target"]?.ToString() ?? "(unspecified)";
            var result = new JObject
            {
                ["status"] = "error",
                ["stub"] = true,
                ["message"] = $"No component matched '{target}' (stub dispatcher — no live canvas).",
            };
            return Task.FromResult(result);
        });

        return registry;
    }

    /// <summary>Pretty-prints every lifecycle event when --verbose is set.</summary>
    private sealed class VerboseObserver : IConversationObserver
    {
        private int _turnNumber;

        public void OnStart(AIRequestCall request)
        {
            Console.WriteLine($"[session] model={request.Model} tools={request.Tools.Count} system_prompt={(string.IsNullOrEmpty(request.SystemPrompt) ? "(none)" : "set")}");
        }

        public void OnTurnStart(string turnId, int turnNumber)
        {
            _turnNumber = turnNumber;
            Console.WriteLine($"[turn {turnNumber}] start (turnId={turnId[..8]})");
        }

        public void OnInteractionCompleted(IAIInteraction interaction)
        {
            switch (interaction)
            {
                case AIInteractionText text when text.Agent == AIAgent.User:
                    Console.WriteLine($"[turn {_turnNumber}] user: {Truncate(text.Content, 120)}");
                    break;
                case AIInteractionText text when text.Agent == AIAgent.Assistant:
                    Console.WriteLine($"[turn {_turnNumber}] assistant: {TerminalTextSanitizer.Sanitize(Truncate(text.Content, 200))}");
                    break;
                case AIInteractionToolResult result:
                    var payload = result.Result?.ToString(Formatting.None) ?? string.Empty;
                    Console.WriteLine($"[turn {_turnNumber}] tool_result {TerminalTextSanitizer.Sanitize(result.Name)} bytes={payload.Length}");
                    break;
                case AIInteractionToolCall call when call is not AIInteractionToolResult:
                    var args = call.Arguments?.ToString(Formatting.None) ?? string.Empty;
                    Console.WriteLine($"[turn {_turnNumber}] tool_use {TerminalTextSanitizer.Sanitize(call.Name)} args={TerminalTextSanitizer.Sanitize(Truncate(args, 160))}");
                    break;
            }
        }

        public void OnToolCall(AIInteractionToolCall toolCall)
        {
            // Already printed in OnInteractionCompleted. No-op.
        }

        public void OnToolResult(AIInteractionToolResult toolResult)
        {
            // Already printed in OnInteractionCompleted. No-op.
        }

        public void OnFinal(AIReturn finalResult)
        {
            Console.WriteLine($"[session] final stop_reason={TerminalTextSanitizer.Sanitize(finalResult.StopReason ?? "(none)")} status={finalResult.Status} in={finalResult.InputTokens} out={finalResult.OutputTokens}");
        }

        public void OnError(Exception error)
        {
            Console.Error.WriteLine($"[session] error: {TerminalTextSanitizer.Sanitize(error.Message)}");
        }

        private static string Truncate(string? s, int max)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }
            return s.Length <= max ? s : s[..max] + "...";
        }
    }

    /// <summary>Parsed command-line options. Minimal flag parser — no external dependencies.</summary>
    private sealed class CliOptions
    {
        public string? Prompt { get; set; }
        public string Model { get; set; } = "claude-opus-4-7";
        public string? System { get; set; }
        public bool Verbose { get; set; }
        public bool Live { get; set; }
        public bool Interactive { get; set; }
        public bool ShowHelp { get; set; }

        public static string Usage =>
            "usage: parti-cli --prompt \"<text>\" [--model <id>] [--system <path|inline>] [--verbose] [--live] [--interactive]\n" +
            "\n" +
            "  --prompt       Required. The user prompt to send.\n" +
            "  --model        Optional. Model id (default: claude-opus-4-7).\n" +
            "  --system       Optional. Path to a system prompt file, or inline text.\n" +
            "  --verbose      Optional. Print per-turn lifecycle events.\n" +
            "  --live         Disabled fail-closed; use the in-Rhino Parti chat controller for live canvas work.\n" +
            "  --interactive  Optional. After the first reply, read follow-ups from stdin until EOF or \"exit\".\n" +
            "\n" +
            "  ANTHROPIC_API_KEY must be set in the environment.";

        public static CliOptions Parse(string[] args)
        {
            var options = new CliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--prompt":
                        options.Prompt = RequireValue(args, ref i, "--prompt");
                        break;
                    case "--model":
                        options.Model = RequireValue(args, ref i, "--model");
                        break;
                    case "--system":
                        options.System = RequireValue(args, ref i, "--system");
                        break;
                    case "--verbose":
                        options.Verbose = true;
                        break;
                    case "--live":
                        options.Live = true;
                        break;
                    case "--interactive":
                        options.Interactive = true;
                        break;
                    case "-h":
                    case "--help":
                        options.ShowHelp = true;
                        break;
                    default:
                        throw new ArgumentException($"unknown argument: {arg}");
                }
            }
            return options;
        }

        private static string RequireValue(string[] args, ref int index, string flag)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{flag} requires a value");
            }
            index++;
            return args[index];
        }
    }
}
