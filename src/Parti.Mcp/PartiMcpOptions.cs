// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.IO;

namespace Parti.Mcp;

/// <summary>
/// Transport modes supported by <see cref="PartiMcpServer"/>. v0.5 ships stdio only
/// (hermetic, Claude Desktop native, no .NET 8 hosting dependencies). HTTP is reserved
/// for v0.6+ when the plugin lane moves to an in-process Streamable-HTTP server.
/// </summary>
public enum PartiMcpTransportKind
{
    /// <summary>Newline-delimited JSON-RPC over stdin/stdout. Default.</summary>
    Stdio,

    /// <summary>Arbitrary paired streams (used in tests).</summary>
    Stream,
}

/// <summary>
/// Configuration for <see cref="PartiMcpServer"/>. Defaults: stdio transport, server name
/// "parti", host 127.0.0.1, port 26930, path /mcp. Host/port/path are carried for the future
/// HTTP transport and ignored when <see cref="Transport"/> is <see cref="PartiMcpTransportKind.Stdio"/>.
/// </summary>
public sealed class PartiMcpOptions
{
    public PartiMcpTransportKind Transport { get; set; } = PartiMcpTransportKind.Stdio;
    public string ServerName { get; set; } = "parti";
    public string ServerVersion { get; set; } = "0.5.0";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 26930;
    public string Path { get; set; } = "/mcp";

    /// <summary>When <see cref="Transport"/> is <see cref="PartiMcpTransportKind.Stream"/>, the stream the server reads from.</summary>
    public Stream? InputStream { get; set; }

    /// <summary>When <see cref="Transport"/> is <see cref="PartiMcpTransportKind.Stream"/>, the stream the server writes to.</summary>
    public Stream? OutputStream { get; set; }

    /// <summary>Optional hook for human-readable lifecycle + tool-call traces. Defaults to stderr.</summary>
    public Action<string>? Trace { get; set; }
}
