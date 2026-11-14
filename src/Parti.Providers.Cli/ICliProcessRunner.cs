// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Parti.Providers.Cli;

/// <summary>
/// One CLI process to run. <paramref name="Arguments"/> are passed as separate argv entries (never
/// through a shell); prompts travel in <paramref name="StandardInput"/> or files, never in argv.
/// </summary>
/// <param name="FileName">Absolute path of the executable.</param>
/// <param name="Arguments">Argument vector, one entry per argument.</param>
/// <param name="WorkingDirectory">Working directory (a fresh, empty temp directory).</param>
/// <param name="StandardInput">Text written to standard input, which is then closed.</param>
/// <param name="Timeout">Wall-clock limit; the process tree is killed when it expires.</param>
/// <param name="MaxOutputBytes">Cap on standard output; the process tree is killed when it is exceeded.</param>
public sealed record CliProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string StandardInput,
    TimeSpan Timeout,
    long MaxOutputBytes);

/// <summary>
/// Outcome of a <see cref="CliProcessRequest"/>.
/// </summary>
/// <param name="ExitCode">Process exit code (meaningless when <paramref name="TimedOut"/> or
/// <paramref name="OutputLimitExceeded"/>).</param>
/// <param name="StandardOutput">Standard output as UTF-8 text (up to the cap).</param>
/// <param name="StandardError">Standard error as UTF-8 text (up to the cap; the rest is discarded).</param>
/// <param name="TimedOut">True when the timeout killed the process.</param>
/// <param name="OutputLimitExceeded">True when standard output went over the cap and the process was killed.</param>
public sealed record CliProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false,
    bool OutputLimitExceeded = false);

/// <summary>
/// Runs a CLI process. The real implementation is <see cref="CliProcessRunner"/>; tests use a fake.
/// </summary>
public interface ICliProcessRunner
{
    /// <summary>
    /// Runs <paramref name="request"/> to completion, timeout, or output cap. Cancelling
    /// <paramref name="cancellationToken"/> kills the process tree and throws
    /// <see cref="OperationCanceledException"/>. Failing to start the executable throws.
    /// </summary>
    Task<CliProcessResult> RunAsync(CliProcessRequest request, CancellationToken cancellationToken);
}
