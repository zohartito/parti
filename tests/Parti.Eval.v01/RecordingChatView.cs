// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System.Collections.Generic;
using System.Text;
using Parti.Core.Design;
using Parti.Core.Grasshopper.Chat;
using Parti.Core.Sessions;

namespace Parti.Eval.V01;

/// <summary>
/// <see cref="IChatView"/> that records everything the controller shows, so fixtures can assert
/// on what the user would have seen (streamed text, reasoning, questions, the decomposition card,
/// errors). Calls arrive from background threads; every member is guarded by one lock.
/// </summary>
public sealed class RecordingChatView : IChatView
{
    private readonly object _gate = new();
    private readonly List<string> _userMessages = new();
    private readonly List<string> _assistantDeltas = new();
    private readonly List<string> _reasoningDeltas = new();
    private readonly List<string> _errors = new();
    private readonly List<IReadOnlyList<Question>> _questions = new();
    private readonly List<(Decomposition Decomposition, int Revision)> _decompositions = new();
    private int _busyTransitions;

    public IReadOnlyList<string> UserMessages { get { lock (_gate) return _userMessages.ToArray(); } }
    public IReadOnlyList<string> AssistantDeltas { get { lock (_gate) return _assistantDeltas.ToArray(); } }
    public IReadOnlyList<string> ReasoningDeltas { get { lock (_gate) return _reasoningDeltas.ToArray(); } }
    public IReadOnlyList<string> Errors { get { lock (_gate) return _errors.ToArray(); } }
    public IReadOnlyList<IReadOnlyList<Question>> Questions { get { lock (_gate) return _questions.ToArray(); } }
    public IReadOnlyList<(Decomposition Decomposition, int Revision)> Decompositions { get { lock (_gate) return _decompositions.ToArray(); } }
    public int BusyTransitions { get { lock (_gate) return _busyTransitions; } }
    public bool Cleared { get; private set; }

    /// <summary>Everything streamed into the assistant pane, concatenated.</summary>
    public string AssistantTranscript
    {
        get
        {
            lock (_gate)
            {
                var sb = new StringBuilder();
                foreach (var delta in _assistantDeltas) sb.Append(delta);
                return sb.ToString();
            }
        }
    }

    public void AppendUserMessage(string text) { lock (_gate) _userMessages.Add(text); }
    public void AppendAssistantText(string delta) { lock (_gate) _assistantDeltas.Add(delta); }
    public void SetBusy(bool busy) { lock (_gate) _busyTransitions++; }
    public void ShowError(string message) { lock (_gate) _errors.Add(message); }
    public void ClearHistory() { lock (_gate) Cleared = true; }
    public void AppendReasoningText(string delta) { lock (_gate) _reasoningDeltas.Add(delta); }
    public void ShowQuestions(IReadOnlyList<Question> questions) { lock (_gate) _questions.Add(questions); }
    public void ShowDecomposition(Decomposition decomposition, int revision) { lock (_gate) _decompositions.Add((decomposition, revision)); }
}
