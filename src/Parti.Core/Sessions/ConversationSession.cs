// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// Ported from SmartHopper (https://github.com/architects-toolkit/SmartHopper)
// Original Copyright (c) SmartHopper contributors, LGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Design;
using Parti.Core.Prompts;
using Parti.Core.Tools;

namespace Parti.Core.Sessions;

/// <summary>
/// Orchestrates a multi-turn conversation with an <see cref="IAIProvider"/>. This is the only
/// turn loop in Parti: the Grasshopper chat controller, <c>parti-cli</c>, and the eval harness
/// all drive it. It handles the provider calls (streamed or not), the per-response tool batch
/// pipeline (freeze, policies, three-pillar gate, plan review, materialize, dispatch), the
/// session tools (<c>ask_user</c>, <c>parti_record_decomposition</c>), bounded error repair,
/// and budget enforcement.
/// </summary>
/// <remarks>
/// Invariants (preserved from SmartHopper, extended for the unified loop):
/// <list type="bullet">
///   <item>Only provider calls increment the <see cref="SessionOptions.MaxTurns"/> counter;
///   tool passes do not consume turns.</item>
///   <item>Back-to-back tool-use turns are bounded by <see cref="SessionOptions.MaxToolPasses"/>,
///   checked before dispatch: a response that asks for one pass more runs none of its calls.
///   After that cap or the error-repair cap, the model gets one closing call with tools off (it
///   counts against <see cref="SessionOptions.MaxTurns"/>) and the loop ends with
///   <see cref="SessionStatus.Error"/>.</item>
///   <item>All interactions produced inside a single assistant turn share the same TurnId.</item>
///   <item>The session appends to its own interaction history on every provider return and every
///   tool result; callers may inspect <see cref="History"/> after <c>RunAsync</c> or
///   <see cref="ContinueAsync(string, CancellationToken)"/>.</item>
///   <item><see cref="SessionOptions.MaxTurns"/> and <see cref="SessionOptions.MaxToolPasses"/>
///   apply per-call (each <c>RunAsync</c> / <c>ContinueAsync</c> / <c>ResumeAsync</c> gets a fresh
///   budget).</item>
///   <item>Each entry call opens exactly one <see cref="ITurnEnvelope"/> before the first provider
///   call. Host capabilities are attached only to materialized, approved copies, and every tool
///   result is scrubbed from a deep copy before it reaches history, observers, or memory.</item>
///   <item>Provider tool-call ids are single-use for the lifetime of the session (cleared by
///   <see cref="Reset"/>), and every tool_use in history gets exactly one tool_result.</item>
/// </list>
/// Thread-safety: a single <see cref="ConversationSession"/> is not safe for concurrent use.
/// Callers must not invoke <c>RunAsync</c>, <c>ContinueAsync</c>, <c>ResumeAsync</c>, or
/// <c>Reset</c> simultaneously from multiple threads on the same instance.
/// </remarks>
public sealed class ConversationSession
{
    private readonly IAIProvider _provider;
    private readonly IToolDispatcher _toolDispatcher;
    private readonly SessionOptions _options;
    private readonly IConversationObserver? _observer;
    private readonly ITurnEnvelopeFactory _envelopeFactory;
    private readonly IReadOnlyList<IToolCallPolicy> _policies;
    private readonly IComponentCatalog? _componentCatalog;
    private readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>>? _declaredArguments;
    private readonly List<AIInteractionBase> _history = new();
    private readonly object _historyGate = new();

    /// <summary>
    /// Provider tool-call ids already frozen in this session. Shared by every per-loop
    /// <see cref="ToolBatchGuard"/> so a replayed id is rejected across user turns.
    /// </summary>
    private readonly HashSet<string> _seenToolUseIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Running count of consecutive turns that ended with tool_use. Used to enforce
    /// <see cref="SessionOptions.MaxToolPasses"/> without burning the turn budget. Reset at the
    /// start of every entry call.
    /// </summary>
    private int _consecutiveToolPasses;
    private ToolBatchGuard? _toolBatchGuard;

    // Bounded error-repair. Tracks consecutive all-error tool passes and short-circuits when
    // the policy cap is hit instead of burning the rest of MaxToolPasses.
    private ErrorRepairTracker _errorRepairTracker;

    /// <summary>
    /// Whether the current loop's envelope (undo record) is kept. Set on a natural finish, after
    /// every completed tool pass, and immediately before a reversible call enters its handler.
    /// </summary>
    private bool _keepEnvelope;

    /// <summary>
    /// True once <see cref="RunAsync"/> has been called at least since the last <see cref="Reset"/>.
    /// Guards <see cref="ContinueAsync"/> against being used to bootstrap a conversation.
    /// </summary>
    private bool _hasRun;

    /// <summary>
    /// Creates a conversation session with no host envelope (no undo scope or document lease)
    /// and no extra tool-call policies. The caller owns the provider and dispatcher lifetimes.
    /// </summary>
    /// <param name="provider">The LLM provider used for every turn.</param>
    /// <param name="toolDispatcher">The tool dispatcher surfaced to the model via <c>tools[]</c>.</param>
    /// <param name="options">Budget and sampling options.</param>
    /// <param name="observer">Optional observer for lifecycle events.</param>
    public ConversationSession(
        IAIProvider provider,
        IToolDispatcher toolDispatcher,
        SessionOptions options,
        IConversationObserver? observer = null)
        : this(provider, toolDispatcher, options, observer, NullTurnEnvelopeFactory.Instance)
    {
    }

    /// <summary>
    /// Creates a conversation session wired to a host <see cref="ITurnEnvelopeFactory"/>, the
    /// deterministic <see cref="IToolCallPolicy"/> checks that run after a batch is frozen, and an
    /// optional shared <see cref="ComponentLockRegistry"/>.
    /// </summary>
    /// <param name="provider">The LLM provider used for every turn.</param>
    /// <param name="toolDispatcher">The tool dispatcher surfaced to the model via <c>tools[]</c>.</param>
    /// <param name="options">Budget and sampling options.</param>
    /// <param name="observer">Optional observer for lifecycle events.</param>
    /// <param name="envelopeFactory">Opens one envelope per entry call.</param>
    /// <param name="policies">Policies run, in order, on every frozen batch before the gate and review.</param>
    /// <param name="componentLocks">
    /// Lock registry shared with the lock tool and policy. A new registry is created when null.
    /// </param>
    /// <param name="componentCatalog">
    /// Host component catalog used to render <c>gh_add_component</c> plan lines from the resolved
    /// catalog entry. Null renders without catalog resolution.
    /// </param>
    /// <param name="declaredArguments">
    /// Declared argument names per tool (see <see cref="ToolBatchGuard.DeclaredArgumentsFrom"/>).
    /// When supplied, the batch guard rejects any argument a listed tool does not declare. Null
    /// keeps the guard's lenient behaviour for library callers.
    /// </param>
    public ConversationSession(
        IAIProvider provider,
        IToolDispatcher toolDispatcher,
        SessionOptions options,
        IConversationObserver? observer,
        ITurnEnvelopeFactory envelopeFactory,
        IReadOnlyList<IToolCallPolicy>? policies = null,
        ComponentLockRegistry? componentLocks = null,
        IComponentCatalog? componentCatalog = null,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? declaredArguments = null)
    {
        _componentCatalog = componentCatalog;
        _declaredArguments = declaredArguments;
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _toolDispatcher = toolDispatcher ?? throw new ArgumentNullException(nameof(toolDispatcher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _observer = observer;
        _envelopeFactory = envelopeFactory ?? throw new ArgumentNullException(nameof(envelopeFactory));
        _policies = policies?.ToArray() ?? Array.Empty<IToolCallPolicy>();
        if (_policies.Any(p => p is null))
        {
            throw new ArgumentException("Policies must not contain null entries.", nameof(policies));
        }
        ComponentLocks = componentLocks ?? new ComponentLockRegistry();
        _errorRepairTracker = new ErrorRepairTracker(_options.ErrorRepair);
    }

    /// <summary>
    /// Full interaction history accumulated across every entry call since the last
    /// <see cref="Reset"/>. Includes every user prompt, assistant turn, tool call, and tool
    /// result, in chronological order. Never contains a host capability such as a document lease.
    /// </summary>
    /// <remarks>
    /// Returns a point-in-time copy, so a UI thread may read it while the loop appends on another
    /// thread (enumerating the live list during an append would throw). The session's own loop
    /// reads its private list directly.
    /// </remarks>
    public IReadOnlyList<AIInteractionBase> History
    {
        get
        {
            lock (_historyGate)
            {
                return _history.ToArray();
            }
        }
    }

    /// <summary>
    /// Cumulative input tokens reported by the provider across every entry call since the last
    /// <see cref="Reset"/>.
    /// </summary>
    public int TotalInputTokens { get; private set; }

    /// <summary>
    /// Cumulative output tokens reported by the provider across every entry call since the last
    /// <see cref="Reset"/>.
    /// </summary>
    public int TotalOutputTokens { get; private set; }

    /// <summary>
    /// Current lifecycle state of the session. Idle initially, Running while a turn loop is in
    /// flight, Paused when the model invoked <c>ask_user</c> and awaits
    /// <see cref="ResumeAsync(IReadOnlyList{ClarificationAnswer}, CancellationToken)"/>,
    /// Error when the last turn ended with an error, a cancel, or a cap hit.
    /// </summary>
    public SessionStatus Status { get; private set; } = SessionStatus.Idle;

    /// <summary>
    /// Clarification-pause holder. UIs watch <see cref="ClarificationState.IsPaused"/> /
    /// <see cref="ClarificationState.Current"/> to render questions when the model needs
    /// disambiguation; the session resumes via
    /// <see cref="ResumeAsync(IReadOnlyList{ClarificationAnswer}, CancellationToken)"/>.
    /// </summary>
    public ClarificationState Clarification { get; } = new();

    /// <summary>
    /// Active <see cref="Parti.Core.Design.Decomposition"/> — the three-pillar
    /// (Formalization / Abstraction / Feedback) understanding of the current design problem,
    /// recorded by the model with <c>parti_record_decomposition</c>. When
    /// <see cref="SessionOptions.RequireDecomposition"/> is set, <see cref="DecompositionGate"/>
    /// rejects reversible batches until it passes <see cref="PillarGate"/>. Cleared by
    /// <see cref="Reset"/>.
    /// </summary>
    public DecompositionState Decomposition { get; } = new();

    /// <summary>
    /// User-managed set of locked component instance GUIDs. Shared with the
    /// <c>gh_component_lock</c> tool and the lock policy when injected through the constructor.
    /// Locks record user intent and are NOT cleared by <see cref="Reset"/>.
    /// </summary>
    public ComponentLockRegistry ComponentLocks { get; }

    /// <summary>
    /// Conversation memory — instance-GUID-to-mention dictionary populated from scrubbed canvas
    /// tool_results (<c>instanceGuid</c> on add results, <c>nodes[].id</c> on snapshots).
    /// </summary>
    public MemoryResolver Memory { get; } = new();

    /// <summary>
    /// Callback invoked immediately before the session dispatches a batch of tool calls that
    /// includes at least one reversible tool. A <c>null</c> reviewer fails closed: the batch is
    /// rejected and no mutation is dispatched.
    /// <para>
    /// The reviewer returns <c>true</c> to approve the batch (normal dispatch) or <c>false</c> to
    /// cancel — the session then emits a synthetic
    /// <c>"User cancelled plan before execution."</c> tool_result for every pending tool_use and
    /// ends the turn cleanly. A reviewer that throws is treated as a cancel. Read-only batches
    /// bypass the reviewer entirely; the session tools are never reviewed.
    /// </para>
    /// </summary>
    public PlanReviewer? PlanReviewer { get; set; }

    /// <summary>
    /// Optional per-call system-prompt assembler. When set, the session invokes
    /// <see cref="SessionPromptBuilder.Build"/> at the start of every entry call and uses the
    /// result as the system prompt for that call's turn loop — overriding
    /// <see cref="SessionOptions.SystemPrompt"/>. If the builder throws, the session falls back to
    /// <see cref="SessionOptions.SystemPrompt"/> for that call.
    /// <para>
    /// Activated sub-skills are surfaced to <see cref="IConversationObserver.OnSkillsActivated"/>
    /// immediately after routing, before the first provider call.
    /// </para>
    /// </summary>
    public SessionPromptBuilder? PromptBuilder { get; set; }

    /// <summary>
    /// Per-call system prompt derived by <see cref="PromptBuilder"/> for the current turn loop.
    /// Takes precedence over <see cref="SessionOptions.SystemPrompt"/> in
    /// <see cref="BuildRequest"/> when non-null. Reset at the start of every entry call.
    /// </summary>
    private string? _builtSystemPrompt;

    /// <summary>
    /// Sub-skills surfaced by the most recent <see cref="PromptBuilder"/> invocation. Exposed for
    /// tests and telemetry consumers that prefer polling over observer wiring.
    /// </summary>
    public IReadOnlyList<string> LastActivatedSubSkills { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Executes a single user prompt against the provider, running the tool loop until the model
    /// emits <c>end_turn</c>, the turn budget is exhausted, or an error occurs. This is the entry
    /// point for a conversation; use <see cref="ContinueAsync(string, CancellationToken)"/> to send
    /// follow-up prompts with the full prior history.
    /// </summary>
    /// <remarks>
    /// If the session is paused on <c>ask_user</c>, the pending question is answered with a
    /// <c>{"status":"declined"}</c> tool_result before the new prompt is appended.
    /// Not thread-safe.
    /// </remarks>
    /// <param name="userPrompt">The user's input text.</param>
    /// <param name="ct">Cancellation token; merged with <see cref="SessionOptions.CancellationToken"/>.</param>
    /// <returns>The last <see cref="AIReturn"/> produced by the provider (or an error return).</returns>
    public Task<AIReturn> RunAsync(string userPrompt, CancellationToken ct = default)
    {
        if (userPrompt is null)
        {
            throw new ArgumentNullException(nameof(userPrompt));
        }

        _hasRun = true;
        return ExecuteTurnLoopAsync(userPrompt, answers: null, ct);
    }

    /// <summary>
    /// Appends a follow-up user message to the existing history and runs the session loop again
    /// with the full prior context.
    /// </summary>
    /// <remarks>
    /// Budgets are applied per-call. If the session is paused on <c>ask_user</c>, the pending
    /// question is answered with a <c>{"status":"declined"}</c> tool_result first.
    /// Not thread-safe.
    /// </remarks>
    /// <param name="userPrompt">The follow-up user text.</param>
    /// <param name="ct">Cancellation token; merged with <see cref="SessionOptions.CancellationToken"/>.</param>
    /// <returns>The last <see cref="AIReturn"/> produced by the provider for this turn.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="RunAsync"/> has not been called since the session was created or
    /// last <see cref="Reset"/>.
    /// </exception>
    public Task<AIReturn> ContinueAsync(string userPrompt, CancellationToken ct = default)
    {
        if (userPrompt is null)
        {
            throw new ArgumentNullException(nameof(userPrompt));
        }
        if (!_hasRun)
        {
            throw new InvalidOperationException(
                "ContinueAsync requires a prior RunAsync call. Start the conversation with RunAsync, then use ContinueAsync for follow-up prompts.");
        }

        return ExecuteTurnLoopAsync(userPrompt, answers: null, ct);
    }

    /// <summary>
    /// Resume a session that paused after an <c>ask_user</c> tool call. Appends the single
    /// tool_result for the pending tool_use carrying the user's <paramref name="answers"/> and
    /// re-enters the turn loop so the model can proceed with the supplied disambiguation.
    /// </summary>
    /// <remarks>
    /// Not thread-safe. Throws <see cref="InvalidOperationException"/> if the session is not
    /// currently paused. Fresh budget applies.
    /// </remarks>
    /// <param name="answers">One <see cref="ClarificationAnswer"/> per question surfaced.</param>
    /// <param name="ct">Cancellation token merged with <see cref="SessionOptions.CancellationToken"/>.</param>
    public Task<AIReturn> ResumeAsync(IReadOnlyList<ClarificationAnswer> answers, CancellationToken ct = default)
    {
        if (answers is null)
        {
            throw new ArgumentNullException(nameof(answers));
        }
        if (!Clarification.IsPaused)
        {
            throw new InvalidOperationException(
                "ResumeAsync called but the session is not paused. Check Status/Clarification.IsPaused first.");
        }

        return ExecuteTurnLoopAsync(userPrompt: null, answers, ct);
    }

    /// <summary>
    /// Resume like <see cref="ResumeAsync(IReadOnlyList{ClarificationAnswer}, CancellationToken)"/>
    /// and carry a host note (e.g. "the canvas changed outside this chat") in the same tool_result,
    /// so the resumed model sees it. The note is host text, never model or canvas data: it is
    /// cleaned (control/format characters dropped, whitespace collapsed), cut to
    /// <see cref="MaxHostNoteLength"/> characters and prefixed with <see cref="AutomaticNotePrefix"/>.
    /// A null or blank note behaves exactly like the overload without it.
    /// </summary>
    public Task<AIReturn> ResumeAsync(IReadOnlyList<ClarificationAnswer> answers, string? hostNote, CancellationToken ct = default)
    {
        if (answers is null)
        {
            throw new ArgumentNullException(nameof(answers));
        }
        if (!Clarification.IsPaused)
        {
            throw new InvalidOperationException(
                "ResumeAsync called but the session is not paused. Check Status/Clarification.IsPaused first.");
        }

        return ExecuteTurnLoopAsync(userPrompt: null, answers, ct, NormalizeHostNote(hostNote));
    }

    /// <summary>Longest host note carried on resume, in characters (prefix included).</summary>
    public const int MaxHostNoteLength = 400;

    /// <summary>
    /// Cleans and bounds a host note and makes sure it carries <see cref="AutomaticNotePrefix"/>.
    /// Returns null for a null or blank note.
    /// </summary>
    internal static string? NormalizeHostNote(string? hostNote)
    {
        var cleaned = CatalogText.Clean(hostNote, MaxHostNoteLength);
        if (cleaned.Length == 0)
        {
            return null;
        }

        if (!cleaned.StartsWith(AutomaticNotePrefix, StringComparison.Ordinal))
        {
            cleaned = CatalogText.Clean(AutomaticNotePrefix + " " + cleaned, MaxHostNoteLength);
        }

        return cleaned;
    }

    /// <summary>
    /// Clears the conversation history, token counters, pending clarification, recorded
    /// decomposition, and the seen tool-call ids back to the "never called" state. Component
    /// locks are kept because they record user intent.
    /// </summary>
    /// <remarks>Not thread-safe.</remarks>
    public void Reset()
    {
        lock (_historyGate) { _history.Clear(); }
        _consecutiveToolPasses = 0;
        _errorRepairTracker = new ErrorRepairTracker(_options.ErrorRepair);
        _toolBatchGuard = null;
        _seenToolUseIds.Clear();
        _hasRun = false;
        TotalInputTokens = 0;
        TotalOutputTokens = 0;
        Status = SessionStatus.Idle;
        _builtSystemPrompt = null;
        LastActivatedSubSkills = Array.Empty<string>();
        _ = Clarification.DrainAndResume();
        Decomposition.Reset();
    }

    /// <summary>
    /// Core turn loop shared by the three entry calls. Opens the envelope, seeds history with
    /// either a user prompt or the clarification answers, runs the loop, and always completes
    /// and disposes the envelope.
    /// </summary>
    private async Task<AIReturn> ExecuteTurnLoopAsync(
        string? userPrompt,
        IReadOnlyList<ClarificationAnswer>? answers,
        CancellationToken ct,
        string? hostNote = null)
    {
        _consecutiveToolPasses = 0;
        _errorRepairTracker = new ErrorRepairTracker(_options.ErrorRepair);
        _toolBatchGuard = null;
        _keepEnvelope = false;
        Status = SessionStatus.Running;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _options.CancellationToken);
        var token = linkedCts.Token;

        // On resume the first turn reuses the paused TurnId so the answer shares it with the
        // original tool_use; otherwise a fresh id is allocated.
        var firstTurnId = answers is not null && Clarification.Current is { } pending
            ? pending.TurnId
            : Guid.NewGuid().ToString("N");

        ITurnEnvelope envelope;
        try
        {
            envelope = _envelopeFactory.Begin(BuildEnvelopeLabel(firstTurnId));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var err = MakeError($"Could not start the turn: {ex.Message}", null);
            Status = SessionStatus.Error;
            _observer?.OnError(ex);
            _observer?.OnFinal(err);
            return err;
        }

        try
        {
            return await RunLoopAsync(userPrompt, answers, hostNote, firstTurnId, envelope, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            var cancelled = new AIReturn();
            cancelled.CreateError("Call cancelled or timed out");
            Status = SessionStatus.Error;
            _observer?.OnError(oce);
            _observer?.OnFinal(cancelled);
            return cancelled;
        }
        catch
        {
            Status = SessionStatus.Error;
            throw;
        }
        finally
        {
            try
            {
                envelope.Complete(_keepEnvelope);
            }
            finally
            {
                envelope.Dispose();
            }
        }
    }

    private async Task<AIReturn> RunLoopAsync(
        string? userPrompt,
        IReadOnlyList<ClarificationAnswer>? answers,
        string? hostNote,
        string firstTurnId,
        ITurnEnvelope envelope,
        CancellationToken token)
    {
        if (answers is not null)
        {
            var paused = Clarification.DrainAndResume()
                ?? throw new InvalidOperationException("ResumeAsync called but the session is not paused.");
            AppendToolResult(new AIInteractionToolResult
            {
                Id = paused.ToolUseId,
                Name = AskUserTool.ToolName,
                Agent = AIAgent.ToolResult,
                TurnId = paused.TurnId,
                Result = BuildAnswersPayload(answers, hostNote),
            });
        }
        else
        {
            // A new message while paused abandons the question: the pending tool_use still gets
            // its one tool_result so the provider history stays well-formed.
            if (Clarification.DrainAndResume() is { } abandoned)
            {
                AppendToolResult(new AIInteractionToolResult
                {
                    Id = abandoned.ToolUseId,
                    Name = AskUserTool.ToolName,
                    Agent = AIAgent.ToolResult,
                    TurnId = abandoned.TurnId,
                    Result = new JObject
                    {
                        ["status"] = "declined",
                        ["message"] = "The user sent a new message instead of answering.",
                    },
                });
            }

            var userInteraction = new AIInteractionText
            {
                Agent = AIAgent.User,
                Content = userPrompt ?? string.Empty,
                TurnId = firstTurnId,
            };
            lock (_historyGate) { _history.Add(userInteraction); }
            _observer?.OnInteractionCompleted(ObserverCopy(userInteraction));
        }

        BuildSystemPrompt(userPrompt, firstTurnId);

        AIReturn? lastReturn = null;
        bool observerStartEmitted = false;
        int emptyResponseNudgesUsed = 0;

        for (int turn = 0; turn < _options.MaxTurns; turn++)
        {
            token.ThrowIfCancellationRequested();

            var turnId = turn == 0 ? firstTurnId : Guid.NewGuid().ToString("N");
            var request = BuildRequest();

            if (!observerStartEmitted)
            {
                _observer?.OnStart(request);
                observerStartEmitted = true;
            }

            _observer?.OnTurnStart(turnId, turn + 1);

            AIReturn? providerReturn;
            try
            {
                providerReturn = _options.Stream
                    ? await CallStreamingAsync(request, turnId, token).ConfigureAwait(false)
                    : await _provider.CallAsync(request, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var err = MakeError(ex.Message, request);
                Status = SessionStatus.Error;
                _observer?.OnError(ex);
                _observer?.OnFinal(err);
                return err;
            }

            if (providerReturn is null)
            {
                // A stream that ended without a Done chunk (or a provider that returned nothing)
                // never completed its transport; nothing from it is dispatched.
                var noResponse = MakeError("Provider returned no response.", request);
                Status = SessionStatus.Error;
                _observer?.OnFinal(noResponse);
                return noResponse;
            }

            AccumulateTokens(providerReturn);
            lastReturn = providerReturn;

            if (providerReturn.Status == AICallStatus.Error || !providerReturn.Success)
            {
                // Tool calls from a failed response are never dispatched, so they are not kept in
                // history either (they would be tool_uses without results).
                StampAndAppend(providerReturn.Interactions.Where(i => i is not AIInteractionToolCall), turnId);
                Status = SessionStatus.Error;
                _observer?.OnFinal(providerReturn);
                return providerReturn;
            }

            if (_options.ProcessTools && providerReturn.HasToolCalls && IsTruncated(providerReturn))
            {
                // Defense in depth: a response cut off by the output budget may carry a partial
                // batch. Keep its text, drop its tool calls, and stop.
                StampAndAppend(providerReturn.Interactions.Where(i => i is not AIInteractionToolCall), turnId);
                var truncated = MakeError(
                    $"Provider output was truncated (stop reason '{providerReturn.StopReason}'); no tool call from it was dispatched.",
                    request);
                Status = SessionStatus.Error;
                _observer?.OnFinal(truncated);
                return truncated;
            }

            if (_options.ProcessTools && providerReturn.HasToolCalls && _consecutiveToolPasses >= _options.MaxToolPasses)
            {
                // Tool-pass cap, checked before dispatch so no batch runs past the budget. Keep the
                // text, drop the calls (none of them ran), and let the model close the turn.
                StampAndAppend(providerReturn.Interactions.Where(i => i is not AIInteractionToolCall), turnId);
                return await EndAfterCapAsync(
                    $"Tool-pass cap ({_options.MaxToolPasses}) reached without resolution.",
                    ToolPassCapNote(_options.MaxToolPasses),
                    turn,
                    request,
                    token).ConfigureAwait(false);
            }

            StampAndAppend(providerReturn.Interactions, turnId);

            // An empty natural stop (no visible text, no tool call — e.g. a reasoning model that
            // spent its whole reply thinking) would end the loop with nothing shown. Nudge the
            // model once, visibly in history; if it is still empty, fail loudly instead of
            // reporting success.
            if (!providerReturn.HasToolCalls && !HasVisibleText(providerReturn))
            {
                if (emptyResponseNudgesUsed < Math.Max(0, _options.EmptyResponseNudges))
                {
                    emptyResponseNudgesUsed++;
                    var nudge = new AIInteractionText
                    {
                        Agent = AIAgent.User,
                        Content = EmptyResponseNudgeText,
                        TurnId = turnId,
                    };
                    lock (_historyGate) { _history.Add(nudge); }
                    _observer?.OnInteractionCompleted(ObserverCopy(nudge));
                    continue;
                }

                var empty = MakeError(EmptyResponseErrorText, request);
                Status = SessionStatus.Error;
                _observer?.OnFinal(empty);
                return empty;
            }

            // Natural stop: no tool use or tools disabled. Done.
            if (!_options.ProcessTools || !providerReturn.HasToolCalls)
            {
                _keepEnvelope = true;
                Status = SessionStatus.Idle;
                _observer?.OnFinal(providerReturn);
                return providerReturn;
            }

            var historyCountBeforeDispatch = _history.Count;
            var passOutcome = await DispatchToolPassAsync(providerReturn, turnId, envelope, token).ConfigureAwait(false);

            // Keep the envelope after every completed tool pass, as the chat controller always has.
            _keepEnvelope = true;

            if (passOutcome == ToolPassOutcome.Paused)
            {
                Status = SessionStatus.Paused;
                if (Clarification.Current is { } paused)
                {
                    _observer?.OnPaused(paused);
                }
                _observer?.OnFinal(providerReturn);
                return providerReturn;
            }
            if (passOutcome == ToolPassOutcome.Cancelled)
            {
                Status = SessionStatus.Idle;
                _observer?.OnFinal(providerReturn);
                return providerReturn;
            }

            // Bounded error-repair: consult the tracker with the tool_results this pass added.
            var passResults = new List<AIInteractionToolResult>();
            for (int i = historyCountBeforeDispatch; i < _history.Count; i++)
            {
                if (_history[i] is AIInteractionToolResult tr) passResults.Add(tr);
            }
            if (_errorRepairTracker.RecordPassAndShouldStop(passResults))
            {
                // The failing results (and any recovery hint in them) reach the model in the
                // closing call instead of being dropped.
                return await EndAfterCapAsync(
                    $"Error-repair cap ({_options.ErrorRepair.MaxConsecutiveErrorPasses}) reached; the model could not recover from repeated tool errors.",
                    ErrorRepairCapNote(_options.ErrorRepair.MaxConsecutiveErrorPasses),
                    turn,
                    request,
                    token).ConfigureAwait(false);
            }

            _consecutiveToolPasses++;
        }

        // Max turns reached without end_turn. Mutate the last return into an error so callers
        // can distinguish a cap-hit from a healthy finish.
        var capped = lastReturn ?? new AIReturn();
        capped.Status = AICallStatus.Error;
        capped.ErrorMessage = $"Max turns ({_options.MaxTurns}) reached without end_turn.";
        Status = SessionStatus.Error;
        _observer?.OnFinal(capped);
        return capped;
    }

    /// <summary>
    /// Ends the loop after the tool-pass or error-repair cap. When a provider turn is left, the model
    /// gets one closing call with tool use off, so the user reads a short summary of the canvas and
    /// what is left instead of a bare error. The loop still ends with <see cref="SessionStatus.Error"/>
    /// and <paramref name="capMessage"/>: a cap stop is not a finished request, and callers (the chat
    /// window, <c>parti-cli</c>, the eval) must be able to tell the two apart.
    /// </summary>
    /// <param name="turn">Zero-based index of the provider turn that hit the cap.</param>
    private async Task<AIReturn> EndAfterCapAsync(
        string capMessage,
        string note,
        int turn,
        AIRequestCall lastRequest,
        CancellationToken token)
    {
        var summary = turn + 1 < _options.MaxTurns
            ? await CallForClosingSummaryAsync(note, turn + 1, token).ConfigureAwait(false)
            : null;

        var final = summary ?? MakeError(capMessage, lastRequest);
        final.Status = AICallStatus.Error;
        final.ErrorMessage = capMessage;
        Status = SessionStatus.Error;
        _observer?.OnFinal(final);
        return final;
    }

    /// <summary>
    /// The closing call after a cap: appends <paramref name="note"/> as an automatic user-role note
    /// and calls the provider once with <c>tool_choice</c> "none". Tool calls in the reply are
    /// dropped, never dispatched, so a provider that ignores <c>tool_choice</c> changes nothing.
    /// Returns null when the call fails, errors, or has no visible text; the cap error then stands
    /// alone, and neither the note nor that reply stays in history.
    /// </summary>
    /// <param name="turn">Zero-based index of this provider turn.</param>
    private async Task<AIReturn?> CallForClosingSummaryAsync(string note, int turn, CancellationToken token)
    {
        var turnId = Guid.NewGuid().ToString("N");
        var noteInteraction = new AIInteractionText
        {
            Agent = AIAgent.User,
            Content = note,
            TurnId = turnId,
        };
        lock (_historyGate) { _history.Add(noteInteraction); }

        var answered = false;
        try
        {
            var request = BuildRequest();
            request.ToolChoice = "none";
            _observer?.OnTurnStart(turnId, turn + 1);

            var reply = _options.Stream
                ? await CallStreamingAsync(request, turnId, token).ConfigureAwait(false)
                : await _provider.CallAsync(request, token).ConfigureAwait(false);
            if (reply is null)
            {
                return null;
            }

            AccumulateTokens(reply);
            reply.Interactions.RemoveAll(i => i is AIInteractionToolCall);
            if (!reply.Success || !HasVisibleText(reply))
            {
                return null;
            }

            // Answered only once the reply is in history: an observer that throws on the way must
            // not leave the note behind without its answer.
            _observer?.OnInteractionCompleted(ObserverCopy(noteInteraction));
            StampAndAppend(reply.Interactions, turnId);
            answered = true;
            return reply;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _observer?.OnError(ex);
            return null;
        }
        finally
        {
            if (!answered)
            {
                // Nothing answered the note, so it comes back out: the user's next message must not
                // arrive under "do not call any tool".
                lock (_historyGate) { _history.Remove(noteInteraction); }
            }
        }
    }

    /// <summary>
    /// Routes the prompt through <see cref="PromptBuilder"/> when one is set. A builder failure
    /// falls back to <see cref="SessionOptions.SystemPrompt"/> for this call.
    /// </summary>
    private void BuildSystemPrompt(string? userPrompt, string firstTurnId)
    {
        _builtSystemPrompt = null;
        LastActivatedSubSkills = Array.Empty<string>();

        if (PromptBuilder is not { } builder)
        {
            return;
        }

        var routingSource = userPrompt ?? LatestUserPromptFromHistory() ?? string.Empty;
        SessionPromptResult built;
        try
        {
            built = builder.Build(routingSource);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        _builtSystemPrompt = built.SystemPrompt;
        LastActivatedSubSkills = built.ActivatedSubSkills;
        _observer?.OnSkillsActivated(firstTurnId, built.ActivatedSubSkills);
    }

    /// <summary>
    /// Streams one provider call, forwarding text and reasoning deltas to the observer. Returns
    /// the Done chunk's final return, or <c>null</c> when the stream ended without one.
    /// </summary>
    private async Task<AIReturn?> CallStreamingAsync(AIRequestCall request, string turnId, CancellationToken token)
    {
        AIReturn? final = null;
        await foreach (var chunk in _provider.CallStreamAsync(request, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();

            switch (chunk.Kind)
            {
                case AIStreamChunkKind.TextDelta:
                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        _observer?.OnTextDelta(turnId, chunk.Text!);
                    }
                    break;

                case AIStreamChunkKind.ReasoningDelta:
                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        _observer?.OnReasoningDelta(turnId, chunk.Text!);
                    }
                    break;

                case AIStreamChunkKind.Done:
                    final = chunk.Final;
                    break;
            }
        }

        return final;
    }

    /// <summary>
    /// Prefix of every user-role message Parti writes itself (not typed by the user). Such
    /// messages are never used to route skills.
    /// </summary>
    public const string AutomaticNotePrefix = "[Parti automatic note]";

    /// <summary>
    /// The automatic note the session appends (as a user-role message) when the model's reply had
    /// no visible text and no tool call. Prefixed so it is recognisable in history and transcripts.
    /// </summary>
    public const string EmptyResponseNudgeText =
        AutomaticNotePrefix + " Your last reply contained no visible text and no tool call, so the user saw nothing. " +
        "Continue now: answer the user in plain text, ask with ask_user, or call the tool you need.";

    /// <summary>The error shown once when the reply is still empty after the nudges.</summary>
    public const string EmptyResponseErrorText =
        "The model ended its reply without any visible text or tool call, even after an automatic nudge. " +
        "Nothing more was changed on the canvas. Try rephrasing the request, or choose a different model in the provider settings.";

    /// <summary>
    /// The automatic note that opens the closing call after the tool-pass cap. The calls in the
    /// response that hit the cap were dropped without running.
    /// </summary>
    public static string ToolPassCapNote(int maxToolPasses) =>
        AutomaticNotePrefix + $" This message has used its limit of {maxToolPasses} responses that call tools, " +
        "so the tool calls in your last response were not run and tools are off for this reply. " +
        "Reply to the user in one short paragraph of plain text: what is on the canvas now, what is left to do, " +
        "and that they can send another message to continue. If you need an answer from the user, ask for it in that paragraph. " +
        "Do not call any tool in this reply.";

    /// <summary>
    /// The automatic note that opens the closing call after the error-repair cap. The failing tool
    /// results, and any recovery hint in them, are already in history above it.
    /// </summary>
    public static string ErrorRepairCapNote(int maxConsecutiveErrorPasses)
    {
        var passes = Math.Max(1, maxConsecutiveErrorPasses);
        var failed = passes == 1 ? "The last tool pass failed" : $"The last {passes} tool passes all failed";
        return AutomaticNotePrefix + $" {failed}, so tools are off for this reply. " +
            "Reply to the user in one short paragraph of plain text: what failed and why (from the tool results above), " +
            "what is on the canvas now, and what they can do next. Do not call any tool in this reply.";
    }

    /// <summary>True when the reply carries non-blank assistant text (reasoning does not count).</summary>
    private static bool HasVisibleText(AIReturn providerReturn) =>
        providerReturn.Interactions.OfType<AIInteractionText>()
            .Any(t => t.Agent != AIAgent.User && t.Agent != AIAgent.System && t.Agent != AIAgent.Context
                      && !string.IsNullOrWhiteSpace(t.Content));

    private static bool IsTruncated(AIReturn providerReturn) =>
        string.Equals(providerReturn.StopReason, "max_tokens", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(providerReturn.StopReason, "length", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the most recent user-typed text in history (automatic notes are skipped), or
    /// <c>null</c> when none exists yet. Used by the <see cref="PromptBuilder"/> path to route on resume.
    /// </summary>
    private string? LatestUserPromptFromHistory()
    {
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i] is AIInteractionText t
                && t.Agent == AIAgent.User
                && !string.IsNullOrWhiteSpace(t.Content)
                && !t.Content.StartsWith(AutomaticNotePrefix, StringComparison.Ordinal))
            {
                return t.Content;
            }
        }
        return null;
    }

    private void AccumulateTokens(AIReturn providerReturn)
    {
        TotalInputTokens += providerReturn.InputTokens;
        TotalOutputTokens += providerReturn.OutputTokens;
    }

    /// <summary>
    /// Every tool definition advertised to the model: the dispatcher's tools plus the session
    /// tools (<c>ask_user</c>, <c>parti_record_decomposition</c>), which the session handles itself.
    /// </summary>
    private List<AIToolDefinition> AdvertisedTools()
    {
        var tools = _toolDispatcher.GetDefinitions().ToList();
        if (!tools.Any(t => t.Name == AskUserTool.ToolName))
        {
            tools.Add(AskUserTool.Definition);
        }
        if (!tools.Any(t => t.Name == RecordDecompositionTool.ToolName))
        {
            tools.Add(RecordDecompositionTool.Definition);
        }
        return tools;
    }

    /// <summary>
    /// Builds a fresh <see cref="AIRequestCall"/> from current options and history. Merges any
    /// <see cref="AIAgent.System"/> interactions into <c>SystemPrompt</c> with '\n---\n' separators
    /// (SmartHopper behaviour).
    /// </summary>
    private AIRequestCall BuildRequest()
    {
        var request = new AIRequestCall
        {
            Model = _options.Model,
            ApiKey = _options.ApiKey,
            BaseUrl = _provider.DefaultServerUrl,
            MaxTokens = _options.MaxTokens,
            Temperature = _options.Temperature,
            Tools = AdvertisedTools(),
            ToolChoice = "auto",
            RequestKind = AIRequestKind.Generation,
        };

        var systemTexts = _history
            .OfType<AIInteractionText>()
            .Where(t => t.Agent == AIAgent.System && !string.IsNullOrWhiteSpace(t.Content))
            .Select(t => t.Content!)
            .ToList();

        // Per-call builder output takes precedence over the static option. Only one of the two
        // paths contributes the top-level system text in a given call.
        var topLevel = !string.IsNullOrWhiteSpace(_builtSystemPrompt)
            ? _builtSystemPrompt
            : _options.SystemPrompt;
        if (!string.IsNullOrWhiteSpace(topLevel))
        {
            systemTexts.Insert(0, topLevel!);
        }

        request.SystemPrompt = systemTexts.Count > 0
            ? string.Join("\n---\n", systemTexts)
            : null;

        // Pass history minus System-agent texts; Anthropic takes them via the top-level "system" field.
        request.Interactions = _history
            .Where(i => !(i is AIInteractionText t && t.Agent == AIAgent.System))
            .Cast<IAIInteraction>()
            .ToList();

        return request;
    }

    /// <summary>
    /// Runs the batch pipeline for one provider response: freeze (guard) → session tools (sole
    /// call, materialized before parsing) → policies → three-pillar gate → plan review →
    /// materialize → attach capabilities → serial dispatch with per-result history.
    /// </summary>
    private async Task<ToolPassOutcome> DispatchToolPassAsync(
        AIReturn providerReturn,
        string turnId,
        ITurnEnvelope envelope,
        CancellationToken token)
    {
        var pendingCalls = ExtractToolCalls(providerReturn.Interactions);
        if (pendingCalls.Count == 0)
        {
            return ToolPassOutcome.Continue;
        }

        var reversibilityByName = PlanReviewBuilder.BuildReversibilityLookup(_toolDispatcher.GetDefinitions());
        reversibilityByName[AskUserTool.ToolName] = false;
        reversibilityByName[RecordDecompositionTool.ToolName] = false;

        var guard = _toolBatchGuard ??= new ToolBatchGuard(_options, _seenToolUseIds, _declaredArguments);
        if (!guard.TryFreeze(pendingCalls, reversibilityByName, turnId, out var frozenCalls, out var bindingId, out var rejection))
        {
            EmitRejectedResults(pendingCalls, turnId, rejection);
            return ToolPassOutcome.Continue;
        }
        var frozen = frozenCalls.ToList();

        foreach (var call in frozen)
        {
            _observer?.OnToolCall(CloneCall(call));
        }

        // Session tools are protocol boundaries: each must be the entire response. A mixed
        // response is rejected as a whole so an unreviewed sibling cannot run.
        var sessionCall = frozen.FirstOrDefault(c => IsSessionTool(c.Name));
        if (sessionCall is not null)
        {
            return HandleSessionTool(frozen, guard, bindingId, turnId);
        }

        foreach (var policy in _policies)
        {
            bool allowed;
            string policyError;
            try
            {
                allowed = policy.TryValidate(CloneCalls(frozen), reversibilityByName, out policyError);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A policy that fails is a rejection, never a pass.
                allowed = false;
                policyError = $"Tool response rejected: a host policy failed ({ex.Message}); no call was dispatched.";
            }

            if (!allowed)
            {
                EmitRejectedResults(frozen, turnId, string.IsNullOrWhiteSpace(policyError)
                    ? "Tool response rejected by a host policy; no call was dispatched."
                    : policyError);
                return ToolPassOutcome.Continue;
            }
        }

        var (decomposition, revision) = Decomposition.Snapshot();
        if (_options.RequireDecomposition &&
            !DecompositionGate.Allows(frozen, reversibilityByName, decomposition, out var gateResult))
        {
            EmitResults(frozen, turnId, DecompositionGate.RejectionPayload(gateResult!));
            return ToolPassOutcome.Continue;
        }

        // Reversible batches require a reviewer; read-only batches skip consent.
        if (PlanReviewBuilder.HasAnyReversible(frozen, reversibilityByName))
        {
            if (PlanReviewer is not { } reviewer)
            {
                EmitRejectedResults(frozen, turnId, "Mutation batch rejected: no consent reviewer is installed.");
                return ToolPassOutcome.Cancelled;
            }

            var assistantText = PlanReviewBuilder.ExtractAssistantText(providerReturn.Interactions);
            var prefix = envelope.BindingPrefix;
            var plan = PlanReviewBuilder.BuildPlan(CloneCalls(frozen), assistantText, reversibilityByName, _componentCatalog) with
            {
                BindingId = string.IsNullOrEmpty(prefix) ? bindingId : $"{prefix}:{bindingId}",
                Decomposition = revision > 0 ? decomposition : null,
                DecompositionRevision = revision,
            };

            bool approved;
            try
            {
                approved = await reviewer(plan, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The loop itself was cancelled while the plan was open. The calls are already in
                // history, so each gets its cancellation result before the cancel propagates.
                EmitCancellationResults(frozen, turnId);
                throw;
            }
            catch
            {
                // A reviewer that throws (including its own cancellation) is treated as a cancel.
                approved = false;
            }

            if (!approved)
            {
                EmitCancellationResults(frozen, turnId);
                return ToolPassOutcome.Cancelled;
            }
        }

        if (!guard.TryMaterializeForDispatch(bindingId, turnId, out var dispatchCalls, out var dispatchRejection))
        {
            EmitRejectedResults(frozen, turnId, dispatchRejection);
            return ToolPassOutcome.Cancelled;
        }

        // Host capabilities are attached only now, to the materialized approved copies. The
        // plain copies are kept for synthetic results so no capability can leak through them.
        var withCapabilities = envelope.AttachCapabilities(dispatchCalls);
        if (withCapabilities is null || withCapabilities.Count != dispatchCalls.Count)
        {
            throw new InvalidOperationException("The turn envelope must return one call per materialized call.");
        }

        await DispatchAllAsync(withCapabilities, dispatchCalls, reversibilityByName, turnId, envelope, token).ConfigureAwait(false);
        return ToolPassOutcome.Continue;
    }

    /// <summary>
    /// Handles a response that contains a session tool. The call must be the only one in its
    /// response; it is materialized (binding consumed, immutable copy) before its arguments
    /// are parsed.
    /// </summary>
    private ToolPassOutcome HandleSessionTool(
        IReadOnlyList<AIInteractionToolCall> frozen,
        ToolBatchGuard guard,
        string bindingId,
        string turnId)
    {
        var isAskUser = frozen.Any(c => c.Name == AskUserTool.ToolName);
        var name = isAskUser ? AskUserTool.ToolName : RecordDecompositionTool.ToolName;

        if (frozen.Count != 1)
        {
            // The rejection goes back to the model (the error-repair cap bounds retries); ending
            // the loop here would leave the user with nothing shown.
            EmitRejectedResults(frozen, turnId,
                $"Tool response rejected: {name} must be the only call in its response; no sibling call was dispatched.");
            return ToolPassOutcome.Continue;
        }

        if (!guard.TryMaterializeForDispatch(bindingId, turnId, out var materialized, out var materializeError))
        {
            EmitRejectedResults(frozen, turnId, materializeError);
            return ToolPassOutcome.Cancelled;
        }

        var call = materialized[0];
        if (isAskUser)
        {
            if (!AskUserTool.TryParseQuestions(call.Arguments, out var questions, out var questionError))
            {
                // The question panel keys answers by id and shows each prompt; a blank prompt or a
                // missing/repeated id would leave the user unable to answer. Nothing is shown; the
                // model gets every problem at once and repairs the call (bounded by the repair cap).
                AppendToolResult(ResultFor(call, turnId, new JObject
                {
                    ["status"] = "error",
                    ["message"] = $"{AskUserTool.ToolName} rejected: {questionError} " +
                                  $"Nothing was shown to the user; call {AskUserTool.ToolName} again with every problem fixed (every question needs a unique id such as q1, q2).",
                }));
                return ToolPassOutcome.Continue;
            }

            Clarification.Pause(new PausedState(
                TurnId: turnId,
                ToolUseId: call.Id ?? string.Empty,
                Questions: questions));

            // No tool_result is appended here: the answer (or a decline) supplies the single
            // result for this tool_use when the session resumes.
            return ToolPassOutcome.Paused;
        }

        JObject payload;
        if (RecordDecompositionTool.TryParse(call.Arguments ?? new JObject(), out var recorded, out var parseError))
        {
            var revision = Decomposition.Update(recorded);
            var gate = PillarGate.Evaluate(recorded);
            payload = new JObject
            {
                ["status"] = "ok",
                ["revision"] = revision,
                ["complete"] = gate.Outcome == PillarGateOutcome.Pass,
                ["missingPillars"] = new JArray(gate.MissingPillars.ToArray()),
            };
            AppendToolResult(ResultFor(call, turnId, payload));
            _observer?.OnDecompositionRecorded(recorded, revision);
        }
        else
        {
            payload = new JObject
            {
                ["status"] = "error",
                ["message"] = $"{RecordDecompositionTool.ToolName} rejected: {parseError}. The recorded decomposition is unchanged.",
            };
            AppendToolResult(ResultFor(call, turnId, payload));
        }

        return ToolPassOutcome.Continue;
    }

    private static bool IsSessionTool(string? name) =>
        name == AskUserTool.ToolName || name == RecordDecompositionTool.ToolName;

    /// <summary>
    /// Emit a synthetic tool_result for every pending tool_use in the cancelled pass so the
    /// conversation history stays well-formed (every tool_use has a matching result).
    /// </summary>
    private void EmitCancellationResults(IReadOnlyList<AIInteractionToolCall> pendingCalls, string turnId)
    {
        EmitResults(pendingCalls, turnId, new JObject
        {
            ["success"] = false,
            ["cancelled"] = true,
            ["message"] = "User cancelled plan before execution.",
        });
    }

    private void EmitRejectedResults(IReadOnlyList<AIInteractionToolCall> pendingCalls, string turnId, string message)
    {
        EmitResults(pendingCalls, turnId, new JObject { ["success"] = false, ["rejected"] = true, ["message"] = message });
    }

    private void EmitResults(IReadOnlyList<AIInteractionToolCall> calls, string turnId, JObject payload)
    {
        foreach (var call in calls)
        {
            AppendToolResult(ResultFor(call, turnId, (JObject)payload.DeepClone()));
        }
    }

    private static AIInteractionToolResult ResultFor(AIInteractionToolCall call, string turnId, JObject payload) => new()
    {
        Id = call.Id,
        Name = call.Name,
        Arguments = (JObject)(call.Arguments ?? new JObject()).DeepClone(),
        TurnId = turnId,
        Agent = AIAgent.ToolResult,
        Result = payload,
    };

    /// <summary>
    /// Dispatches the approved calls serially. Each result is scrubbed from a deep copy,
    /// appended to history, surfaced to the observer, and harvested into memory before the next
    /// call is dispatched. On cancellation or timeout, every call that did not return gets a
    /// synthetic result so each tool_use keeps exactly one tool_result, and the cancel propagates.
    /// </summary>
    /// <param name="calls">Materialized calls carrying the envelope's capabilities.</param>
    /// <param name="plainCalls">The same calls without capabilities, index-aligned.</param>
    private async Task DispatchAllAsync(
        IReadOnlyList<AIInteractionToolCall> calls,
        IReadOnlyList<AIInteractionToolCall> plainCalls,
        IReadOnlyDictionary<string, bool> reversibilityByName,
        string turnId,
        ITurnEnvelope envelope,
        CancellationToken token)
    {
        if (_options.MaxToolDispatchDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("MaxToolDispatchDuration must be positive.");
        }

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        budgetCts.CancelAfter(_options.MaxToolDispatchDuration);

        // Calls are intentionally serial: mutation handlers share one document capability and a
        // reviewed call order. Parallel dispatch would reintroduce target drift after approval.
        for (int i = 0; i < calls.Count; i++)
        {
            var call = calls[i];
            var plain = plainCalls[i];
            AIInteractionToolResult raw;
            try
            {
                budgetCts.Token.ThrowIfCancellationRequested();

                if (reversibilityByName.TryGetValue(call.Name ?? string.Empty, out var reversible) && reversible)
                {
                    // Record before entering a host handler. A cancellation or timeout may arrive
                    // after the host action commits but before its task completes; that partial
                    // effect must keep the loop's undo record.
                    _keepEnvelope = true;
                    envelope.BeforeReversibleDispatch(plain);
                }

                raw = await DispatchSafeAsync(call, budgetCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                for (int j = i; j < plainCalls.Count; j++)
                {
                    AppendToolResult(ResultFor(plainCalls[j], turnId, new JObject
                    {
                        ["success"] = false,
                        ["cancelled"] = true,
                        ["message"] = "Cancelled before this call returned a result; the canvas may have changed. Read it before retrying.",
                    }));
                }
                throw;
            }

            AppendToolResult(ScrubbedCopy(raw, plain, turnId, envelope));
        }
    }

    /// <summary>
    /// Deep-copies a dispatcher result (dispatchers may alias the dispatched call's arguments,
    /// which carry host capabilities) and lets the envelope scrub the copy. Id and name always
    /// come from the call so each tool_use keeps exactly one matching tool_result.
    /// </summary>
    private static AIInteractionToolResult ScrubbedCopy(
        AIInteractionToolResult raw,
        AIInteractionToolCall plain,
        string turnId,
        ITurnEnvelope envelope)
    {
        var copy = new AIInteractionToolResult
        {
            Id = plain.Id,
            Name = plain.Name,
            Arguments = (JObject)(raw.Arguments ?? new JObject()).DeepClone(),
            Result = (JObject)(raw.Result ?? new JObject()).DeepClone(),
            Reasoning = raw.Reasoning,
            TurnId = turnId,
            Agent = AIAgent.ToolResult,
            Time = raw.Time,
        };
        envelope.ScrubResult(copy);
        return copy;
    }

    /// <summary>
    /// Dispatches one tool call and normalises exceptions into an error-shaped
    /// <see cref="AIInteractionToolResult"/> so the conversation can continue.
    /// </summary>
    private async Task<AIInteractionToolResult> DispatchSafeAsync(AIInteractionToolCall call, CancellationToken token)
    {
        try
        {
            return await _toolDispatcher.DispatchAsync(call, token).ConfigureAwait(false)
                ?? new AIInteractionToolResult { Result = new JObject { ["success"] = false, ["error"] = "Tool returned no result." } };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AIInteractionToolResult
            {
                Result = new JObject
                {
                    ["success"] = false,
                    ["error"] = ex.Message,
                },
            };
        }
    }

    /// <summary>
    /// Appends one (already scrubbed) tool result to history, gives the observer its own deep
    /// copies, and harvests canvas mentions into <see cref="Memory"/>.
    /// </summary>
    private void AppendToolResult(AIInteractionToolResult result)
    {
        lock (_historyGate) { _history.Add(result); }
        if (_observer is not null)
        {
            _observer.OnToolResult(CloneResult(result));
            _observer.OnInteractionCompleted(CloneResult(result));
        }
        ExtractMentions(result);
    }

    /// <summary>
    /// Returns the tool-call interactions in <paramref name="interactions"/> while excluding any
    /// <see cref="AIInteractionToolResult"/> (which inherits from <see cref="AIInteractionToolCall"/>).
    /// </summary>
    private static List<AIInteractionToolCall> ExtractToolCalls(IEnumerable<IAIInteraction> interactions)
    {
        var result = new List<AIInteractionToolCall>();
        foreach (var i in interactions)
        {
            if (i is AIInteractionToolCall call && call is not AIInteractionToolResult)
            {
                result.Add(call);
            }
        }
        return result;
    }

    /// <summary>
    /// Stamps <paramref name="turnId"/> onto every interaction that does not already have one,
    /// then appends them to history and notifies the observer with a copy.
    /// </summary>
    private void StampAndAppend(IEnumerable<IAIInteraction> interactions, string turnId)
    {
        foreach (var interaction in interactions)
        {
            if (interaction is not AIInteractionBase baseInteraction)
            {
                // Defensive skip: a third-party provider may yield something that cannot be
                // stored in the strongly-typed history list. Still notify the observer.
                _observer?.OnInteractionCompleted(interaction);
                continue;
            }

            if (string.IsNullOrWhiteSpace(baseInteraction.TurnId))
            {
                baseInteraction.TurnId = turnId;
            }

            lock (_historyGate) { _history.Add(baseInteraction); }
            _observer?.OnInteractionCompleted(ObserverCopy(baseInteraction));
        }
    }

    private static IAIInteraction ObserverCopy(AIInteractionBase interaction) => interaction switch
    {
        AIInteractionToolResult result => CloneResult(result),
        AIInteractionToolCall call => CloneCall(call),
        AIInteractionText text => new AIInteractionText
        {
            Agent = text.Agent,
            Content = text.Content,
            Reasoning = text.Reasoning,
            TurnId = text.TurnId,
            Time = text.Time,
        },
        _ => interaction,
    };

    private static AIInteractionToolCall CloneCall(AIInteractionToolCall call) => new()
    {
        Id = call.Id,
        Name = call.Name,
        Arguments = (JObject)(call.Arguments ?? new JObject()).DeepClone(),
        Reasoning = call.Reasoning,
        Agent = call.Agent,
        TurnId = call.TurnId,
        Time = call.Time,
    };

    private static IReadOnlyList<AIInteractionToolCall> CloneCalls(IReadOnlyList<AIInteractionToolCall> calls) =>
        calls.Select(CloneCall).ToList();

    private static AIInteractionToolResult CloneResult(AIInteractionToolResult result) => new()
    {
        Id = result.Id,
        Name = result.Name,
        Arguments = (JObject)(result.Arguments ?? new JObject()).DeepClone(),
        Result = (JObject)(result.Result ?? new JObject()).DeepClone(),
        Reasoning = result.Reasoning,
        Agent = result.Agent,
        TurnId = result.TurnId,
        Time = result.Time,
    };

    private static string BuildEnvelopeLabel(string turnId)
    {
        var short8 = turnId.Length >= 8 ? turnId[..8] : turnId;
        return $"Parti chat turn {short8}";
    }

    /// <summary>
    /// Builds an error <see cref="AIReturn"/> attached to the given request.
    /// </summary>
    private static AIReturn MakeError(string message, AIRequestCall? request)
    {
        var ret = new AIReturn();
        ret.CreateError(message, request);
        return ret;
    }

    /// <summary>
    /// Serialize clarification answers into the tool_result payload the provider will see when
    /// the session resumes: <c>{"status":"resumed","answers":[{"id":"q1","answer":"…"}, …]}</c>, plus a
    /// <c>note</c> when the host supplied one on resume.
    /// </summary>
    private static JObject BuildAnswersPayload(IReadOnlyList<ClarificationAnswer> answers, string? hostNote)
    {
        var arr = new JArray();
        foreach (var a in answers)
        {
            arr.Add(new JObject
            {
                ["id"] = a.QuestionId,
                ["answer"] = a.Answer,
            });
        }
        var payload = new JObject
        {
            ["status"] = "resumed",
            ["answers"] = arr,
        };
        if (hostNote is not null)
        {
            payload["note"] = hostNote;
        }
        return payload;
    }

    /// <summary>
    /// Scan a scrubbed tool_result for canvas mentions and fold them into <see cref="Memory"/>.
    /// Handles single add results (<c>instanceGuid</c> + <c>canonicalName</c>) and snapshot lists
    /// (<c>nodes[]</c> of <c>{id, n}</c>). Only stable instance GUIDs are recorded.
    /// </summary>
    private void ExtractMentions(AIInteractionToolResult result)
    {
        if (result.Result is not JObject obj)
        {
            return;
        }

        var now = result.Time == default ? DateTime.UtcNow : result.Time;

        var instanceGuid = obj["instanceGuid"]?.ToString();
        if (IsGuid(instanceGuid))
        {
            var canonicalName = obj["canonicalName"]?.ToString() ?? obj["name"]?.ToString() ?? obj["nickname"]?.ToString();
            Memory.Mention(instanceGuid!, canonicalName ?? string.Empty, now);
        }

        foreach (var key in new[] { "nodes", "components", "items" })
        {
            if (obj[key] is not JArray arr)
            {
                continue;
            }

            foreach (var entry in arr)
            {
                if (entry is not JObject node) continue;
                var nodeId = node["id"]?.ToString() ?? node["instanceGuid"]?.ToString();
                if (!IsGuid(nodeId)) continue;
                var nodeName = node["n"]?.ToString() ?? node["name"]?.ToString()
                    ?? node["canonicalName"]?.ToString();
                Memory.Mention(nodeId!, nodeName ?? string.Empty, now);
            }
        }
    }

    private static bool IsGuid(string? value) => !string.IsNullOrWhiteSpace(value) && Guid.TryParse(value, out _);

    /// <summary>
    /// Internal outcome of a single tool pass — the loop continues with another provider turn,
    /// the session paused for clarification, or the pass ended the loop cleanly (plan cancelled
    /// or rejected before dispatch).
    /// </summary>
    private enum ToolPassOutcome
    {
        Continue,
        Paused,
        Cancelled,
    }
}
