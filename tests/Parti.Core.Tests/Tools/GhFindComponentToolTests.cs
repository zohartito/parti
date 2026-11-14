// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Core.Tests.Tools;

public class GhFindComponentToolTests
{
    private const string NumberSlider = "57da07bd-ecab-415d-9d86-af36d7073abc";
    private const string CurveCircle = "807b86e3-be8d-4970-92b5-f8cdcb45b06b";

    private static Task<JObject> Find(JObject args, IComponentCatalog? catalog = null) =>
        GhFindComponentTool.HandlerFor(catalog ?? TestCatalog.Instance)(args, CancellationToken.None);

    private static string[] Guids(JObject result) =>
        ((JArray)result["candidates"]!).Select(c => c["componentGuid"]!.ToString()).ToArray();

    private static ComponentCatalogEntry Entry(
        string guid, string name, string nick = "", string category = "Cat", string sub = "Sub",
        string library = "Core", bool obsolete = false, string exposure = "primary", string description = "") =>
        new(Guid.Parse(guid), name, nick, category, sub, library, obsolete, exposure, description);

    [Fact]
    public async Task FindComponent_NumberSlider_ReturnsCoreGuid()
    {
        var result = await Find(new JObject { ["query"] = "Number Slider" });

        Assert.Equal("ok", result["status"]!.Value<string>());
        var first = ((JArray)result["candidates"]!)[0];
        Assert.Equal(NumberSlider, first["componentGuid"]!.ToString());
        Assert.Equal("Number Slider", first["name"]!.ToString());
        Assert.Equal("Params", first["category"]!.ToString());
        Assert.Equal("Input", first["subcategory"]!.ToString());
        Assert.Equal("Grasshopper", first["library"]!.ToString());
        Assert.False(first["obsolete"]!.Value<bool>());
        Assert.Equal("primary", first["exposure"]!.ToString());
    }

    [Fact]
    public async Task FindComponent_Slider_RanksNumberSliderFirst()
    {
        var result = await Find(new JObject { ["query"] = "slider" });
        Assert.Equal(NumberSlider, Guids(result)[0]);
    }

    [Fact]
    public async Task FindComponent_CircleInCurveTab_ReturnsTheCurveCircleOnly()
    {
        var all = await Find(new JObject { ["query"] = "circle" });
        Assert.Equal(CurveCircle, Guids(all)[0]); // tie on score/exposure/core/name broken by GUID

        var curve = await Find(new JObject { ["query"] = "Circle", ["category"] = "curve", ["limit"] = 20 });
        Assert.Contains(CurveCircle, Guids(curve));
        Assert.All((JArray)curve["candidates"]!, c => Assert.Equal("Curve", c["category"]!.ToString()));
    }

    [Fact]
    public async Task FindComponent_LimitClampedTo20()
    {
        var result = await Find(new JObject { ["query"] = "a", ["limit"] = 500 });
        Assert.Equal(GhFindComponentTool.MaxLimit, result["count"]!.Value<int>());
        Assert.Equal(20, ((JArray)result["candidates"]!).Count);

        var one = await Find(new JObject { ["query"] = "a", ["limit"] = 0 });
        Assert.Equal(1, one["count"]!.Value<int>());

        var byDefault = await Find(new JObject { ["query"] = "a" });
        Assert.Equal(GhFindComponentTool.DefaultLimit, byDefault["count"]!.Value<int>());
    }

    [Fact]
    public async Task FindComponent_OrderIsStableAcrossRuns()
    {
        var args = new JObject { ["query"] = "point", ["limit"] = 20 };
        var first = Guids(await Find(args));
        var second = Guids(await Find(args));
        // A catalog enumerated in the opposite order ranks identically.
        var reversed = new ComponentCatalogSnapshot(TestCatalog.Instance.All.Reverse(), TestCatalog.Instance.Libraries());
        var third = Guids(await Find(args, reversed));

        Assert.Equal(20, first.Length);
        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    [Fact]
    public void Ranking_FollowsTheScoreTable()
    {
        var catalog = new ComponentCatalogSnapshot(new[]
        {
            Entry("00000000-0000-0000-0000-000000000006", "Unrelated", description: "draws a grid box"),
            Entry("00000000-0000-0000-0000-000000000005", "Mesh", nick: "grid", category: "Box"),
            Entry("00000000-0000-0000-0000-000000000004", "Hex Grid Box"),
            Entry("00000000-0000-0000-0000-000000000003", "Grid Box Maker"),
            Entry("00000000-0000-0000-0000-000000000002", "Something", nick: "Grid Box"),
            Entry("00000000-0000-0000-0000-000000000001", "Grid Box"),
            Entry("00000000-0000-0000-0000-000000000007", "Nothing Here", description: "grid only"),
        }, Array.Empty<LibraryEntry>());

        var names = GhFindComponentTool.Search(catalog, "grid box").Select(e => e.Name).ToArray();

        // exact name 100, exact nickname 90, name prefix 70, all tokens in name 50,
        // tokens across name/nickname/category/subcategory 30, all tokens in description 10.
        Assert.Equal(new[] { "Grid Box", "Something", "Grid Box Maker", "Hex Grid Box", "Mesh", "Unrelated" }, names);
    }

    [Fact]
    public void Ties_BreakByObsoleteExposureCoreNameGuid()
    {
        var catalog = new ComponentCatalogSnapshot(new[]
        {
            Entry("00000000-0000-0000-0000-00000000000f", "Tie", obsolete: true),
            Entry("00000000-0000-0000-0000-00000000000e", "Tie", exposure: "secondary"),
            Entry("00000000-0000-0000-0000-00000000000d", "Tie", exposure: "primary, obscure"),
            Entry("00000000-0000-0000-0000-00000000000c", "Tie", library: "ThirdParty"),
            Entry("00000000-0000-0000-0000-00000000000b", "tie"),
            Entry("00000000-0000-0000-0000-00000000000a", "Tie"),
            Entry("00000000-0000-0000-0000-000000000009", "Tie"),
        }, new[] { new LibraryEntry("Core", "1", "A", true), new LibraryEntry("ThirdParty", "1", "B", false) });

        var guids = GhFindComponentTool.Search(catalog, "tie", includeObsolete: true)
            .Select(e => e.ComponentGuid.ToString("D")[^2..]).ToArray();

        // "Tie" < "tie" ordinally; equal names break by GUID; then non-core, then obscure,
        // then lower exposure level, then obsolete last.
        Assert.Equal(new[] { "09", "0a", "0b", "0c", "0d", "0e", "0f" }, guids);
    }

    [Fact]
    public async Task HiddenAndObsolete_AreExcludedUnlessRequested()
    {
        var hidden = TestCatalog.Instance.All.First(e => e.Exposure == "hidden" && !e.Obsolete);
        var obsolete = TestCatalog.Instance.All.First(e => e.Obsolete);

        foreach (var entry in new[] { hidden, obsolete })
        {
            var guid = entry.ComponentGuid.ToString("D");
            var byDefault = await Find(new JObject { ["query"] = entry.Name, ["limit"] = 20 });
            Assert.DoesNotContain(guid, Guids(byDefault));

            var included = await Find(new JObject { ["query"] = entry.Name, ["limit"] = 20, ["includeObsolete"] = true });
            Assert.Contains(guid, Guids(included));
        }
    }

    [Fact]
    public async Task Candidates_HaveTheResultShape_AndBoundedDescriptions()
    {
        var result = await Find(new JObject { ["query"] = "a", ["limit"] = 20, ["includeObsolete"] = true });

        foreach (JObject candidate in (JArray)result["candidates"]!)
        {
            Assert.Equal(
                new[] { "componentGuid", "name", "nickname", "category", "subcategory", "library", "obsolete", "exposure", "description" },
                candidate.Properties().Select(p => p.Name));
            Assert.True(candidate["description"]!.ToString().Length <= GhFindComponentTool.DescriptionLength);
        }
    }

    [Fact]
    public async Task LongCatalogDescription_IsTruncatedTo160()
    {
        var catalog = new ComponentCatalogSnapshot(
            new[] { Entry("00000000-0000-0000-0000-000000000001", "Long", description: new string('x', 400)) },
            Array.Empty<LibraryEntry>());

        var result = await Find(new JObject { ["query"] = "long" }, catalog);
        var description = ((JArray)result["candidates"]!)[0]["description"]!.ToString();
        Assert.Equal(160, description.Length);
    }

    [Fact]
    public async Task NoMatch_ReturnsEmptyList()
    {
        var result = await Find(new JObject { ["query"] = "zzzqqqxxx" });
        Assert.Equal("ok", result["status"]!.Value<string>());
        Assert.Equal(0, result["count"]!.Value<int>());
    }

    [Theory]
    [InlineData(@"{}")]
    [InlineData(@"{""query"":""   ""}")]
    [InlineData(@"{""query"":5}")]
    [InlineData(@"{""query"":""slider"",""category"":7}")]
    public async Task InvalidArguments_AreErrors(string json)
    {
        var result = await Find(JObject.Parse(json));
        Assert.Equal("error", result["status"]!.Value<string>());
    }

    [Fact]
    public async Task OverlongQueryOrCategory_AreErrors()
    {
        Assert.Equal("error", (await Find(new JObject { ["query"] = new string('a', 81) }))["status"]!.ToString());
        Assert.Equal("error", (await Find(new JObject { ["query"] = "a", ["category"] = new string('c', 41) }))["status"]!.ToString());
    }

    [Fact]
    public void Definition_MatchesTheSpecSchema()
    {
        var def = GhFindComponentTool.Definition;
        Assert.Equal("gh_find_component", def.Name);
        Assert.False(def.IsReversible);
        var schema = def.InputSchema;
        Assert.False(schema["additionalProperties"]!.Value<bool>());
        Assert.Equal(new[] { "query" }, schema["required"]!.Values<string>());
        Assert.Equal(new[] { "query", "category", "includeObsolete", "limit" },
            ((JObject)schema["properties"]!).Properties().Select(p => p.Name));
        Assert.Equal(80, schema["properties"]!["query"]!["maxLength"]!.Value<int>());
        Assert.Equal(20, schema["properties"]!["limit"]!["maximum"]!.Value<int>());
    }

    [Theory]
    [InlineData("primary", 2)]
    [InlineData("primary, obscure", 3)]
    [InlineData("secondary", 4)]
    [InlineData("quarternary, obscure", 9)]
    [InlineData("last, obscure", 19)]
    [InlineData("hidden", 1000)]
    public void ExposureRank_OrdersPanelLevels(string exposure, int rank)
    {
        Assert.Equal(rank, GhFindComponentTool.ExposureRank(exposure));
    }
}

