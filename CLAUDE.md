# hma-mcp

MCP server (C#, .NET 8) exposing HexManiacAdvance's core engine as tools for GBA Pokémon ROM hacking. See README.md for the user-facing view and CONTRIBUTING.md for code layout.

## Build, test, install

- Build: `dotnet build src/HmaMcp -c Release` (the .NET SDK is at `C:\Program Files\dotnet`; add it to PATH in Git Bash if `dotnet` isn't found).
- Quick test (no ROM): `python tests/smoke.py src/HmaMcp/bin/Release/net8.0/hma-mcp.exe`
- Full test: `python tests/smoke.py <exe> <firered.gba>`. It copies the ROM to a temp folder first.
- The installed server Claude Code uses is `%LOCALAPPDATA%\hma-mcp\hma-mcp.exe`, registered as user-scope MCP server `hma`. To ship a change: `dotnet publish src/HmaMcp -c Release -o "$LOCALAPPDATA/hma-mcp"`. If a session is using the server, the exe is locked; the change takes effect in the next session. To install while it's running, use `scripts/install.ps1` (sets locked files aside).

## Rules

- **Never edit a user's original ROM.** Work on a copy in `playground/` (git-ignored). Never commit `.gba` or `.toml` files.
- HMA lives in the `external/HexManiacAdvance` submodule, pinned to upstream. Don't change the pin to a fork commit.
- Problems found in HMA itself go in `docs/hma-upstream-notes.md` (where, problem, fix, size, status). Fix branches go to the fork `https://github.com/dewayneku/HexManiacAdvance` (remote `fork` in the submodule), one branch per fix off upstream `master`. Don't open PRs or issues on `haven1433/HexManiacAdvance` unless the owner asks.
- Limitations of hma-mcp found while testing go in `docs/limitations.md`.
- HMA's C# files use CRLF line endings and a UTF-8 BOM. Git Bash `sed -i` silently converts them to LF, so edit them with the Edit tool or a byte-exact Python script instead.
- Each edit tool must go through `sessions.Edit(...)` so it's one undo step. Read-only tools use `sessions.With(...)`. User-facing errors are `McpException`.
- Before claiming an anchor name or table field exists (in docs, examples or server instructions), check it on a real ROM with `list_anchors` or `describe_table`.

## Status

Public since 2026-11-10. Tested on FireRed (U) 1.0 (`tests/smoke.py`) and Emerald (U) (`tests/smoke_emerald.py`). Anything pushed is visible to everyone: keep ROMs, `.toml` files, local paths and personal info out of commits.
