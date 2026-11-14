// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Threading;
using Eto.Drawing;
using Eto.Forms;
using Parti.Providers.Cli;
using Parti.Providers.Ollama;

namespace Parti.Components.Chat;

/// <summary>
/// Provider settings: provider, endpoint, model (listed from Ollama), an in-memory API key, and for
/// the subscription CLIs the effort and an optional executable path.
/// Save validates through <see cref="ProviderSettings.TryValidate"/> and hands the result to the
/// owner's apply callback; the dialog stays open when validation or the apply step refuses it.
/// Eto.Forms only. Must be shown on the UI thread.
/// </summary>
internal sealed class ProviderSettingsDialog : Dialog
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<ProviderSelection, ProviderConfiguration?> _savedFor;
    private readonly Func<ProviderConfiguration, bool> _apply;
    private readonly DropDown _provider = new();
    private readonly TextBox _endpoint = new();
    private readonly ComboBox _model = new() { AutoComplete = true };
    private readonly Button _refresh = new() { Text = "List models" };
    private readonly DropDown _effort = new();
    private readonly TextBox _executable = new();
    private readonly PasswordBox _apiKey = new();
    private readonly Label _apiKeyLabel = new();
    private readonly Label _status = new() { Wrap = WrapMode.Word };
    private int _listGeneration;
    private bool _closed;

    private ProviderSettingsDialog(
        ProviderSelection initial,
        Func<ProviderSelection, ProviderConfiguration?> savedFor,
        Func<ProviderConfiguration, bool> apply)
    {
        _savedFor = savedFor;
        _apply = apply;

        Title = "Parti Chat Settings";
        MinimumSize = new Size(460, 300);
        Padding = new Padding(10);
        Resizable = true;

        foreach (ProviderSelection provider in Enum.GetValues(typeof(ProviderSelection)))
        {
            _provider.Items.Add(new ListItem { Text = ProviderSettings.DisplayName(provider), Key = provider.ToString() });
        }
        _provider.SelectedKey = initial.ToString();
        _provider.SelectedKeyChanged += (_, _) => LoadProvider(SelectedProvider);
        _refresh.Click += (_, _) => ListOllamaModels();

        // Consent is a security boundary, not an optional convenience setting.
        var previewCheck = new CheckBox
        {
            Text = "Plan approval required for canvas changes",
            Checked = true,
            Enabled = false,
        };

        var save = new Button { Text = "Save" };
        var cancel = new Button { Text = "Cancel" };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        Closed += (_, _) => _closed = true;

        DefaultButton = save;
        AbortButton = cancel;
        Content = new TableLayout
        {
            Spacing = new Size(6, 6),
            Rows =
            {
                new TableRow(new Label { Text = "Provider" }),
                new TableRow(new TableCell(_provider, true)),
                new TableRow(new Label { Text = "Endpoint" }),
                new TableRow(new TableCell(_endpoint, true)),
                new TableRow(new Label { Text = "Model" }),
                new TableRow(new TableLayout(new TableRow(new TableCell(_model, true), new TableCell(_refresh))) { Spacing = new Size(6, 0) }),
                new TableRow(new Label { Text = "Effort (subscription CLIs)" }),
                new TableRow(new TableCell(_effort, true)),
                new TableRow(new Label { Text = "CLI executable (optional)" }),
                new TableRow(new TableCell(_executable, true)),
                new TableRow(_apiKeyLabel),
                new TableRow(new TableCell(_apiKey, true)),
                new TableRow(new TableCell(previewCheck, true)),
                new TableRow(new TableCell(_status, true)),
                new TableRow(new TableLayout(new TableRow(new TableCell(null, true), new TableCell(cancel), new TableCell(save))) { Spacing = new Size(6, 0) }),
            },
        };

        LoadProvider(initial);
    }

    /// <summary>
    /// Shows the dialog modally. <paramref name="apply"/> runs on Save with a validated
    /// configuration and returns false to keep the dialog open.
    /// </summary>
    public static void Show(
        Control owner,
        ProviderSelection initial,
        Func<ProviderSelection, ProviderConfiguration?> savedFor,
        Func<ProviderConfiguration, bool> apply)
    {
        new ProviderSettingsDialog(initial, savedFor, apply).ShowModal(owner);
    }

    private ProviderSelection SelectedProvider =>
        Enum.TryParse<ProviderSelection>(_provider.SelectedKey, out var provider) ? provider : ProviderSettings.DefaultProvider;

    // Fills the form from the last saved configuration for this provider, or its defaults.
    private void LoadProvider(ProviderSelection provider)
    {
        _listGeneration++;
        var saved = _savedFor(provider);
        var usesEndpoint = ProviderSettings.UsesEndpoint(provider);

        _endpoint.Enabled = usesEndpoint;
        _endpoint.Text = usesEndpoint ? saved?.Endpoint?.ToString() ?? string.Empty : string.Empty;
        _endpoint.PlaceholderText = provider switch
        {
            ProviderSelection.Ollama => OllamaProviderOptions.DefaultBaseUrl.ToString(),
            ProviderSelection.OpenAICompatible => "http://localhost:11434/v1",
            _ => "(fixed for this provider)",
        };

        _model.Items.Clear();
        foreach (var suggestion in ProviderSettings.SuggestedModels(provider))
        {
            _model.Items.Add(suggestion);
        }
        _model.Text = saved?.Model ?? string.Empty;

        var subscription = ProviderSettings.IsSubscription(provider);
        _effort.Items.Clear();
        foreach (var level in ProviderSettings.EffortLevels(provider))
        {
            _effort.Items.Add(new ListItem { Text = level, Key = level });
        }
        _effort.SelectedKey = subscription ? saved?.Effort ?? ProviderSettings.DefaultEffort : null;
        _effort.Enabled = subscription;
        _executable.Enabled = subscription;
        _executable.Text = subscription ? saved?.ExecutablePath ?? string.Empty : string.Empty;
        _executable.PlaceholderText = subscription
            ? $"Blank: find {SubscriptionCliOptions.ExecutableName(ProviderSettings.CliBackend(provider))} on PATH or in the usual install folders"
            : "(subscription CLIs only)";

        var variable = ProviderSettings.ApiKeyEnvironmentVariable(provider);
        _apiKey.Enabled = ProviderSettings.AcceptsApiKey(provider);
        _apiKey.Text = ProviderSettings.AcceptsApiKey(provider) ? saved?.ApiKeyOverride ?? string.Empty : string.Empty;
        _apiKeyLabel.Text = provider switch
        {
            ProviderSelection.Ollama => "API key (not used by Ollama)",
            ProviderSelection.OpenAICompatible => "API key (optional, kept in memory only)",
            _ when subscription => "API key (not used: the CLI uses its own sign-in)",
            _ => $"API key (kept in memory only; blank uses {variable})",
        };

        _refresh.Enabled = provider == ProviderSelection.Ollama;
        _status.Text = subscription
            ? $"Parti runs your signed-in {SubscriptionCliOptions.ExecutableName(ProviderSettings.CliBackend(provider))} CLI with its own tools off, " +
              "in an empty temp folder: one CLI call per model turn."
            : string.Empty;
        if (provider == ProviderSelection.Ollama)
        {
            ListOllamaModels();
        }
    }

    // Lists installed models off the UI thread and preselects qwen3.6:27b, gemma4:31b-65k, or the first tag.
    private async void ListOllamaModels()
    {
        if (!ProviderSettings.TryParseEndpoint(ProviderSelection.Ollama, _endpoint.Text, out var endpoint, out var error))
        {
            _status.Text = error;
            return;
        }

        var generation = ++_listGeneration;
        _status.Text = $"Listing models at {endpoint}...";
        IReadOnlyList<string>? models = null;
        string? failure = null;
        try
        {
            using var provider = new OllamaProvider(new OllamaProviderOptions { BaseUrl = endpoint! });
            using var cts = new CancellationTokenSource(ListTimeout);
            models = await provider.ListModelsAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex is OperationCanceledException ? "timed out" : ex.Message;
        }

        Application.Instance?.AsyncInvoke(() =>
        {
            if (_closed || generation != _listGeneration)
            {
                return;
            }

            if (models is null || models.Count == 0)
            {
                _status.Text = failure is null
                    ? $"Ollama at {endpoint!.Host} has no models installed."
                    : $"Could not list models at {endpoint!.Host}: {failure}";
                return;
            }

            _model.Items.Clear();
            foreach (var name in models)
            {
                _model.Items.Add(name);
            }

            var current = _model.Text?.Trim();
            if (string.IsNullOrEmpty(current) || !Contains(models, current!))
            {
                _model.Text = ProviderSettings.PreselectModel(models) ?? string.Empty;
            }
            _status.Text = $"{models.Count} models at {endpoint!.Host}.";
        });
    }

    private void Save()
    {
        if (!ProviderSettings.TryValidate(SelectedProvider, _model.Text, _endpoint.Text, _apiKey.Text, _effort.SelectedKey, _executable.Text, out var configuration, out var error))
        {
            _status.Text = error;
            return;
        }

        if (_apply(configuration!))
        {
            Close();
        }
    }

    private static bool Contains(IReadOnlyList<string> models, string name)
    {
        foreach (var model in models)
        {
            if (string.Equals(model, name, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
