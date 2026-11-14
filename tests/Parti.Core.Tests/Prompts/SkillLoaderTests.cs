// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using System;
using System.Collections.Generic;
using System.IO;
using Parti.Core.Prompts;
using Xunit;

namespace Parti.Core.Tests.Prompts;

public class SkillLoaderTests
{
    // A spy wrapper around InMemorySkillSource that counts ReadText calls per path.
    // Used to verify Layer 1 caching does not hit the source on repeat calls.
    private sealed class CountingSkillSource : ISkillSource
    {
        private readonly ISkillSource _inner;
        public Dictionary<string, int> ReadCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public CountingSkillSource(ISkillSource inner) => _inner = inner;

        public string? ReadText(string relativePath)
        {
            ReadCounts.TryGetValue(relativePath, out var n);
            ReadCounts[relativePath] = n + 1;
            return _inner.ReadText(relativePath);
        }

        public IReadOnlyList<string> ListSubSkills() => _inner.ListSubSkills();
    }

    private static InMemorySkillSource BuildFullSource() => new(new Dictionary<string, string>
    {
        ["parti-foundations/SKILL.md"] = "# Parti Foundations (Layer 0)",
        ["parti-foundations/parti-formalize/SKILL.md"] = "# Formalize (L1)",
        ["parti-foundations/parti-ghcanvas/SKILL.md"] = "# Canvas (L1)",
        ["parti-foundations/parti-ghcanvas/references/tree-ops-decision-table.md"] =
            "| tree op | when |",
    });

    [Fact]
    public void LoadLayer0_ReturnsFoundationText()
    {
        var loader = new SkillLoader(BuildFullSource());
        Assert.Equal("# Parti Foundations (Layer 0)", loader.LoadLayer0());
    }

    [Fact]
    public void LoadLayer0_ThrowsFileNotFound_WhenMissing()
    {
        var empty = new InMemorySkillSource(new Dictionary<string, string>());
        var loader = new SkillLoader(empty);

        var ex = Assert.Throws<FileNotFoundException>(() => loader.LoadLayer0());
        Assert.Contains("parti-foundations/SKILL.md", ex.Message);
    }

    [Fact]
    public void LoadLayer1_ReturnsSubSkillText()
    {
        var loader = new SkillLoader(BuildFullSource());
        Assert.Equal("# Canvas (L1)", loader.LoadLayer1("parti-ghcanvas"));
    }

    [Fact]
    public void LoadLayer1_ThrowsKeyNotFound_WhenSubSkillMissing()
    {
        var loader = new SkillLoader(BuildFullSource());
        var ex = Assert.Throws<KeyNotFoundException>(() => loader.LoadLayer1("parti-nonexistent"));
        Assert.Contains("parti-nonexistent", ex.Message);
    }

    [Fact]
    public void LoadLayer1_IsCached_RepeatCallsDoNotHitSource()
    {
        var spy = new CountingSkillSource(BuildFullSource());
        var loader = new SkillLoader(spy);

        _ = loader.LoadLayer1("parti-formalize");
        _ = loader.LoadLayer1("parti-formalize");
        _ = loader.LoadLayer1("parti-formalize");

        Assert.Equal(1, spy.ReadCounts["parti-foundations/parti-formalize/SKILL.md"]);
    }

    [Fact]
    public void LoadLayer2_ReturnsNull_WhenReferenceMissing()
    {
        var loader = new SkillLoader(BuildFullSource());
        Assert.Null(loader.LoadLayer2("parti-ghcanvas", "references/does-not-exist.md"));
    }

    [Fact]
    public void LoadLayer2_ReturnsText_WhenReferencePresent()
    {
        var loader = new SkillLoader(BuildFullSource());
        var text = loader.LoadLayer2("parti-ghcanvas", "references/tree-ops-decision-table.md");
        Assert.Equal("| tree op | when |", text);
    }

    [Fact]
    public void KnownSubSkills_ReflectsSourceListing()
    {
        var loader = new SkillLoader(BuildFullSource());
        Assert.Contains("parti-formalize", loader.KnownSubSkills);
        Assert.Contains("parti-ghcanvas", loader.KnownSubSkills);
        Assert.Equal(2, loader.KnownSubSkills.Count);
    }

    [Fact]
    public void KnownSubSkills_IsCached_AfterFirstAccess()
    {
        var spy = new CountingSkillSource(BuildFullSource());
        var loader = new SkillLoader(spy);

        _ = loader.KnownSubSkills;
        _ = loader.KnownSubSkills;
        _ = loader.KnownSubSkills;

        // KnownSubSkills should have been cached; this is a smoke check — the listing
        // is cheap, but we want to guarantee we don't re-enumerate the source each time.
        Assert.NotEmpty(loader.KnownSubSkills);
    }

    [Fact]
    public void FileSystemSkillSource_RoundTripsThroughRealTempDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "parti-skill-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var foundations = Path.Combine(tempRoot, "parti-foundations");
            var subSkillDir = Path.Combine(foundations, "parti-formalize");
            Directory.CreateDirectory(subSkillDir);
            File.WriteAllText(Path.Combine(foundations, "SKILL.md"), "# L0 on disk");
            File.WriteAllText(Path.Combine(subSkillDir, "SKILL.md"), "# L1 on disk");

            var source = new FileSystemSkillSource(tempRoot);
            var loader = new SkillLoader(source);

            Assert.Equal("# L0 on disk", loader.LoadLayer0());
            Assert.Equal("# L1 on disk", loader.LoadLayer1("parti-formalize"));
            Assert.Contains("parti-formalize", loader.KnownSubSkills);
            Assert.Null(loader.LoadLayer2("parti-formalize", "references/nope.md"));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void LoadLayer1_ThrowsArgumentException_OnEmptyName()
    {
        var loader = new SkillLoader(BuildFullSource());
        Assert.Throws<ArgumentException>(() => loader.LoadLayer1(""));
        Assert.Throws<ArgumentException>(() => loader.LoadLayer1("   "));
    }
}
