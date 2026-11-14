// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Text;

namespace Parti.Core.Ai;

/// <summary>
/// Makes lower-trust text safe to render in an interactive terminal. Line feeds remain
/// readable; all other C0/C1 control characters are rendered visibly rather than executed by
/// the terminal emulator.
/// </summary>
public static class TerminalTextSanitizer
{
    /// <summary>Returns <paramref name="text"/> with terminal control characters encoded.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder? escaped = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var unsafeControl = c != '\n' && (char.IsControl(c) || (c >= '\u0080' && c <= '\u009f'));
            if (!unsafeControl)
            {
                escaped?.Append(c);
                continue;
            }

            escaped ??= new StringBuilder(text.Length + 8).Append(text, 0, i);
            escaped.Append("\\u").Append(((int)c).ToString("X4"));
        }

        return escaped?.ToString() ?? text;
    }
}
