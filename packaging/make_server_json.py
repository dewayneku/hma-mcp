"""Write the MCP Registry server.json for a release.

usage: python packaging/make_server_json.py <version> <tag> <folder with hma-mcp-*.mcpb>

Each .mcpb bundle in the folder becomes one package, with its download URL on the
GitHub release for <tag> and its SHA-256 (MCP clients check it before installing).
"""
import hashlib
import json
import pathlib
import sys

sys.stdout.reconfigure(encoding="utf-8")
version, tag, folder = sys.argv[1], sys.argv[2], pathlib.Path(sys.argv[3])
repo = "https://github.com/dewayneku/hma-mcp"

packages = []
for bundle in sorted(folder.glob("hma-mcp-*.mcpb")):
    packages.append({
        "registryType": "mcpb",
        "identifier": f"{repo}/releases/download/{tag}/{bundle.name}",
        "fileSha256": hashlib.sha256(bundle.read_bytes()).hexdigest(),
        "transport": {"type": "stdio"},
    })
if not packages:
    sys.exit(f"no hma-mcp-*.mcpb files in {folder}")

print(json.dumps({
    "$schema": "https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json",
    "name": "io.github.dewayneku/hma-mcp",
    "title": "HexManiacAdvance (hma-mcp)",
    "description": "Hack GBA Pokémon ROMs with HexManiacAdvance's engine: tables, maps, scripts, text, undo.",
    "version": version,
    "websiteUrl": repo,
    "repository": {"url": repo, "source": "github"},
    "packages": packages,
}, indent=2, ensure_ascii=False))
