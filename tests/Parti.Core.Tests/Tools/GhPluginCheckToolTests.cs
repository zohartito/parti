// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Core.Tests.Tools;

public class GhPluginCheckToolTests : System.IDisposable
{
    private readonly string _fixtureRoot;

    public GhPluginCheckToolTests()
    {
        _fixtureRoot = Path.Combine(Path.GetTempPath(), "parti-ghplugin-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_fixtureRoot);

        // Fake Libraries root with a .gha and a .ghpy
        var libs = Path.Combine(_fixtureRoot, "libs");
        Directory.CreateDirectory(libs);
        File.WriteAllText(Path.Combine(libs, "Kangaroo2.gha"), "fake");
        File.WriteAllText(Path.Combine(libs, "LunchBox.gha"), "fake");
        File.WriteAllText(Path.Combine(libs, "HelloWorld.ghpy"), "fake");

        // Fake Yak packages root with a versioned package directory.
        var yak = Path.Combine(_fixtureRoot, "yak");
        Directory.CreateDirectory(Path.Combine(yak, "ladybug", "1.8.0"));
        Directory.CreateDirectory(Path.Combine(yak, "anemone", "0.4.0"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_fixtureRoot, recursive: true); } catch { }
    }

    [Fact]
    public void Scan_FindsGhaGhpyAndYakPackages()
    {
        var plugins = GhPluginScanner.Scan(new[]
        {
            Path.Combine(_fixtureRoot, "libs"),
            Path.Combine(_fixtureRoot, "yak"),
        });

        Assert.Contains(plugins, p => p.Name == "Kangaroo2" && p.Kind == "gha");
        Assert.Contains(plugins, p => p.Name == "LunchBox" && p.Kind == "gha");
        Assert.Contains(plugins, p => p.Name == "HelloWorld" && p.Kind == "ghpy");
        Assert.Contains(plugins, p => p.Name == "ladybug" && p.Kind == "yak" && p.Version == "1.8.0");
        Assert.Contains(plugins, p => p.Name == "anemone" && p.Kind == "yak" && p.Version == "0.4.0");
    }

    [Fact]
    public void Scan_SkipsMissingRootsSilently()
    {
        var plugins = GhPluginScanner.Scan(new[]
        {
            Path.Combine(_fixtureRoot, "nope"),
        });
        Assert.Empty(plugins);
    }

    [Fact]
    public void Scan_DeduplicatesAcrossRoots()
    {
        var libsDup = Path.Combine(_fixtureRoot, "libs-dup");
        Directory.CreateDirectory(libsDup);
        File.WriteAllText(Path.Combine(libsDup, "Kangaroo2.gha"), "fake");
        var plugins = GhPluginScanner.Scan(new[]
        {
            Path.Combine(_fixtureRoot, "libs"),
            libsDup,
        });
        Assert.Single(plugins, p => p.Name == "Kangaroo2" && p.Kind == "gha");
    }

    [Fact]
    public async System.Threading.Tasks.Task Tool_ReturnsJsonArrayOfPlugins()
    {
        var handler = GhPluginCheckTool.HandlerFor(new[]
        {
            Path.Combine(_fixtureRoot, "libs"),
        });
        var result = await handler(new JObject(), CancellationToken.None);

        Assert.Equal("ok", result["status"]!.Value<string>());
        Assert.Equal(3, result["count"]!.Value<int>());
        var arr = (JArray)result["plugins"]!;
        Assert.Equal(3, arr.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Tool_FilterIsCaseInsensitive()
    {
        var handler = GhPluginCheckTool.HandlerFor(new[]
        {
            Path.Combine(_fixtureRoot, "libs"),
        });
        var result = await handler(new JObject { ["filter"] = "KANG" }, CancellationToken.None);
        Assert.Equal(1, result["count"]!.Value<int>());
        Assert.Equal("Kangaroo2", ((JArray)result["plugins"]!)[0]["name"]!.Value<string>());
    }

    [Fact]
    public void Definition_IsNonReversible()
    {
        Assert.False(GhPluginCheckTool.Definition.IsReversible);
        Assert.Equal("gh_plugin_check", GhPluginCheckTool.Definition.Name);
    }

    [Fact]
    public void Definition_MatchesTheSpecSchema()
    {
        var schema = GhPluginCheckTool.Definition.InputSchema;
        Assert.False(schema["additionalProperties"]!.Value<bool>());
        Assert.Equal(new[] { "filter" }, ((JObject)schema["properties"]!).Properties().Select(p => p.Name));
        Assert.Equal(64, schema["properties"]!["filter"]!["maxLength"]!.Value<int>());
        Assert.Null(schema["required"]);
    }

    [Fact]
    public async System.Threading.Tasks.Task PluginCheck_ListsCatalogLibraries()
    {
        var result = await GhPluginCheckTool.HandlerFor(TestCatalog.Instance)(new JObject(), CancellationToken.None);

        Assert.Equal("ok", result["status"]!.Value<string>());
        var plugins = ((JArray)result["plugins"]!).Cast<JObject>().ToList();
        Assert.Equal(plugins.Count, result["count"]!.Value<int>());
        Assert.Contains(plugins, p => p["name"]!.ToString() == "Grasshopper" && p["core"]!.Value<bool>());
        Assert.Contains(plugins, p => p["name"]!.ToString() == "LunchBox" && !p["core"]!.Value<bool>());
        Assert.DoesNotContain(plugins, p => p["name"]!.ToString().Length == 0);
        Assert.All(plugins, p => Assert.Equal(new[] { "name", "version", "author", "core" }, p.Properties().Select(x => x.Name)));
    }

    [Fact]
    public async System.Threading.Tasks.Task PluginCheck_OutputHasNoFilesystemPaths()
    {
        var catalog = new ComponentCatalogSnapshot(
            System.Array.Empty<ComponentCatalogEntry>(),
            TestCatalog.Instance.Libraries().Concat(new[]
            {
                new LibraryEntry("/Users/someone/Grasshopper/Libraries/Secret.gha", "1.0", @"C:\Users\someone\AppData\Author", false),
                new LibraryEntry("/Users/someone/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper/Libraries/Long.gha", "", "", false),
            }));

        var result = await GhPluginCheckTool.HandlerFor(catalog)(new JObject(), CancellationToken.None);
        var text = result.ToString(Newtonsoft.Json.Formatting.None);

        Assert.DoesNotContain("/", text);
        Assert.DoesNotContain("\\\\", text);
        Assert.DoesNotContain("Users", text);
        Assert.DoesNotContain("path", text);
        Assert.Contains(((JArray)result["plugins"]!), p => p["name"]!.ToString() == "Secret.gha");
    }

    [Fact]
    public async System.Threading.Tasks.Task PluginCheck_FilterIsCaseInsensitiveAndBounded()
    {
        var handler = GhPluginCheckTool.HandlerFor(TestCatalog.Instance);

        var result = await handler(new JObject { ["filter"] = "LUNCH" }, CancellationToken.None);
        Assert.Equal(new[] { "LunchBox", "LunchBoxML" },
            ((JArray)result["plugins"]!).Select(p => p["name"]!.ToString()).OrderBy(n => n, System.StringComparer.Ordinal));

        var tooLong = await handler(new JObject { ["filter"] = new string('x', 65) }, CancellationToken.None);
        Assert.Equal("error", tooLong["status"]!.Value<string>());
    }

    [Fact]
    public void DefaultRoots_ReturnsTwoPaths()
    {
        var roots = GhPluginScanner.DefaultRoots();
        Assert.Equal(2, roots.Count);
        Assert.Contains(roots, r => r.Contains("Grasshopper"));
        Assert.Contains(roots, r => r.Contains("McNeel"));
    }
}
