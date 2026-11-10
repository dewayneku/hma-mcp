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
| Round trip | save, reopen, re-read every edit, check `.toml` | ✅ moved anchors and free-space start are kept |

## Open

### Maps can only be reached by bank/map number and pointer chasing
**Tried:** "edit the NPC next to the sign in Pallet Town" and "change Route 1's wild Pokémon".
**What happens:** there are no anchors per map or per script. You go `data.maps.banks` row 3 → map 0 (`<350618>`) → `events` → `objects` (`<3B4D84>`) → the object's `script`, one `read_table` per hop. Wild data is keyed by bank/map numbers, not names, and you have to know that Route 1 is bank 3, map 19.
**Workaround:** `read_table` with `where`, e.g. `table=data.pokemon.wild, where="bank=3, map=19"`, then `read_table` at the address a pointer field shows. A `find_map`/`map_events` tool (name → bank/map → objects, warps, signs, scripts) is the most useful next tool.

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
**Workaround:** leave `<auto>` as decompiled. To read or edit such a text, decompile the script again after a save/reopen, or use `read_bytes`.

### Some script texts aren't shown inline
**Tried:** decompiling the Pallet Town sign (`165838`).
**What happens:** `msgbox.default <17D866>` with no text block, because HMA hadn't recognized that address as text yet. After any compile of that script it is shown.
**Workaround:** recompile the script unchanged (byte-identical), then decompile again. There's no standalone tool to read text at an address yet.

### Graphics are not supported
No tools yet for viewing or importing sprites, palettes or tilesets.

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
- `read_table` gained `where` (e.g. `bank=3, map=19`, `name=BROCK`) and rejects unknown field names; `describe_table` explains each special field type.
