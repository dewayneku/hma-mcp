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

exe = os.path.abspath(sys.argv[1])
source_rom = sys.argv[2] if len(sys.argv) > 2 else None

c = McpClient(exe)
print("server:", c.server_info)
names = [t["name"] for t in c.tools()]
print(len(names), "tools:", ", ".join(names), "\n")
expected = {"open_rom", "rom_info", "save", "undo", "redo", "close_rom", "list_anchors", "resolve",
            "describe_table", "read_table", "write_table_field", "read_bytes", "write_bytes",
            "find_free_space", "hma_edit", "decompile_script", "compile_script"}
assert expected <= set(names), f"missing tools: {expected - set(names)}"
c.call("rom_info", expect_error=True)

if not source_rom:
    # No real ROM: open a blank one. That still makes HMA load all of its reference data
    # (resources/*.txt, *.toml), which is what breaks first on a new OS or install layout.
    blank_dir = tempfile.mkdtemp(prefix="hma-mcp-blank-")
    blank = os.path.join(blank_dir, "blank.gba")
    with open(blank, "wb") as f:
        f.write(b"\xff" * 0x100000)
    info = c.call("open_rom", path=blank)
    assert info["size"] == "0x100000", info
    c.call("find_free_space", length=16)
    c.call("decompile_script", address="0", kind="event")  # needs the script reference tables
    c.close()
    shutil.rmtree(blank_dir)
    print("NO-ROM CHECKS PASSED")
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

# --- values are checked against the field size instead of silently wrapping
c.call("write_table_field", expect_error=True, table="data.pokemon.stats", row="IVYSAUR", field="speed", value="300")
c.call("write_table_field", expect_error=True, table="data.pokemon.stats", row="IVYSAUR", field="catchRate", value="-1")
c.call("write_table_field", expect_error=True, table="data.items.stats", row="POTION", field="price", value="70000")
c.call("write_table_field", expect_error=True, table="data.pokemon.names", row="2", field="name", value="IVYSAURRRRRRR")
assert c.call("read_table", table="data.pokemon.names", start=2, count=1)["rows"][0]["name"] == "IVYSAUR"
r = c.call("write_table_field", table="data.pokemon.moves.stats.battle", row="SPARK", field="priority", value="-1")
assert r["after"] == -1, r  # signed field

# --- row names: unquoted, and matched without quotes/accents/case
items = c.call("read_table", table="data.items.stats", start=4, count=1, fields="price")["rows"][0]
assert items["_name"] == "POKé BALL", items
r = c.call("write_table_field", table="data.items.stats", row="poke ball", field="price", value=str(items["price"] + 1))
assert r["row"] == 4 and r["after"] == items["price"] + 1, r

# --- calculated, tuple, bits and switch fields
row = c.call("read_table", table="data.pokemon.stats", start=2, count=1)["rows"][0]
assert row["baseStatTotal"] == sum(row[k] for k in ("hp", "attack", "def", "speed", "spatk", "spdef")), row
assert isinstance(row["evs"], dict) and "spatk" in row["evs"], row
r = c.call("write_table_field", table="data.pokemon.stats", row="IVYSAUR", field="evs", value="hp=2, spdef=1")
assert r["after"]["hp"] == 2 and r["after"]["spdef"] == 1 and r["after"]["spatk"] == row["evs"]["spatk"], r
c.call("write_table_field", expect_error=True, table="data.pokemon.stats", row="IVYSAUR", field="evs", value="hp=9")  # 2 bits
c.call("write_table_field", expect_error=True, table="data.pokemon.stats", row="IVYSAUR", field="baseStatTotal", value="600")
tms = c.call("read_table", table="data.pokemon.moves.tmcompatibility", start=2, count=1)["rows"][0]["moves"]
assert isinstance(tms, list) and "SOLARBEAM" in tms, tms
r = c.call("write_table_field", table="data.pokemon.moves.tmcompatibility", row="IVYSAUR", field="moves", value="+FOCUS PUNCH, -SOLARBEAM")
assert "FOCUS PUNCH" in r["after"] and "SOLARBEAM" not in r["after"] and len(r["after"]) == len(tms), r
c.call("write_table_field", expect_error=True, table="data.pokemon.moves.tmcompatibility", row="IVYSAUR", field="moves", value="+NOT A MOVE")
evo = c.call("read_table", table="data.pokemon.evolutions", start=133, count=1, fields="method1,arg1")["rows"][0]
assert evo["method1"] == "Stone" and evo["arg1"].endswith("STONE"), evo  # arg switches to an item for stones
r = c.call("write_table_field", table="data.pokemon.evolutions", row="EEVEE", field="arg1", value="FIRE STONE")
assert r["after"] == "FIRE STONE", r
assert "unused_2" in [f["name"] for f in c.call("describe_table", table="data.pokemon.moves.stats.battle")["fields"]]

# --- nested data behind pointers: learnsets, trainer teams, 1-element tables
lv = c.call("read_table", table="data.pokemon.moves.levelup", start=2, count=1)["rows"][0]["movesFromLevel"]
r = c.call("write_table_field", table="data.pokemon.moves.levelup", row="IVYSAUR", field="movesFromLevel", value=lv.replace("\r\n", "\n") + '\n("VINE WHIP" 70)')
assert r["after"].endswith('("VINE WHIP" 70)') and "warning" not in r, r
r = c.call("write_table_field", table="data.pokemon.moves.levelup", row="IVYSAUR", field="movesFromLevel", value="(NOTAMOVE 5)")
assert r.get("changed_lines"), r  # HMA stores a default for unknown names: the tool must say so
c.call("undo")
brock = c.call("read_table", table="data.trainers.stats", where="name=BROCK", count=1, fields="pokemonCount,pokemon")["rows"][0]
team = brock["pokemon"] + '\n15 ONIX (IVs=0)\n- TACKLE\n- BIND\n- "ROCK TOMB"\n- -\n'
c.call("write_table_field", table="data.trainers.stats", row=str(brock["_index"]), field="pokemon", value=team)
after = c.call("read_table", table="data.trainers.stats", start=brock["_index"], count=1, fields="pokemonCount")["rows"][0]
assert after["pokemonCount"] == brock["pokemonCount"] + 1, after
route1 = c.call("read_table", table="data.pokemon.wild", where="bank=3, map=19", count=5)["rows"]
assert len(route1) == 1, route1
grass = route1[0]["grass"]
r = c.call("write_table_field", table="data.pokemon.wild", row=str(route1[0]["_index"]), field="grass", value=grass.replace("rate : ", "rate : 1"))
assert r["after"] != grass and "warning" not in r, r
c.call("write_table_field", expect_error=True, table="data.pokemon.wild", row=str(route1[0]["_index"]), field="surf", value="rate : 1")

# --- errors from inside HMA come back with a message, not "An error occurred"
c.call("write_table_field", expect_error=True, table="data.pokemon.moves.levelup", row="IVYSAUR", field="movesFromLevel", value="@FFFFFFFF")

# --- scripts with text/movement blocks: LF-only text must recompile byte-identically (HMA's parser
# assumes CRLF and used to add a newline to every block, moving all its text), and pointing at inline
# movement by address must not erase it. 16575C is the Pallet Town NPC by the sign (FireRed 1.0).
npc = "16575C"
npc_before = c.call("read_bytes", address=npc, length=256)
d = c.call("decompile_script", address=npc)["script"]
assert "<auto>" in d and "{" in d, d
c.call("compile_script", address=npc, script=d.replace("\r\n", "\n"))
assert c.call("read_bytes", address=npc, length=256) == npc_before, "LF script recompile changed bytes"
movement = c.call("read_bytes", address="165829", length=6)
explicit = (d.replace("\r\n", "\n").replace("move.npc 1 <auto>\n{\nwalk_right", "move.npc 1 <165829>\n{\nwalk_right", 1)
            .replace("move.npc 1 <auto>\n{\nwalk_left", "move.npc 1 <16582C>\n{\nwalk_left", 1))
assert explicit != d.replace("\r\n", "\n")
c.call("compile_script", address=npc, script=explicit)
assert c.call("read_bytes", address="165829", length=6) == movement, "inline movement was erased"
c.call("undo")
assert c.call("read_bytes", address=npc, length=256) == npc_before

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
