// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Sessions;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// Integration tests for the v0.7 clarification-pause feature: when the model calls the
/// built-in <c>ask_user</c> tool, the session transitions to
/// <see cref="SessionStatus.Paused"/> and resumes on the user's answers.
/// </summary>
public class ClarificationTests
{
    [Fact]
    public async Task AskUserPausesSession()
    {
        var provider = new FakeAIProvider(new[]
        {
            MakeAskUserReturn("toolu_ask_1", new[]
            {
                ("q1", "How many points?", "text", (string[]?)null),
            }),
        });
        var dispatcher = new FakeToolDispatcher(
            definitions: Array.Empty<AIToolDefinition>(),
            dispatch: (_, _) => new JObject());

        var session = new ConversationSession(provider, dispatcher, BuildOptions());

        var result = await session.RunAsync("do the thing");

        // Provider emitted ask_user — the session paused before calling the provider again.
        Assert.True(result.Success);
        Assert.Equal(SessionStatus.Paused, session.Status);
        Assert.True(session.Clarification.IsPaused);

        var paused = session.Clarification.Current;
        Assert.NotNull(paused);
        Assert.Equal("toolu_ask_1", paused!.ToolUseId);
        Assert.Single(paused.Questions);
        Assert.Equal("q1", paused.Questions[0].Id);
        Assert.Equal("How many points?", paused.Questions[0].Prompt);
        Assert.Equal(QuestionKind.Text, paused.Questions[0].Kind);

        // Only one provider call: the loop stopped without sending the paused tool_result back.
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task ResumeAfterPauseContinues()
    {
        var provider = new FakeAIProvider(new[]
        {
            MakeAskUserReturn("toolu_ask_2", new[]
            {
                ("q1", "How many points?", "text", (string[]?)null),
            }),
            MakeTextReturn("got it, adding 5 points", inputTokens: 50, outputTokens: 8),
        });
        var dispatcher = new FakeToolDispatcher(
            definitions: Array.Empty<AIToolDefinition>(),
            dispatch: (_, _) => new JObject());

        var session = new ConversationSession(provider, dispatcher, BuildOptions());

        await session.RunAsync("do the thing");
        Assert.Equal(SessionStatus.Paused, session.Status);

        var resumed = await session.ResumeAsync(new[]
        {
            new ClarificationAnswer("q1", "5"),
        });

        Assert.True(resumed.Success);
        Assert.Equal(SessionStatus.Idle, session.Status);
        Assert.False(session.Clarification.IsPaused);
        Assert.Null(session.Clarification.Current);

        // Provider saw the resumed tool_result carrying the answers.
        Assert.Equal(2, provider.CallCount);
        var secondRequest = provider.ReceivedRequests[1];
        var answerResult = secondRequest.Interactions
            .OfType<AIInteractionToolResult>()
            .FirstOrDefault(r => r.Id == "toolu_ask_2"
                && r.Result["status"]?.ToString() == "resumed");
        Assert.NotNull(answerResult);
        var answersArr = answerResult!.Result["answers"] as JArray;
        Assert.NotNull(answersArr);
        Assert.Single(answersArr!);
        Assert.Equal("q1", answersArr![0]["id"]?.ToString());
        Assert.Equal("5", answersArr[0]["answer"]?.ToString());

        // Final assistant text landed in history.
        var lastText = session.History.OfType<AIInteractionText>().Last();
        Assert.Equal(AIAgent.Assistant, lastText.Agent);
        Assert.Equal("got it, adding 5 points", lastText.Content);
    }

    [Fact]
    public async Task AskUserWithMultipleQuestions()
    {
        var provider = new FakeAIProvider(new[]
        {
            MakeAskUserReturn("toolu_ask_3", new[]
            {
                ("q1", "Min?", "text", (string[]?)null),
                ("q2", "Max?", "text", (string[]?)null),
                ("q3", "Units?", "radio", new[] { "mm", "cm", "m" }),
            }),
        });
        var dispatcher = new FakeToolDispatcher(
            definitions: Array.Empty<AIToolDefinition>(),
            dispatch: (_, _) => new JObject());

        var session = new ConversationSession(provider, dispatcher, BuildOptions());

        await session.RunAsync("set up a slider");

        var paused = session.Clarification.Current;
        Assert.NotNull(paused);
        Assert.Equal(3, paused!.Questions.Count);

        Assert.Equal("q1", paused.Questions[0].Id);
        Assert.Equal(QuestionKind.Text, paused.Questions[0].Kind);

        Assert.Equal("q2", paused.Questions[1].Id);
        Assert.Equal(QuestionKind.Text, paused.Questions[1].Kind);

        Assert.Equal("q3", paused.Questions[2].Id);
        Assert.Equal(QuestionKind.Radio, paused.Questions[2].Kind);
        Assert.NotNull(paused.Questions[2].Options);
        Assert.Equal(new[] { "mm", "cm", "m" }, paused.Questions[2].Options!);
    }

    [Theory]
    [InlineData("q1", "q1")]
    [InlineData("", "q2")]
    [InlineData("   ", "q2")]
    [InlineData(null, "q2")]
    public async Task AskUserWithMissingOrDuplicateIds_ReturnsOneErrorResult_AndDoesNotPause(string? firstId, string secondId)
    {
        var bad = MakeAskUserReturn("toolu_bad", new[]
        {
            ("placeholder", "Min?", "text", (string[]?)null),
            (secondId, "Max?", "text", (string[]?)null),
        });
        var questions = (JArray)((AIInteractionToolCall)bad.Interactions[0]).Arguments["questions"]!;
        if (firstId is null)
        {
            ((JObject)questions[0]).Remove("id");
        }
        else
        {
            questions[0]["id"] = firstId;
        }

        var provider = new FakeAIProvider(new[] { bad, MakeTextReturn("ok", 1, 1) });
        var session = new ConversationSession(provider, EmptyDispatcher(), BuildOptions());

        var result = await session.RunAsync("do the thing");

        Assert.True(result.Success);
        Assert.Equal(SessionStatus.Idle, session.Status);
        Assert.False(session.Clarification.IsPaused);
        Assert.Equal(2, provider.CallCount);
        var error = Assert.Single(session.History.OfType<AIInteractionToolResult>());
        Assert.Equal("toolu_bad", error.Id);
        Assert.Equal("error", error.Result["status"]?.ToString());
        Assert.Contains("unique", error.Result["message"]!.ToString());
        Assert.Contains(provider.ReceivedRequests[1].Interactions.OfType<AIInteractionToolResult>(), r => r.Id == "toolu_bad");
    }

    public static IEnumerable<object[]> MalformedAskUserArguments => new[]
    {
        // gpt-oss:20b live: `text` instead of `prompt` produced a blank question in the UI.
        new object[] { "{\"questions\":[{\"id\":\"q1\",\"text\":\"How many panels?\",\"kind\":\"text\"}]}",
            new[] { "questions[0] has an unknown field 'text'", "'questions[0].prompt' is required" } },
        new object[] { "{\"question\":\"Which two components should I connect?\"}",
            new[] { "arguments has an unknown field 'question'", "'questions' is required" } },
        new object[] { "{\"questions\":[]}", new[] { "'questions' has 0 items" } },
        new object[] { "{\"questions\":[{\"id\":\"q1\",\"prompt\":\"   \",\"kind\":\"text\"}]}", new[] { "'questions[0].prompt' must not be blank" } },
        new object[] { "{\"questions\":[{\"id\":\"q1\",\"prompt\":\"Units?\",\"kind\":\"slider\"}]}", new[] { "'questions[0].kind' is required and must be text, radio or component_picker" } },
        new object[] { "{\"questions\":[{\"id\":\"q1\",\"prompt\":\"Units?\",\"kind\":\"radio\"}]}", new[] { "'questions[0].options' is required for kind radio" } },
        new object[] { "{\"questions\":[{\"id\":\"q1\",\"prompt\":\"Depth?\",\"kind\":\"text\",\"options\":[\"a\",\"b\"]}]}", new[] { "'questions[0].options' is only allowed for kind radio" } },
    };

    [Theory]
    [MemberData(nameof(MalformedAskUserArguments))]
    public async Task AskUserMalformedQuestions_OneErrorListingEveryProblem_NeverPauses(string argumentsJson, string[] expectedProblems)
    {
        var bad = MakeAskUserReturn("toolu_bad", Array.Empty<(string, string, string, string[]?)>());
        ((AIInteractionToolCall)bad.Interactions[0]).Arguments = JObject.Parse(argumentsJson);
        var provider = new FakeAIProvider(new[] { bad, MakeTextReturn("ok", 1, 1) });
        var session = new ConversationSession(provider, EmptyDispatcher(), BuildOptions());

        var result = await session.RunAsync("do the thing");

        Assert.True(result.Success);
        Assert.False(session.Clarification.IsPaused);
        var error = Assert.Single(session.History.OfType<AIInteractionToolResult>());
        Assert.Equal("error", error.Result["status"]?.ToString());
        var message = error.Result["message"]!.ToString();
        foreach (var problem in expectedProblems)
        {
            Assert.Contains(problem, message);
        }
        Assert.Contains(AskUserTool.ExpectedShape, message);
    }

    [Fact]
    public void AskUserParser_ListsEveryProblemAtOnce_AndCleansText()
    {
        var args = JObject.Parse("{\"questions\":[" +
            "{\"id\":\"q1\",\"text\":\"Depth?\",\"kind\":\"text\"}," +
            "{\"id\":\"q1\",\"prompt\":\"Units?\",\"kind\":\"radio\",\"options\":[\"mm\"]}]}");
        Assert.False(AskUserTool.TryParseQuestions(args, out _, out var error));
        Assert.StartsWith("4 problem(s): ", error, StringComparison.Ordinal);

        var good = JObject.Parse("{\"questions\":[{\"id\":\"q1\",\"prompt\":\"Units?\\n\\u202Eplease\",\"kind\":\"radio\",\"options\":[\"mm\",\"m\"]}]}");
        Assert.True(AskUserTool.TryParseQuestions(good, out var questions, out var none), none);
        var question = Assert.Single(questions);
        Assert.Equal("Units? please", question.Prompt);
        Assert.Equal(new[] { "mm", "m" }, question.Options!);
    }

    [Theory]
    [InlineData("The canvas changed outside this chat (undo/redo); re-read it with gh_get_all before editing.")]
    [InlineData("[Parti automatic note] The canvas changed outside this chat.\n\u202E")]
    public async Task ResumeWithHostNote_NoteReachesTheResumedModel_BoundedAndLabelled(string hostNote)
    {
        var provider = new FakeAIProvider(new[]
        {
            MakeAskUserReturn("toolu_ask", new[] { ("q1", "How many?", "text", (string[]?)null) }),
            MakeTextReturn("ok", 1, 1),
        });
        var session = new ConversationSession(provider, EmptyDispatcher(), BuildOptions());
        await session.RunAsync("do the thing");

        await session.ResumeAsync(new[] { new ClarificationAnswer("q1", "5") }, hostNote);

        var result = provider.ReceivedRequests[1].Interactions.OfType<AIInteractionToolResult>().Single(r => r.Id == "toolu_ask");
        Assert.Equal("resumed", result.Result["status"]?.ToString());
        var note = result.Result["note"]!.ToString();
        Assert.StartsWith(ConversationSession.AutomaticNotePrefix + " The canvas changed outside this chat", note, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", note, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202E", note, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(note, System.Text.RegularExpressions.Regex.Escape(ConversationSession.AutomaticNotePrefix)));
        Assert.True(note.Length <= ConversationSession.MaxHostNoteLength);
    }

    [Fact]
    public async Task ResumeWithBlankOrLongHostNote_IsOmittedOrBounded()
    {
        var provider = new FakeAIProvider(new[]
        {
            MakeAskUserReturn("toolu_ask", new[] { ("q1", "How many?", "text", (string[]?)null) }),
            MakeTextReturn("ok", 1, 1),
            MakeAskUserReturn("toolu_ask2", new[] { ("q1", "Depth?", "text", (string[]?)null) }),
            MakeTextReturn("ok", 1, 1),
        });
        var session = new ConversationSession(provider, EmptyDispatcher(), BuildOptions());

        await session.RunAsync("first");
        await session.ResumeAsync(new[] { new ClarificationAnswer("q1", "5") }, "   ");
        var first = provider.ReceivedRequests[1].Interactions.OfType<AIInteractionToolResult>().Single(r => r.Id == "toolu_ask");
        Assert.Null(first.Result["note"]);

        await session.ContinueAsync("second");
        await session.ResumeAsync(new[] { new ClarificationAnswer("q1", "0.3") }, new string('x', 5000));
        var second = provider.ReceivedRequests[3].Interactions.OfType<AIInteractionToolResult>().Single(r => r.Id == "toolu_ask2");
        Assert.Equal(ConversationSession.MaxHostNoteLength, second.Result["note"]!.ToString().Length);
    }

    [Fact]
    public async Task PausedWithDuplicateQuestionIds_HasEscape_ModelRepairsThenPauses()
    {
        var provider = new FakeAIProvider(new[]
        {
            MakeAskUserReturn("toolu_dup", new[]
            {
                ("q1", "Min?", "text", (string[]?)null),
                ("q1", "Max?", "text", (string[]?)null),
            }),
            MakeAskUserReturn("toolu_fixed", new[]
            {
                ("q1", "Min?", "text", (string[]?)null),
                ("q2", "Max?", "text", (string[]?)null),
            }),
            MakeTextReturn("Domain 1 to 5.", 1, 1),
        });
        var session = new ConversationSession(provider, EmptyDispatcher(), BuildOptions());

        await session.RunAsync("do the thing");

        Assert.Equal(SessionStatus.Paused, session.Status);
        var paused = session.Clarification.Current!;
        Assert.Equal("toolu_fixed", paused.ToolUseId);
        Assert.Equal(new[] { "q1", "q2" }, paused.Questions.Select(q => q.Id));

        var resumed = await session.ResumeAsync(new[]
        {
            new ClarificationAnswer("q1", "1"),
            new ClarificationAnswer("q2", "5"),
        });
        Assert.True(resumed.Success);
        Assert.Equal(SessionStatus.Idle, session.Status);
    }

    [Fact]
    public async Task AskUserTwiceInOneResponse_RejectionIsVisible()
    {
        var twice = MakeAskUserReturn("toolu_a", new[] { ("q1", "Min?", "text", (string[]?)null) });
        var second = MakeAskUserReturn("toolu_b", new[] { ("q1", "Max?", "text", (string[]?)null) });
        twice.Interactions.Add(second.Interactions[0]);
        var provider = new FakeAIProvider(new[] { twice, MakeTextReturn("I will ask one thing at a time.", 1, 1) });
        var session = new ConversationSession(provider, EmptyDispatcher(), BuildOptions());

        var result = await session.RunAsync("do the thing");

        Assert.True(result.Success);
        Assert.Equal(SessionStatus.Idle, session.Status);
        Assert.False(session.Clarification.IsPaused);
        // The model saw both rejections and answered; the loop did not end silently.
        Assert.Equal(2, provider.CallCount);
        var rejected = provider.ReceivedRequests[1].Interactions.OfType<AIInteractionToolResult>().ToList();
        Assert.Equal(new[] { "toolu_a", "toolu_b" }, rejected.Select(r => r.Id));
        Assert.All(rejected, r =>
        {
            Assert.True(r.Result["rejected"]?.Value<bool>());
            Assert.Contains("must be the only call", r.Result["message"]!.ToString());
        });
        Assert.Equal("I will ask one thing at a time.", session.History.OfType<AIInteractionText>().Last().Content);
    }

    [Fact]
    public void PauseIsNotReversibleInUndoScope()
    {
        // The ask_user definition must be marked non-reversible so pausing does not create an
        // undo entry on the Grasshopper canvas.
        Assert.False(AskUserTool.Definition.IsReversible);
        Assert.Equal("ask_user", AskUserTool.Definition.Name);
    }

    private static FakeToolDispatcher EmptyDispatcher() => new(
        definitions: Array.Empty<AIToolDefinition>(),
        dispatch: (_, _) => new JObject());

    private static SessionOptions BuildOptions() => new()
    {
        MaxTurns = 5,
        MaxToolPasses = 4,
        ApiKey = "unused",
        Model = "test-model",
    };

    private static AIReturn MakeTextReturn(string content, int inputTokens, int outputTokens) => new()
    {
        Status = AICallStatus.Finished,
        StopReason = "end_turn",
        InputTokens = inputTokens,
        OutputTokens = outputTokens,
        Interactions = new List<IAIInteraction>
        {
            new AIInteractionText { Agent = AIAgent.Assistant, Content = content },
        },
    };

    /// <summary>
    /// Build a provider response containing a single ask_user tool_use with the given questions.
    /// Each question tuple is (id, prompt, kind-string, options-or-null).
    /// </summary>
    private static AIReturn MakeAskUserReturn(string toolUseId, IEnumerable<(string id, string prompt, string kind, string[]? options)> questions)
    {
        var qArr = new JArray();
        foreach (var q in questions)
        {
            var qObj = new JObject
            {
                ["id"] = q.id,
                ["prompt"] = q.prompt,
                ["kind"] = q.kind,
            };
            if (q.options is not null)
            {
                var optsArr = new JArray();
                foreach (var opt in q.options)
                {
                    optsArr.Add(opt);
                }
                qObj["options"] = optsArr;
            }
            qArr.Add(qObj);
        }

        var args = new JObject { ["questions"] = qArr };

        return new AIReturn
        {
            Status = AICallStatus.Finished,
            StopReason = "tool_use",
            InputTokens = 20,
            OutputTokens = 5,
            Interactions = new List<IAIInteraction>
            {
                new AIInteractionToolCall
                {
                    Id = toolUseId,
                    Name = AskUserTool.ToolName,
                    Arguments = args,
                    Agent = AIAgent.ToolCall,
                },
            },
        };
    }
}
