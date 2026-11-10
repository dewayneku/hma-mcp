"""End-to-end test of hma-mcp against a real ROM.

usage: python tests/smoke.py <path-to-hma-mcp.exe> [firered.gba]

Without a ROM it only checks the handshake and tool list. With a ROM it edits a COPY:
it copies the ROM into a temp folder first and never writes to the path you pass.
Written against FireRed (U) 1.0 (BPRE); other games may name anchors differently.
"""
import os
import shutil
import sys
import tempfile

from mcp_client import McpClient

exe = sys.argv[1]
source_rom = sys.argv[2] if len(sys.argv) > 2 else None

c = McpClient(exe)
print("server:", c.server_info)
names = [t["name"] for t in c.tools()]
print(len(names), "tools:", ", ".join(names), "\n")
c.call("rom_info", expect_error=True)

if not source_rom:
    c.close()
    sys.exit(0)

work = tempfile.mkdtemp(prefix="hma-mcp-test-")
rom = os.path.join(work, "test.gba")
shutil.copy(source_rom, rom)

# --- open / read
info = c.call("open_rom", path=rom)
assert info["game_code"].startswith("BPRE"), info
stats = c.call("read_table", table="data.pokemon.stats", start=1, count=1)["rows"][0]
assert stats["_name"] == "BULBASAUR" and stats["hp"] == 45 and stats["type1"] == "GRASS", stats

# --- int write + undo/redo
c.call("write_table_field", table="data.pokemon.stats", row="BULBASAUR", field="hp", value="99")
c.call("undo")
assert c.call("read_table", table="data.pokemon.stats", start=1, count=1, fields="hp")["rows"][0]["hp"] == 45
c.call("redo")
assert c.call("read_table", table="data.pokemon.stats", start=1, count=1, fields="hp")["rows"][0]["hp"] == 99

# --- enum write by name
r = c.call("write_table_field", table="data.pokemon.stats", row="CHARMANDER", field="type2", value="DRAGON")
assert r["after"] == "DRAGON", r

# --- fixed-length text write
r = c.call("write_table_field", table="data.pokemon.names", row="4", field="name", value="CHARMY")
assert r["after"] == "CHARMY", r

# --- bad input gives clean errors
c.call("write_table_field", expect_error=True, table="data.pokemon.stats", row="1", field="nope", value="1")
c.call("write_table_field", expect_error=True, table="data.pokemon.stats", row="1", field="type1", value="NOTATYPE")
c.call("resolve", expect_error=True, target="not.an.anchor")

# --- scripts: find an event script, decompile, recompile unchanged (byte-identical), then grow it
anchors = c.call("list_anchors", filter="", limit=2000)["anchors"]
event = next((a for a in anchors if a["kind"] == "event-script"), None)
if event:
    before = c.call("read_bytes", address=event["address"], length=64)
    d = c.call("decompile_script", address=event["anchor"])
    c.call("compile_script", address=event["anchor"], script=d["script"])
    after = c.call("read_bytes", address=event["address"], length=64)
    assert before == after, "recompiling an unchanged script changed bytes"

    # grow it by one command: it no longer fits, so HMA must move it and update its pointers
    grown = d["script"].replace("  sound se_ding_dong", "  sound se_ding_dong\r\n  sound se_ding_dong", 1)
    assert grown != d["script"], "test script changed; update the grow step"
    r = c.call("compile_script", address=event["anchor"], script=grown)
    if r["moved_from"]:
        assert c.call("resolve", target=event["anchor"])["address"] == r["address"], "anchor did not follow the moved script"
    again = c.call("decompile_script", address=event["anchor"])["script"]
    assert again.count("sound se_ding_dong") == 2, again
    c.call("undo")
    assert c.call("read_bytes", address=event["address"], length=64) == before, "undo did not restore the script"
    assert c.call("resolve", target=event["anchor"])["address"] == event["address"]
else:
    print("!! no event-script anchor found; script round-trip skipped")

# --- hma_edit: write a new anchored text in free space
free = c.call("find_free_space", length=32)["address"]
c.call("hma_edit", address=free, text='^test.hello"" "Hello from hma-mcp!" ')
res = c.call("resolve", target="test.hello")
assert res["run"]["kind"] == "text", res

# --- save round-trip: reopen and check every edit stuck, and the metadata kept our anchor
saved = c.call("save")
assert saved["backup"] and os.path.exists(saved["backup"]), saved
assert os.path.exists(saved["metadata"]), saved
c.call("open_rom", path=rom)
assert c.call("read_table", table="data.pokemon.stats", start=1, count=1, fields="hp")["rows"][0]["hp"] == 99
assert c.call("read_table", table="data.pokemon.stats", start=4, count=1, fields="type2")["rows"][0]["type2"] == "DRAGON"
assert c.call("read_table", table="data.pokemon.names", start=4, count=1)["rows"][0]["name"] == "CHARMY"
assert c.call("resolve", target="test.hello")["run"]["kind"] == "text"
# the backup must still be the untouched original
with open(source_rom, "rb") as a, open(saved["backup"], "rb") as b:
    assert a.read() == b.read(), "backup differs from the original ROM"

c.close()
shutil.rmtree(work)
print("ALL CHECKS PASSED")
