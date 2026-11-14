// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;
using Parti.Core.Design;
using Parti.Core.Sessions;

namespace Parti.Core.Grasshopper.Chat;

/// <summary>
/// Minimal contract the <see cref="ChatController"/> uses to talk back to the UI layer.
/// The real implementation is <c>ChatForm</c> (in Parti.Components); unit tests substitute
/// an in-memory fake.
///
/// Every method may be called from a background thread; implementations are responsible
/// for marshalling to the UI thread (<c>Eto.Forms.Application.Instance.AsyncInvoke</c>).
///
/// <para>This interface lives in <c>Parti.Core.Grasshopper</c> so unit tests can consume it
/// without dragging in the <c>.gha</c> target in <c>Parti.Components</c>.</para>
/// </summary>
public interface IChatView
{
    /// <summary>
    /// Appends a user-submitted prompt to the visible transcript. Called once per
    /// <see cref="ChatController.SendAsync"/> call, before the stream begins.
    /// </summary>
    void AppendUserMessage(string text);

    /// <summary>
    /// Appends an incremental assistant text fragment to the currently-streaming
    /// assistant turn. The view is expected to coalesce consecutive deltas into one
    /// visual block and scroll the transcript to the end.
    /// </summary>
    void AppendAssistantText(string delta);

    /// <summary>
    /// Toggles the "streaming in flight" UI state: disables Send while true, enables
    /// the Cancel button, and vice versa when false.
    /// </summary>
    void SetBusy(bool busy);

    /// <summary>
    /// Surfaces a non-fatal error to the user (provider failure, cancelled turn,
    /// missing API key). The transcript is preserved.
    /// </summary>
    void ShowError(string message);

    /// <summary>
    /// Wipes the transcript back to an empty state. Called when the user presses
    /// the Reset button.
    /// </summary>
    void ClearHistory();

    /// <summary>
    /// Appends an incremental reasoning ("thinking") fragment to a display-only reasoning pane.
    /// Reasoning never becomes transcript content. Default: ignored.
    /// </summary>
    void AppendReasoningText(string delta)
    {
        // Default no-op so existing views do not need to change.
    }

    /// <summary>
    /// Shows the questions of a paused <c>ask_user</c> call so the user can answer them.
    /// Default: ignored.
    /// </summary>
    void ShowQuestions(IReadOnlyList<Question> questions)
    {
        // Default no-op so existing views do not need to change.
    }

    /// <summary>
    /// Shows the currently recorded three-pillar decomposition and its revision. Default: ignored.
    /// </summary>
    void ShowDecomposition(Decomposition decomposition, int revision)
    {
        // Default no-op so existing views do not need to change.
    }

    /// <summary>
    /// Appends one compact tool line (built by <see cref="TranscriptLines.ToolResult"/>) on its own
    /// line of the transcript. The line never carries the document lease. Default: ignored.
    /// </summary>
    void AppendToolActivity(string line)
    {
        // Default no-op so existing views do not need to change.
    }

    /// <summary>
    /// Starts a new assistant block: the model is replying again after tool calls in the same
    /// loop. Called just before that reply's first text delta. Default: ignored.
    /// </summary>
    void BeginAssistantTurn()
    {
        // Default no-op so existing views do not need to change.
    }

    /// <summary>
    /// Appends a short notice from Parti itself on its own line (for example
    /// <see cref="TranscriptLines.TokenLimitNotice"/>). Default: ignored.
    /// </summary>
    void AppendNotice(string text)
    {
        // Default no-op so existing views do not need to change.
    }
}
