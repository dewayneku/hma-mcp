# hma-mcp

An [MCP](https://modelcontextprotocol.io) server that lets AI assistants (Claude Code, Claude Desktop, and any other MCP client) edit GBA Pokémon ROMs through the engine behind [HexManiacAdvance](https://github.com/haven1433/HexManiacAdvance) (HMA).

The assistant gets HMA's knowledge of the ROM: named anchors, tables with enum-aware fields, the script decompiler and compiler, free-space search, and automatic repointing. It works on the ROM directly and never clicks around a GUI. Saves stay fully compatible with the HMA editor, because the same `.toml` metadata is written next to the ROM.

> **Status:** early (v0.1). Feedback and PRs are welcome.

## What it can do

| Tool | What it does |
|---|---|
| `open_rom` / `close_rom` / `rom_info` | Open a `.gba` file (loads HMA's `.toml` metadata if present) |
| `list_anchors` | Search HMA's named anchors (`data.pokemon.stats`, `scripts.…`, `graphics.…`) |
| `resolve` | Anchor or address → what's there (run type, table row/field, pointer count) |
| `describe_table` | Field layout of a table, with types and enums |
| `read_table` | Read rows as JSON; enums come back as names (`FIRE`, `THUNDERBOLT`) |
| `write_table_field` | Set one field by row index or row name; repoints text if it grows |
| `decompile_script` | Event, battle, animation, or trainer-AI script → HMA script text |
| `compile_script` | HMA script text → ROM; moves it to free space and updates pointers if it grows |
| `hma_edit` | Type into HMA's hex view, using HMA's whole edit language (`^anchor`, `<pointer>`, `"text"`) |
| `read_bytes` / `write_bytes` | Raw hex access |
| `find_free_space` | Find a block of free (`0xFF`) bytes |
| `undo` / `redo` | Each edit tool call is one undo step |
| `save` | Write the ROM and its `.toml`; the first save makes a timestamped backup |

### Safety

- Nothing is written to disk until `save`.
- The first `save` of a session copies the original ROM (and its `.toml`) into `.hma-mcp-backups/` next to it.
- Every edit can be undone.
- Opening another ROM, or closing this one, refuses to drop unsaved changes unless you tell it to.

## Install

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows, macOS or Linux).

```bash
git clone --recurse-submodules https://github.com/dewayneku/hma-mcp.git
cd hma-mcp
dotnet build src/HmaMcp -c Release
```

The server is `src/HmaMcp/bin/Release/net8.0/hma-mcp` (`hma-mcp.exe` on Windows). Keep the `resources/` folder next to it: it holds HMA's game reference data.

### Claude Code

```bash
claude mcp add hma -- "C:/path/to/hma-mcp/src/HmaMcp/bin/Release/net8.0/hma-mcp.exe"
```

### Claude Desktop / other clients

```json
{
  "mcpServers": {
    "hma": {
      "command": "C:/path/to/hma-mcp/src/HmaMcp/bin/Release/net8.0/hma-mcp.exe"
    }
  }
}
```

## Example prompts

- "Open `C:/hacks/fire_red.gba` and show me Charmander's base stats."
- "Make Pikachu Electric/Fairy and give it 95 base speed."
- "Decompile the script at `scripts.newgame.names` and change the professor's first line."
- "Find 512 bytes of free space and tell me where."

## How it works

HMA is included as a git submodule (`external/HexManiacAdvance`) and its core library is compiled from source (`src/HexManiac.Core.Build`), so the server uses exactly the same code as the editor. ROMs are opened headlessly the way HMA's own test suite opens them. Script compiling follows HMA's code editor, including repointing and keeping any sub-scripts an edit leaves orphaned.

To update to a newer HMA:

```bash
git submodule update --remote external/HexManiacAdvance
```

## Legal

This tool does not include, download, or distribute any ROMs. Use it with ROMs you dumped yourself from games you own.

## Credits

- [HexManiacAdvance](https://github.com/haven1433/HexManiacAdvance) by haven1433 and contributors (MIT): all the ROM knowledge comes from HMA.
- [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk).

## License

MIT; see [LICENSE](LICENSE).
