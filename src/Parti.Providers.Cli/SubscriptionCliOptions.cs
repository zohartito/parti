// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Parti.Providers.Cli;

/// <summary>
/// The vendor CLI a <see cref="SubscriptionCliProvider"/> drives.
/// </summary>
public enum CliBackendKind
{
    /// <summary>Anthropic's Claude Code CLI (<c>claude</c>).</summary>
    Claude,

    /// <summary>OpenAI's Codex CLI (<c>codex</c>).</summary>
    Codex,
}

/// <summary>
/// Configuration for <see cref="SubscriptionCliProvider"/>. The provider snapshots these values
/// when it is constructed. No credential is configured here: each CLI uses its own login.
/// </summary>
public sealed class SubscriptionCliOptions
{
    /// <summary>Default wall-clock limit for one CLI call.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Default cap on the bytes read from the CLI's standard output (and the reply file).</summary>
    public const long DefaultMaxOutputBytes = 4L * 1024 * 1024;

    /// <summary>Default reasoning effort.</summary>
    public const string DefaultEffort = "medium";

    private static readonly Regex SafeModelName = new("^[A-Za-z0-9][A-Za-z0-9._:\\[\\]-]{0,127}$", RegexOptions.CultureInvariant);

    private static readonly IReadOnlyList<string> ClaudeModels = new[] { "claude-opus-5-5", "claude-fable-5-1", "claude-sonnet-5" };
    private static readonly IReadOnlyList<string> CodexModels = new[] { "gpt-6-astra", "gpt-6-sol" };
    private static readonly IReadOnlyList<string> ClaudeEfforts = new[] { "low", "medium", "high", "xhigh", "max" };
    private static readonly IReadOnlyList<string> CodexEfforts = new[] { "low", "medium", "high", "xhigh", "max", "ultra" };

    /// <summary>Which CLI to run.</summary>
    public CliBackendKind Backend { get; set; } = CliBackendKind.Claude;

    /// <summary>
    /// Default model. A non-blank <c>AIRequestCall.Model</c> takes precedence (the session sends the
    /// configured model on every call).
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Reasoning effort passed to the CLI; one of <see cref="EffortLevels"/>.</summary>
    public string Effort { get; set; } = DefaultEffort;

    /// <summary>
    /// Explicit path to the CLI executable. Null or blank searches <c>PATH</c>, then the usual
    /// install locations (see <see cref="CliExecutableResolver"/>).
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Wall-clock limit for one CLI call; the whole process tree is killed when it expires.</summary>
    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>Cap on standard output (and the reply file); a larger output fails the call.</summary>
    public long MaxOutputBytes { get; set; } = DefaultMaxOutputBytes;

    /// <summary>Model suggestions for <paramref name="backend"/>.</summary>
    public static IReadOnlyList<string> KnownModels(CliBackendKind backend) =>
        backend == CliBackendKind.Codex ? CodexModels : ClaudeModels;

    /// <summary>Effort levels the CLI for <paramref name="backend"/> accepts.</summary>
    public static IReadOnlyList<string> EffortLevels(CliBackendKind backend) =>
        backend == CliBackendKind.Codex ? CodexEfforts : ClaudeEfforts;

    /// <summary>The executable name the CLI installs as (<c>claude</c> or <c>codex</c>).</summary>
    public static string ExecutableName(CliBackendKind backend) =>
        backend == CliBackendKind.Codex ? "codex" : "claude";

    /// <summary>
    /// True when <paramref name="model"/> is a plain model id: letters, digits and <c>._:[]-</c>,
    /// starting with a letter or digit. It goes on the CLI's command line, so nothing else is allowed.
    /// </summary>
    public static bool IsSafeModelName(string? model) => model is not null && SafeModelName.IsMatch(model);

    /// <summary>True when <paramref name="effort"/> is one of <see cref="EffortLevels"/>.</summary>
    public static bool IsKnownEffort(CliBackendKind backend, string? effort) =>
        effort is not null && EffortLevels(backend).Contains(effort, StringComparer.Ordinal);

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when a value would be unsafe or meaningless on the
    /// command line.
    /// </summary>
    public void Validate()
    {
        if (!Enum.IsDefined(typeof(CliBackendKind), Backend))
        {
            throw new ArgumentException($"Unknown CLI backend {Backend}.");
        }
        if (!string.IsNullOrWhiteSpace(Model) && !IsSafeModelName(Model))
        {
            throw new ArgumentException($"\"{Model}\" is not a valid model id.");
        }
        if (!IsKnownEffort(Backend, Effort))
        {
            throw new ArgumentException(
                $"Effort \"{Effort}\" is not one of {string.Join(", ", EffortLevels(Backend))}.");
        }
        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("The timeout must be positive.");
        }
        if (MaxOutputBytes <= 0)
        {
            throw new ArgumentException("The output cap must be positive.");
        }
    }

    /// <summary>Returns a detached copy.</summary>
    public SubscriptionCliOptions Clone() => new()
    {
        Backend = Backend,
        Model = Model,
        Effort = Effort,
        ExecutablePath = ExecutablePath,
        Timeout = Timeout,
        MaxOutputBytes = MaxOutputBytes,
    };
}
