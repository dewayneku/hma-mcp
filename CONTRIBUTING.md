# Contributing to hma-mcp

Thanks for helping! Bug reports, game test results and PRs are all welcome.

## Never share ROMs

Don't attach, link or commit ROMs (`.gba`), including in issues, PRs, test fixtures and screenshots of large hex dumps. The repo's `.gitignore` blocks `*.gba` and `*.toml`. To describe a problem, give the **game code** (from `rom_info`, e.g. `BPRE0`), the **anchor or address**, and what you expected.

## Setup

```bash
git clone --recurse-submodules https://github.com/dewayneku/hma-mcp.git
cd hma-mcp
dotnet build src/HmaMcp -c Release
```

Requires the .NET 8 SDK. HMA's core is built from the `external/HexManiacAdvance` submodule by `src/HexManiac.Core.Build`.

## Layout

| Path | What's there |
|---|---|
| `src/HmaMcp/Program.cs` | Server startup (stdio transport) |
| `src/HmaMcp/RomSessions.cs` | The open ROM, address/anchor parsing, undo grouping |
| `src/HmaMcp/Tools/` | The MCP tools: `RomTools`, `DataTools`, `ScriptTools`, `MapTools`, `TextTools`, `GraphicsTools`, `MapEditTools`, `WorldMapTools`, `BlockTools`, `TilesetImportTools` |
| `src/HmaMcp/Png.cs` | PNG reading/writing and color conversion for the graphics tools |
| `src/HexManiac.Core.Build/` | Builds HMA's core library from the submodule |
| `tests/smoke.py` | End-to-end test over real MCP stdio |
| `docs/hma-upstream-notes.md` | Issues found in HMA itself |

## Adding a tool

1. Add a `[McpServerTool]` method to a class in `src/HmaMcp/Tools/`. Give it a clear `Description`, since that's what the AI reads to decide when to use it.
2. Read-only tools use `sessions.With(...)`. Tools that change the ROM use `sessions.Edit(...)`, so that each call is exactly one undo step.
3. Throw `McpException` with a message that says what to do next (e.g. "use list_anchors to find…"). Other exceptions reach the client as a generic error.
4. Add a check to `tests/smoke.py`.

## Before opening a PR

```bash
dotnet build src/HmaMcp -c Release
python tests/smoke.py src/HmaMcp/bin/Release/net8.0/hma-mcp     # no ROM: what CI runs
python tests/smoke.py src/HmaMcp/bin/Release/net8.0/hma-mcp path/to/firered.gba   # full run, if you have FireRed (U) 1.0
python tests/smoke_emerald.py src/HmaMcp/bin/Release/net8.0/hma-mcp path/to/emerald.gba   # if you have Emerald (U)
```

Say in the PR which games you tested with.

## Bugs in HMA itself

If a problem comes from HexManiacAdvance rather than hma-mcp, add it to [docs/hma-upstream-notes.md](docs/hma-upstream-notes.md) with where it is, what goes wrong, and a suggested fix.

## Releasing

1. Set `<Version>` in `src/HmaMcp/HmaMcp.csproj` and write `.github/release-notes/v<version>.md`.
2. Push a tag `v<version>`. The [release workflow](.github/workflows/release.yml) builds and tests self-contained servers for Windows, Linux and macOS, packs `.mcpb` bundles, and publishes the GitHub release, then publishes its `server.json` to the [MCP Registry](https://registry.modelcontextprotocol.io).
