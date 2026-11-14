// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Grasshopper;
using Parti.Core.Grasshopper.Chat;
using Parti.Core.Sessions;
using Parti.Core.Tools;
using Xunit;

namespace Parti.Integration.Tests;

/// <summary>
/// Hermetic tests for chat form lifecycle behavior. The actual close-on-remove
/// path lives in <c>PartiChatComponent.RemovedFromDocument</c> and requires a
/// live Grasshopper document we can't spin up in-process, so these tests cover
/// the adjacent concern: that <see cref="ChatController.Dispose"/> leaves the
/// controller in a safe, non-exploding state so the form can close without
/// dragging a live stream along with it.
/// </summary>
public class ChatLifecycleTests
{
    private static SessionOptions MakeOptions() => new()
    {
        Model = "test-model",
        ApiKey = "test-key",
        MaxTurns = 5,
        MaxToolPasses = 4,
        Temperature = 0.1,
        MaxTokens = 256,
    };

    [Fact]
    public void Dispose_WhenIdle_DoesNotThrow()
    {
        var view = new SilentChatView();
        var provider = new SilentStreamingProvider();
        var dispatcher = new FakeToolDispatcher(
            Array.Empty<AIToolDefinition>(),
            (_, _) => new JObject());

        var controller = new ChatController(
            provider,
            dispatcher,
            new NoOpUndoScopeFactory(),
            MakeOptions(),
            view);

        // Should dispose cleanly with no in-flight work.
        var ex = Record.Exception(() => controller.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void CancelAfterDispose_DoesNotThrow()
    {
        // Disposing then Cancel-ing is the real close-on-remove sequence: when
        // PartiChatComponent.RemovedFromDocument fires, the form closes, which
        // disposes the controller. Anything still queued on the UI thread may
        // still bump Cancel. That must stay safe.
        var view = new SilentChatView();
        var provider = new SilentStreamingProvider();
        var dispatcher = new FakeToolDispatcher(
            Array.Empty<AIToolDefinition>(),
            (_, _) => new JObject());

        var controller = new ChatController(
            provider,
            dispatcher,
            new NoOpUndoScopeFactory(),
            MakeOptions(),
            view);

        controller.Dispose();

        var ex = Record.Exception(() => controller.Cancel());
        Assert.Null(ex);
    }

    [Fact]
    public async Task Dispose_DuringStream_CancelsWithoutThrowing()
    {
        var view = new SilentChatView();

        // Provider that blocks on the cancellation token after a single delta,
        // so we can dispose while the stream is still live.
        var provider = new BlockingStreamingProvider();
        var dispatcher = new FakeToolDispatcher(
            Array.Empty<AIToolDefinition>(),
            (_, _) => new JObject());

        var controller = new ChatController(
            provider,
            dispatcher,
            new NoOpUndoScopeFactory(),
            MakeOptions(),
            view);

        var sendTask = controller.SendAsync("streaming");

        // Wait for the provider to land at least one chunk so we know we're
        // mid-stream, then dispose.
        await provider.FirstChunkEmitted.Task;

        var ex = Record.Exception(() => controller.Dispose());
        Assert.Null(ex);

        // The send task should complete (cancellation is handled inside SendAsync).
        var ok = await sendTask;
        Assert.True(ok);
        Assert.False(controller.IsBusy);
    }

    [Fact]
    public async Task ReplacementIsDeferredUntilCancelledControllerIsQuiescent()
    {
        var blockingProvider = new BlockingStreamingProvider();
        var dispatcher = new FakeToolDispatcher(
            Array.Empty<AIToolDefinition>(),
            (_, _) => new JObject());
        var controller = new ChatController(
            blockingProvider,
            dispatcher,
            new NoOpUndoScopeFactory(),
            MakeOptions(),
            new SilentChatView());

        var sendTask = controller.SendAsync("streaming");
        await blockingProvider.FirstChunkEmitted.Task;
        Assert.True(controller.IsBusy);

        // ChatForm's close/replacement path is required to wait for this state before
        // releasing the old singleton/controller. A replacement is created only after
        // SendAsync has handled cancellation and cleared IsBusy.
        controller.Cancel();
        Assert.True(await sendTask);
        Assert.False(controller.IsBusy);

        using var replacement = new ChatController(
            new SilentStreamingProvider(),
            dispatcher,
            new NoOpUndoScopeFactory(),
            MakeOptions(),
            new SilentChatView());
        Assert.False(replacement.IsBusy);
        controller.Dispose();
    }

    // ----- helpers --------------------------------------------------------

    /// <summary>
    /// No-op <see cref="IChatView"/>. Records every call so we can assert the
    /// post-dispose controller stays silent.
    /// </summary>
    private sealed class SilentChatView : IChatView
    {
        public List<string> UserMessages { get; } = new();
        public List<string> AssistantDeltas { get; } = new();
        public List<string> Errors { get; } = new();

        public void AppendUserMessage(string text) => UserMessages.Add(text);
        public void AppendAssistantText(string delta) => AssistantDeltas.Add(delta);
        public void SetBusy(bool busy) { }
        public void ShowError(string message) => Errors.Add(message);
        public void ClearHistory() { }
    }

    /// <summary>
    /// Streaming provider that yields nothing — useful when we just need a
    /// controller instance and don't plan to exercise the stream.
    /// </summary>
    private sealed class SilentStreamingProvider : IAIProvider
    {
        public string Name => "silent";
        public Uri DefaultServerUrl { get; } = new("https://example.invalid");

        public AIRequestCall PreCall(AIRequestCall request) => request;
        public string CreateRequestBody(AIRequestCall request) => "{}";
        public List<IAIInteraction> Decode(JObject response) => new();
        public string Encode(IAIInteraction interaction) => "{}";

        public Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<AIStreamChunk> CallStreamAsync(
            AIRequestCall request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var final = new AIReturn
            {
                Status = AICallStatus.Finished,
                StopReason = "end_turn",
                Interactions = new List<IAIInteraction>(),
            };
            yield return new AIStreamChunk(AIStreamChunkKind.Done, Final: final, StopReason: "end_turn");
        }
    }

    /// <summary>
    /// Streaming provider that emits one text delta, signals via
    /// <see cref="FirstChunkEmitted"/>, then blocks on the cancellation token.
    /// </summary>
    private sealed class BlockingStreamingProvider : IAIProvider
    {
        public TaskCompletionSource<bool> FirstChunkEmitted { get; } = new();

        public string Name => "blocking";
        public Uri DefaultServerUrl { get; } = new("https://example.invalid");

        public AIRequestCall PreCall(AIRequestCall request) => request;
        public string CreateRequestBody(AIRequestCall request) => "{}";
        public List<IAIInteraction> Decode(JObject response) => new();
        public string Encode(IAIInteraction interaction) => "{}";

        public Task<AIReturn> CallAsync(AIRequestCall request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<AIStreamChunk> CallStreamAsync(
            AIRequestCall request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AIStreamChunk(AIStreamChunkKind.TextDelta, Text: "hello");
            FirstChunkEmitted.TrySetResult(true);

            // Block until cancelled. Give up after a generous ceiling so a buggy
            // test can't hang the suite forever.
            var tcs = new TaskCompletionSource<bool>();
            using var reg = cancellationToken.Register(() => tcs.TrySetResult(true));
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                // Safety valve: test harness failure rather than an infinite hang.
                throw new TimeoutException("Blocking provider was not cancelled within 5s.");
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
