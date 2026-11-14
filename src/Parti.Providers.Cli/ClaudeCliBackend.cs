// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Parti.Providers.Cli;

/// <summary>
/// Claude Code (<c>claude -p</c>) with no tools of its own: <c>--tools ""</c>, no MCP servers, no
/// settings, skills or slash commands, no saved session, Parti's system prompt from a file, and the
/// reply schema enforced with <c>--json-schema</c>. The transcript arrives on standard input and the
/// reply is read from <c>structured_output</c> in the single JSON result object. <c>--bare</c> is
/// not used: it turns off the subscription (Max plan) login.
/// </summary>
public sealed class ClaudeCliBackend : ICliBackend
{
    /// <summary>Inline settings that make Claude Code accept only a Claude subscription login.</summary>
    public const string ForceSubscriptionLogin = "{\"forceLoginMethod\":\"claudeai\"}";

    /// <inheritdoc />
    public CliBackendKind Kind => CliBackendKind.Claude;

    /// <inheritdoc />
    public string DisplayName => "claude CLI";

    /// <inheritdoc />
    public IReadOnlyList<string> BuildArguments(CliInvocation invocation)
    {
        if (invocation is null)
        {
            throw new ArgumentNullException(nameof(invocation));
        }

        return new[]
        {
            "-p",
            "--model", invocation.Model,
            "--effort", invocation.Effort,
            "--tools", string.Empty,
            "--strict-mcp-config",
            "--setting-sources", string.Empty,
            "--safe-mode",
            "--disable-slash-commands",
            "--no-session-persistence",
            // Subscription only: refuse a Console (API-billed) login.
            "--settings", ForceSubscriptionLogin,
            "--system-prompt-file", invocation.SystemPromptPath,
            "--json-schema", CliReplyContract.SchemaJson,
            "--output-format", "json",
        };
    }

    /// <inheritdoc />
    public CliBackendResult ParseResult(CliProcessResult process, CliInvocation invocation)
    {
        if (process is null)
        {
            throw new ArgumentNullException(nameof(process));
        }

        var envelope = ReadEnvelope(process.StandardOutput);
        if (envelope is null)
        {
            return process.ExitCode != 0
                ? CliBackendResult.Failed(CliText.Failure(DisplayName, FirstLine(process.StandardError), process.ExitCode))
                : CliBackendResult.Unparsed("the claude CLI printed no JSON result");
        }

        var (input, output) = Usage(envelope);

        // An auth failure exits 1 but reports subtype "success"; is_error is the field to trust.
        if (envelope.Value<bool?>("is_error") == true)
        {
            var message = envelope["result"]?.Type == JTokenType.String ? envelope.Value<string>("result") : envelope.Value<string>("subtype");
            return CliBackendResult.Failed(CliText.Failure(DisplayName, message, process.ExitCode), input, output);
        }
        if (process.ExitCode != 0)
        {
            return CliBackendResult.Failed(CliText.Failure(DisplayName, FirstLine(process.StandardError), process.ExitCode), input, output);
        }

        if (envelope["structured_output"] is JObject structured)
        {
            return new CliBackendResult(structured, null, null, input, output);
        }

        // Older builds put the JSON reply in "result" only.
        if (envelope["result"]?.Type == JTokenType.String && CliReplyContract.ParseObject(envelope.Value<string>("result")) is { } fromResult)
        {
            return new CliBackendResult(fromResult, null, null, input, output);
        }

        var subtype = envelope.Value<string>("subtype");
        return CliBackendResult.Unparsed(
            $"the claude CLI returned no structured reply{(string.IsNullOrEmpty(subtype) ? string.Empty : $" ({CliText.OneLine(subtype)})")}",
            input,
            output);
    }

    // --output-format json prints one object; tolerate stray lines before it.
    private static JObject? ReadEnvelope(string stdout)
    {
        var whole = CliReplyContract.ParseObject(stdout);
        if (whole is not null)
        {
            return whole;
        }

        var lines = stdout.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (CliReplyContract.ParseObject(lines[i]) is { } line && line.ContainsKey("type"))
            {
                return line;
            }
        }
        return null;
    }

    private static (int Input, int Output) Usage(JObject envelope)
    {
        if (envelope["usage"] is not JObject usage)
        {
            return (0, 0);
        }

        static int Read(JObject source, string key) =>
            source[key]?.Type == JTokenType.Integer ? (int)Math.Clamp(source.Value<long>(key), 0, int.MaxValue) : 0;

        long input = Read(usage, "input_tokens") + (long)Read(usage, "cache_creation_input_tokens") + Read(usage, "cache_read_input_tokens");
        return ((int)Math.Min(input, int.MaxValue), Read(usage, "output_tokens"));
    }

    private static string? FirstLine(string? text) =>
        text?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
}
