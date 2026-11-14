// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Threading;
using Eto.Drawing;
using Eto.Forms;
using Parti.Core.Sessions;

namespace Parti.Components.Chat;

/// <summary>
/// Modal preview dialog surfaced before the session executes a batch of reversible tool calls
/// (v0.7 "Trust" deliverable). The user sees what is about to happen — a short assistant preamble
/// and a numbered list of tool calls with human-readable argument previews, each add's catalog
/// label, and the "Why" block from the recorded decomposition — and chooses Approve or Cancel.
/// <para>
/// The dialog is Eto.Forms so it runs alongside the rest of <see cref="ChatForm"/> (cross-platform
/// in Rhino). Must be shown from the Eto UI thread — <see cref="ChatForm"/> marshals via
/// <see cref="Application.Instance"/> before calling <see cref="ShowPlan"/>.
/// </para>
/// </summary>
internal sealed class PlanPreviewDialog : Dialog<bool>
{
    // True from construction until the modal loop returns; read and written on the UI thread only.
    private bool _shown = true;

    private PlanPreviewDialog(ToolCallPlan plan)
    {
        Title = "Parti \u2014 Approve plan";
        MinimumSize = new Size(520, 420);
        Padding = new Padding(10);
        Resizable = true;

        var rows = new TableLayout { Spacing = new Size(6, 6) };

        // Optional assistant preamble ("I'll add a slider and connect it to a panel.").
        if (!string.IsNullOrWhiteSpace(plan.AssistantText))
        {
            var assistantLabel = new Label
            {
                Text = plan.AssistantText,
                Wrap = WrapMode.Word,
            };
            rows.Rows.Add(new TableRow(new TableCell(assistantLabel, true)));
        }

        // Numbered list of calls: "{idx}. {name}  -  {ArgumentPreview}". Read-only TextBox so the
        // user can scroll but not edit.
        var body = new TextArea
        {
            ReadOnly = true,
            Wrap = true,
            Font = new Font(FontFamilies.Monospace, 10),
            Text = PlanText.Build(plan),
        };
        rows.Rows.Add(new TableRow(new TableCell(body, true)) { ScaleHeight = true });

        var approveButton = new Button { Text = "Approve" };
        var cancelButton = new Button { Text = "Cancel" };
        approveButton.Click += (_, _) => Close(true);
        cancelButton.Click += (_, _) => Close(false);

        var buttonRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Padding = new Padding(0, 4, 0, 0),
            Items =
            {
                new StackLayoutItem(null, true),
                new StackLayoutItem(cancelButton),
                new StackLayoutItem(approveButton),
            },
        };
        rows.Rows.Add(new TableRow(new TableCell(buttonRow, true)));

        // Enter == approve, Escape == cancel.
        DefaultButton = approveButton;
        AbortButton = cancelButton;
        Result = false;

        Content = rows;
    }

    /// <summary>
    /// Shows the plan preview dialog modally and returns <c>true</c> when the user clicks Approve
    /// or <c>false</c> otherwise (Cancel, escape, window close, or <paramref name="ct"/> firing).
    /// Must be invoked from the Eto UI thread. When <paramref name="ct"/> fires the dialog closes
    /// itself, so a cancel or reset never waits on an unattended dialog.
    /// </summary>
    public static bool ShowPlan(Control? owner, ToolCallPlan plan, CancellationToken ct = default)
    {
        if (plan is null || ct.IsCancellationRequested)
        {
            return false;
        }

        var dialog = new PlanPreviewDialog(plan);
        using var registration = ct.Register(() => Application.Instance?.AsyncInvoke(dialog.CloseCancelled));
        bool approved = owner is null ? dialog.ShowModal() : dialog.ShowModal(owner);
        dialog._shown = false;
        return approved && !ct.IsCancellationRequested;
    }

    // Runs on the UI thread. The registration can fire after ShowModal returned, so only a dialog
    // that is still showing is closed.
    private void CloseCancelled()
    {
        if (_shown)
        {
            _shown = false;
            Close(false);
        }
    }
}
