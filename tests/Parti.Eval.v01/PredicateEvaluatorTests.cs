// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Parti.Eval.V01;

/// <summary>
/// Hermetic tests for the v0.2 predicate vocabulary. Each predicate shape is
/// exercised with both a passing fixture and a failing fixture, so a regression
/// that silently stops enforcing a predicate surfaces as a failing test rather
/// than as a quietly-green suite.
///
/// <para>
/// Tests are written against the in-memory <see cref="EvalFixture"/> object model
/// rather than JSON-on-disk to keep the predicate surface decoupled from the
/// fixture loader. The fixtures directory exercises the loader end-to-end.
/// </para>
/// </summary>
public class PredicateEvaluatorTests
{
    // ---- toolCallsInclude ----

    [Fact]
    public async Task ToolCallsInclude_Passes_WhenAllToolsCalled()
    {
        var fixture = AddAndConnectFixture(new FixtureExpected
        {
            ToolCallsInclude = new List<string> { "gh_add_component", "gh_connect" },
        });
        var failures = await EvaluateAsync(fixture);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task ToolCallsInclude_Fails_WhenRequiredToolMissing()
    {
        var fixture = AddAndConnectFixture(new FixtureExpected
        {
            ToolCallsInclude = new List<string> { "gh_add_component", "gh_never_called" },
        });
        var failures = await EvaluateAsync(fixture);
        Assert.Single(failures);
        Assert.Equal("toolCallsInclude", failures[0].PredicateName);
        Assert.Contains("gh_never_called", failures[0].Actual);
    }

    // ---- toolCallsExclude ----

    [Fact]
    public async Task ToolCallsExclude_Passes_WhenForbiddenToolAbsent()
    {
        var fixture = AddAndConnectFixture(new FixtureExpected
        {
            ToolCallsExclude = new List<string> { "gh_delete" },
        });
        var failures = await EvaluateAsync(fixture);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task ToolCallsExclude_Fails_WhenForbiddenToolPresent()
    {
        var fixture = AddAndConnectFixture(new FixtureExpected
        {
            ToolCallsExclude = new List<string> { "gh_connect" },
        });
        var failures = await EvaluateAsync(fixture);
        Assert.Single(failures);
        Assert.Equal("toolCallsExclude", failures[0].PredicateName);
        Assert.Contains("gh_connect", failures[0].Actual);
    }

    // ---- toolCallsMinCount ----

    [Fact]
    public async Task ToolCallsMinCount_Passes_WhenCountsMet()
    {
        var fixture = AddAndConnectFixture(new FixtureExpected
        {
            ToolCallsMinCount = new Dictionary<string, int>
            {
                { "gh_add_component", 2 },
                { "gh_connect", 1 },
            },
        });
        var failures = await EvaluateAsync(fixture);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task ToolCallsMinCount_Fails_WhenUnderCount()
    {
        var fixture = AddAndConnectFixture(new FixtureExpected
        {
            ToolCallsMinCount = new Dictionary<string, int>
            {
                { "gh_add_component", 5 }, // only 2 in the fixture
            },
        });
        var failures = await EvaluateAsync(fixture);
        Assert.Single(failures);
        Assert.Equal("toolCallsMinCount", failures[0].PredicateName);
        Assert.Contains(">= 5", failures[0].Expected);
        Assert.Contains("called 2 time(s)", failures[0].Actual);
    }

    // ---- toolCallsMaxCount ----

    [Fact]
    public async Task ToolCallsMaxCount_Passes_WhenWithinCap()
    {
        var fixture = AddAndConnectFixture(new FixtureExpected
        {
            ToolCallsMaxCount = new Dictionary<string, int>
            {
                { "gh_delete", 0 },      // never called
                { "gh_connect", 1 },     // called once
            },
        });
        var failures = await EvaluateAsync(fixture);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task ToolCallsMaxCount_Fails_WhenOverCap()
    {
        var fixture = AddAndConnectFixture(new FixtureExpected
        {
            ToolCallsMaxCount = new Dictionary<string, int>
            {
                { "gh_add_component", 1 }, // fixture calls it twice
            },
        });
        var failures = await EvaluateAsync(fixture);
        Assert.Single(failures);
        Assert.Equal("toolCallsMaxCount", failures[0].PredicateName);
        Assert.Contains("<= 1", failures[0].Expected);
        Assert.Contains("called 2 time(s)", failures[0].Actual);
    }

    // ---- finalTextContainsAny ----

    [Fact]
    public async Task FinalTextContainsAny_Passes_OnCaseInsensitiveMatch()
    {
        // The canned final text is "Wired the Slider to the panel."
        var fixture = AddAndConnectFixture(new FixtureExpected
        {
            FinalTextContainsAny = new List<string> { "sliDer", "nonexistent" },
        });
        var failures = await EvaluateAsync(fixture);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task FinalTextContainsAny_Fails_WhenNoMatch()
    {
        var fixture = AddAndConnectFixture(new FixtureExpected
        {
            FinalTextContainsAny = new List<string> { "zzz_not_there", "also_missing" },
        });
        var failures = await EvaluateAsync(fixture);
        Assert.Single(failures);
        Assert.Equal("finalTextContainsAny", failures[0].PredicateName);
    }

    // ---- finalTextContainsAll ----

    [Fact]
    public async Task FinalTextContainsAll_Passes_OnCaseInsensitiveMatch()
    {
        // Assistant texts across the run include "Adding slider and panel" and
        // "Wired the Slider to the panel." so both needles appear (case-insensitive).
        var fixture = AddAndConnectFixture(new FixtureExpected
        {
            FinalTextContainsAll = new List<string> { "adding", "wired" },
        });
        var failures = await EvaluateAsync(fixture);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task FinalTextContainsAll_Fails_WhenOneMissing()
    {
        var fixture = AddAndConnectFixture(new FixtureExpected
        {
            FinalTextContainsAll = new List<string> { "adding", "Cthulhu" },
        });
        var failures = await EvaluateAsync(fixture);
        Assert.Single(failures);
        Assert.Equal("finalTextContainsAll", failures[0].PredicateName);
        Assert.Contains("Cthulhu", failures[0].Actual);
    }

    // ---- Regression: multiple predicates in one block report all failures ----

    [Fact]
    public async Task Evaluate_ReportsEveryViolation_NotJustTheFirst()
    {
        var fixture = AddAndConnectFixture(new FixtureExpected
        {
            ToolCallsMinCount = new Dictionary<string, int> { { "gh_add_component", 5 } },
            ToolCallsMaxCount = new Dictionary<string, int> { { "gh_add_component", 1 } },
            FinalTextContainsAny = new List<string> { "xyzzy" },
        });
        var failures = await EvaluateAsync(fixture);
        Assert.Equal(3, failures.Count);
        Assert.Contains(failures, f => f.PredicateName == "toolCallsMinCount");
        Assert.Contains(failures, f => f.PredicateName == "toolCallsMaxCount");
        Assert.Contains(failures, f => f.PredicateName == "finalTextContainsAny");
    }

    // ---- Helpers ----

    /// <summary>
    /// Builds a product-protocol fixture on the hermetic canvas: the model records a complete
    /// decomposition, adds a slider and a panel by exact componentGuid in one reviewed batch, and
    /// wires them by the instance GUIDs the canvas returned. Canned text blocks let the text
    /// predicates assert substring/any/all behaviours. The <paramref name="expected"/> block is
    /// whatever the caller wants to validate; its terminal status defaults to Idle.
    /// </summary>
    private static EvalFixture AddAndConnectFixture(FixtureExpected expected)
    {
        expected.TerminalStatus ??= "Idle";
        var fixture = FixtureLoader.Parse(@"{
  ""id"": ""hermetic_add_connect"",
  ""description"": ""in-memory fixture used by PredicateEvaluatorTests"",
  ""userPrompt"": ""Add a radius slider and a panel, then wire them."",
  ""canvas"": ""hermetic"",
  ""providerScript"": [
    { ""stopReason"": ""tool_use"", ""interactions"": [
      { ""kind"": ""tool_call"", ""toolId"": ""toolu_rec"", ""toolName"": ""parti_record_decomposition"", ""input"": {
        ""inputs"": [ { ""name"": ""radius"", ""domain"": ""1..10"", ""unit"": ""m"", ""source"": ""user_stated"" } ],
        ""rules"": [ { ""statement"": ""circle at the origin"", ""source"": ""user_stated"" } ],
        ""principle"": ""one-parameter circle"",
        ""evaluations"": [ { ""metric"": ""area"", ""direction"": ""KEEP"", ""source"": ""assumed"" } ] } } ] },
    { ""stopReason"": ""tool_use"", ""interactions"": [
      { ""kind"": ""text"", ""text"": ""Adding slider and panel."" },
      { ""kind"": ""tool_call"", ""toolId"": ""toolu_a"", ""toolName"": ""gh_add_component"", ""input"": { ""componentGuid"": ""57da07bd-ecab-415d-9d86-af36d7073abc"", ""x"": 0, ""y"": 0 } },
      { ""kind"": ""tool_call"", ""toolId"": ""toolu_b"", ""toolName"": ""gh_add_component"", ""input"": { ""componentGuid"": ""59e0b89a-e487-49f8-bab8-b5bab16be14c"", ""x"": 250, ""y"": 0 } } ] },
    { ""stopReason"": ""tool_use"", ""interactions"": [
      { ""kind"": ""tool_call"", ""toolId"": ""toolu_c"", ""toolName"": ""gh_connect"", ""input"": { ""from"": ""c0000000-0000-4000-8000-000000000001"", ""to"": ""c0000000-0000-4000-8000-000000000002"" } } ] },
    { ""stopReason"": ""end_turn"", ""interactions"": [ { ""kind"": ""text"", ""text"": ""Wired the Slider to the panel."" } ] }
  ],
  ""expected"": { ""terminalStatus"": ""Idle"" }
}");
        fixture.Expected = expected;
        return fixture;
    }

    private static async Task<List<PredicateFailure>> EvaluateAsync(EvalFixture fixture)
    {
        var run = await FixtureRunner.RunAsync(fixture);
        return PredicateEvaluator.Evaluate(run);
    }
}
