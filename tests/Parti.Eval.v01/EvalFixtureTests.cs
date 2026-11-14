// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Parti.Eval.V01;

/// <summary>
/// Data-driven xUnit harness: every *.json directly under fixtures/ is loaded strictly and run
/// through the product loop (<see cref="FixtureRunner"/>: <c>PartiComposition</c> →
/// <c>ChatController</c>). The expectations block in the fixture drives the assertions; the
/// deferred/ and live/ subfolders are not gated.
///
/// <para>
/// Assertions are mediated by <see cref="PredicateEvaluator"/>, which collects every predicate
/// and invariant violation into one list, so a failure surfaces one message per failing
/// predicate.
/// </para>
/// </summary>
public class EvalFixtureTests
{
    public static IEnumerable<object[]> FixturePaths()
    {
        var files = FixtureLoader.DiscoverFixtureFiles();
        foreach (var file in files)
        {
            yield return new object[] { Path.GetFileName(file) };
        }
    }

    [Theory]
    [MemberData(nameof(FixturePaths))]
    public async Task Fixture_MatchesExpectations(string fixtureFileName)
    {
        var path = Path.Combine(FixtureLoader.FixturesDirectory(), fixtureFileName);
        var fixture = FixtureLoader.Load(path);

        var run = await FixtureRunner.RunAsync(fixture);
        var failures = PredicateEvaluator.Evaluate(run);

        Assert.True(
            failures.Count == 0,
            $"Fixture '{fixture.Id}' failed {failures.Count} predicate(s):\n  - " +
                string.Join("\n  - ", failures.Select(f => f.ToString())));
    }

    [Fact]
    public void Fixtures_Directory_Is_NonEmpty()
    {
        var files = FixtureLoader.DiscoverFixtureFiles();
        Assert.NotEmpty(files);
    }
}
