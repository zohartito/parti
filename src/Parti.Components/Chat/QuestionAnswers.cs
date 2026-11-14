// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Parti.Core.Sessions;

namespace Parti.Components.Chat;

/// <summary>
/// Non-UI rules for the <c>ask_user</c> question panel: which widget a question gets, how raw
/// input becomes <see cref="ClarificationAnswer"/>s, and the transcript echo.
/// </summary>
public static class QuestionAnswers
{
    /// <summary>
    /// The widget a question gets. A radio question without options falls back to text, since
    /// there is nothing to choose from.
    /// </summary>
    public static QuestionKind EffectiveKind(Question question)
    {
        if (question is null)
        {
            throw new ArgumentNullException(nameof(question));
        }

        return question.Kind == QuestionKind.Radio && !HasOptions(question)
            ? QuestionKind.Text
            : question.Kind;
    }

    /// <summary>
    /// The answer text for a canvas component chosen in the picker: its instance GUID, which is
    /// what the model needs for a later write, then its display name.
    /// </summary>
    public static string FormatComponentChoice(Guid instanceGuid, string? name, string? nickName)
    {
        var label = string.IsNullOrWhiteSpace(nickName) || string.Equals(nickName, name, StringComparison.Ordinal)
            ? name
            : $"{name} \"{nickName}\"";
        return string.IsNullOrWhiteSpace(label)
            ? instanceGuid.ToString("D")
            : $"{instanceGuid:D} ({label})";
    }

    /// <summary>
    /// Builds one answer per question from <paramref name="raw"/> (question id → typed or chosen
    /// text). Question ids must be unique, every question needs a non-blank answer, and a radio
    /// answer must be one of its options exactly.
    /// </summary>
    public static bool TryBuild(
        IReadOnlyList<Question> questions,
        IReadOnlyDictionary<string, string?> raw,
        out IReadOnlyList<ClarificationAnswer> answers,
        out string error)
    {
        if (questions is null)
        {
            throw new ArgumentNullException(nameof(questions));
        }
        if (raw is null)
        {
            throw new ArgumentNullException(nameof(raw));
        }

        var built = new List<ClarificationAnswer>(questions.Count);
        answers = built;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in questions)
        {
            // One input per id: duplicate ids would make two answers indistinguishable.
            if (string.IsNullOrEmpty(question.Id) || !seen.Add(question.Id))
            {
                error = "The questions have missing or repeated ids; reply in the message box instead.";
                return false;
            }

            raw.TryGetValue(question.Id, out var value);
            var isRadio = EffectiveKind(question) == QuestionKind.Radio;
            // A radio answer is the option exactly as offered; typed answers are trimmed.
            var text = isRadio ? value : value?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                error = $"Answer \"{question.Prompt}\".";
                return false;
            }

            if (isRadio && !question.Options!.Contains(text, StringComparer.Ordinal))
            {
                error = $"Pick one of the options for \"{question.Prompt}\".";
                return false;
            }

            built.Add(new ClarificationAnswer(question.Id, text!));
        }

        error = string.Empty;
        return true;
    }

    /// <summary>The transcript line for submitted answers: one "prompt: answer" per question.</summary>
    public static string Summarize(IReadOnlyList<Question> questions, IReadOnlyList<ClarificationAnswer> answers)
    {
        if (questions is null)
        {
            throw new ArgumentNullException(nameof(questions));
        }
        if (answers is null)
        {
            throw new ArgumentNullException(nameof(answers));
        }

        var sb = new StringBuilder();
        foreach (var answer in answers)
        {
            var prompt = questions.FirstOrDefault(q => q.Id == answer.QuestionId)?.Prompt ?? answer.QuestionId;
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }
            sb.Append(prompt).Append(": ").Append(answer.Answer);
        }
        return sb.ToString();
    }

    private static bool HasOptions(Question question) =>
        question.Options is { Count: > 0 } options && options.Any(o => !string.IsNullOrWhiteSpace(o));
}
