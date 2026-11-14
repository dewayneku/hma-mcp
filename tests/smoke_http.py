"""End-to-end check of hma-mcp's HTTP mode (--http): the token gate, the ROM folder limit, and tools over Streamable HTTP.

usage: python tests/smoke_http.py <path-to-hma-mcp.exe> [firered.gba]

Without a ROM it opens a blank one. It copies any ROM into a temp ROM folder first.
"""
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time

from http_client import HttpMcpClient, status_of
import png_util

exe = os.path.abspath(sys.argv[1])
source_rom = sys.argv[2] if len(sys.argv) > 2 else None
work = tempfile.mkdtemp(prefix="hma-mcp-http-")
rom_dir = os.path.join(work, "roms")
os.makedirs(rom_dir)
if source_rom:
    shutil.copyfile(source_rom, os.path.join(rom_dir, "game.gba"))
else:
    with open(os.path.join(rom_dir, "game.gba"), "wb") as f:
        f.write(b"\xff" * 0x100000)
with open(os.path.join(work, "outside.gba"), "wb") as f:
    f.write(b"\xff" * 0x100000)

with socket.socket() as s:
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
token = "test-token-0123456789abcdef"
server = subprocess.Popen([exe, "--http", "--port", str(port), "--token", token, "--rom-dir", rom_dir],
                          stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True)
base = f"http://127.0.0.1:{port}"
try:
    # wait for it to listen
    for _ in range(100):
        try:
            socket.create_connection(("127.0.0.1", port), timeout=0.2).close()
            break
        except OSError:
            time.sleep(0.2)
    else:
        raise AssertionError("server didn't start: " + server.stderr.read())

    # --- the token gate: no token, a wrong one, or another path is a plain 404
    assert status_of(f"{base}/mcp") == 404
    assert status_of(f"{base}/mcp/wrong-token-0000000000") == 404
    assert status_of(f"{base}/mcp", bearer="wrong-token-0000000000") == 404
    assert status_of(f"{base}/") == 404

    # --- the token in the URL (how ChatGPT-style clients connect), and as a bearer header
    c = HttpMcpClient(f"{base}/mcp/{token}")
    assert c.server_info["name"] == "hma-mcp", c.server_info
    names = [t["name"] for t in c.tools()]
    assert {"open_rom", "view_image", "import_tileset", "add_region_name"} <= set(names), names
    c2 = HttpMcpClient(f"{base}/mcp", bearer=token)
    assert len(c2.tools()) == len(names)

    # --- the ROM folder: relative paths are inside it, anything outside is refused
    info = c.call("open_rom", path="game.gba")
    assert info["path"] == "game.gba", info  # shown relative: the server's folders aren't revealed
    err = c.call("open_rom", expect_error=True, path=os.path.join(work, "outside.gba"))
    assert "outside the ROM folder" in err, err
    err = c.call("open_rom", expect_error=True, path="../outside.gba")
    assert "outside the ROM folder" in err, err
    c.call("save", expect_error=True, path="../escape.gba")
    c.call("save", expect_error=True, path="notes.txt")  # save-as must be a .gba

    if source_rom:
        # images come back over HTTP too, and save_to stays inside the folder
        r, png = c.call_image("view_image", address="graphics.pokemon.sprites.front", row="CHARMANDER", save_to="art/charmander.png")
        assert r["saved_to"] == "art/charmander.png" and png_util.size(png) == (256, 256), r
        assert os.path.exists(os.path.join(rom_dir, "art", "charmander.png"))
        c.call("view_image", expect_error=True, address="graphics.pokemon.sprites.front", row="CHARMANDER", save_to="../x.png")
        c.call("write_table_field", table="data.pokemon.stats", row="BULBASAUR", field="hp", value="99")
        saved = c.call("save")
        assert saved["saved"] == "game.gba" and saved["backup"].startswith(".hma-mcp-backups/"), saved
    print("\nHTTP CHECKS PASSED")
finally:
    server.terminate()
    try:
        server.wait(10)
    except subprocess.TimeoutExpired:
        server.kill()
    shutil.rmtree(work, ignore_errors=True)
