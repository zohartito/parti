// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Sessions;
using Xunit;

namespace Parti.Eval.V01;

/// <summary>
/// Acceptance tests for the eval harness itself (spec §3 L4 and amendment A14): strict loading,
/// the pass rule, report determinism, that the runner drives the product controller, and the
/// hermetic canvas's fail-closed checks.
/// </summary>
public class HarnessTests
{
    private const string MinimalFixture = @"{
  ""id"": ""inline"",
  ""description"": ""inline"",
  ""userPrompt"": ""hello"",
  ""providerScript"": [ { ""stopReason"": ""end_turn"", ""interactions"": [ { ""kind"": ""text"", ""text"": ""Hi there"" } ] } ],
  ""expected"": { ""terminalStatus"": ""Idle"" }
}";

    // ---- strict loading ----

    [Fact]
    public void Loader_RejectsUnknownPredicateKey()
    {
        var json = MinimalFixture.Replace(@"""terminalStatus"": ""Idle""", @"""terminalStatus"": ""Idle"", ""toolCalsInclude"": [""gh_get_all""]");
        var ex = Assert.Throws<InvalidOperationException>(() => FixtureLoader.Parse(json));
        Assert.Contains("toolCalsInclude", ex.Message);
    }

    [Fact]
    public void Loader_RejectsUnknownKeyAnywhere()
    {
        var topLevel = MinimalFixture.Replace(@"""userPrompt"": ""hello"",", @"""userPrompt"": ""hello"", ""sessionOption"": {},");
        Assert.Throws<InvalidOperationException>(() => FixtureLoader.Parse(topLevel));

        var inBlock = MinimalFixture.Replace(@"""text"": ""Hi there""", @"""text"": ""Hi there"", ""args"": {}");
        Assert.Throws<InvalidOperationException>(() => FixtureLoader.Parse(inBlock));
    }

    [Fact]
    public void Loader_RejectsCaseVariantAndDuplicateKeys()
    {
        // Newtonsoft binds keys case-insensitively and lets a later duplicate win; the loader does not.
        var caseVariant = MinimalFixture.Replace(@"""terminalStatus""", @"""TerminalStatus""");
        Assert.Contains("TerminalStatus", Assert.Throws<InvalidOperationException>(() => FixtureLoader.Parse(caseVariant)).Message);

        var duplicate = MinimalFixture.Replace(@"""userPrompt"": ""hello"",", @"""userPrompt"": ""hello"", ""userPrompt"": ""again"",");
        Assert.Throws<InvalidOperationException>(() => FixtureLoader.Parse(duplicate));
    }

    [Fact]
    public void Loader_RejectsEmptyPredicateOperands()
    {
        var emptyMetric = FixtureLoader.Parse(MinimalFixture);
        emptyMetric.Expected.EvaluationsInclude = new() { new FixtureEvaluationExpectation { Direction = "KEEP" } };
        Assert.Throws<InvalidOperationException>(() => FixtureLoader.Validate(emptyMetric));

        var emptyAny = FixtureLoader.Parse(MinimalFixture);
        emptyAny.Expected.FinalTextContainsAny = new();
        Assert.Throws<InvalidOperationException>(() => FixtureLoader.Validate(emptyAny));
    }

    [Theory]
    [InlineData("providerScript")]
    [InlineData("toolHandlers")]
    [InlineData("toolCallsMaxCount")]
    [InlineData("toolCallsInclude")]
    public void Loader_RejectsUnregisteredToolName(string where)
    {
        var fixture = FixtureLoader.Parse(MinimalFixture);
        switch (where)
        {
            case "providerScript":
                fixture.ProviderScript.Insert(0, new FixtureProviderStep
                {
                    StopReason = "tool_use",
                    Interactions = { new FixtureInteraction { Kind = "tool_call", ToolName = "gh_undo", Input = new JObject() } },
                });
                break;
            case "toolHandlers":
                fixture.ToolHandlers["gh_galapagos_setup"] = new FixtureToolHandler { Result = new JObject() };
                break;
            case "toolCallsMaxCount":
                fixture.Expected.ToolCallsMaxCount = new() { ["loop_tool"] = 0 };
                break;
            case "toolCallsInclude":
                fixture.Expected.ToolCallsInclude = new() { "gh_octopus_setup" };
                break;
        }

        var ex = Assert.Throws<InvalidOperationException>(() => FixtureLoader.Validate(fixture));
        Assert.Contains("not registered in the product tool set", ex.Message);
    }

    [Fact]
    public void Loader_UnknownToolOptOut_IsExplicitAndOnlyForUnknownTools()
    {
        var unknown = FixtureLoader.Load(Path.Combine(FixtureLoader.FixturesDirectory(), "09_unknown_tool.json"));
        Assert.True(unknown.ExpectUnknownTool);

        var misuse = FixtureLoader.Parse(MinimalFixture);
        misuse.ExpectUnknownTool = true;
        Assert.Throws<InvalidOperationException>(() => FixtureLoader.Validate(misuse));
    }

    [Fact]
    public void Loader_RequiresExpectedTerminalStatus()
    {
        var fixture = FixtureLoader.Parse(MinimalFixture.Replace(@"""terminalStatus"": ""Idle""", @"""finalStatus"": ""Finished"""));
        var ex = Assert.Throws<InvalidOperationException>(() => FixtureLoader.Validate(fixture));
        Assert.Contains("terminalStatus", ex.Message);

        var alternatives = FixtureLoader.Parse(MinimalFixture.Replace(@"""Idle""", @"""Idle|Paused"""));
        Assert.Throws<InvalidOperationException>(() => FixtureLoader.Validate(alternatives)); // scripted fixtures state one status
    }

    [Fact]
    public void Loader_CanvasPredicatesNeedHermeticCanvas()
    {
        var fixture = FixtureLoader.Parse(MinimalFixture);
        fixture.Expected.CanvasUnchanged = true;
        Assert.Throws<InvalidOperationException>(() => FixtureLoader.Validate(fixture));
    }

    private const string SliderGuid = "57da07bd-ecab-415d-9d86-af36d7073abc";
    private const string PanelGuid = "59e0b89a-e487-49f8-bab8-b5bab16be14c";
    private const string CircleGuid = "807b86e3-be8d-4970-92b5-f8cdcb45b06b";
    private const string CircleCnrGuid = "d114323a-e6ee-4164-946b-e4ca0ce15efa";

    private static EvalFixture StagedBuild() =>
        FixtureLoader.Load(Path.Combine(FixtureLoader.FixturesDirectory(), "49_staged_build_adds_then_wires.json"));

    [Fact]
    public void Loader_CanvasHasComponentAnyOf_IsExclusiveAndCatalogChecked()
    {
        var both = StagedBuild();
        both.Expected.CanvasHasComponent = new() { new() { ComponentGuid = CircleGuid, AnyOf = new() { CircleGuid, CircleCnrGuid } } };
        Assert.Contains("not both", Assert.Throws<InvalidOperationException>(() => FixtureLoader.Validate(both)).Message);

        var single = StagedBuild();
        single.Expected.CanvasHasComponent = new() { new() { AnyOf = new() { CircleGuid } } };
        Assert.Throws<InvalidOperationException>(() => FixtureLoader.Validate(single));

        var unknown = StagedBuild();
        unknown.Expected.CanvasHasComponent = new() { new() { AnyOf = new() { CircleGuid, Guid.NewGuid().ToString() } } };
        Assert.Contains("not a component GUID in the catalog", Assert.Throws<InvalidOperationException>(() => FixtureLoader.Validate(unknown)).Message);

        var valid = StagedBuild();
        valid.Expected.CanvasHasComponent = new() { new() { AnyOf = new() { CircleGuid, CircleCnrGuid } } };
        FixtureLoader.Validate(valid);
    }

    [Fact]
    public async Task CanvasHasComponentAnyOf_CountsEveryListedType()
    {
        var fixture = StagedBuild();
        fixture.Expected.CanvasHasComponent = new() { new() { AnyOf = new() { SliderGuid, PanelGuid }, Min = 12, Max = 12 } };
        Assert.Empty(PredicateEvaluator.Evaluate(await FixtureRunner.RunAsync(fixture)));

        fixture.Expected.CanvasHasComponent = new() { new() { AnyOf = new() { CircleGuid, CircleCnrGuid } } };
        var failure = Assert.Single(PredicateEvaluator.Evaluate(await FixtureRunner.RunAsync(fixture)));
        Assert.Equal("canvasHasComponent", failure.PredicateName);
        Assert.Contains($"{CircleGuid} or {CircleCnrGuid}", failure.Expected);
    }

    [Fact]
    public async Task CanvasHasWireToAnyOf_MatchesAnyListedTarget()
    {
        var both = StagedBuild();
        both.Expected.CanvasHasWire = new() { new() { From = SliderGuid, To = PanelGuid, ToAnyOf = new() { PanelGuid, CircleGuid } } };
        Assert.Contains("not both", Assert.Throws<InvalidOperationException>(() => FixtureLoader.Validate(both)).Message);

        var fixture = StagedBuild();
        fixture.Expected.CanvasHasWire = new() { new() { From = SliderGuid, ToAnyOf = new() { CircleGuid, PanelGuid } } };
        FixtureLoader.Validate(fixture);
        Assert.Empty(PredicateEvaluator.Evaluate(await FixtureRunner.RunAsync(fixture)));

        fixture.Expected.CanvasHasWire = new() { new() { From = SliderGuid, ToAnyOf = new() { CircleGuid, CircleCnrGuid } } };
        var failure = Assert.Single(PredicateEvaluator.Evaluate(await FixtureRunner.RunAsync(fixture)));
        Assert.Equal("canvasHasWire", failure.PredicateName);
        Assert.Contains($"{CircleGuid} or {CircleCnrGuid}", failure.Expected);
    }

    [Fact]
    public void GatedFixtures_ExcludeDeferredAndLive()
    {
        var gated = FixtureLoader.DiscoverFixtureFiles();
        Assert.All(gated, path => Assert.Equal(FixtureLoader.FixturesDirectory(), Path.GetDirectoryName(path)));
        Assert.DoesNotContain(gated, path => Path.GetFileName(path).StartsWith("26_", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(FixtureLoader.FixturesDirectory(), "deferred", "26_galapagos_setup_happy.json")));
    }

    // ---- A14: the pass rule ----

    [Fact]
    public async Task Reporter_PassRequiresPredicates()
    {
        // The loop finishes cleanly (Idle / Finished) but an expectation fails: that is a FAIL.
        var fixture = FixtureLoader.Parse(MinimalFixture);
        fixture.Expected.TextContains = "never said";
        var row = EvalReporter.Classify(await FixtureRunner.RunAsync(fixture));
        Assert.Equal("Idle / Finished", row.Runtime);
        Assert.Equal("FAIL", row.Result);
        Assert.Contains("textContains", row.FailedPredicates);

        // The loop ends in Error, as the fixture expects: that is a pass.
        var capRow = EvalReporter.Classify(await FixtureRunner.RunAsync(
            FixtureLoader.Load(Path.Combine(FixtureLoader.FixturesDirectory(), "25_error_repair_cap.json"))));
        Assert.Equal("Error / Error", capRow.Runtime);
        Assert.Equal("pass", capRow.Result);
    }

    [Fact]
    public async Task TerminalStatusMismatch_FailsTheFixture()
    {
        var fixture = FixtureLoader.Load(Path.Combine(FixtureLoader.FixturesDirectory(), "22_ask_user_clarify.json"));
        fixture.Expected.TerminalStatus = "Idle";
        var failures = PredicateEvaluator.Evaluate(await FixtureRunner.RunAsync(fixture));
        Assert.Equal("terminalStatus", Assert.Single(failures).PredicateName);
    }

    [Fact]
    public async Task ReportIsByteStableAcrossTwoRuns()
    {
        var first = await EvalReporter.BuildReportAsync();
        var second = await EvalReporter.BuildReportAsync();
        Assert.Equal(first, second);
        Assert.DoesNotContain("duration", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" ms ", first, StringComparison.Ordinal);
    }

    // ---- the runner drives the product ----

    [Fact]
    public async Task Runner_SendsTheShippedSkillBundlePrompt_NotTheFallback()
    {
        // Every eval run (hermetic and live) must measure the prompt the product ships.
        var run = await FixtureRunner.RunAsync(StagedBuild());
        var system = JObject.Parse(run.Provider.RequestSnapshots.First())["system"]!.ToString();

        Assert.NotEqual(Parti.Core.Grasshopper.Chat.PartiComposition.FallbackSystemPrompt, system);
        Assert.Contains("# parti-foundations", system);
        Assert.Contains("### Build in steps", system);
        Assert.Contains("# parti-ghcanvas", system); // keyword-routed Layer 1 for a build request
    }

    [Fact]
    public async Task Runner_DrivesChatController_NotBareSession()
    {
        var run = await FixtureRunner.RunAsync(FixtureLoader.Load(Path.Combine(FixtureLoader.FixturesDirectory(), "02_add_slider.json")));

        // The view saw what the chat window would: the user message, streamed deltas, busy
        // transitions, and the decomposition card.
        Assert.Equal(new[] { run.Fixture.UserPrompt }, run.View.UserMessages);
        Assert.NotEmpty(run.View.AssistantDeltas);
        Assert.Contains("Added a Number Slider", run.View.AssistantTranscript);
        Assert.True(run.View.BusyTransitions >= 2);
        Assert.Single(run.View.Decompositions);
        Assert.Empty(run.View.Errors);

        // The product envelope opened, attached the lease to the approved dispatch copy only,
        // and kept the undo record because a mutation was attempted.
        Assert.Equal(1, run.Envelopes.Begun);
        Assert.Equal(1, run.Envelopes.LeasedCalls);
        Assert.Equal(new[] { true }, run.Envelopes.Completions);
        Assert.Empty(run.InvariantFailures);
        Assert.Equal(SessionStatus.Idle, run.TerminalStatus);
    }

    [Fact]
    public async Task Runner_UsesStrictDeclaredArguments()
    {
        // A bare session is lenient about undeclared arguments; the product composition is not.
        var run = await FixtureRunner.RunAsync(FixtureLoader.Load(
            Path.Combine(FixtureLoader.FixturesDirectory(), "47_conflicting_type_and_component_guid_rejected.json")));
        var messages = run.History.OfType<AIInteractionToolResult>().Select(r => (string?)r.Result?["message"] ?? "").ToList();
        Assert.Contains(messages, m => m.Contains("does not declare the argument 'nickname'", StringComparison.Ordinal));
        Assert.Equal(0, run.ToolDispatchCount);
    }

    [Fact]
    public async Task LeaseNeverLeavesDispatch_AndIsChecked()
    {
        var run = await FixtureRunner.RunAsync(FixtureLoader.Load(Path.Combine(FixtureLoader.FixturesDirectory(), "11_add_connect.json")));
        Assert.Equal(3, run.Envelopes.LeasedCalls);
        Assert.Empty(run.InvariantFailures);
        var history = JsonConvert.SerializeObject(RequestSnapshot.Interactions(run.History.Cast<IAIInteraction>()));
        Assert.DoesNotContain(EvalToolSet.LeaseArgument, history, StringComparison.Ordinal);
        Assert.All(run.Provider.RequestSnapshots, r => Assert.DoesNotContain(EvalToolSet.LeaseArgument, r, StringComparison.Ordinal));
    }

    // ---- hermetic canvas ----

    [Fact]
    public void HermeticCanvas_RejectsUnknownComponentGuid()
    {
        var leases = new LeaseUndoScopeFactory();
        using var scope = (LeaseUndoScope)leases.Begin("test");
        var canvas = new HermeticCanvas(FixtureLoader.Catalog, null, leases.IsLive, () => null);

        var unknown = canvas.Execute("gh_add_component", new JObject
        {
            ["componentGuid"] = "00000000-1111-4222-8333-444444444444",
            [EvalToolSet.LeaseArgument] = scope.DocumentLeaseToken,
        });
        Assert.Equal("error", (string?)unknown["status"]);
        Assert.Contains("Unknown component type GUID", (string?)unknown["message"]);
        Assert.True(canvas.Unchanged);

        var known = canvas.Execute("gh_add_component", new JObject
        {
            ["componentGuid"] = HermeticCanvas.NumberSliderGuid,
            [EvalToolSet.LeaseArgument] = scope.DocumentLeaseToken,
        });
        Assert.Equal("success", (string?)known["status"]);
        Assert.Equal("Number Slider", (string?)known["canonicalName"]);
        Assert.Single(canvas.Components);
    }

    [Fact]
    public void HermeticCanvas_FailsClosedWithoutALiveLease()
    {
        var leases = new LeaseUndoScopeFactory();
        var scope = (LeaseUndoScope)leases.Begin("test");
        var canvas = new HermeticCanvas(FixtureLoader.Catalog, null, leases.IsLive, () => null);
        var args = new JObject { ["componentGuid"] = HermeticCanvas.NumberSliderGuid };

        Assert.Contains("lease", (string?)canvas.Execute("gh_add_component", args)["message"]);
        scope.Dispose();
        args[EvalToolSet.LeaseArgument] = scope.DocumentLeaseToken; // expired
        Assert.Contains("lease", (string?)canvas.Execute("gh_add_component", args)["message"]);
        Assert.True(canvas.Unchanged);
    }

    [Fact]
    public async Task PluginFixtures_MatchTheCatalogSnapshot()
    {
        // 23_plugin_check and live brief L07 assume Weaverbird is absent. Kangaroo2, Ladybug and
        // LunchBox ship in the Rhino 8 snapshot, so they cannot stand in for a missing plugin.
        var handler = Parti.Core.Tools.GhPluginCheckTool.HandlerFor(FixtureLoader.Catalog);
        var weaverbird = await handler(new JObject { ["filter"] = "weaverbird" }, default);
        var kangaroo = await handler(new JObject { ["filter"] = "kangaroo" }, default);
        Assert.Equal(0, (int)weaverbird["count"]!);
        Assert.Equal(1, (int)kangaroo["count"]!);
    }

    [Fact]
    public void HermeticCanvas_NeverReusesASeededInstanceGuid()
    {
        var leases = new LeaseUndoScopeFactory();
        using var scope = (LeaseUndoScope)leases.Begin("test");
        var seeded = new FixtureCanvasSeed
        {
            Components = { new FixtureSeedComponent { InstanceGuid = "c0000000-0000-4000-8000-000000000001", ComponentGuid = HermeticCanvas.PanelGuid } },
        };
        var canvas = new HermeticCanvas(FixtureLoader.Catalog, seeded, leases.IsLive, () => null);
        var added = canvas.Execute("gh_add_component", new JObject { ["componentGuid"] = HermeticCanvas.NumberSliderGuid, [EvalToolSet.LeaseArgument] = scope.DocumentLeaseToken });
        Assert.Equal("c0000000-0000-4000-8000-000000000002", (string?)added["instanceGuid"]);
    }

    [Fact]
    public void HermeticCanvas_RelayoutMovesComponentsIntoRankColumns()
    {
        var leases = new LeaseUndoScopeFactory();
        using var scope = (LeaseUndoScope)leases.Begin("test");
        var seed = new FixtureCanvasSeed
        {
            Components =
            {
                new FixtureSeedComponent { InstanceGuid = "5eed0000-0000-4000-8000-000000000001", ComponentGuid = HermeticCanvas.NumberSliderGuid, X = 400, Y = -120 },
                new FixtureSeedComponent { InstanceGuid = "5eed0000-0000-4000-8000-000000000002", ComponentGuid = HermeticCanvas.PanelGuid, X = 0, Y = 0 },
            },
            Wires = { new FixtureSeedWire { From = "5eed0000-0000-4000-8000-000000000001", To = "5eed0000-0000-4000-8000-000000000002" } },
        };
        var canvas = new HermeticCanvas(FixtureLoader.Catalog, seed, leases.IsLive, () => null);
        var result = canvas.Execute("gh_relayout", new JObject
        {
            ["root"] = "5eed0000-0000-4000-8000-000000000001",
            ["spacing"] = new JObject { ["x"] = 200, ["y"] = 80 },
            [EvalToolSet.LeaseArgument] = scope.DocumentLeaseToken,
        });
        Assert.Equal(1, (int)result["moved"]!);
        Assert.False(canvas.Unchanged);
        Assert.Equal((400d, -120d), (canvas.Components[0].X, canvas.Components[0].Y)); // the root stays put
        Assert.Equal((600d, -120d), (canvas.Components[1].X, canvas.Components[1].Y));
    }

    [Fact]
    public void HermeticCanvas_SliderRangeMatchesTheRealPrimitive()
    {
        var leases = new LeaseUndoScopeFactory();
        using var scope = (LeaseUndoScope)leases.Begin("test");
        var canvas = new HermeticCanvas(FixtureLoader.Catalog, null, leases.IsLive, () => null);
        var lease = scope.DocumentLeaseToken;
        var id = (string)canvas.Execute("gh_add_component", new JObject { ["componentGuid"] = HermeticCanvas.NumberSliderGuid, [EvalToolSet.LeaseArgument] = lease })["instanceGuid"]!;

        var outside = canvas.Execute("gh_set_input", new JObject { ["target"] = id, ["value"] = 7.5, [EvalToolSet.LeaseArgument] = lease });
        Assert.Equal("error", (string?)outside["status"]);
        Assert.Equal(0.5, canvas.Components[0].Value!.Value<double>());

        var widened = canvas.Execute("gh_set_input", new JObject { ["target"] = id, ["value"] = 7.5, ["max"] = 10, [EvalToolSet.LeaseArgument] = lease });
        Assert.Equal("success", (string?)widened["status"]);
        Assert.Equal(7.5, widened["value"]!.Value<double>());

        var emptyDomain = canvas.Execute("gh_set_input", new JObject { ["target"] = id, ["value"] = 3, ["min"] = 3, ["max"] = 3, [EvalToolSet.LeaseArgument] = lease });
        Assert.Contains("min < max", (string?)emptyDomain["message"]);
    }
}
