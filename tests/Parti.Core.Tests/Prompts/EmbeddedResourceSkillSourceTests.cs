// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using System.IO;
using System.Reflection;
using Parti.Core.Prompts;
using Xunit;

namespace Parti.Core.Tests.Prompts;

public class EmbeddedResourceSkillSourceTests
{
    private static readonly Assembly TestAssembly = typeof(EmbeddedResourceSkillSourceTests).Assembly;

    private const string NormalPrefix = "TestFixtures.Skills.parti-foundations";
    private const string BackslashPrefix = "TestFixtures.Skills.Win.parti-foundations";

    [Fact]
    public void ReadText_Layer0_ReturnsFoundationText()
    {
        var source = new EmbeddedResourceSkillSource(TestAssembly, NormalPrefix);
        var text = source.ReadText("parti-foundations/SKILL.md");
        Assert.Contains("Test Foundations (Layer 0)", text);
    }

    [Fact]
    public void ReadText_Layer1_ReturnsSubSkillText()
    {
        var source = new EmbeddedResourceSkillSource(TestAssembly, NormalPrefix);
        var text = source.ReadText("parti-foundations/parti-alpha/SKILL.md");
        Assert.Contains("Alpha (L1)", text);
    }

    [Fact]
    public void ReadText_Layer1_AcceptsBackslashInputPath()
    {
        var source = new EmbeddedResourceSkillSource(TestAssembly, NormalPrefix);
        // Caller passes a Windows-style path; the source must normalize it.
        var text = source.ReadText(@"parti-foundations\parti-beta\SKILL.md");
        Assert.Contains("Beta (L1)", text);
    }

    [Fact]
    public void ReadText_ResolvesBackslashResourceNames_FromForwardSlashInput()
    {
        // The Win.* fixtures are embedded with literal backslashes in their logical names,
        // simulating the MSBuild %(RecursiveDir) artifact on Windows builds. The caller
        // still passes forward slashes; the source must fall back to a backslash variant.
        var source = new EmbeddedResourceSkillSource(TestAssembly, BackslashPrefix);

        var layer0 = source.ReadText("parti-foundations/SKILL.md");
        Assert.Contains("Test Foundations (Layer 0)", layer0);

        var layer1 = source.ReadText("parti-foundations/parti-alpha/SKILL.md");
        Assert.Contains("Alpha (L1)", layer1);
    }

    [Fact]
    public void ListSubSkills_ReturnsExpectedNames()
    {
        var source = new EmbeddedResourceSkillSource(TestAssembly, NormalPrefix);
        var subSkills = source.ListSubSkills();
        Assert.Contains("parti-alpha", subSkills);
        Assert.Contains("parti-beta", subSkills);
        Assert.Equal(2, subSkills.Count);
    }

    [Fact]
    public void ListSubSkills_HandlesBackslashResourceNames()
    {
        var source = new EmbeddedResourceSkillSource(TestAssembly, BackslashPrefix);
        var subSkills = source.ListSubSkills();
        Assert.Contains("parti-alpha", subSkills);
    }

    [Fact]
    public void ReadText_ThrowsFileNotFound_WhenResourceMissing()
    {
        var source = new EmbeddedResourceSkillSource(TestAssembly, NormalPrefix);
        Assert.Throws<FileNotFoundException>(
            () => source.ReadText("parti-foundations/parti-nonexistent/SKILL.md"));
    }

    [Fact]
    public void SkillLoader_WorksAgainstEmbeddedSource()
    {
        // End-to-end: prove the source plugs into SkillLoader the same way the in-memory
        // and filesystem sources do.
        var source = new EmbeddedResourceSkillSource(TestAssembly, NormalPrefix);
        var loader = new SkillLoader(source);

        Assert.Contains("Test Foundations (Layer 0)", loader.LoadLayer0());
        Assert.Contains("Alpha (L1)", loader.LoadLayer1("parti-alpha"));
        Assert.Contains("parti-beta", loader.KnownSubSkills);
    }
}
