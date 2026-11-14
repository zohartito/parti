// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// Layer0SizeGateTests — enforces progressive-disclosure size budgets on the shipped
// SKILL.md files. Layer 0 is always loaded so its body must stay under 16 KB; Layer 1
// bodies are only loaded on demand so they get a bigger 32 KB budget.
// Budget rationale: Layer 0 is always in context, so it must stay small (progressive disclosure).

using System;
using System.IO;
using System.Text;
using Parti.Core.Prompts;
using Xunit;

namespace Parti.Core.Tests.Prompts;

public class Layer0SizeGateTests
{
    private const int Layer0MaxBytes = 16 * 1024;
    private const int Layer1MaxBytes = 32 * 1024;
    private const string BudgetReference =
        "Layer 0 is always in context; move detail into Layer 1 skills.";

    [Fact]
    public void Layer0_BodyUnder16KB()
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "prompts", "parti-foundations", "SKILL.md");
        Assert.True(File.Exists(path), $"Expected Layer 0 at '{path}'.");

        var raw = File.ReadAllText(path);
        FrontmatterParser.SplitFrontmatter(raw, out _, out var body);
        var bodyBytes = Encoding.UTF8.GetByteCount(body);

        Assert.True(
            bodyBytes <= Layer0MaxBytes,
            $"Layer 0 budget exceeded — {bodyBytes} bytes > {Layer0MaxBytes} — {BudgetReference}");
    }

    [Fact]
    public void Layer1_BodiesUnder32KB()
    {
        var repoRoot = FindRepoRoot();
        var foundations = Path.Combine(repoRoot, "prompts", "parti-foundations");
        Assert.True(Directory.Exists(foundations), $"Expected foundations at '{foundations}'.");

        foreach (var subDir in Directory.EnumerateDirectories(foundations))
        {
            var skill = Path.Combine(subDir, "SKILL.md");
            if (!File.Exists(skill)) continue;

            var raw = File.ReadAllText(skill);
            FrontmatterParser.SplitFrontmatter(raw, out _, out var body);
            var bodyBytes = Encoding.UTF8.GetByteCount(body);

            Assert.True(
                bodyBytes <= Layer1MaxBytes,
                $"Layer 1 budget exceeded for '{Path.GetFileName(subDir)}' — " +
                $"{bodyBytes} bytes > {Layer1MaxBytes} — {BudgetReference}");
        }
    }

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> until it finds a directory
    /// containing the <c>prompts/</c> folder. Works in any CI environment regardless of
    /// where the test binary ends up.
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var prompts = Path.Combine(dir.FullName, "prompts", "parti-foundations");
            if (Directory.Exists(prompts))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repo root (no 'prompts/parti-foundations' folder above " +
            $"'{AppContext.BaseDirectory}').");
    }
}
