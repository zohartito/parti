// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Grasshopper;
using Grasshopper.Kernel;
using Parti.Core.Design;
using Parti.Core.Grasshopper;
using Parti.Core.Grasshopper.Chat;
using Parti.Core.Prompts;
using Parti.Core.Sessions;
using Parti.Providers.Cli;
using Parti.Providers.Ollama;

namespace Parti.Components.Chat;

/// <summary>
/// Floating Eto.Forms chat window. Implements <see cref="IChatView"/> — the
/// <see cref="ChatController"/> drives it via plain method calls; every mutation
/// of an Eto control is marshalled onto the UI thread via
/// <see cref="Application.AsyncInvoke(Action)"/>.
/// <para>
/// Besides the transcript it shows a dimmed reasoning pane, the recorded design brief
/// (decomposition), the components the user has locked (with a human-only Unlock), and a question
/// panel when the model pauses on <c>ask_user</c>. Provider settings live in
/// <see cref="ProviderSettingsDialog"/>; the rules behind them in <see cref="ProviderSettings"/>.
/// </para>
///
/// Lifetime: one singleton instance per Rhino session, shared between every
/// <see cref="PartiChatComponent"/> placed on the canvas. <see cref="ShowOrBringToFront"/>
/// creates the form on first use and brings it to front on subsequent use.
/// </summary>
public sealed class ChatForm : Form, IChatView
{
    private const int MaxPickerComponents = 500;

    private static ChatForm? _instance;
    private static readonly object _instanceLock = new();

    private readonly TextArea _history;
    private readonly TextArea _reasoning;
    private readonly GroupBox _decompositionBox;
    private readonly Label _decompositionText;
    private readonly GroupBox _locksBox;
    private readonly ListBox _locksList;
    private readonly Button _unlockButton;
    private readonly QuestionPanel _questionPanel;
    private readonly TextBox _input;
    private readonly Button _sendButton;
    private readonly Button _cancelButton;
    private readonly Button _resetButton;
    private readonly Button _settingsButton;
    private readonly Label _statusLabel;

    // Owns the controller, its provider and the one lock registry that survives controller
    // rebuilds. Provider state is in memory only (no preferences file): the configuration is null
    // until the first model is known; _saved remembers the last configuration per provider.
    private readonly ChatControllerHost _host;
    private readonly ReasoningPaneBuffer _reasoningBuffer = new();
    private int _reasoningPaneGeneration; // UI thread only: the buffer generation the pane shows
    private bool _closing;
    private readonly Dictionary<ProviderSelection, ProviderConfiguration> _saved = new();
    private bool _discovering;
    private int _warmGeneration;
    private bool _lastLoopFailed;

    private ChatForm()
    {
        Title = "Parti Chat";
        ClientSize = new Size(560, 760);
        _host = new ChatControllerHost(BuildController);
        Resizable = true;
        MinimumSize = new Size(400, 480);

        _history = new TextArea
        {
            ReadOnly = true,
            Wrap = true,
            Font = new Font(FontFamilies.Monospace, 10),
        };

        // Reasoning is shown dimmed and apart from the reply; it is never part of the transcript.
        _reasoning = new TextArea
        {
            ReadOnly = true,
            Wrap = true,
            Height = 90,
            TextColor = SystemColors.DisabledText,
            Font = new Font(FontFamilies.Monospace, 9),
        };
        var reasoningExpander = new Expander
        {
            Header = new Label { Text = "Reasoning", TextColor = SystemColors.DisabledText },
            Expanded = false,
            Content = _reasoning,
        };

        _decompositionText = new Label { Wrap = WrapMode.Word };
        _decompositionBox = new GroupBox
        {
            Text = "Design brief",
            Content = new Panel { Padding = new Padding(6), Content = _decompositionText },
            Visible = false,
        };

        _locksList = new ListBox { Height = 70 };
        _unlockButton = new Button { Text = "Unlock" };
        _unlockButton.Click += (_, _) => UnlockSelected();
        _locksBox = new GroupBox
        {
            Text = "Locked components",
            Content = new TableLayout(new TableRow(new TableCell(_locksList, true), new TableCell(_unlockButton)))
            {
                Spacing = new Size(6, 0),
                Padding = new Padding(6),
            },
            Visible = false,
        };

        _questionPanel = new QuestionPanel();
        _questionPanel.Submitted += SubmitAnswers;

        _input = new TextBox { PlaceholderText = "Type a message and press Enter..." };
        _input.KeyDown += OnInputKeyDown;

        _sendButton = new Button { Text = "Send" };
        _sendButton.Click += (_, _) => SubmitInput();

        _cancelButton = new Button { Text = "Cancel", Enabled = false };
        _cancelButton.Click += (_, _) => _host.Controller?.Cancel();

        _resetButton = new Button { Text = "Reset" };
        _resetButton.Click += async (_, _) =>
        {
            if (_host.Controller is not null)
            {
                await _host.Controller.ResetAsync().ConfigureAwait(true);
            }
        };

        _settingsButton = new Button { Text = "Settings" };
        _settingsButton.Click += (_, _) => ProviderSettingsDialog.Show(
            this,
            _host.Configuration?.Provider ?? ProviderSettings.DefaultProvider,
            provider => _saved.TryGetValue(provider, out var saved) ? saved : null,
            ApplyConfiguration);

        _statusLabel = new Label { Text = "Ready." };

        var buttonRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Padding = new Padding(0, 4),
            Items =
            {
                new StackLayoutItem(_sendButton),
                new StackLayoutItem(_cancelButton),
                new StackLayoutItem(null, true),
                new StackLayoutItem(_resetButton),
                new StackLayoutItem(_settingsButton),
            },
        };

        Content = new TableLayout
        {
            Spacing = new Size(6, 6),
            Padding = new Padding(10),
            Rows =
            {
                new TableRow(new TableCell(_history, true)) { ScaleHeight = true },
                new TableRow(new TableCell(reasoningExpander, true)),
                new TableRow(new TableCell(_decompositionBox, true)),
                new TableRow(new TableCell(_locksBox, true)),
                new TableRow(new TableCell(_questionPanel, true)),
                new TableRow(new TableCell(_input, true)),
                new TableRow(new TableCell(buttonRow, true)),
                new TableRow(new TableCell(_statusLabel, true)),
            },
        };

        // Build the controller lazily on first show so we pick up the current
        // environment (API keys) at the moment the user actually opens the window.
        Closed += OnClosed;
    }

    /// <summary>
    /// Returns the singleton window, creating it (and its underlying controller) on
    /// first call. Brings it to front on subsequent calls. Safe to call from any
    /// thread; all UI work hops to <see cref="Application.Instance"/>.
    /// </summary>
    public static ChatForm ShowOrBringToFront()
    {
        ChatForm form;
        bool closing;
        lock (_instanceLock)
        {
            if (_instance is null)
            {
                _instance = new ChatForm();
                _instance.EnsureController();
            }
            form = _instance;
            closing = form._closing;
        }

        if (closing)
        {
            // The previous controller still owns a cancelling turn. Keep the singleton closed
            // until its quiescence path releases it; do not expose a replacement controller.
            return form;
        }
        RunOnUi(() =>
        {
            if (!form.Visible)
            {
                form.Show();
            }
            form.BringToFront();
            form.Focus();
        });

        return form;
    }

    /// <summary>
    /// True when the singleton window is currently visible on screen.
    /// </summary>
    public static bool IsOpen
    {
        get
        {
            lock (_instanceLock)
            {
                return _instance is not null && _instance.Visible;
            }
        }
    }

    /// <summary>
    /// Closes the singleton chat form if it exists and is visible. Safe to call
    /// from any thread; marshals to Eto's UI thread. No-op when no form is open.
    /// Called by <see cref="PartiChatComponent.RemovedFromDocument"/> when the
    /// last Parti Chat component leaves the document.
    /// </summary>
    public static void CloseIfOpen()
    {
        ChatForm? form;
        lock (_instanceLock)
        {
            form = _instance;
        }

        if (form is null)
        {
            return;
        }

        RunOnUi(() =>
        {
            try
            {
                form.Close();
            }
            catch
            {
                // Form may already be closing; swallow.
            }
        });
    }

    // --- IChatView ---------------------------------------------------------

    /// <inheritdoc />
    public void AppendUserMessage(string text)
    {
        var generation = _reasoningBuffer.Clear();
        RunOnUi(() =>
        {
            ResetReasoningPane(generation);
            if (_history.Text.Length > 0)
            {
                _history.Append("\n\n", scrollToCursor: false);
            }
            _history.Append("you> " + text + "\n\nassistant> ", scrollToCursor: true);
        });
    }

    /// <inheritdoc />
    public void AppendAssistantText(string delta)
    {
        RunOnUi(() => _history.Append(delta, scrollToCursor: true));
    }

    /// <inheritdoc />
    public void AppendReasoningText(string delta)
    {
        // Batched: at most one pane update per ReasoningPaneBuffer.FlushInterval, pane capped.
        if (_reasoningBuffer.Append(delta))
        {
            _ = Task.Delay(ReasoningPaneBuffer.FlushInterval)
                .ContinueWith(_ => RunOnUi(FlushReasoning), TaskScheduler.Default);
        }
    }

    /// <inheritdoc />
    public void AppendToolActivity(string line)
    {
        RunOnUi(() =>
        {
            StartOwnLine();
            _history.Append(line + "\n", scrollToCursor: true);
        });
    }

    /// <inheritdoc />
    public void BeginAssistantTurn()
    {
        RunOnUi(() =>
        {
            StartOwnLine();
            _history.Append("\nassistant> ", scrollToCursor: true);
        });
    }

    /// <inheritdoc />
    public void AppendNotice(string text)
    {
        RunOnUi(() =>
        {
            StartOwnLine();
            _history.Append(text + "\n", scrollToCursor: true);
        });
    }

    /// <inheritdoc />
    public void ShowQuestions(IReadOnlyList<Question> questions)
    {
        RunOnUi(() => _questionPanel.ShowQuestions(questions, ReadCanvasComponents()));
    }

    /// <inheritdoc />
    public void ShowDecomposition(Decomposition decomposition, int revision)
    {
        RunOnUi(() =>
        {
            _decompositionBox.Text = $"Design brief (revision {revision})";
            _decompositionText.Text = PlanText.FormatDecomposition(decomposition);
            _decompositionBox.Visible = true;
        });
    }

    /// <inheritdoc />
    public void SetBusy(bool busy)
    {
        RunOnUi(() =>
        {
            _sendButton.Enabled = !busy && _host.Controller is not null && HasUsableApiKey();
            _cancelButton.Enabled = busy;
            _input.Enabled = !busy;
            _resetButton.Enabled = !busy;
            _unlockButton.Enabled = !busy;
            if (busy)
            {
                // A new loop consumes any pending answers; the panel returns if the model asks again.
                _lastLoopFailed = false;
                _questionPanel.Clear();
                _statusLabel.Text = "Streaming...";
                return;
            }

            RefreshLocks();
            _statusLabel.Text = _questionPanel.Visible
                ? "Waiting for your answer."
                : _lastLoopFailed ? "Error." : ReadyText();
        });
    }

    /// <inheritdoc />
    public void ShowError(string message)
    {
        RunOnUi(() =>
        {
            _lastLoopFailed = true;
            _statusLabel.Text = "Error.";
            if (_history.Text.Length > 0)
            {
                _history.Append("\n", scrollToCursor: false);
            }
            _history.Append($"\n[error] {message}\n", scrollToCursor: true);
        });
    }

    /// <inheritdoc />
    public void ClearHistory()
    {
        var generation = _reasoningBuffer.Clear();
        RunOnUi(() =>
        {
            _history.Text = string.Empty;
            ResetReasoningPane(generation);
            _questionPanel.Clear();
            _decompositionBox.Visible = false;
            _decompositionText.Text = string.Empty;
            _lastLoopFailed = false;
            RefreshLocks();
            _statusLabel.Text = ReadyText();
        });
    }

    // --- UI helpers --------------------------------------------------------

    // UI thread only: makes the next append start on its own transcript line.
    private void StartOwnLine()
    {
        var text = _history.Text;
        if (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal))
        {
            _history.Append("\n", scrollToCursor: false);
        }
    }

    // UI thread only: clears the pane once per buffer generation, whichever of the queued reset or
    // an earlier-scheduled flush carrying the new generation's text runs first.
    private void ResetReasoningPane(int generation)
    {
        if (generation > _reasoningPaneGeneration)
        {
            _reasoningPaneGeneration = generation;
            _reasoning.Text = string.Empty;
        }
    }

    // UI thread only: moves the batched reasoning text into the pane, keeping its tail.
    private void FlushReasoning()
    {
        var added = _reasoningBuffer.Drain(out var generation);
        ResetReasoningPane(generation);
        if (added.Length == 0)
        {
            return;
        }

        var current = _reasoning.Text ?? string.Empty;
        if (current.Length + added.Length <= ReasoningPaneBuffer.MaxCharacters)
        {
            _reasoning.Append(added, scrollToCursor: true);
            return;
        }

        _reasoning.Text = ReasoningPaneBuffer.Combine(current, added);
        _reasoning.CaretIndex = _reasoning.Text.Length;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter submits; Shift+Enter is not supported on a single-line TextBox.
        if (e.Key == Keys.Enter)
        {
            e.Handled = true;
            SubmitInput();
        }
    }

    private void SubmitInput()
    {
        var text = _input.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        if (_host.Controller is null)
        {
            ShowError("No model is selected yet. Open Settings to choose a provider and model.");
            return;
        }
        if (!HasUsableApiKey())
        {
            var variable = ProviderSettings.ApiKeyEnvironmentVariable(_host.Configuration!.Provider);
            ShowError($"{variable} is not set. Enter a key in Settings, or switch the provider to Ollama.");
            return;
        }

        _input.Text = string.Empty;
        // Fire-and-forget; the controller owns threading discipline. While paused on a single text
        // question the controller treats this text as the answer.
        _ = _host.Controller.SendAsync(text);
    }

    private void SubmitAnswers(IReadOnlyList<Question> questions, IReadOnlyList<ClarificationAnswer> answers)
    {
        var controller = _host.Controller;
        if (controller is null || controller.IsBusy || controller.PendingQuestions is null)
        {
            ShowError("No question is waiting for an answer.");
            _questionPanel.Clear();
            return;
        }

        _questionPanel.Clear();
        AppendUserMessage(QuestionAnswers.Summarize(questions, answers));
        var resumed = controller.AnswerAsync(answers);
        if (resumed.IsCompletedSuccessfully && !resumed.Result)
        {
            ShowError("The answers could not be sent; no question is pending.");
        }
    }

    private void UnlockSelected()
    {
        var controller = _host.Controller;
        if (controller is null || controller.IsBusy || !Guid.TryParse(_locksList.SelectedKey, out var id))
        {
            return;
        }

        controller.Unlock(id);
        _history.Append($"\n[unlocked] {DescribeCanvasObject(id.ToString("D"))}\n", scrollToCursor: true);
        RefreshLocks();
    }

    // UI thread only. Locks change through the model's gh_component_lock calls, so this runs at the
    // end of every loop, after a reset, and after an unlock.
    private void RefreshLocks()
    {
        var locked = _host.Controller?.LockedComponents ?? Array.Empty<string>();
        _locksList.Items.Clear();
        foreach (var id in locked)
        {
            _locksList.Items.Add(new ListItem { Text = DescribeCanvasObject(id), Key = id });
        }
        _locksBox.Visible = locked.Count > 0;
    }

    private static string DescribeCanvasObject(string instanceId)
    {
        try
        {
            if (Guid.TryParse(instanceId, out var guid)
                && Instances.ActiveCanvas?.Document?.FindObject(guid, true) is { } obj)
            {
                return QuestionAnswers.FormatComponentChoice(guid, obj.Name, obj.NickName);
            }
        }
        catch
        {
            // The canvas may be closing; fall back to the bare id.
        }
        return instanceId;
    }

    // UI thread only: reads the active canvas for the component picker.
    private static IReadOnlyList<CanvasComponentChoice> ReadCanvasComponents()
    {
        try
        {
            var document = Instances.ActiveCanvas?.Document;
            if (document is null)
            {
                return Array.Empty<CanvasComponentChoice>();
            }

            return document.Objects
                .OfType<IGH_ActiveObject>()
                .Take(MaxPickerComponents)
                .Select(obj => new CanvasComponentChoice(obj.InstanceGuid, obj.Name, obj.NickName))
                .ToList();
        }
        catch
        {
            // The picker stays editable, so a GUID can still be typed.
            return Array.Empty<CanvasComponentChoice>();
        }
    }

    private string ReadyText()
    {
        var configuration = _host.Configuration;
        return configuration is null
            ? "Ready."
            : ProviderSettings.IsSubscription(configuration.Provider)
                ? ProviderSettings.Describe(configuration)
                : $"Ready: {ProviderSettings.Describe(configuration)}";
    }

    private void SetStatus(string text) => RunOnUi(() => _statusLabel.Text = text);

    /// <summary>
    /// Settings-dialog apply step (see <see cref="ChatControllerHost.Apply"/>). An unchanged
    /// configuration rebuilds nothing. A real change swaps the provider and controller
    /// transactionally: when the replacement cannot be built, or a turn is still running, the
    /// previous configuration and controller stay in place and the dialog stays open. After a
    /// swap the transcript, brief and questions are cleared together and the transcript says so;
    /// component locks are kept.
    /// </summary>
    private bool ApplyConfiguration(ProviderConfiguration next)
    {
        if (_closing)
        {
            return false;
        }

        switch (_host.Apply(next))
        {
            case ApplyOutcome.Unchanged:
                _saved[next.Provider] = next;
                return true;

            case ApplyOutcome.Busy:
                // Keep settings and controller intact while a previous turn owns a document lease.
                ShowError("Wait for the current Parti turn to finish or cancel before changing settings.");
                return false;

            case ApplyOutcome.Failed:
                ShowError($"Failed to initialise chat: {_host.LastError}");
                return false;

            case ApplyOutcome.Replaced:
                // The new controller starts a new conversation: clear everything that belonged to
                // the old one at once, then say what happened.
                ClearHistory();
                AppendNotice(
                    $"[settings] Now using {ProviderSettings.Describe(next)}. " +
                    "The conversation was restarted (history, design brief and pending questions cleared); " +
                    "component locks were kept.");
                break;
        }

        _saved[next.Provider] = next;
        SetBusy(false);
        StartWarmUp();
        return true;
    }

    // First open with Ollama and no model chosen yet: list the local server's models and pick
    // qwen3.6:27b, gemma4:31b-65k, or the first installed tag.
    private async void DiscoverDefaultModel()
    {
        if (_discovering)
        {
            return;
        }
        _discovering = true;

        var endpoint = OllamaProviderOptions.DefaultBaseUrl;
        SetStatus($"Looking for Ollama models at {endpoint}...");
        string? model = null;
        string? failure = null;
        try
        {
            using var provider = new OllamaProvider(new OllamaProviderOptions { BaseUrl = endpoint });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var models = await provider.ListModelsAsync(cts.Token).ConfigureAwait(false);
            model = ProviderSettings.PreselectModel(models);
            failure = model is null ? "no models installed" : null;
        }
        catch (Exception ex)
        {
            failure = ex is OperationCanceledException ? "timed out" : ex.Message;
        }

        RunOnUi(() =>
        {
            _discovering = false;
            if (_closing || _host.Configuration is not null)
            {
                return;
            }
            if (model is null)
            {
                _statusLabel.Text = $"No Ollama model at {endpoint} ({failure}). Open Settings to choose a provider, endpoint, and model.";
                return;
            }
            ApplyConfiguration(new ProviderConfiguration(ProviderSelection.Ollama, model, endpoint, ApiKeyOverride: null));
        });
    }

    // Loads the selected Ollama model in the background so the first reply does not pay the
    // cold-load time. Fire-and-forget; only the latest warm-up may update the status line.
    private void StartWarmUp()
    {
        var ollama = _host.Handle?.Ollama;
        var configuration = _host.Configuration;
        if (ollama is null || configuration is null)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _warmGeneration);
        SetStatus($"Loading {configuration.Model} on {ollama.DefaultServerUrl.Host}...");
        _ = Task.Run(async () =>
        {
            bool loaded;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ProviderSettings.LocalTimeoutSeconds));
                loaded = await ollama.WarmAsync(configuration.Model, cts.Token).ConfigureAwait(false);
            }
            catch
            {
                loaded = false;
            }

            RunOnUi(() =>
            {
                if (generation != Volatile.Read(ref _warmGeneration) || _host.Controller?.IsBusy == true || _closing)
                {
                    return;
                }
                _statusLabel.Text = loaded
                    ? $"{ReadyText()} (loaded)"
                    : $"Could not preload {configuration.Model}; the first reply may be slow.";
            });
        });
    }

    /// <summary>
    /// v0.7 plan-preview bridge: called from a background thread by <see cref="ChatController"/>
    /// whenever a reversible tool batch is about to run. Marshals the modal dialog back onto the
    /// Eto UI thread and resolves the returned <see cref="Task{Boolean}"/> with Approve/Cancel.
    /// When <paramref name="ct"/> fires (Cancel, Reset, close) the dialog closes itself, because
    /// <see cref="ChatController.ResetAsync"/> waits for the loop that is waiting on it.
    /// </summary>
    private Task<bool> InvokePlanPreviewDialogAsync(ToolCallPlan plan, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // If the token is already cancelled, short-circuit to Cancel.
        if (ct.IsCancellationRequested)
        {
            tcs.TrySetResult(false);
            return tcs.Task;
        }

        var app = Application.Instance;
        if (app is null)
        {
            // Approval cannot be displayed, so fail closed rather than silently authorising a
            // canvas mutation from a background thread.
            tcs.TrySetResult(false);
            return tcs.Task;
        }

        // Resolve to Cancel as soon as the token fires, even before the dialog has been shown.
        // Register returns a disposable, but we keep it alive until the TCS resolves so a late
        // cancel still fires on the TCS before the caller unwinds.
        CancellationTokenRegistration registration = ct.Register(() => tcs.TrySetResult(false));
        tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);

        app.AsyncInvoke(() =>
        {
            try
            {
                // Name each delete target from the pinned document (display only).
                var shown = PlanReviewBuilder.WithInstanceLabels(plan, id => CanvasObjectLabels.Describe(plan.BindingId, id));
                bool approved = PlanPreviewDialog.ShowPlan(this, shown, ct);
                tcs.TrySetResult(approved);
            }
            catch
            {
                tcs.TrySetResult(false);
            }
        });

        return tcs.Task;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        var controller = _host.Controller;
        if (controller?.IsBusy == true)
        {
            controller.Cancel();
            _ = ReleaseAfterControllerQuiescesAsync(controller);
            return;
        }
        ReleaseClosedInstance(controller);
    }

    private async Task ReleaseAfterControllerQuiescesAsync(ChatController controller)
    {
        // Do not clear the singleton or dispose its lease-owning controller until SendAsync has
        // observed cancellation and completed its finally block.
        while (controller.IsBusy)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
        ReleaseClosedInstance(controller);
    }

    private void ReleaseClosedInstance(ChatController? controller)
    {
        try { controller?.Dispose(); } catch { /* defensive */ }
        lock (_instanceLock)
        {
            if (ReferenceEquals(_instance, this))
            {
                _instance = null;
            }
        }
    }

    private void EnsureController()
    {
        if (_host.Controller is not null || _closing)
        {
            return;
        }

        // No model yet: Claude on the user's subscription when the claude CLI is found, otherwise
        // look for a model on the default local Ollama server. That result, or the Settings dialog,
        // builds the first controller through ApplyConfiguration.
        if (_host.Configuration is null)
        {
            if (ProviderSettings.DefaultSubscription(name => CliExecutableResolver.Default.Resolve(name, null)) is { } subscription)
            {
                ApplyConfiguration(subscription);
            }
            else
            {
                DiscoverDefaultModel();
            }
        }
    }

    /// <summary>
    /// Builds the product controller for <paramref name="configuration"/> over the window's one
    /// lock registry. Called by <see cref="ChatControllerHost.Apply"/>, which keeps the previous
    /// controller published until this returns, so a failed construction cannot strand a live
    /// document lease behind a half-configured controller.
    /// </summary>
    private ChatControllerBuild BuildController(ProviderConfiguration configuration, ComponentLockRegistry locks)
    {
        var handle = ProviderSettings.CreateProvider(configuration);
        try
        {
            // The real Grasshopper undo factory resolves the active document lazily, on the UI
            // thread, at the start of every loop.
            IUndoScopeFactory undoFactory = new GrasshopperUndoScopeFactory();

            // The plan-preview reviewer is called from a background thread; it marshals to the Eto
            // UI thread and signals completion through a TaskCompletionSource. Canvas mutations
            // always require a visible consent decision.
            var controller = PartiComposition.CreateController(
                new PartiCompositionOptions(
                    Provider: handle.Provider,
                    UndoFactory: undoFactory,
                    Skills: new EmbeddedResourceSkillSource(typeof(ChatForm).Assembly),
                    Catalog: new GrasshopperComponentCatalog(),
                    Reviewer: InvokePlanPreviewDialogAsync,
                    Model: configuration.Model,
                    MaxTokens: ProviderSettings.DefaultMaxTokens,
                    ApiKey: ProviderSettings.SessionApiKey(configuration),
                    Locks: locks,
                    CanvasProbe: new GrasshopperCanvasStateProbe()),
                this);
            return new ChatControllerBuild(controller, handle);
        }
        catch
        {
            try { (handle.Provider as IDisposable)?.Dispose(); } catch { /* defensive */ }
            throw;
        }
    }

    private bool HasUsableApiKey()
    {
        var configuration = _host.Configuration;
        if (configuration is null)
        {
            return false;
        }
        // Ollama and custom servers need no key; the hosted APIs do.
        return !ProviderSettings.RequiresApiKey(configuration.Provider)
            || ProviderSettings.ResolveApiKey(configuration.Provider, configuration.ApiKeyOverride) is not null;
    }

    private static void RunOnUi(Action action)
    {
        var app = Application.Instance;
        if (app is null)
        {
            // Outside Eto (should not happen inside Rhino). Run inline.
            action();
            return;
        }
        app.AsyncInvoke(action);
    }
}
