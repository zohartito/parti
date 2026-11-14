// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// FrontmatterParserTests — covers YAML frontmatter parsing for SKILL.md files.

using System.Collections.Generic;
using Parti.Core.Prompts;
using Xunit;

namespace Parti.Core.Tests.Prompts;

public class FrontmatterParserTests
{
    private const string ValidFrontmatter =
        "---\n" +
        "title: parti-ghcanvas\n" +
        "description: Canvas specialist\n" +
        "version: 0.9.0\n" +
        "tags: [grasshopper, canvas, component]\n" +
        "auto_activate: true\n" +
        "user_invocable: true\n" +
        "invocation: /parti-ghcanvas\n" +
        "---\n" +
        "\n" +
        "# parti-ghcanvas\n" +
        "body here\n";

    [Fact]
    public void TryParse_ValidFrontmatter_ReturnsDictionary()
    {
        var result = FrontmatterParser.TryParse(ValidFrontmatter);

        Assert.NotNull(result);
        Assert.Equal("parti-ghcanvas", (string)result!["title"]);
        Assert.Equal("Canvas specialist", (string)result["description"]);
        Assert.Equal("0.9.0", (string)result["version"]);
        Assert.True((bool)result["auto_activate"]);
        Assert.True((bool)result["user_invocable"]);
        Assert.Equal("/parti-ghcanvas", (string)result["invocation"]);

        var tags = Assert.IsType<List<string>>(result["tags"]);
        Assert.Equal(new[] { "grasshopper", "canvas", "component" }, tags);
    }

    [Fact]
    public void TryParse_MissingFrontmatter_ReturnsNull()
    {
        var text = "# just a heading\nbody text\n";
        Assert.Null(FrontmatterParser.TryParse(text));
    }

    [Fact]
    public void TryParse_EmptyString_ReturnsNull()
    {
        Assert.Null(FrontmatterParser.TryParse(string.Empty));
        Assert.Null(FrontmatterParser.TryParse(null!));
    }

    [Fact]
    public void TryParse_MalformedMissingColon_ReturnsNullNoThrow()
    {
        var text = "---\ntitle parti-foo\n---\nbody\n";
        Assert.Null(FrontmatterParser.TryParse(text));
    }

    [Fact]
    public void TryParse_UnclosedFrontmatter_ReturnsNull()
    {
        // No closing --- fence.
        var text = "---\ntitle: parti-foo\ndescription: bar\n# never closed\nbody\n";
        Assert.Null(FrontmatterParser.TryParse(text));
    }

    [Fact]
    public void TryParse_QuotedStrings_AreUnquoted()
    {
        var text = "---\ntitle: \"quoted title\"\ndescription: 'single quoted'\n---\nbody\n";
        var dict = FrontmatterParser.TryParse(text);

        Assert.NotNull(dict);
        Assert.Equal("quoted title", (string)dict!["title"]);
        Assert.Equal("single quoted", (string)dict["description"]);
    }

    [Fact]
    public void TryParse_ListWithQuotedEntry_PreservesSpacesInside()
    {
        var text = "---\ntags: [foo, \"what varies\", bar]\n---\nbody\n";
        var dict = FrontmatterParser.TryParse(text);

        Assert.NotNull(dict);
        var tags = Assert.IsType<List<string>>(dict!["tags"]);
        Assert.Equal(new[] { "foo", "what varies", "bar" }, tags);
    }

    [Fact]
    public void TryParse_CrlfLineEndings_ParseCorrectly()
    {
        var text = "---\r\ntitle: foo\r\nauto_activate: false\r\n---\r\nbody\r\n";
        var dict = FrontmatterParser.TryParse(text);

        Assert.NotNull(dict);
        Assert.Equal("foo", (string)dict!["title"]);
        Assert.False((bool)dict["auto_activate"]);
    }

    [Fact]
    public void SplitFrontmatter_StripsBlockFromBody()
    {
        FrontmatterParser.SplitFrontmatter(ValidFrontmatter, out var fm, out var body);

        Assert.NotNull(fm);
        Assert.DoesNotContain("---", body);
        Assert.StartsWith("\n# parti-ghcanvas", body);
    }

    [Fact]
    public void SplitFrontmatter_NoFrontmatter_ReturnsFullBody()
    {
        const string text = "# heading\nbody\n";
        FrontmatterParser.SplitFrontmatter(text, out var fm, out var body);

        Assert.Null(fm);
        Assert.Equal(text, body);
    }

    [Fact]
    public void TryParseManifest_ValidFrontmatter_ReturnsManifest()
    {
        var manifest = FrontmatterParser.TryParseManifest(ValidFrontmatter);

        Assert.NotNull(manifest);
        Assert.Equal("parti-ghcanvas", manifest!.Title);
        Assert.Equal("Canvas specialist", manifest.Description);
        Assert.Equal("0.9.0", manifest.Version);
        Assert.True(manifest.AutoActivate);
        Assert.True(manifest.UserInvocable);
        Assert.Equal("/parti-ghcanvas", manifest.Invocation);
        Assert.Equal(3, manifest.Tags.Count);
    }

    [Fact]
    public void TryParseManifest_NullInvocation_BecomesNull()
    {
        var text = "---\ntitle: parti-foundations\nauto_activate: true\nuser_invocable: false\ninvocation: null\n---\nbody\n";
        var manifest = FrontmatterParser.TryParseManifest(text);

        Assert.NotNull(manifest);
        Assert.Null(manifest!.Invocation);
    }

    [Fact]
    public void TryParseManifest_MissingTitle_ReturnsNull()
    {
        var text = "---\ndescription: no title here\n---\nbody\n";
        Assert.Null(FrontmatterParser.TryParseManifest(text));
    }
}
