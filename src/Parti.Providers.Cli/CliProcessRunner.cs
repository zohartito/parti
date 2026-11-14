// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Parti.Providers.Cli;

/// <summary>
/// Runs a CLI with <see cref="Process"/>: the real executable (<c>UseShellExecute = false</c>,
/// arguments through <see cref="ProcessStartInfo.ArgumentList"/>, never a shell, so shell functions
/// and aliases that add flags never apply), standard input written then closed, standard output and
/// error read concurrently under a byte cap, and the whole process tree killed on timeout, cap or
/// cancellation. The environment is inherited (each CLI keeps its subscription login in the user's
/// home directory) minus <see cref="NonSubscriptionVariables"/>; nothing is added to it.
/// </summary>
public sealed class CliProcessRunner : ICliProcessRunner
{
    /// <summary>
    /// Prefixes of variables that would make a CLI bill an API key, a gateway, or a cloud account
    /// instead of the user's subscription login (keys, base URLs, custom headers, and Claude Code's
    /// <c>CLAUDE_CODE_USE_*</c> provider switches). The provider is subscription-only, so the child
    /// never sees them. <c>CLAUDE_CODE_OAUTH_TOKEN</c> (a subscription login) is kept.
    /// </summary>
    public static readonly IReadOnlyList<string> NonSubscriptionPrefixes = new[]
    {
        "ANTHROPIC_", "CLAUDE_CODE_USE_", "OPENAI_", "AZURE_OPENAI_",
    };

    /// <summary>
    /// Single variables removed as well: Codex's API key, and Claude Code's effort override, which
    /// would take precedence over the effort Parti passes on the command line.
    /// </summary>
    public static readonly IReadOnlyList<string> NonSubscriptionVariables = new[]
    {
        "CODEX_API_KEY", "CLAUDE_CODE_EFFORT_LEVEL",
    };

    /// <summary>Removes the non-subscription variables from a child environment.</summary>
    public static void RemoveNonSubscriptionVariables(IDictionary<string, string?> environment)
    {
        bool Remove(string name) =>
            NonSubscriptionVariables.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            NonSubscriptionPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        foreach (var name in environment.Keys.Where(Remove).ToList())
        {
            environment.Remove(name);
        }
    }

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>How long to wait for the output pipes to drain after the process has exited.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    /// <summary>Shared instance (the runner is stateless).</summary>
    public static CliProcessRunner Instance { get; } = new();

    /// <inheritdoc />
    public async Task<CliProcessResult> RunAsync(CliProcessRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        RemoveNonSubscriptionVariables(startInfo.Environment);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        using var overflow = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, overflow.Token);

        var stdoutTask = ReadCappedAsync(process.StandardOutput.BaseStream, request.MaxOutputBytes, killOnOverflow: overflow);
        var stderrTask = ReadCappedAsync(process.StandardError.BaseStream, request.MaxOutputBytes, killOnOverflow: null);
        var stdinTask = WriteInputAsync(process.StandardInput, request.StandardInput);

        var stopped = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            stopped = true;
            KillTree(process);
        }

        // Bounded: a descendant that outlives the CLI can keep the pipes open (and block a large
        // stdin write) indefinitely, so input delivery and draining share one grace period. Disposing
        // the process below closes Parti's ends of the pipes.
        var pipes = Task.WhenAll(IgnoreFailures(stdinTask), stdoutTask, stderrTask);
        await Task.WhenAny(pipes, Task.Delay(DrainGrace)).ConfigureAwait(false);

        if (stopped && cancellationToken.IsCancellationRequested && !timeout.IsCancellationRequested && !overflow.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : new CappedText(string.Empty, false);
        var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : new CappedText(string.Empty, false);
        var exitCode = process.HasExited ? process.ExitCode : -1;
        return new CliProcessResult(
            exitCode,
            stdout.Text,
            stderr.Text,
            TimedOut: stopped && timeout.IsCancellationRequested && !overflow.IsCancellationRequested,
            OutputLimitExceeded: overflow.IsCancellationRequested || stdout.Overflowed);
    }

    private static async Task WriteInputAsync(StreamWriter input, string text)
    {
        try
        {
            await input.WriteAsync(text).ConfigureAwait(false);
            await input.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The process exited (or was killed) before reading all of its input.
        }
        finally
        {
            try
            {
                input.Close();
            }
            catch (IOException)
            {
                // Broken pipe on close: same as above.
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="stream"/> to its end, keeping at most <paramref name="cap"/> bytes. Past
    /// the cap it either signals <paramref name="killOnOverflow"/> and stops (standard output) or
    /// keeps draining and discards the rest (standard error, so the child never blocks on a full pipe).
    /// </summary>
    private static async Task<CappedText> ReadCappedAsync(Stream stream, long cap, CancellationTokenSource? killOnOverflow)
    {
        using var kept = new MemoryStream();
        var buffer = new byte[81920];
        var overflowed = false;
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                break;
            }
            if (read == 0)
            {
                break;
            }

            var room = cap - kept.Length;
            if (read > room)
            {
                if (room > 0)
                {
                    kept.Write(buffer, 0, (int)room);
                }
                overflowed = true;
                if (killOnOverflow is not null)
                {
                    try { killOnOverflow.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
                    break;
                }
                continue;
            }

            if (!overflowed)
            {
                kept.Write(buffer, 0, read);
            }
        }

        return new CappedText(Utf8NoBom.GetString(kept.GetBuffer(), 0, (int)kept.Length), overflowed);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    private static async Task IgnoreFailures(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Input delivery problems surface as a missing or unparseable reply.
        }
    }

    private readonly record struct CappedText(string Text, bool Overflowed);
}
