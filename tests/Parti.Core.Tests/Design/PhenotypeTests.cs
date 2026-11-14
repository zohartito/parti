// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using System.Collections.Generic;
using Parti.Core.Design;
using Xunit;

namespace Parti.Core.Tests.Design;

public class PhenotypeTests
{
    [Fact]
    public void Construction_AssignsAllProperties()
    {
        var metrics = new Dictionary<string, double> { ["glare"] = 42.0 };
        var genomes = new Dictionary<string, double> { ["depth"] = 0.25 };
        var p = new Phenotype(
            Rank: 0,
            MetricValues: metrics,
            GenomeValues: genomes,
            ViewportScreenshotPath: @"C:\tmp\shot.png");
        Assert.Equal(0, p.Rank);
        Assert.Equal(42.0, p.MetricValues["glare"]);
        Assert.Equal(0.25, p.GenomeValues["depth"]);
        Assert.Equal(@"C:\tmp\shot.png", p.ViewportScreenshotPath);
    }

    [Fact]
    public void ViewportScreenshotPath_DefaultsToNull()
    {
        var p = new Phenotype(
            Rank: 3,
            MetricValues: new Dictionary<string, double>(),
            GenomeValues: new Dictionary<string, double>());
        Assert.Null(p.ViewportScreenshotPath);
    }

    [Fact]
    public void Phenotype_RecordEqualityByValue()
    {
        var metrics = new Dictionary<string, double> { ["glare"] = 42.0 };
        var genomes = new Dictionary<string, double> { ["depth"] = 0.25 };
        var a = new Phenotype(0, metrics, genomes, "shot.png");
        var b = new Phenotype(0, metrics, genomes, "shot.png");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Phenotype_ExposesDictionariesAsReadOnly()
    {
        var p = new Phenotype(
            Rank: 1,
            MetricValues: new Dictionary<string, double> { ["m"] = 1.0 },
            GenomeValues: new Dictionary<string, double> { ["g"] = 2.0 });
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, double>>(p.MetricValues);
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, double>>(p.GenomeValues);
    }
}
