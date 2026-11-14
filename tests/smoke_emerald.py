"""End-to-end check of hma-mcp against Emerald (U) (BPEE0).

usage: python tests/smoke_emerald.py <path-to-hma-mcp.exe> <emerald.gba>

Like smoke.py, it copies the ROM into a temp folder and never writes to the path you pass.
smoke.py is the full test (written against FireRed); this one checks that the same tools
work on Emerald's tables, maps, scripts and text.
"""
import os
import re
import shutil
import sys
import tempfile

from mcp_client import McpClient
import png_util

exe = os.path.abspath(sys.argv[1])
work = tempfile.mkdtemp(prefix="hma-mcp-emerald-")
rom = os.path.join(work, "emerald.gba")
shutil.copyfile(sys.argv[2], rom)

c = McpClient(exe)
try:
    info = c.call("open_rom", path=rom)
    assert info["game_code"] == "BPEE0", info["game_code"]

    # --- tables: Gen 3 species order (TREECKO is row 277), range checks, enums
    row = c.call("read_table", table="data.pokemon.stats", start=277, count=1)["rows"][0]
    assert row["_name"] == "TREECKO" and row["type1"] == "GRASS" and row["ability1"] == "OVERGROW", row
    r = c.call("write_table_field", table="data.pokemon.stats", row="TREECKO", field="type2", value="DRAGON")
    assert (r["before"], r["after"]) == ("GRASS", "DRAGON"), r
    c.call("write_table_field", expect_error=True, table="data.pokemon.stats", row="TREECKO", field="speed", value="300")
    c.call("write_table_field", expect_error=True, table="data.pokemon.stats", row="TREECKO", field="type2", value="FAIRY")
    potion = c.call("read_table", table="data.items.stats", start=13, count=1)["rows"][0]
    assert potion["_name"] == "POTION" and potion["price"] == 300, potion
    trainer = c.call("read_table", table="data.trainers.stats", start=1, count=1)["rows"][0]
    assert trainer["name"] == "SAWYER" and "GEODUDE" in trainer["pokemon"], trainer
    wild = c.call("read_table", table="data.pokemon.wild", where="bank=0, map=16")["rows"]
    assert len(wild) == 1 and wild[0]["grass"].startswith("rate : 20"), wild

    # --- maps: Littleroot has 6 maps with that name (town + houses/lab), Route 101 one
    town = c.call("find_map", name="littleroot")
    assert town["total"] == 6 and town["maps"][0]["id"] == "0.9", town
    assert [m["id"] for m in c.call("find_map", name="route 101")["maps"]] == ["0.16"]
    ev = c.call("map_events", map="0.9")
    assert ev["name"] == "LITTLEROOT TOWN" and ev["settings"]["music"] == "mus_littleroot", ev["settings"]
    assert (ev["layout"]["width"], ev["layout"]["height"]) == (20, 20), ev["layout"]

    # --- scripts: recompiling an unchanged NPC script leaves it byte-identical
    npc = next(o["script"].strip("<>") for o in ev["events"]["objects"]["items"] if o["script"].startswith("<"))
    d = c.call("decompile_script", address=npc)
    before = c.call("read_bytes", address=npc, length=d["compiled_length"])
    r = c.call("compile_script", address=npc, script=d["script"])
    assert r["new_length"] == d["compiled_length"] and r["pointers_updated"] == 0, r
    assert c.call("read_bytes", address=npc, length=d["compiled_length"]) == before

    # --- text: the town sign, grown so it has to move, then undone
    sign = ev["events"]["signposts"]["items"][0]["arg"].strip("<>")
    text_at = re.search(r"msgbox\.\w+ <([0-9A-F]{6})>", c.call("decompile_script", address=sign)["script"]).group(1)
    t = c.call("read_text", address=text_at)
    assert t["text"].startswith("LITTLEROOT TOWN\n"), t
    w = c.call("write_text", address=text_at, text="LITTLEROOT TOWN\nA much longer sign that won't fit in place.")
    assert w["moved_from"] == text_at, w
    assert f"<{w['address']}>" in c.call("decompile_script", address=sign)["script"]
    c.call("undo")
    assert c.call("read_text", address=text_at)["text"] == t["text"]

    # --- graphics: Treecko's sprite and palette, an overworld frame, and Littleroot drawn with its NPCs
    sprite, png = c.call_image("view_image", address="graphics.pokemon.sprites.front", row="TREECKO")
    assert sprite["size"] == "64x64" and png_util.size(png) == (256, 256), sprite
    assert len(c.call("read_palette", address=sprite["palette"])["colors"]) == 16
    ow = c.call("view_image", address="graphics.overworld.sprites", row="0")
    assert ow["size"] == "16x32" and ow["pages"] > 1, ow
    m, png = c.call_image("render_map", map="0.9", events="objects")
    assert m["size_blocks"] == "20x20" and png_util.size(png) == (320, 320), m

    # --- map editing on Emerald: an NPC in Littleroot, painted blocks, a resize; all undone
    npcs = len(c.call("map_events", map="0.9")["events"]["objects"]["items"])
    a = c.call("add_map_event", map="0.9", kind="npc", x=5, y=5, fields={"graphics": "5"})
    assert a["index"] == npcs and a["event"]["elevation"] == "3", a
    c.call("undo")
    assert len(c.call("map_events", map="0.9")["events"]["objects"]["items"]) == npcs
    blocks = c.call("read_map_blocks", map="0.9", x=0, y=0, width=2, height=2)
    c.call("paint_map_blocks", map="0.9", x=0, y=0, blocks=["001 001", "001 001"])
    c.call("undo")
    assert c.call("read_map_blocks", map="0.9", x=0, y=0, width=2, height=2)["blocks"] == blocks["blocks"]
    assert c.call("resize_map", map="0.9", right=2)["after"] == "22x20"
    c.call("undo")

    # --- a new map connected to Littleroot, then undone
    n = c.call("create_map", like="0.16", width=16, height=16)
    c.call("connect_maps", map="0.9", direction="west", to_map=n["id"])
    assert [x["direction"] for x in c.call("map_events", map=n["id"])["connections"]["items"]] == ["Right"]
    c.call("undo")
    c.call("undo")
    assert not any(x["to"].startswith(n["id"]) for x in (c.call("map_events", map="0.9")["connections"] or {"items": []})["items"])

    # --- blocks on Emerald: tall grass, a behavior change undone, tiles
    grass = c.call("read_block", map="0.16", block="00D")
    assert grass["behavior"] == "02 Tall_Grass" and "terrain" not in grass, grass  # Emerald's 2-byte attributes
    c.call("write_block", map="0.16", block="00D", layer="covered")
    c.call("undo")
    assert c.call("read_block", map="0.16", block="00D")["layer"] == "normal"
    assert c.call("view_tiles", map="0.16", tileset="secondary", count=16)["tiles"].startswith("200-")

    # --- save -> reopen keeps the edit
    c.call("save")
    c.call("close_rom")
    c.call("open_rom", path=rom)
    assert c.call("read_table", table="data.pokemon.stats", start=277, count=1, fields="type2")["rows"][0]["type2"] == "DRAGON"
    print("\nALL EMERALD CHECKS PASSED")
finally:
    c.close()
    shutil.rmtree(work, ignore_errors=True)
