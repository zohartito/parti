// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// RhinoRuntimeCompatibilityTests — Parti runs inside Rhino's process and binds to the
// Newtonsoft.Json that Rhino ships, not to the NuGet copy it compiled against. Rhino 8.35
// ships Newtonsoft.Json 13.0.3. Compiling against 13.0.4 binds calls such as
// JToken.ToString(Formatting) to an overload 13.0.3 does not have, and every Grasshopper
// tool then fails inside Rhino with MissingMethodException while all unit tests pass
// (found in a live Rhino 8.35 run on 2026-09-27).

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Parti.Core.Tests;

public class RhinoRuntimeCompatibilityTests
{
    private const string RhinoShippedNewtonsoftVersion = "13.0.3";

    [Fact]
    public void NewtonsoftJson_IsPinnedToTheVersionRhinoShips()
    {
        var root = FindRepoRoot();
        var projects = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(root, "tools"), "*.csproj", SearchOption.AllDirectories))
            .ToList();
        var reference = new Regex(
            "<PackageReference\\s+Include=\"Newtonsoft\\.Json\"\\s+Version=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);

        var offenders = projects
            .SelectMany(path => reference.Matches(File.ReadAllText(path))
                .Select(match => (Path: Path.GetRelativePath(root, path), Version: match.Groups[1].Value)))
            .Where(entry => entry.Version != RhinoShippedNewtonsoftVersion)
            .Select(entry => $"{entry.Path} references Newtonsoft.Json {entry.Version}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Newtonsoft.Json must stay at {RhinoShippedNewtonsoftVersion} (the version Rhino 8 ships):\n  " +
                string.Join("\n  ", offenders));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Parti.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repo root (no 'Parti.sln' above '{AppContext.BaseDirectory}').");
    }
}
