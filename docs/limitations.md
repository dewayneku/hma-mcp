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
| Round trip | save, reopen, re-read every edit, check `.toml` | ✅ moved anchors and free-space start are kept |

Emerald (U) (BPEE0), 2026-11-10, with `tests/smoke_emerald.py`: Treecko's type and a rejected out-of-range speed, Potion price, a trainer team, Route 101 wild data by `where`, Littleroot Town by name and its events, an unchanged NPC script recompiled byte-identical, the town sign grown and moved (pointer updated) then undone, save → reopen. All ✅ with no Emerald-specific fixes needed. Note that species rows 252–276 are the unused `?` slots, so Treecko is row 277.

## Open

### Maps: what's not covered yet
- **Overworld sprites show as numbers** (`graphics: "23"`), because HMA's sprite list has no names. `view_image` on `graphics.overworld.sprites` with that row shows which one it is.
- **Indoor maps share their town's name**, so `PALLET TOWN` matches 5 maps; tools then ask for an id (`3.0` is the town itself).
- **No new maps, map connections or tileset (block) editing yet.** Connections can be changed with `write_table_field` on the list `map_events` gives; adding one, creating a map, or editing what a block looks like isn't covered.
- **Removing events shifts the later ones down.** Warps on other maps that lead here by warp number, and scripts that refer to NPCs by local id, aren't updated. `remove_map_event` warns about this.
- **A new event's script must already exist.** Write it first (e.g. `compile_script` or `hma_edit` into free space) and pass its address as `script`/`arg`.

### Nested data is one more call away
**Tried:** reading Route 1's grass encounter list.
**What happens:** `read_table` shows a nested table as `list : <3C8E60>`; the slots themselves need `read_table table=3C8E60`. Learnsets, trainer teams and text are shown inline.
**Workaround:** as described. Writing works at either level.

### Null pointers can't be filled in by `write_table_field`
**Tried:** giving Route 1 a surf table (`surf` is `null`).
**What happens:** a clear error: there's no data to edit.
**Workaround:** create the data with `hma_edit` (in free space from `find_free_space`) and point the field at it with `@ABCDEF`. This needs HMA's edit syntax for the inner format; there's no helper yet.

### Typos in nested text are stored as something else
**Tried:** a learnset line `(NOTAMOVE 5)`.
**What happens:** HMA parses nested text leniently (unknown names fuzzy-match or become a default), so it stored `(TACKLE 5)`. hma-mcp can't reject it up front, but it now compares what it wrote with what it reads back and returns a `warning` and `changed_lines`.
**Workaround:** check `changed_lines` and `undo` if needed. Names with spaces must be quoted, exactly as `read_table` shows them (`("VINE WHIP" 10)`).

### Inline `<auto>` data after a script
**Tried:** recompiling the Pallet Town NPC script, whose two movements are stored right after it (decompile shows them as `<auto>`).
**What happens:** keeping them as `<auto>` is safe: they're written back in place. Replacing *both* with their real addresses also works. Replacing only one makes HMA place the remaining `<auto>` block on top of it, which corrupts the movement. A brand-new `<auto>` text placed right after a script is not marked as text, so `decompile_script` shows its pointer without the text.
**Workaround:** leave `<auto>` as decompiled. To read or edit such a text, use `read_text`/`write_text` at the address the pointer shows.

### Some script texts aren't shown inline
**Tried:** decompiling the Pallet Town sign (`165838`).
**What happens:** `msgbox.default <17D866>` with no text block, because HMA hadn't recognized that address as text yet. After any compile of that script it is shown.
**Workaround:** `read_text` and `write_text` at that address. They find the pointer by searching the ROM when HMA doesn't know it, so a longer text can still be moved safely.

### Graphics: what's not covered yet
- **Sprite size can't change.** An imported image must match the graphic's size (or all its pages side by side / stacked), as in HMA.
- **Imports may move compressed graphics.** HMA's LZ compressor packs a little less tightly than the original game's, so even a small edit to a compressed sprite usually moves it to free space (pointers are updated; the result says `sprite_moved_from`). Importing an image identical to what's there writes nothing.
- **Tilemaps export as plain color images**, not indexed, since their tiles use several palette pages.

### One ROM at a time
`open_rom` replaces the open ROM (and refuses if there are unsaved changes).

### Updating the installed server while it's running
`dotnet publish -o %LOCALAPPDATA%\hma-mcp` fails with "file is locked" while any Claude session uses the server. Workaround: publish to a staging folder, rename the locked `hma-mcp.exe`, `hma-mcp.dll` and `HexManiac.Core.dll` out of the way (Windows allows renaming loaded files), and copy the new files in. Running sessions keep the old version; new sessions get the new one.

## Fixed

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
