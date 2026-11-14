// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Parti.Providers.Cli;

/// <summary>
/// Files and settings for one CLI call. Every path is inside <see cref="WorkingDirectory"/>, a
/// fresh temp directory the provider deletes afterwards.
/// </summary>
public sealed record CliInvocation(
    string Model,
    string Effort,
    string WorkingDirectory,
    string SystemPromptPath,
    string SchemaPath,
    string LastMessagePath,
    long MaxOutputBytes);

/// <summary>
/// What a backend made of a finished process. Exactly one of <see cref="Reply"/> (to be checked
/// against <see cref="CliReplyContract"/>), <see cref="Error"/> (a hard failure: no retry) or
/// <see cref="ParseProblem"/> (the reply could not be read: retried once) is set.
/// </summary>
public sealed record CliBackendResult(
    JObject? Reply,
    string? Error,
    string? ParseProblem,
    int InputTokens,
    int OutputTokens)
{
    /// <summary>A hard failure.</summary>
    public static CliBackendResult Failed(string error, int inputTokens = 0, int outputTokens = 0) =>
        new(null, error, null, inputTokens, outputTokens);

    /// <summary>A reply that could not be read.</summary>
    public static CliBackendResult Unparsed(string problem, int inputTokens = 0, int outputTokens = 0) =>
        new(null, null, problem, inputTokens, outputTokens);
}

/// <summary>
/// One vendor CLI: the locked-down argument vector and the reading of its output.
/// </summary>
public interface ICliBackend
{
    /// <summary>Which CLI this is.</summary>
    CliBackendKind Kind { get; }

    /// <summary>Label used in messages, e.g. "claude CLI".</summary>
    string DisplayName { get; }

    /// <summary>
    /// The argument vector (without the executable). It carries flags, the model, the effort,
    /// the fixed reply schema and file paths only; prompts and transcripts never go on argv.
    /// </summary>
    IReadOnlyList<string> BuildArguments(CliInvocation invocation);

    /// <summary>Reads a finished (not timed out, not over the cap) process.</summary>
    CliBackendResult ParseResult(CliProcessResult process, CliInvocation invocation);
}

/// <summary>Shared helpers for backend error messages.</summary>
internal static class CliText
{
    private const int MaxMessageLength = 200;

    // Anything that looks like a key or token is dropped from messages, whatever the CLI printed.
    private static readonly Regex Secret = new(
        @"(?i)(bearer\s+[A-Za-z0-9._~+/=-]+|\b(sk|sess|rk|pk)-[A-Za-z0-9_-]{8,}|\bey[A-Za-z0-9_-]{20,}\.[A-Za-z0-9._-]+)",
        RegexOptions.CultureInvariant);

    /// <summary>One line, no control characters, no token-like strings, at most 200 characters.</summary>
    public static string OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var redacted = Secret.Replace(text!, "[redacted]");
        var builder = new StringBuilder(Math.Min(redacted.Length, MaxMessageLength + 1));
        var lastWasSpace = false;
        foreach (var ch in redacted)
        {
            var c = char.IsControl(ch) || char.IsWhiteSpace(ch) ? ' ' : ch;
            if (c == ' ' && (lastWasSpace || builder.Length == 0))
            {
                continue;
            }
            builder.Append(c);
            lastWasSpace = c == ' ';
            if (builder.Length >= MaxMessageLength)
            {
                builder.Append("...");
                break;
            }
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>True when a CLI message means the user is not signed in.</summary>
    public static bool LooksUnauthenticated(string? message) =>
        message is not null
        && (message.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf("401", StringComparison.Ordinal) >= 0
            || message.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf("authentication", StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>A failure message: "not logged in" when it looks like one, else the CLI's words.</summary>
    public static string Failure(string displayName, string? message, int exitCode)
    {
        if (LooksUnauthenticated(message))
        {
            return $"{displayName}: not logged in (exit {exitCode}). Run the CLI once in a terminal and sign in.";
        }
        var line = OneLine(message);
        return line.Length == 0
            ? $"{displayName} failed (exit {exitCode})."
            : $"{displayName}: {line} (exit {exitCode})";
    }
}
