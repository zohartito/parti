// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Parti.Providers.Cli;

/// <summary>
/// Codex (<c>codex exec</c>) with no tools of its own: read-only sandbox, user config and rules
/// ignored, no saved session, web search off, every tool feature disabled, Parti's system prompt as
/// the model instructions file, and the reply schema enforced with <c>--output-schema</c>. The
/// transcript arrives on standard input (<c>-</c>), the reply is read from the <c>-o</c> file, and
/// token usage from the <c>--json</c> event stream.
/// </summary>
public sealed class CodexCliBackend : ICliBackend
{
    /// <summary>Config override that makes Codex accept only a ChatGPT (subscription) login.</summary>
    public const string ForceSubscriptionLogin = "forced_login_method=\"chatgpt\"";

    /// <summary>Codex features switched off with <c>--disable</c>, in argv order.</summary>
    public static IReadOnlyList<string> DisabledFeatures { get; } = new[]
    {
        "shell_tool", "unified_exec", "code_mode_host", "apps", "browser_use", "browser_use_external",
        "computer_use", "image_generation", "multi_agent", "multi_agent_v2", "view_image", "plugins",
        "memories", "hooks", "goals", "sleep_tool", "skill_search", "tool_suggest", "shell_snapshot",
        "in_app_browser", "remote_plugin", "workspace_dependencies",
    };

    /// <inheritdoc />
    public CliBackendKind Kind => CliBackendKind.Codex;

    /// <inheritdoc />
    public string DisplayName => "codex CLI";

    /// <inheritdoc />
    public IReadOnlyList<string> BuildArguments(CliInvocation invocation)
    {
        if (invocation is null)
        {
            throw new ArgumentNullException(nameof(invocation));
        }

        var arguments = new List<string>
        {
            "exec",
            "-m", invocation.Model,
            "-c", $"model_reasoning_effort=\"{invocation.Effort}\"",
            // Subscription only: refuse a cached API-key login (which would bill the Platform account).
            "-c", ForceSubscriptionLogin,
            "-s", "read-only",
            "--ignore-user-config",
            "--ignore-rules",
            "--ephemeral",
            "--skip-git-repo-check",
            "--color", "never",
            "-c", "web_search=\"disabled\"",
        };
        foreach (var feature in DisabledFeatures)
        {
            arguments.Add("--disable");
            arguments.Add(feature);
        }
        arguments.Add("-c");
        arguments.Add($"model_instructions_file={TomlString(invocation.SystemPromptPath)}");
        arguments.Add("--output-schema");
        arguments.Add(invocation.SchemaPath);
        arguments.Add("-o");
        arguments.Add(invocation.LastMessagePath);
        arguments.Add("--json");
        arguments.Add("-");
        return arguments;
    }

    /// <inheritdoc />
    public CliBackendResult ParseResult(CliProcessResult process, CliInvocation invocation)
    {
        if (process is null)
        {
            throw new ArgumentNullException(nameof(process));
        }
        if (invocation is null)
        {
            throw new ArgumentNullException(nameof(invocation));
        }

        var input = 0;
        var output = 0;
        string? failure = null;
        string? lastError = null;
        foreach (var raw in process.StandardOutput.Split('\n'))
        {
            if (CliReplyContract.ParseObject(raw) is not { } evt)
            {
                continue;
            }
            switch (evt.Value<string>("type"))
            {
                case "turn.completed" when evt["usage"] is JObject usage:
                    input = Clamp(usage, "input_tokens");
                    output = Clamp(usage, "output_tokens");
                    break;
                case "turn.failed":
                    failure = evt["error"]?["message"]?.Type == JTokenType.String
                        ? evt["error"]!.Value<string>("message")
                        : "turn failed";
                    break;
                case "error":
                    lastError = evt.Value<string>("message");
                    break;
            }
        }

        if (failure is not null || process.ExitCode != 0)
        {
            return CliBackendResult.Failed(CliText.Failure(DisplayName, failure ?? lastError ?? FirstLine(process.StandardError), process.ExitCode), input, output);
        }

        var file = new FileInfo(invocation.LastMessagePath);
        if (!file.Exists)
        {
            return CliBackendResult.Unparsed("the codex CLI wrote no final message", input, output);
        }
        if (file.Length > invocation.MaxOutputBytes)
        {
            return CliBackendResult.Failed($"{DisplayName} reply exceeded {Megabytes(invocation.MaxOutputBytes)}; it was discarded.", input, output);
        }

        var reply = CliReplyContract.ParseObject(File.ReadAllText(file.FullName));
        return reply is null
            ? CliBackendResult.Unparsed("the codex CLI's final message was not one JSON object", input, output)
            : new CliBackendResult(reply, null, null, input, output);
    }

    /// <summary>A TOML basic string (the value of a <c>-c key=value</c> override).</summary>
    internal static string TomlString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    internal static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024.0):0.#} MB";

    private static int Clamp(JObject source, string key) =>
        source[key]?.Type == JTokenType.Integer ? (int)Math.Clamp(source.Value<long>(key), 0, int.MaxValue) : 0;

    private static string? FirstLine(string? text)
    {
        if (text is null)
        {
            return null;
        }
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                return line.Trim();
            }
        }
        return null;
    }
}
