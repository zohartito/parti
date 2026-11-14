// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parti.Providers.Cli;

/// <summary>
/// Finds a CLI executable. Rhino started from Finder (or a Windows shortcut) has a minimal
/// <c>PATH</c>, so the search is: the explicit path from Settings, then <c>PATH</c>, then the usual
/// install locations. An explicit path that does not exist is an error, not a reason to search
/// elsewhere. The result is always a real file, so shell functions or aliases that wrap the CLI
/// (and may add flags) never apply.
/// </summary>
public sealed class CliExecutableResolver
{
    private readonly Func<string, string?> environment;
    private readonly Func<string, bool> fileExists;
    private readonly bool isWindows;
    private readonly string? home;

    /// <summary>Creates a resolver over the given environment and file system (injectable for tests).</summary>
    public CliExecutableResolver(Func<string, string?> environment, Func<string, bool> fileExists, bool isWindows, string? homeDirectory)
    {
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
        this.fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        this.isWindows = isWindows;
        home = string.IsNullOrWhiteSpace(homeDirectory) ? null : homeDirectory;
    }

    /// <summary>The resolver for this process: real environment, file system and OS.</summary>
    public static CliExecutableResolver Default { get; } = new(
        Environment.GetEnvironmentVariable,
        File.Exists,
        OperatingSystem.IsWindows(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Install locations searched after <c>PATH</c>, in order: Homebrew, <c>/usr/local/bin</c>,
    /// <c>~/.local/bin</c> (the native installers), <c>~/.npm-global/bin</c>, <c>~/.claude/local</c>,
    /// and on Windows <c>%APPDATA%\npm</c> and <c>%LOCALAPPDATA%\Programs</c>.
    /// </summary>
    public IReadOnlyList<string> KnownDirectories(string executableName)
    {
        var directories = new List<string>();
        if (isWindows)
        {
            if (home is not null)
            {
                directories.Add(Path.Combine(home, ".local", "bin"));
            }
            if (environment("APPDATA") is { Length: > 0 } appData)
            {
                directories.Add(Path.Combine(appData, "npm"));
            }
            if (environment("LOCALAPPDATA") is { Length: > 0 } localAppData)
            {
                directories.Add(Path.Combine(localAppData, "Programs"));
                directories.Add(Path.Combine(localAppData, "Programs", executableName));
            }
            return directories;
        }

        directories.Add("/opt/homebrew/bin");
        directories.Add("/usr/local/bin");
        if (home is not null)
        {
            directories.Add(Path.Combine(home, ".local", "bin"));
            directories.Add(Path.Combine(home, ".npm-global", "bin"));
            directories.Add(Path.Combine(home, ".claude", "local"));
        }
        return directories;
    }

    /// <summary>
    /// Resolves <paramref name="executableName"/> (<c>claude</c> or <c>codex</c>). Returns null when
    /// nothing is found, or when <paramref name="explicitPath"/> is set and does not exist.
    /// </summary>
    public string? Resolve(string executableName, string? explicitPath)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            throw new ArgumentException("An executable name is required.", nameof(executableName));
        }

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var path = ExpandHome(explicitPath!.Trim());
            return fileExists(path) ? path : null;
        }

        var separator = isWindows ? ';' : ':';
        var pathDirectories = (environment("PATH") ?? string.Empty)
            .Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathRooted);

        foreach (var directory in pathDirectories.Concat(KnownDirectories(executableName)))
        {
            foreach (var candidate in Candidates(executableName))
            {
                var path = Path.Combine(directory, candidate);
                if (fileExists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    // Windows: the native .exe only. An npm .cmd shim cannot be started without a shell
    // (UseShellExecute = false), and running it through cmd.exe would re-parse the arguments; the
    // user sets the executable path to the real binary instead. Elsewhere: the bare name.
    private IEnumerable<string> Candidates(string executableName) => isWindows
        ? new[] { executableName + ".exe" }
        : new[] { executableName };

    private string ExpandHome(string path)
    {
        if (home is null)
        {
            return path;
        }
        if (path == "~")
        {
            return home;
        }
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(home, path.Substring(2));
        }
        return path;
    }
}
