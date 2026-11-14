# Parti.Mcp

Standalone MCP companion server for Parti. Exposes the four Grasshopper tools
(`gh_add_component`, `gh_get_all`, `gh_get_selected`, `gh_describe`) over stdio so
any MCP client (Claude Desktop, ChatGPT, mcp-inspector) can drive them.

## Launch

```
dotnet run --project src/Parti.Mcp -- --dispatcher stub
```

The stub dispatcher returns canned success payloads so the handshake is testable
without Rhino. `--dispatcher live` is rejected here; the live path lives in the
in-process plugin server (v0.6+).

## Claude Desktop config

Add to `%APPDATA%\Claude\claude_desktop_config.json` (or
`~/Library/Application Support/Claude/claude_desktop_config.json` on macOS):

```
{
  "mcpServers": {
    "parti": {
      "command": "dotnet",
      "args": ["run", "--project", "C:/path/to/parti/src/Parti.Mcp", "--",
               "--dispatcher", "stub"]
    }
  }
}
```

Restart Claude Desktop; the four `gh_*` tools appear in the tool picker.

## v0.5 limitations

- Stdio transport only. HTTP is reserved for v0.6.
- Stub dispatcher only (no live canvas).
- No auth, no sampling, no resources/prompts. Tools only.
