// Copyright (c) Zohar Tito 2026. Licensed under LGPL-3.0-or-later.

using System;
using System.Threading;
using System.Threading.Tasks;
using Parti.Core.Prompts;
using Xunit;

namespace Parti.Core.Tests.Prompts;

public class LlmSkillRouterTests
{
    private sealed class StubInvoker : ILlmInvoker
    {
        private readonly Func<string, string, string> _respond;
        public int CallCount { get; private set; }
        public string? LastSystemPrompt { get; private set; }
        public string? LastUserPrompt { get; private set; }

        public StubInvoker(Func<string, string, string> respond)
        {
            _respond = respond;
        }

        public Task<string> InvokeAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            CallCount++;
            LastSystemPrompt = systemPrompt;
            LastUserPrompt = userPrompt;
            return Task.FromResult(_respond(systemPrompt, userPrompt));
        }
    }

    private sealed class ThrowingInvoker : ILlmInvoker
    {
        public Task<string> InvokeAsync(string systemPrompt, string userPrompt, CancellationToken ct)
            => throw new InvalidOperationException("model unavailable");
    }

    [Fact]
    public void Route_ValidJson_ReturnsLlmSubSkills()
    {
        const string json = """
            {
              "sub_skills": ["parti-ghcanvas", "parti-feedback"],
              "evidence": {
                "parti-ghcanvas": ["add a slider"],
                "parti-feedback": ["maximize daylight"]
              }
            }
            """;
        var invoker = new StubInvoker((_, _) => json);
        var router = new LlmSkillRouter(invoker);

        var result = router.Route("add a slider and maximize daylight");

        Assert.Equal(new[] { "parti-ghcanvas", "parti-feedback" }, result.SubSkills);
        Assert.True(result.Evidence.ContainsKey("parti-ghcanvas"));
        Assert.Contains("add a slider", result.Evidence["parti-ghcanvas"]);
        Assert.Contains("maximize daylight", result.Evidence["parti-feedback"]);
        Assert.Equal(1, invoker.CallCount);
    }

    [Fact]
    public void Route_MalformedJson_FallsBackToKeywordRouter()
    {
        var invoker = new StubInvoker((_, _) => "not json at all {{{");
        var router = new LlmSkillRouter(invoker);

        // "add a slider" triggers parti-ghcanvas in the keyword router.
        var result = router.Route("add a slider");

        Assert.Equal("parti-ghcanvas", result.SubSkills[0]);
    }

    [Fact]
    public void Route_InvokerThrows_FallsBackToKeywordRouter()
    {
        var router = new LlmSkillRouter(new ThrowingInvoker());

        var result = router.Route("set up a Galapagos run");

        Assert.Equal("parti-optimize", result.SubSkills[0]);
    }

    [Fact]
    public void Route_UnknownSubSkill_FallsBackToKeywordRouter()
    {
        const string json = """
            {
              "sub_skills": ["parti-teleport"],
              "evidence": {"parti-teleport": ["teleport me"]}
            }
            """;
        var invoker = new StubInvoker((_, _) => json);
        var router = new LlmSkillRouter(invoker);

        // Empty-ish prompt → keyword router falls back to parti-formalize.
        var result = router.Route("   ");

        Assert.Equal("parti-formalize", result.SubSkills[0]);
    }

    [Fact]
    public void Route_EmptySubSkillsArray_FallsBackToKeywordRouter()
    {
        const string json = """{"sub_skills": [], "evidence": {}}""";
        var invoker = new StubInvoker((_, _) => json);
        var router = new LlmSkillRouter(invoker);

        var result = router.Route("add a slider");

        Assert.Equal("parti-ghcanvas", result.SubSkills[0]);
    }

    [Fact]
    public void SystemPrompt_ContainsAllSixKnownSubSkillNames()
    {
        var router = new LlmSkillRouter(new StubInvoker((_, _) => "{\"sub_skills\":[\"parti-formalize\"]}"));

        var prompt = router.SystemPrompt;

        Assert.Contains("parti-formalize", prompt);
        Assert.Contains("parti-abstract", prompt);
        Assert.Contains("parti-feedback", prompt);
        Assert.Contains("parti-optimize", prompt);
        Assert.Contains("parti-ghcanvas", prompt);
        Assert.Contains("parti-substrate", prompt);
    }

    [Fact]
    public void Route_PassesUserPromptToInvoker()
    {
        var invoker = new StubInvoker((_, _) => "{\"sub_skills\":[\"parti-formalize\"]}");
        var router = new LlmSkillRouter(invoker);

        router.Route("what parameters should I vary?");

        Assert.Equal("what parameters should I vary?", invoker.LastUserPrompt);
        Assert.Equal(router.SystemPrompt, invoker.LastSystemPrompt);
    }

    [Fact]
    public void Route_NullPrompt_StillCallsInvokerWithEmptyString()
    {
        var invoker = new StubInvoker((_, _) => "{\"sub_skills\":[\"parti-formalize\"]}");
        var router = new LlmSkillRouter(invoker);

        var result = router.Route(null!);

        Assert.Equal("parti-formalize", result.SubSkills[0]);
        Assert.Equal(string.Empty, invoker.LastUserPrompt);
    }
}
