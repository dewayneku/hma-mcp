"""End-to-end test of hma-mcp against a real ROM.

usage: python tests/smoke.py <path-to-hma-mcp.exe> [firered.gba]

Without a ROM it only checks the handshake and tool list. With a ROM it edits a COPY:
it copies the ROM into a temp folder first and never writes to the path you pass.
Written against FireRed (U) 1.0 (BPRE); other games may name anchors differently.
"""
import os
import re
import shutil
import sys
import tempfile

from mcp_client import McpClient
import png_util

exe = os.path.abspath(sys.argv[1])
source_rom = sys.argv[2] if len(sys.argv) > 2 else None

c = McpClient(exe)
print("server:", c.server_info)
names = [t["name"] for t in c.tools()]
print(len(names), "tools:", ", ".join(names), "\n")
expected = {"open_rom", "rom_info", "save", "undo", "redo", "close_rom", "list_anchors", "resolve",
            "describe_table", "read_table", "write_table_field", "read_bytes", "write_bytes",
            "find_free_space", "hma_edit", "decompile_script", "compile_script",
            "find_map", "map_events", "read_text", "write_text",
            "view_image", "read_palette", "write_palette", "import_image", "render_map",
            "add_map_event", "remove_map_event", "read_map_blocks", "paint_map_blocks", "view_blockset", "resize_map"}
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

# --- maps: find by name, everything on a map, warps/connections name where they lead
pallet = c.call("find_map", name="pallet town")
assert pallet["total"] == 5 and pallet["maps"][0]["id"] == "3.0", pallet
assert [m["id"] for m in c.call("find_map", name="route 1")["maps"]] == ["3.19"]  # not ROUTE 10-19
c.call("map_events", expect_error=True, map="PALLET TOWN")  # 5 maps share the name: asks for an id
c.call("map_events", expect_error=True, map="99.99")
ev = c.call("map_events", map="3.0")
assert ev["name"] == "PALLET TOWN" and ev["layout"]["width"] == 24 and ev["settings"]["music"] == "mus_pallet", ev["settings"]
objs = ev["events"]["objects"]
assert len(objs["items"]) == 3 and objs["items"][0]["script"] == "<16575C>", objs
assert ev["events"]["warps"]["items"][0]["to"] == "4.0 PALLET TOWN", ev["events"]["warps"]
signs = ev["events"]["signposts"]["items"]
assert len(signs) == 5 and signs[0]["arg"].startswith("<"), signs
assert [x["to"] for x in ev["connections"]["items"]] == ["3.19 ROUTE 1", "3.39 ROUTE 21"], ev["connections"]
# the table address and _index it returns work with write_table_field
c.call("write_table_field", table=objs["table"], row="0", field="x", value="4")
assert c.call("map_events", map="3.0")["events"]["objects"]["items"][0]["x"] == 4
c.call("undo")

# --- text by address. The sign's text: HMA knows no pointer to it until the sign script is formatted,
# so read_text/write_text find the pointer by searching the ROM.
sign_text = re.search(r"msgbox\.\w+ <([0-9A-F]{6})>", c.call("decompile_script", address=signs[0]["arg"].strip("<>"))["script"]).group(1)
t = c.call("read_text", address=sign_text)
assert t["text"] == "OAK POKéMON RESEARCH LAB" and len(t["pointed_from"]) == 1, t
raw = c.call("read_bytes", address=sign_text, length=t["length"])
c.call("write_text", address=sign_text, text=t["text"])  # unchanged: byte-identical
assert c.call("read_bytes", address=sign_text, length=t["length"]) == raw
w = c.call("write_text", address=sign_text, text="PROF. OAK'S POKéMON\nRESEARCH LABORATORY")
assert w["moved_from"] == sign_text and w["after"] == "PROF. OAK'S POKéMON\nRESEARCH LABORATORY", w
assert f"<{w['address']}>" in c.call("decompile_script", address=signs[0]["arg"].strip("<>"))["script"]
c.call("undo")
assert c.call("read_bytes", address=sign_text, length=t["length"]) == raw
# line breaks: first is \n (FE), later ones \l (FA), a blank line a new box (FB); a short text is FF-filled
c.call("write_text", address=sign_text, text="A\nB\nC\n\nD")
assert c.call("read_bytes", address=sign_text, length=10).endswith("BB FE BC FA BD FB BE FF FF FF"), "line breaks"
c.call("undo")
c.call("write_text", expect_error=True, address=sign_text, text="A{B")  # not in the font
c.call("write_text", expect_error=True, address="data.pokemon.stats", text="X")  # not text
# text through a table pointer gets the same line-break handling; an unchanged rewrite is byte-identical
potion = c.call("read_table", table="data.items.stats", start=13, count=1, fields="description")["rows"][0]["description"]
r = c.call("write_table_field", table="data.items.stats", row="POTION", field="description", value=potion.replace("\r\n", "\n"))
assert r["after"] == potion, r
r = c.call("write_table_field", table="data.items.stats", row="POTION", field="description", value="Heals 20 HP\nof one POKéMON.")
assert r["after"].replace("\r\n", "\n") == "Heals 20 HP\nof one POKéMON.", r
c.call("undo")

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

# --- graphics (row 4 is Charmander, renamed CHARMY above): sprites and palettes as PNG, import with exact indices or color matching, map pictures
front, png = c.call_image("view_image", address="graphics.pokemon.sprites.front", row="4",
                          save_to=os.path.join(work, "charmander.png"))
assert front["kind"] == "sprite" and front["size"] == "64x64" and front["compressed"], front
assert png_util.size(png) == (256, 256), "preview is drawn at 4x"
pal = c.call("read_palette", address=front["palette"])
assert len(pal["colors"]) == 16 and pal["colors"][7] == "#F76300", pal["colors"]  # Charmander orange, not swapped to blue
assert any(u["address"] == front["address"] for u in pal["used_by"])
back = c.call("view_image", address="graphics.pokemon.sprites.back", row="4")  # text part only
back_bytes = c.call("read_bytes", address=back["address"], length=64)
front_bytes = c.call("read_bytes", address=front["address"], length=64)
# the saved PNG is indexed with the sprite's palette; importing it unchanged writes nothing
w, h, ct, plte, rows = png_util.read(os.path.join(work, "charmander.png"))
assert (w, h, ct, len(plte)) == (64, 64, 3, 48), (w, h, ct, len(plte))
r, _ = c.call_image("import_image", address="graphics.pokemon.sprites.front", row="4", file=os.path.join(work, "charmander.png"))
assert r["changed"] is False and c.call("read_bytes", address=front["address"], length=64) == front_bytes, r
# flipped, same palette: exact indices, the shared palette and the back sprite are untouched
png_util.write(os.path.join(work, "flip.png"), w, h, ct, plte, [row[::-1] for row in rows])
r, _ = c.call_image("import_image", address="graphics.pokemon.sprites.front", row="4", file=os.path.join(work, "flip.png"))
assert r["changed"] and r["palette_changed"] is False and r["mode"].startswith("exact"), r
assert c.call("read_bytes", address=back["address"], length=64) == back_bytes
assert c.call("read_palette", address=front["palette"])["colors"] == pal["colors"]
c.call("undo")
assert c.call("read_bytes", address=front["address"], length=64) == front_bytes
# a truecolor PNG is matched to the palette
rgb = [bytearray(b"".join(plte[i * 3:i * 3 + 3] for i in row[::-1])) for row in rows]
png_util.write(os.path.join(work, "flip_rgb.png"), w, h, 2, None, rgb)
r, _ = c.call_image("import_image", address="graphics.pokemon.sprites.front", row="4", file=os.path.join(work, "flip_rgb.png"), mode="cautious")
assert r["changed"] and r["palette_changed"] is False, r
c.call("undo")
assert c.call("read_bytes", address=front["address"], length=64) == front_bytes
c.call("import_image", expect_error=True, address="graphics.pokemon.sprites.front", row="4", file=os.path.join(work, "nope.png"))
c.call("import_image", expect_error=True, address="graphics.pokemon.sprites.front", row="4", file=os.path.join(work, "flip.png"), mode="bold")
c.call("view_image", expect_error=True, address="graphics.pokemon.sprites.front")  # a table needs a row
c.call("view_image", expect_error=True, address="data.pokemon.stats", row="1")  # not graphics
# palettes: write, see it, undo
c.call("write_palette", address=front["palette"], colors=[pal["colors"][0], "#000000"])
assert c.call("read_palette", address=front["palette"])["colors"][1] == "#000000"
c.call("write_palette", expect_error=True, address=front["palette"], colors=["red"])
c.call("undo")
assert c.call("read_palette", address=front["palette"])["colors"] == pal["colors"]
# palette tables use the sprite's row; overworld rows reach their frames through nested pointers
shiny = c.call("view_image", address="graphics.pokemon.sprites.front", row="4", palette="graphics.pokemon.palettes.shiny")
assert shiny["palette"] != front["palette"], shiny
ow = c.call("view_image", address="graphics.overworld.sprites", row="0")
assert ow["size"] == "16x32" and ow["pages"] > 1, ow
# maps: 16 px per block
m, png = c.call_image("render_map", map="3.0")
assert m["size_blocks"] == "24x20" and png_util.size(png) == (384, 320), m
m, png = c.call_image("render_map", map="3.0", events="none", scale=2)
assert png_util.size(png) == (768, 640)
c.call("render_map", expect_error=True, map="3.0", events="some")

# --- map editing: add/remove events, read/paint blocks, blockset, resize; each is one undo step
before = c.call("map_events", map="3.0")
npcs = before["events"]["objects"]["items"]
a = c.call("add_map_event", map="3.0", kind="npc", x=12, y=14, fields={"graphics": "5", "script": npcs[0]["script"]})
assert a["index"] == len(npcs) and a["event"]["x"] == 12 and a["event"]["graphics"] == "5", a
assert a["event"]["elevation"] == "3" and a["event"]["id"] == len(npcs) + 1, a  # elevation copied from the other NPCs; next free id
s = c.call("add_map_event", map="3.0", kind="sign", x=5, y=5, fields={"arg": before["events"]["signposts"]["items"][0]["arg"]})
w = c.call("add_map_event", map="3.0", kind="warp", x=1, y=1, fields={"bank": "4", "map": "1", "warpID": "0"})
assert c.call("map_events", map="3.0")["events"]["warps"]["items"][w["index"]]["to"].startswith("4.1"), w
c.call("add_map_event", expect_error=True, map="3.0", kind="npc", x=99, y=1)
c.call("add_map_event", expect_error=True, map="3.0", kind="npc", x=1, y=1, fields={"nope": "1"})
c.call("add_map_event", expect_error=True, map="3.0", kind="statue", x=1, y=1)
r = c.call("remove_map_event", map="3.0", kind="npc", index=a["index"])
assert r["remaining"] == len(npcs) and r["removed"]["x"] == 12, r
c.call("remove_map_event", expect_error=True, map="3.0", kind="npc", index=99)
for _ in range(4):
    c.call("undo")
after = c.call("map_events", map="3.0")["events"]
assert [len(after[k]["items"]) for k in ("objects", "warps", "signposts")] == [len(before["events"][k]["items"]) for k in ("objects", "warps", "signposts")]
# blocks: copy the top-left house onto the bottom of the map, then undo
house = c.call("read_map_blocks", map="3.0", x=4, y=2, width=6, height=5)
assert house["blocks"][2].split()[1] == "289" and house["movement"][2].split()[1] == "01", house
original = c.call("read_map_blocks", map="3.0", x=4, y=13, width=6, height=5)
p = c.call("paint_map_blocks", map="3.0", x=4, y=13, blocks=house["blocks"], movement=house["movement"])
assert p["cells_changed"] > 0 and c.call("read_map_blocks", map="3.0", x=4, y=13, width=6, height=5)["blocks"] == house["blocks"]
c.call("paint_map_blocks", map="3.0", x=4, y=13, blocks=[". 001"], movement=["01"])  # '.' keeps a cell; one movement value for all painted cells
cell = c.call("read_map_blocks", map="3.0", x=4, y=13, width=2, height=1)
assert cell["blocks"][0].split() == [house["blocks"][0].split()[0], "001"] and cell["movement"][0].split()[1] == "01", cell
c.call("undo")
c.call("undo")
assert c.call("read_map_blocks", map="3.0", x=4, y=13, width=6, height=5)["blocks"] == original["blocks"]
c.call("paint_map_blocks", expect_error=True, map="3.0", x=22, y=0, blocks=["001 001 001"])  # off the edge
c.call("paint_map_blocks", expect_error=True, map="3.0", x=0, y=0, blocks=["3FF"])  # not in the tilesets
bs, png = c.call_image("view_blockset", map="3.0", count=64)
assert bs["primary_tileset_blocks"] == "000-27F" and png_util.size(png) == (256, 256), bs
# resize: growing on the left shifts events right so they stay in place
r = c.call("resize_map", map="3.0", left=2, bottom=1)
assert (r["before"], r["after"]) == ("24x20", "26x21"), r
assert c.call("map_events", map="3.0")["events"]["objects"]["items"][0]["x"] == npcs[0]["x"] + 2
c.call("undo")
assert c.call("map_events", map="3.0")["layout"]["width"] == 24
c.call("resize_map", expect_error=True, map="3.0", right=200, bottom=200)

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
