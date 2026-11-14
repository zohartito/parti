// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;

namespace Parti.Core.Sessions;

/// <summary>
/// Classification of a clarification question so the UI can pick the right widget.
/// </summary>
public enum QuestionKind
{
    /// <summary>Free-form text response.</summary>
    Text,
    /// <summary>Single choice from <see cref="Question.Options"/>.</summary>
    Radio,
    /// <summary>Pick a component on the Grasshopper canvas (UI supplies the SimpleId).</summary>
    ComponentPicker,
}

/// <summary>
/// A single clarification question surfaced by the model through the <c>ask_user</c> tool.
/// </summary>
public sealed record Question(
    string Id,
    string Prompt,
    QuestionKind Kind,
    IReadOnlyList<string>? Options = null);

/// <summary>
/// Snapshot of a session's paused state. Produced when the model calls <c>ask_user</c>.
/// <para>
/// <see cref="ToolUseId"/> is the id of the original <c>ask_user</c> tool call; it is echoed back
/// on resume so the provider can match the answer tool_result to the original call.
/// </para>
/// </summary>
public sealed record PausedState(
    string TurnId,
    string ToolUseId,
    IReadOnlyList<Question> Questions);

/// <summary>
/// A single answer supplied by the user in response to a paused <see cref="Question"/>.
/// </summary>
public sealed record ClarificationAnswer(string QuestionId, string Answer);
