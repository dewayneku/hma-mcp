# Known limitations

What hma-mcp can't do yet, or does badly, found by using it on real hacks. Each entry: what you tried, what happened, and a workaround if there is one. Entries move to "Fixed" when resolved. Problems that are in HMA itself are also tracked in [hma-upstream-notes.md](hma-upstream-notes.md).

Tested so far: FireRed (U) 1.0 (BPRE0), 2026-11-10, with these tasks:

| Area | Tried | Result |
|---|---|---|
| Base stats, types, abilities | Pikachu attack/type2, Raichu speed/catch rate, Eevee ability2 | ✅ |
| Moves | Thunderbolt power, Tackle accuracy, Spark priority −1 (signed) | ✅ |
| Items | Poké Ball and Potion prices | ✅ (row names with spaces/accents fixed, see below) |
| Names and text | Pokémon name, ability name (fixed length), ability description that grows | ✅ grown text is repointed and the old copy freed |
| Level-up learnset | Add moves to Pikachu/Bulbasaur | ✅ after fix |
| TM/HM compatibility | Add Focus Punch, remove Solarbeam | ✅ after fix |
| Evolutions | Add a level evolution; change a stone | ✅ after fix (stone item names) |
| Trainer team | Add a third Pokémon to Brock | ✅ after fix; `pokemonCount` follows |
| Wild encounters | Find Route 1, swap a slot, change grass rate | ✅ after fix (`where` filter, nested write) |
| Scripts | Pallet Town NPC and sign, `scripts.newgame.setflags` | ✅ after fixes: dialogue edit, growth + relocation, undo/redo |
| Maps | Find Pallet Town and Route 1 by name, list objects/warps/signs/connections, move an NPC | ✅ new `find_map`, `map_events` |
| Text by address | Read the Pallet Town sign text, grow it (moved, sign pointer updated), undo; line breaks and new boxes | ✅ new `read_text`, `write_text` |
| Graphics | View Charmander's front/back/shiny sprites, an overworld sprite's frames, a Pokémon icon, the title-screen tilemap; export to PNG, flip and re-import (indexed and truecolor, smart/cautious), edit a palette, undo each | ✅ new `view_image`, `import_image`, `read_palette`, `write_palette` (2026-11-14; Emerald too) |
| Map pictures | Draw Pallet Town and Route 1 (FireRed), Littleroot Town (Emerald), with and without NPCs and event markers | ✅ new `render_map` |
| Map editing | Add an NPC, sign and warp to Pallet Town and remove them; add the first NPC to a map with none; copy a house's blocks to another spot; resize Pallet Town on each side (NPCs stay in place); the same in Littleroot Town; undo each | ✅ new `add_map_event`, `remove_map_event`, `read_map_blocks`, `paint_map_blocks`, `view_blockset`, `resize_map` (2026-11-14) |
| New maps, connections | Create a route like Route 1 (named ROUTE 22), connect it east of Pallet Town, add a warp, save and reopen; disconnect and undo; a map in a new bank; the same with Littleroot Town in Emerald | ✅ new `create_map`, `connect_maps`, `disconnect_maps` |
| Round trip | save, reopen, re-read every edit, check `.toml` | ✅ moved anchors and free-space start are kept |

Emerald (U) (BPEE0), 2026-11-10, with `tests/smoke_emerald.py`: Treecko's type and a rejected out-of-range speed, Potion price, a trainer team, Route 101 wild data by `where`, Littleroot Town by name and its events, an unchanged NPC script recompiled byte-identical, the town sign grown and moved (pointer updated) then undone, save → reopen. All ✅ with no Emerald-specific fixes needed. Note that species rows 252–276 are the unused `?` slots, so Treecko is row 277.

## Open

### Maps: what's not covered yet
- **Custom tilesets from images aren't covered yet** (in progress). Existing blocks can be changed with `write_block` (tiles, behavior, layer); a primary-tileset block changes on every map that uses that tileset.
- **Indoor maps share their town's name**, as in the game's data; `find_map` shows each map's `type` (town, route, indoor, ...) to tell them apart.
- **New region names can't be added.** The region name list (`data.maps.names`) has a fixed size that the game's code relies on. A new map can use an existing name; for its own Fly spot and Town Map squares, use a name no other Fly spot uses.
- **Fly needs the visit flag.** `add_fly_spot` adds the destination; in the game it appears in the Fly menu after the player has been there.
- **Nested data is one call deeper.** `read_table` shows a nested table as `list : <3C8E60>`; read or write its rows with that address as the table. An empty pointer gets new data with `write_table_field ... value="new"`.

### Inline `<auto>` data after a script
**Tried:** recompiling the Pallet Town NPC script, whose two movements are stored right after it (decompile shows them as `<auto>`).
**What happens:** keeping them as `<auto>` is safe: they're written back in place. Replacing *both* with their real addresses also works. Replacing only one makes HMA place the remaining `<auto>` block on top of it, which corrupts the movement (HMA upstream note #10).
**Workaround:** leave `<auto>` blocks as decompiled, or replace all of them.

### Graphics: what's not covered yet
- **Sprite sizes are fixed by the game.** An imported image must match the graphic's size (or all its pages side by side / stacked); Pokémon sprites are always 64x64, for example.
- **Imports may move compressed graphics.** HMA's LZ compressor packs a little less tightly than the original game's, so even a small edit to a compressed sprite usually moves it to free space (pointers are updated; the result says `sprite_moved_from`). Importing an image identical to what's there writes nothing.

### One ROM at a time
One ROM is open per session; `open_rom` on another asks you to save or discard first. Copying data between two ROMs isn't covered.

### Updating the installed server while it's running
Windows won't overwrite a running server. `scripts/install.ps1` builds and installs anyway: it sets the locked files aside and copies the new build in. Running sessions keep the old version until they restart.

## Fixed

- **Connections could overlap.** `connect_maps` now refuses a neighbor that would overlap one already on that side, or that wouldn't touch the edge, on both maps.
- **A new NPC or sign needed its script written first.** `add_map_event` takes `message` and writes a standard talk script.
- **Removing a warp broke warps leading here.** `remove_map_event` renumbers warps on every map that lead to later warps of this map, and lists any that led to the removed one. NPCs keep their own local ids, so removing one doesn't shift the others.
- **Overworld sprites were only numbers.** `view_overworld_sprites` shows a labeled sheet; `view_blockset` labels each block's id.
- **New maps couldn't be Fly destinations or put on the Town Map.** New `add_fly_spot`, `read_town_map`, `paint_town_map`.
- **Tilemaps exported as plain colors.** They now save as indexed PNGs with all their palette pages, and an unchanged tilemap imports as a no-op.
- **Empty pointers couldn't be filled.** `write_table_field` creates new data of the right format (as HMA's table tool "Create New" does), then writes the value.
- **Typos in nested text were stored as something else.** Such a write is now undone and refused, listing the lines HMA would change, unless `accept_changes=true`.
- **Some script texts weren't shown inline.** `decompile_script` formats the script first, as HMA's editor does when it opens one (format only, not an undo step).

All of these have checks in `tests/smoke.py`.

- **Out-of-range numbers wrapped silently.** `speed=300` stored 44, `price=70000` stored 4464, `catchRate=-1` stored 255. Now an error that gives the field's range (signed fields such as move priority accept negatives).
- **Too-long fixed-length text was truncated silently** (`PIKACHUUUUUUUUU` → `PIKACHUUUU`). Now an error that says how many bytes fit.
- **Row names with spaces needed quotes.** `read_table` showed `"\"MASTER BALL\""`, and `row="POKE BALL"` didn't match. Names are now shown unquoted and matched ignoring quotes, case and accents (`poke ball` finds `POKé BALL`).
- **Tuple and bit fields** (EVs, Pokédex color, trainer music/gender, TM/HM and tutor compatibility, move targets/flags, trainer AI) read as opaque hex and couldn't be written. Tuples now read as objects and take `part=value, ...`; flag sets read as name lists and take `A, B` or `+A, -B`.
- **Switch fields** (evolution `arg` for stones, signpost `arg`) read as raw numbers and rejected item names. They now follow the field they depend on: `arg1: "THUNDERSTONE"`, signpost scripts as `<165838>`.
- **Calculated fields** (`baseStatTotal`, trainer `prizeMoney`) read as 0. They now show the computed value and are read-only.
- **Comment markers** (`basic`, `common`... in wild encounter slots) showed as fields full of 0. They're hidden.
- **Repeated field names** (two `unused` in move data) collapsed into one. The second is now `unused_2`.
- **Writing learnsets, trainer teams or nested tables** failed with an empty "An error occurred" (HMA cast every pointer target to text). They're now written like HMA's own text box does, and repointed when they grow.
- **Errors from inside HMA had no message.** They now say what failed. A failed edit is rolled back, so it leaves the ROM unchanged.
- **Recompiling a script with LF line endings moved all of its text.** HMA's parser assumes CRLF and added a newline at the start of every text and movement block, so each one grew, was moved to free space and the original erased. `compile_script` now normalizes line endings; an unchanged script recompiles byte-identically.
- **A shorter script erased data it still pointed to.** Pointing at the inline movement after a script by address made the script shorter, and the "clear the old tail" step wiped the movement. The tail is now only cleared up to the first byte the new script points into.
- **Line breaks in text were dropped.** HMA's text parser only recognizes the OS line break (CRLF on Windows), so text sent with LF lost every line break, and it also ignores a line break in the last few characters. Text written with `write_table_field` or `write_text` now has its line breaks turned into HMA's explicit codes first (`\n`, `\l`, and a blank line becomes `\pn`, the same rules HMA uses). Characters the game's font doesn't have are now an error instead of being dropped.
- **Maps had no names.** Reaching a Pallet Town NPC took five `read_table` calls and knowing it's bank 3, map 0. `find_map` goes from a name to `bank.map`, and `map_events` lists everything on the map.
- **There was no tool for text at an address.** `read_text` and `write_text` read and write a script's message (or any text), moving it and updating pointers if it grows.
- `read_table` gained `where` (e.g. `bank=3, map=19`, `name=BROCK`) and rejects unknown field names; `describe_table` explains each special field type.
