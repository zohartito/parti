// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using System.Linq;
using Parti.Core.Design;
using Xunit;

namespace Parti.Core.Tests.Design;

public class TreeHygieneAnalyzerTests
{
    [Fact]
    public void EmptyChain_ProducesNoFindings()
    {
        var f = TreeHygieneAnalyzer.Analyze(System.Array.Empty<TreeOp>());
        Assert.Empty(f);
    }

    [Fact]
    public void SingleGraft_ProducesNoFindings()
    {
        var f = TreeHygieneAnalyzer.Analyze(new[] { TreeOp.Graft });
        Assert.Empty(f);
    }

    [Fact]
    public void SinglePathMapper_ProducesNoFindings()
    {
        var f = TreeHygieneAnalyzer.Analyze(new[] { TreeOp.PathMapper });
        Assert.Empty(f);
    }

    [Fact]
    public void GraftThenFlatten_FiresError()
    {
        var f = TreeHygieneAnalyzer.Analyze(new[] { TreeOp.Graft, TreeOp.Flatten });
        Assert.Contains(f, x => x.Code == "TH002-graft-then-flatten" && x.Severity == HygieneSeverity.Error);
    }

    [Fact]
    public void FlattenThenGraft_FiresWarn()
    {
        var f = TreeHygieneAnalyzer.Analyze(new[] { TreeOp.Flatten, TreeOp.Graft });
        Assert.Contains(f, x => x.Code == "TH003-flatten-then-graft" && x.Severity == HygieneSeverity.Warn);
    }

    [Fact]
    public void DoubleFlipMatrix_FiresError()
    {
        var f = TreeHygieneAnalyzer.Analyze(new[] { TreeOp.FlipMatrix, TreeOp.FlipMatrix });
        Assert.Contains(f, x => x.Code == "TH005-double-flipmatrix");
    }

    [Fact]
    public void ThreeConsecutiveTreeOps_RecommendPathMapper()
    {
        var f = TreeHygieneAnalyzer.Analyze(new[] { TreeOp.Graft, TreeOp.Shift, TreeOp.Trim });
        Assert.Contains(f, x => x.Code == "TH001-collapse-to-pathmapper" && x.Length == 3);
        var f001 = f.First(x => x.Code == "TH001-collapse-to-pathmapper");
        Assert.Equal(new[] { TreeOp.PathMapper }, f001.Suggestion);
    }

    [Fact]
    public void TwoConsecutiveTreeOps_DoNotTriggerCollapse()
    {
        var f = TreeHygieneAnalyzer.Analyze(new[] { TreeOp.Graft, TreeOp.Shift });
        Assert.DoesNotContain(f, x => x.Code == "TH001-collapse-to-pathmapper");
    }

    [Fact]
    public void DoubleFlatten_FiresWarn()
    {
        var f = TreeHygieneAnalyzer.Analyze(new[] { TreeOp.Flatten, TreeOp.Flatten });
        Assert.Contains(f, x => x.Code == "TH004-double-flatten");
    }

    [Fact]
    public void ApplySuggestions_CollapsesGraftFlatten()
    {
        var chain = new[] { TreeOp.Graft, TreeOp.Flatten };
        var findings = TreeHygieneAnalyzer.Analyze(chain);
        var result = TreeHygieneAnalyzer.ApplySuggestions(chain, findings);
        Assert.Empty(result);
    }

    [Fact]
    public void ApplySuggestions_ReplacesLongChainWithPathMapper()
    {
        var chain = new[] { TreeOp.Graft, TreeOp.Shift, TreeOp.Trim, TreeOp.FlipMatrix };
        var findings = TreeHygieneAnalyzer.Analyze(chain);
        var result = TreeHygieneAnalyzer.ApplySuggestions(chain, findings);
        Assert.Single(result);
        Assert.Equal(TreeOp.PathMapper, result[0]);
    }

    [Fact]
    public void CustomThreshold_ChangesWhenTH001Fires()
    {
        var chain = new[] { TreeOp.Graft, TreeOp.Shift };
        var findings = TreeHygieneAnalyzer.Analyze(chain, new TreeHygieneAnalyzer.Options(CollapseAfterConsecutive: 2));
        Assert.Contains(findings, x => x.Code == "TH001-collapse-to-pathmapper");
    }
}
