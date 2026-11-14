// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using Parti.Core.Ai;
using Parti.Core.Tools;

namespace Parti.Mcp;

/// <summary>
/// Bridges an <see cref="IToolDispatcher"/> onto a ModelContextProtocol 1.2 server. Enumerates
/// the dispatcher's definitions once and registers them as MCP tools; routes each MCP tool call
/// through <see cref="IToolDispatcher.DispatchAsync"/>; serializes the resulting JObject as a
/// single text content block so MCP clients (Claude Desktop, ChatGPT, mcp-inspector) can read it.
/// </summary>
public sealed class PartiMcpServer : IAsyncDisposable
{
    private readonly IToolDispatcher _dispatcher;
    private readonly PartiMcpOptions _options;
    private readonly Dictionary<string, AIToolDefinition> _defsByName;
    private readonly IList<Tool> _protocolTools;

    private McpServer? _server;
    private IAsyncDisposable? _transport;

    public PartiMcpServer(IToolDispatcher dispatcher, PartiMcpOptions? options = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _options = options ?? new PartiMcpOptions();

        _defsByName = new Dictionary<string, AIToolDefinition>(StringComparer.Ordinal);
        _protocolTools = new List<Tool>();

        foreach (var def in _dispatcher.GetDefinitions())
        {
            _defsByName[def.Name] = def;
            _protocolTools.Add(new Tool
            {
                Name = def.Name,
                Description = def.Description,
                InputSchema = SchemaToElement(def.InputSchema),
            });
        }
    }

    /// <summary>Tool names exposed to MCP clients. Useful for asserts + logging.</summary>
    public IReadOnlyCollection<string> ToolNames => _defsByName.Keys;

    /// <summary>
    /// Builds the transport + server, then blocks until the peer disconnects or <paramref name="ct"/> fires.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        Trace($"starting mcp server name={_options.ServerName} transport={_options.Transport} tools={_protocolTools.Count}");

        var serverOptions = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = _options.ServerName, Version = _options.ServerVersion },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) =>
                {
                    var result = new ListToolsResult();
                    foreach (var t in _protocolTools)
                    {
                        result.Tools.Add(t);
                    }
                    return new ValueTask<ListToolsResult>(result);
                },
                CallToolHandler = (ctx, innerCt) => DispatchAsync(ctx, innerCt),
            },
        };

        ITransport transport;
        switch (_options.Transport)
        {
            case PartiMcpTransportKind.Stdio:
                var stdio = new StdioServerTransport(_options.ServerName, loggerFactory: null!);
                transport = stdio;
                _transport = stdio;
                break;

            case PartiMcpTransportKind.Stream:
                if (_options.InputStream is null || _options.OutputStream is null)
                {
                    throw new InvalidOperationException("Stream transport requires InputStream and OutputStream.");
                }
                var stream = new StreamServerTransport(_options.InputStream, _options.OutputStream, _options.ServerName, loggerFactory: null!);
                transport = stream;
                _transport = stream;
                break;

            default:
                throw new NotSupportedException($"Transport {_options.Transport} is not supported in v0.5.");
        }

        _server = McpServer.Create(transport, serverOptions, loggerFactory: null!, serviceProvider: null!);
        await _server.RunAsync(ct).ConfigureAwait(false);
        Trace("mcp server stopped");
    }

    private async ValueTask<CallToolResult> DispatchAsync(
        RequestContext<CallToolRequestParams> ctx,
        CancellationToken ct)
    {
        var name = ctx.Params?.Name ?? string.Empty;
        var argsNewtonsoft = ArgsToNewtonsoft(ctx.Params?.Arguments);

        Trace($"tool_call name={name} args_bytes={argsNewtonsoft.ToString(Newtonsoft.Json.Formatting.None).Length}");

        if (!_dispatcher.HasTool(name))
        {
            return ErrorResult($"Unknown tool: {name}");
        }

        var call = new AIInteractionToolCall
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Arguments = argsNewtonsoft,
        };

        AIInteractionToolResult dispatched;
        try
        {
            dispatched = await _dispatcher.DispatchAsync(call, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace($"tool_result name={name} exception={ex.GetType().Name}");
            return ErrorResult(ex.Message);
        }

        var payload = dispatched.Result?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}";
        var isError = string.Equals(dispatched.Result?["status"]?.ToString(), "error", StringComparison.Ordinal);
        Trace($"tool_result name={name} bytes={payload.Length} is_error={isError}");

        var result = new CallToolResult { IsError = isError };
        result.Content.Add(new TextContentBlock { Text = payload });
        return result;
    }

    private static CallToolResult ErrorResult(string message)
    {
        var result = new CallToolResult { IsError = true };
        result.Content.Add(new TextContentBlock { Text = $"{{\"status\":\"error\",\"message\":{JsonEncode(message)}}}" });
        return result;
    }

    private static string JsonEncode(string value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.GetRawText();
    }

    private static JObject ArgsToNewtonsoft(IEnumerable<KeyValuePair<string, JsonElement>>? src)
    {
        if (src is null)
        {
            return new JObject();
        }

        var node = new JsonObject();
        foreach (var kv in src)
        {
            node[kv.Key] = JsonNode.Parse(kv.Value.GetRawText());
        }
        return JObject.Parse(node.ToJsonString());
    }

    private static JsonElement SchemaToElement(JObject schema)
    {
        using var doc = JsonDocument.Parse(schema.ToString(Newtonsoft.Json.Formatting.None));
        return doc.RootElement.Clone();
    }

    private void Trace(string message)
    {
        if (_options.Trace is { } hook)
        {
            try { hook(message); } catch { /* trace hook must never break the server */ }
        }
        else
        {
            Console.Error.WriteLine($"[parti-mcp] {message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
        }
        _server = null;
    }
}
