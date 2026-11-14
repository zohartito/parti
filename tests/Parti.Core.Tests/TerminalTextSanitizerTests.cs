// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using Parti.Core.Ai;
using Xunit;

namespace Parti.Core.Tests;

public class TerminalTextSanitizerTests
{
    [Fact]
    public void Sanitize_EncodesAnsiAndOtherTerminalControls_ButKeepsLinesReadable()
    {
        var value = "safe\n\u001b]52;c;clipboard\u0007\r\t\u009b31m";

        var sanitized = TerminalTextSanitizer.Sanitize(value);

        Assert.Equal("safe\n\\u001B]52;c;clipboard\\u0007\\u000D\\u0009\\u009B31m", sanitized);
        Assert.DoesNotContain('\u001b', sanitized);
        Assert.Contains('\n', sanitized);
    }

    [Fact]
    public void Sanitize_LeavesPrintableTextUntouched()
    {
        const string value = "A Grasshopper panel: 12.5 mm";

        Assert.Same(value, TerminalTextSanitizer.Sanitize(value));
    }
}
