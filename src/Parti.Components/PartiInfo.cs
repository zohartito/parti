// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Drawing;
using System.Reflection;
using Grasshopper.Kernel;

namespace Parti.Components;

public sealed class PartiInfo : GH_AssemblyInfo
{
    public override string Name => "Parti";
    public override Bitmap? Icon => null;
    public override string Description => "Open-source Grasshopper AI copilot — BYO model, local-capable, scoped per-component edits.";
    public override Guid Id => new("d7b3a1c4-2a0e-4e8e-9b4b-0000000000a1");
    public override string AuthorName => "Zohar Tito";
    public override string AuthorContact => "https://github.com/zohartito/parti";

    // <Version> in Directory.Build.props. The SDK appends "+<commit sha>" to the
    // informational version; Grasshopper shows this string, so drop that suffix.
    public override string Version => AssemblyVersionText;

    private static readonly string AssemblyVersionText = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(PartiInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
            return typeof(PartiInfo).Assembly.GetName().Version?.ToString(3) ?? string.Empty;
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational.Substring(0, plus);
    }
}
