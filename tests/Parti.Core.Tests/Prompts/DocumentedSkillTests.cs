// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.IO;
using System.Text.RegularExpressions;
using Parti.Core.Prompts;
using Xunit;

namespace Parti.Core.Tests.Prompts;

public class DocumentedSkillTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Parti.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Parti.sln");
    }

    [Fact]
    public void AddingSubSkill_DocumentedManifestLoadsAndRoutes()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "PROMPTS.md")).Replace("\r\n", "\n"); // Windows checkouts use CRLF
        var recipe = doc.Split("## Adding a new sub-skill")[1].Split("## Versioning policy")[0];
        var example = Regex.Match(recipe, "```markdown\\n(.*?)\\n```", RegexOptions.Singleline);
        Assert.True(example.Success, "The extension recipe needs a copyable SKILL.md example.");
        var text = example.Groups[1].Value;
        Assert.NotNull(FrontmatterParser.TryParseManifest(text));

        var root = Path.Combine(Path.GetTempPath(), "parti-skill-" + Guid.NewGuid().ToString("N"));
        try
        {
            var folder = Path.Combine(root, "parti-foundations", "parti-example");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), text);
            var source = new FileSystemSkillSource(root);
            Assert.Contains("parti-example", source.ListSubSkills());
            Assert.Contains("parti-example", new KeywordSkillRouter(source).Route("exampleworkflow").SubSkills);
            Assert.NotNull(new SkillLoader(source).LoadLayer1("parti-example"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Build_RoutesEachMessageWithoutRetainingPreviousSkills()
    {
        var source = new FileSystemSkillSource(Path.Combine(RepoRoot(), "prompts"));
        var builder = new SessionPromptBuilder(new SkillLoader(source), new KeywordSkillRouter(source));
        Assert.Contains("parti-fabricate", builder.Build("prepare a cnc toolpath").ActivatedSubSkills);
        Assert.Equal(new[] { "parti-formalize" }, builder.Build("hello").ActivatedSubSkills);
    }
}
