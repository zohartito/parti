# Parti.Mcp

Standalone MCP companion server for Parti. Exposes the four Grasshopper tools
(`gh_add_component`, `gh_get_all`, `gh_get_selected`, `gh_describe`) over stdio so
any MCP client (Claude Desktop, ChatGPT, mcp-inspector) can drive them.

## Launch

The project targets `net7.0`. The command below needs the .NET 7 runtime
alongside your SDK. With only a newer runtime installed, explicitly allow
major-version roll-forward (tested with .NET 10):

```bash
DOTNET_ROLL_FORWARD=Major dotnet run --project src/Parti.Mcp -- --dispatcher stub
```

If .NET 7 is installed, the environment setting is unnecessary:

```bash
dotnet run --project src/Parti.Mcp -- --dispatcher stub
```

The stub dispatcher returns canned success payloads so the handshake is testable
without Rhino. Live MCP is not implemented: `--dispatcher live` is rejected,
and the plugin does not contain an in-process MCP server.

## Claude Desktop config

Add to `%APPDATA%\Claude\claude_desktop_config.json` (or
`~/Library/Application Support/Claude/claude_desktop_config.json` on macOS):

```
{
  "mcpServers": {
    "parti": {
      "command": "dotnet",
      "args": ["run", "--project", "C:/path/to/parti/src/Parti.Mcp", "--",
               "--dispatcher", "stub"],
      "env": { "DOTNET_ROLL_FORWARD": "Major" }
    }
  }
}
```

The explicit environment setting also supports clients launched without a shell
when only a newer .NET runtime is installed; omit it if using .NET 7.

Restart Claude Desktop; the four `gh_*` tools appear in the tool picker.

## Limitations

- Stdio transport only. HTTP is not implemented.
- Stub dispatcher only (no live canvas).
- No auth, no sampling, no resources/prompts. Tools only.
