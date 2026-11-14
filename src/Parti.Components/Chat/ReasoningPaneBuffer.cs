// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Text;

namespace Parti.Components.Chat;

/// <summary>
/// Coalesces reasoning deltas for the chat window's reasoning pane so the UI thread gets at most
/// one update per <see cref="FlushInterval"/> instead of one per token, and keeps the pane
/// bounded to its last <see cref="MaxCharacters"/> characters. No Eto types, so it is testable.
/// </summary>
/// <remarks>
/// <see cref="Append"/> runs on the stream's thread, <see cref="Clear"/> on the thread that starts
/// a new message, and <see cref="Drain"/> on the UI thread; every member takes the lock.
/// <see cref="Clear"/> starts a new generation. The pane remembers the generation it shows, so a
/// flush that drains new-generation text before the queued pane reset has run clears the pane
/// first, and the late reset then leaves that text alone.
/// </remarks>
public sealed class ReasoningPaneBuffer
{
    /// <summary>The pane keeps at most this many characters (the tail).</summary>
    public const int MaxCharacters = 20_000;

    /// <summary>Minimum time between two pane updates (at most 10 per second).</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly StringBuilder _pending = new();
    private bool _flushScheduled;
    private int _generation;

    /// <summary>
    /// Queues <paramref name="delta"/>. Returns true when the caller must schedule a flush (none
    /// is pending yet); false when an already scheduled flush will pick it up.
    /// </summary>
    public bool Append(string? delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return false;
        }

        lock (_gate)
        {
            _pending.Append(delta);
            if (_pending.Length > MaxCharacters)
            {
                _pending.Remove(0, _pending.Length - MaxCharacters);
            }
            if (_flushScheduled)
            {
                return false;
            }
            _flushScheduled = true;
            return true;
        }
    }

    /// <summary>
    /// Takes everything queued since the last drain and lets the next append schedule a flush.
    /// <paramref name="generation"/> is the generation the text belongs to.
    /// </summary>
    public string Drain(out int generation)
    {
        lock (_gate)
        {
            _flushScheduled = false;
            generation = _generation;
            var text = _pending.ToString();
            _pending.Clear();
            return text;
        }
    }

    /// <summary>
    /// Drops queued text and starts a new generation (a new user message clears the pane). Returns
    /// the new generation, which the pane reset carries.
    /// </summary>
    public int Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            return ++_generation;
        }
    }

    /// <summary>
    /// The pane text after adding <paramref name="added"/> to <paramref name="current"/>, keeping
    /// only the last <see cref="MaxCharacters"/> characters.
    /// </summary>
    public static string Combine(string? current, string? added)
    {
        var combined = (current ?? string.Empty) + (added ?? string.Empty);
        return combined.Length <= MaxCharacters ? combined : combined.Substring(combined.Length - MaxCharacters);
    }
}
