// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using Eto.Drawing;
using Eto.Forms;
using Parti.Core.Sessions;

namespace Parti.Components.Chat;

/// <summary>A canvas object offered by the component picker.</summary>
internal sealed record CanvasComponentChoice(Guid InstanceGuid, string Name, string NickName);

/// <summary>
/// Panel shown when the model pauses on <c>ask_user</c>: one row per question with a text box, a
/// radio list (when the question has options), or an editable component picker filled from the
/// canvas. Eto.Forms only, so it runs on Windows and macOS Rhino. Must be used on the UI thread.
/// </summary>
internal sealed class QuestionPanel : Panel
{
    private readonly Dictionary<string, Func<string?>> _readers = new(StringComparer.Ordinal);
    private readonly Label _error = new() { TextColor = Colors.IndianRed, Wrap = WrapMode.Word, Visible = false };
    private IReadOnlyList<Question> _questions = Array.Empty<Question>();

    public QuestionPanel()
    {
        Visible = false;
    }

    /// <summary>Raised with validated answers, one per question.</summary>
    public event Action<IReadOnlyList<Question>, IReadOnlyList<ClarificationAnswer>>? Submitted;

    /// <summary>Builds the rows for <paramref name="questions"/> and shows the panel.</summary>
    public void ShowQuestions(IReadOnlyList<Question> questions, IReadOnlyList<CanvasComponentChoice> canvasComponents)
    {
        _questions = questions ?? Array.Empty<Question>();
        _readers.Clear();
        _error.Visible = false;

        var rows = new TableLayout { Spacing = new Size(4, 4), Padding = new Padding(6) };
        if (_questions.Count == 0)
        {
            // Nothing parseable to render: the controller takes the next message-box text as the answer.
            rows.Rows.Add(new TableRow(new Label { Text = "Parti is waiting for your reply. Type it in the message box below." }));
            Content = new GroupBox { Text = "Parti has a question", Content = rows };
            Visible = true;
            return;
        }

        foreach (var question in _questions)
        {
            rows.Rows.Add(new TableRow(new TableCell(new Label { Text = question.Prompt, Wrap = WrapMode.Word }, true)));
            rows.Rows.Add(new TableRow(new TableCell(CreateInput(question, canvasComponents), true)));
        }

        var submit = new Button { Text = "Send answers" };
        submit.Click += (_, _) => Submit();
        rows.Rows.Add(new TableRow(new TableCell(_error, true)));
        rows.Rows.Add(new TableRow(new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Items = { new StackLayoutItem(null, true), new StackLayoutItem(submit) },
        }));

        Content = new GroupBox
        {
            Text = _questions.Count == 1 ? "Parti has a question" : "Parti has questions",
            Content = rows,
        };
        Visible = true;
    }

    /// <summary>Hides the panel and drops its rows.</summary>
    public void Clear()
    {
        _questions = Array.Empty<Question>();
        _readers.Clear();
        Content = null;
        Visible = false;
    }

    private Control CreateInput(Question question, IReadOnlyList<CanvasComponentChoice> canvasComponents)
    {
        switch (QuestionAnswers.EffectiveKind(question))
        {
            case QuestionKind.Radio:
                var radios = new RadioButtonList { Orientation = Orientation.Vertical, Spacing = new Size(4, 4) };
                foreach (var option in question.Options!)
                {
                    if (!string.IsNullOrWhiteSpace(option))
                    {
                        radios.Items.Add(new ListItem { Text = option, Key = option });
                    }
                }
                _readers[question.Id] = () => radios.SelectedKey;
                return radios;

            case QuestionKind.ComponentPicker:
                // Editable, so a GUID can also be pasted. Each item's text is the answer itself.
                var picker = new ComboBox { AutoComplete = true };
                foreach (var choice in canvasComponents)
                {
                    picker.Items.Add(QuestionAnswers.FormatComponentChoice(choice.InstanceGuid, choice.Name, choice.NickName));
                }
                _readers[question.Id] = () => picker.Text;
                return picker;

            default:
                var box = new TextBox();
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Keys.Enter)
                    {
                        e.Handled = true;
                        Submit();
                    }
                };
                _readers[question.Id] = () => box.Text;
                return box;
        }
    }

    private void Submit()
    {
        var raw = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var reader in _readers)
        {
            raw[reader.Key] = reader.Value();
        }

        if (!QuestionAnswers.TryBuild(_questions, raw, out var answers, out var error))
        {
            _error.Text = error;
            _error.Visible = true;
            return;
        }

        Submitted?.Invoke(_questions, answers);
    }
}
