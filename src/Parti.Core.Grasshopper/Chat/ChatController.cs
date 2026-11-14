// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Parti.Core.Ai;
using Parti.Core.Design;
using Parti.Core.Prompts;
using Parti.Core.Sessions;
using Parti.Core.Tools;

namespace Parti.Core.Grasshopper.Chat;

/// <summary>
/// Optional wiring for a <see cref="ChatController"/>: the prompt builder, the deterministic
/// tool-call policies, the lock registry shared with the lock tool, and the turn-envelope
/// factory. <see cref="PartiComposition"/> fills every member for the product.
/// </summary>
/// <param name="PromptBuilder">Per-call system prompt builder (Layer 0 + routed Layer 1), or null.</param>
/// <param name="Policies">Policies run on every frozen batch before the gate and review.</param>
/// <param name="Locks">Lock registry shared with <c>gh_component_lock</c> and the lock policy.</param>
/// <param name="EnvelopeFactory">
/// Envelope factory. Null means an <see cref="UndoScopeTurnEnvelopeFactory"/> over the controller's
/// undo factory.
/// </param>
/// <param name="Catalog">Host component catalog used to render plan labels for added components.</param>
/// <param name="DeclaredArguments">
/// Declared argument names per tool; when set, the batch guard rejects undeclared arguments.
/// </param>
/// <param name="CanvasProbe">
/// Canvas-state probe. When set, a change of the canvas's undo history between loops adds
/// <see cref="TranscriptLines.CanvasChangedNote"/> to the next user message.
/// </param>
public sealed record ChatControllerWiring(
    SessionPromptBuilder? PromptBuilder = null,
    IReadOnlyList<IToolCallPolicy>? Policies = null,
    ComponentLockRegistry? Locks = null,
    ITurnEnvelopeFactory? EnvelopeFactory = null,
    IComponentCatalog? Catalog = null,
    IReadOnlyDictionary<string, IReadOnlyCollection<string>>? DeclaredArguments = null,
    ICanvasStateProbe? CanvasProbe = null);

/// <summary>
/// Pure C# brain of the Parti chat UI and a thin adapter over <see cref="ConversationSession"/>,
/// which is the only turn loop. The controller owns the busy gate, cancel/reset, the
/// <c>ask_user</c> answer path, and the bridge from session events to an <see cref="IChatView"/>:
/// streamed text and reasoning deltas, questions, and the recorded decomposition.
///
/// Has zero dependencies on Eto, Grasshopper, or Rhino — unit-testable against a
/// <c>FakeChatView</c>.
///
/// <para>Every session loop runs inside one turn envelope: by default an
/// <see cref="UndoScopeTurnEnvelope"/>, so each loop is one Grasshopper undo record and carries
/// the document lease to approved calls only.</para>
///
/// <para>Threading discipline: callers should invoke <see cref="SendAsync"/>,
/// <see cref="AnswerAsync"/>, <see cref="Cancel"/>, and <see cref="ResetAsync"/> from the UI
/// thread. The controller hops to the thread pool for provider streaming and tool dispatch, and
/// calls back into <see cref="IChatView"/> from background threads — views are responsible for
/// marshalling those calls onto the UI thread.</para>
///
/// <para>Concurrency policy: while a loop is in flight, <see cref="SendAsync"/> and
/// <see cref="AnswerAsync"/> reject new input (return <c>false</c>) rather than queuing.</para>
///
/// <para>Errors reach the view through exactly one path: the session's final return. A user
/// cancel produces no error.</para>
/// </summary>
public sealed class ChatController : IDisposable
{
    /// <summary>Text shown when the user types while several (or non-text) questions are pending.</summary>
    public const string AnswerQuestionsMessage = "Answer the questions above.";

    private readonly SessionOptions _options;
    private readonly IChatView _view;
    private readonly ConversationSession _session;
    private readonly ComponentLockRegistry _locks;
    private readonly ICanvasStateProbe? _canvasProbe;

    private CancellationTokenSource? _activeCts;
    private Task? _activeLoop;
    private int _busy; // 0 = idle, 1 = in flight
    private bool _hasRun;
    private bool _lastLoopCancelled;

    // Canvas token taken after the last loop, and whether a canvas-changed note still has to reach
    // the model (a change seen on an answer-only resume is delivered with the next user message).
    private string? _canvasStateAfterLoop;
    private string? _canvasStateAtLoopStart;
    private bool _canvasNotePending;

    /// <summary>
    /// Creates a chat controller wired to the supplied provider, tool dispatcher, undo factory,
    /// and view, with no prompt builder and no extra policies.
    /// </summary>
    public ChatController(
        IAIProvider provider,
        IToolDispatcher toolDispatcher,
        IUndoScopeFactory undoScopeFactory,
        SessionOptions options,
        IChatView view)
        : this(provider, toolDispatcher, undoScopeFactory, options, view, new ChatControllerWiring())
    {
    }

    /// <summary>
    /// Creates a chat controller with explicit <paramref name="wiring"/>. The session always
    /// streams, whatever <paramref name="options"/> says; the caller's options object is not
    /// mutated.
    /// </summary>
    public ChatController(
        IAIProvider provider,
        IToolDispatcher toolDispatcher,
        IUndoScopeFactory undoScopeFactory,
        SessionOptions options,
        IChatView view,
        ChatControllerWiring wiring)
    {
        if (provider is null) throw new ArgumentNullException(nameof(provider));
        if (toolDispatcher is null) throw new ArgumentNullException(nameof(toolDispatcher));
        if (undoScopeFactory is null) throw new ArgumentNullException(nameof(undoScopeFactory));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (wiring is null) throw new ArgumentNullException(nameof(wiring));
        _view = view ?? throw new ArgumentNullException(nameof(view));

        _options = options.Clone();
        _options.Stream = true;
        _locks = wiring.Locks ?? new ComponentLockRegistry();
        _canvasProbe = wiring.CanvasProbe;

        _session = new ConversationSession(
            provider,
            toolDispatcher,
            _options,
            new ViewObserver(_view),
            wiring.EnvelopeFactory ?? new UndoScopeTurnEnvelopeFactory(undoScopeFactory),
            wiring.Policies,
            _locks,
            wiring.Catalog,
            wiring.DeclaredArguments)
        {
            PromptBuilder = wiring.PromptBuilder,
        };
    }

    /// <summary>True while a session loop is in flight.</summary>
    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    /// <summary>
    /// The conversation history accumulated since the last <see cref="ResetAsync"/>. This is the
    /// session's history: it never contains the document lease.
    /// </summary>
    public IReadOnlyList<AIInteractionBase> History => _session.History;

    /// <summary>
    /// Plan reviewer required for reversible batches. The reviewer receives the complete canonical
    /// plan and returns <c>true</c> to approve or <c>false</c> to cancel. A null reviewer rejects
    /// mutations before dispatch; read-only calls still proceed.
    /// </summary>
    public PlanReviewer? PlanReviewer
    {
        get => _session.PlanReviewer;
        set => _session.PlanReviewer = value;
    }

    /// <summary>Questions awaiting an answer, or null when the loop is not paused.</summary>
    public IReadOnlyList<Question>? PendingQuestions => _session.Clarification.Current?.Questions;

    /// <summary>
    /// Loop status: <see cref="SessionStatus.Running"/> while busy, <see cref="SessionStatus.Paused"/>
    /// while questions are pending, <see cref="SessionStatus.Error"/> after a failed loop, and
    /// <see cref="SessionStatus.Idle"/> otherwise (including after a user cancel).
    /// </summary>
    public SessionStatus Status
    {
        get
        {
            if (IsBusy)
            {
                return SessionStatus.Running;
            }
            if (_lastLoopCancelled && _session.Status == SessionStatus.Error)
            {
                return SessionStatus.Idle;
            }
            return _session.Status;
        }
    }

    /// <summary>The final return of the most recent loop, or null before the first loop.</summary>
    public AIReturn? LastReturn { get; private set; }

    /// <summary>The recorded three-pillar decomposition (empty until one is recorded).</summary>
    public Decomposition CurrentDecomposition => _session.Decomposition.Current;

    /// <summary>Instance GUIDs the user has locked against AI edits, sorted.</summary>
    public IReadOnlyList<string> LockedComponents => _locks.ListLocked();

    /// <summary>
    /// Human-only unlock of a component instance. The model has no unlock path; this method is
    /// called from the UI.
    /// </summary>
    public void Unlock(Guid id)
    {
        _locks.Unlock(id.ToString("D"));
    }

    /// <summary>
    /// Starts a new loop driven by <paramref name="userPrompt"/>. Returns <c>false</c> immediately
    /// if another loop is in flight. While paused on <c>ask_user</c>: when exactly one text
    /// question (or no parseable question) is pending the text becomes the answer; otherwise the
    /// view shows <see cref="AnswerQuestionsMessage"/> and this returns <c>false</c>. Returns
    /// <c>true</c> after the loop completes (or errors).
    /// </summary>
    public async Task<bool> SendAsync(string userPrompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return false;
        }

        IReadOnlyList<ClarificationAnswer>? answers = null;
        var modelPrompt = userPrompt;
        string? resumeNote = null;
        try
        {
            if (_session.Clarification.Current is { } paused)
            {
                var questions = paused.Questions;
                if (questions.Count == 0)
                {
                    answers = new[] { new ClarificationAnswer("answer", userPrompt) };
                }
                else if (questions.Count == 1 && questions[0].Kind == QuestionKind.Text)
                {
                    answers = new[] { new ClarificationAnswer(questions[0].Id, userPrompt) };
                }
                else
                {
                    ReleaseBusy();
                    _view.ShowError(AnswerQuestionsMessage);
                    return false;
                }
            }

            _view.AppendUserMessage(userPrompt);

            var canvasChanged = CanvasChangedOutsideParti();
            if (_canvasNotePending || canvasChanged)
            {
                if (answers is null)
                {
                    // The note goes to the model with this message; the transcript shows the user's own text.
                    modelPrompt = userPrompt + "\n\n" + TranscriptLines.CanvasChangedNote;
                    _canvasNotePending = false;
                    _view.AppendNotice(TranscriptLines.CanvasChangedNotice);
                }
                else
                {
                    // Answers resume the paused loop; the note rides on the resumed request.
                    resumeNote = TranscriptLines.CanvasChangedNote;
                    _canvasNotePending = false;
                    _view.AppendNotice(TranscriptLines.CanvasChangedNotice);
                }
            }
        }
        catch
        {
            ReleaseBusy();
            throw;
        }


        Func<CancellationToken, Task<AIReturn>> entry = answers is not null
            ? token => _session.ResumeAsync(answers, resumeNote, token)
            : _hasRun
                ? token => _session.ContinueAsync(modelPrompt, token)
                : token => _session.RunAsync(modelPrompt, token);
        _hasRun = true;
        await StartLoop(entry, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Answers the pending <c>ask_user</c> questions and resumes the loop. Returns <c>false</c>
    /// when nothing is pending or another loop is in flight; otherwise <c>true</c> after the
    /// resumed loop completes.
    /// </summary>
    public async Task<bool> AnswerAsync(IReadOnlyList<ClarificationAnswer> answers)
    {
        if (answers is null)
        {
            throw new ArgumentNullException(nameof(answers));
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return false;
        }

        if (!_session.Clarification.IsPaused)
        {
            ReleaseBusy();
            return false;
        }

        string? resumeNote = null;
        if (_canvasNotePending || CanvasChangedOutsideParti())
        {
            // The note rides on the resumed request so the model re-reads the canvas before editing.
            resumeNote = TranscriptLines.CanvasChangedNote;
            _canvasNotePending = false;
            _view.AppendNotice(TranscriptLines.CanvasChangedNotice);
        }

        await StartLoop(token => _session.ResumeAsync(answers, resumeNote, token), CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Cancels the in-flight loop, if any. Partial text already shown in the view stays visible.
    /// Safe to call when idle — it's a no-op.
    /// </summary>
    public void Cancel()
    {
        var cts = _activeCts;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Race with the finally-disposal of the loop; harmless.
        }
    }

    /// <summary>
    /// Wipes conversation history, pending questions, the recorded decomposition, and the visible
    /// transcript. Cancels any in-flight loop first and waits for it to finish, so a late result
    /// can never land in the cleared history. Component locks are kept.
    /// </summary>
    public async Task ResetAsync()
    {
        Cancel();

        if (_activeLoop is { } loop)
        {
            await loop.ConfigureAwait(false);
        }

        _session.Reset();
        _hasRun = false;
        _lastLoopCancelled = false;
        _canvasStateAfterLoop = null;
        _canvasStateAtLoopStart = null;
        _canvasNotePending = false;
        LastReturn = null;
        _view.ClearHistory();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Cancel();
    }

    private Task StartLoop(Func<CancellationToken, Task<AIReturn>> entry, CancellationToken ct)
    {
        var loop = RunLoopAsync(entry, ct);
        _activeLoop = loop;
        return loop;
    }

    private void ReleaseBusy() => Volatile.Write(ref _busy, 0);

    /// <summary>
    /// Takes the canvas token for the loop about to start and returns true when it differs from
    /// the one taken after the previous loop. False when there is no probe, no earlier token (first
    /// message, after a reset) or no current token.
    /// </summary>
    private bool CanvasChangedOutsideParti()
    {
        if (_canvasProbe is null)
        {
            return false;
        }
        var now = CaptureCanvasState();
        _canvasStateAtLoopStart = now;
        var before = _canvasStateAfterLoop;
        return before is not null && now is not null && !string.Equals(before, now, StringComparison.Ordinal);
    }

    /// <summary>The document part of a probe token: the text before its first '|'.</summary>
    private static string? DocumentPart(string? state)
    {
        if (state is null)
        {
            return null;
        }
        var bar = state.IndexOf('|');
        return bar < 0 ? state : state.Substring(0, bar);
    }

    private string? CaptureCanvasState()
    {
        if (_canvasProbe is null)
        {
            return null;
        }
        try
        {
            return _canvasProbe.Capture();
        }
        catch
        {
            // A failing probe only means no note.
            return null;
        }
    }

    /// <summary>
    /// Runs one session entry call under the busy gate. The caller has already taken
    /// <see cref="_busy"/>; it is released however the loop ends, and this task never faults.
    /// Errors reach the view once, from the final return or an unexpected exception, and never
    /// when this controller's token was cancelled.
    /// </summary>
    private async Task RunLoopAsync(Func<CancellationToken, Task<AIReturn>> entry, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _options.CancellationToken);
        _activeCts = linkedCts;
        _lastLoopCancelled = false;
        string? error = null;

        try
        {
            _view.SetBusy(true);
            var result = await entry(linkedCts.Token).ConfigureAwait(false);
            LastReturn = result;
            if (_session.Status == SessionStatus.Error)
            {
                error = result.ErrorMessage ?? "Provider error.";
            }
            else if (TranscriptLines.IsCutOffAtTokenLimit(result))
            {
                // A text reply stopped at the output limit is not a normal success.
                _view.AppendNotice(TranscriptLines.TokenLimitNotice);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            _activeCts = null;

            // The loop's undo record is already on the stack (the envelope is disposed before the
            // session returns), so this token includes Parti's own change.
            if (_canvasProbe is not null)
            {
                var after = CaptureCanvasState();

                // The loop worked on the document active when it started. If another document is
                // active now, the model's picture is of the other one: warn with the next message.
                var startDocument = DocumentPart(_canvasStateAtLoopStart);
                var endDocument = DocumentPart(after);
                if (startDocument is not null && endDocument is not null
                    && !string.Equals(startDocument, endDocument, StringComparison.Ordinal))
                {
                    _canvasNotePending = true;
                }
                _canvasStateAfterLoop = after;
                _canvasStateAtLoopStart = null;
            }

            try
            {
                if (linkedCts.IsCancellationRequested)
                {
                    // A user cancel is deliberate: partial text stays, no error is shown.
                    _lastLoopCancelled = true;
                }
                else if (error is not null)
                {
                    _view.ShowError(error);
                }

                _view.SetBusy(false);
            }
            catch
            {
                // A failing view must not leave the controller busy.
            }
            finally
            {
                ReleaseBusy();
            }
        }
    }

    /// <summary>
    /// Bridges session events to the view. Deliberately ignores <c>OnError</c>/<c>OnFinal</c>:
    /// the controller reports errors from the final return only. Each tool result becomes one
    /// compact transcript line (the session's result copy is already scrubbed of the lease), and a
    /// reply after tool calls in the same loop starts a new assistant block.
    /// </summary>
    private sealed class ViewObserver : IConversationObserver
    {
        private readonly IChatView _view;
        private int _assistantTurnPending; // 1 once a later provider turn of the loop has started

        public ViewObserver(IChatView view) => _view = view;

        public void OnStart(AIRequestCall request) { }

        public void OnTurnStart(string turnId, int turnNumber) =>
            Volatile.Write(ref _assistantTurnPending, turnNumber > 1 ? 1 : 0);

        public void OnInteractionCompleted(IAIInteraction interaction) { }

        public void OnToolCall(AIInteractionToolCall toolCall) { }

        public void OnToolResult(AIInteractionToolResult toolResult)
        {
            if (toolResult is not null)
            {
                _view.AppendToolActivity(TranscriptLines.ToolResult(toolResult));
            }
        }

        public void OnFinal(AIReturn finalResult) { }

        public void OnError(Exception error) { }

        public void OnTextDelta(string turnId, string delta)
        {
            if (Interlocked.Exchange(ref _assistantTurnPending, 0) == 1)
            {
                _view.BeginAssistantTurn();
            }
            _view.AppendAssistantText(delta);
        }

        public void OnReasoningDelta(string turnId, string delta) => _view.AppendReasoningText(delta);

        public void OnPaused(PausedState state) => _view.ShowQuestions(state.Questions);

        public void OnDecompositionRecorded(Decomposition decomposition, int revision) =>
            _view.ShowDecomposition(decomposition, revision);
    }
}
