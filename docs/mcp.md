# MCP Host

Hermes Desktop can load Model Context Protocol servers at startup and expose their tools beside the native Hermes tools.

## Config Locations

Hermes checks these files on launch, in order:

1. `%LOCALAPPDATA%\hermes\hermes-cs\mcp.json`
2. `%LOCALAPPDATA%\hermes\mcp.json`
3. `%APPDATA%\Hermes\mcp.json`
4. `%USERPROFILE%\.hermes\mcp.json`

If no file exists, MCP stays disabled and Hermes starts normally.

## Example

```json
{
  "mcpServers": {
    "filesystem": {
      "command": "npx",
      "args": [
        "-y",
        "@modelcontextprotocol/server-filesystem",
        "%TEMP%"
      ]
    }
  }
}
```

When the server connects, its tools are registered as `mcp__{server}__{tool}`. For example, a filesystem server tool named `read_file` appears as `mcp__filesystem__read_file`.

## Notes

- MCP initialization is best-effort. A bad or missing server logs an error but does not block Hermes startup.
- MCP tools use the server-provided input schema when available, so the model can call tools with their native arguments.
- Permission gates still apply to MCP tools through the normal Hermes tool pipeline.
