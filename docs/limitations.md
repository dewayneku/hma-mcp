# Known limitations

What hma-mcp can't do yet, or does badly, found by using it on real hacks. Each entry: what you tried, what happened, and a workaround if there is one. Entries move to "Fixed" when resolved.

Tested so far: FireRed (U) 1.0 (BPRE0).

## Open

### Tuple and bit-field table fields can't be set with `write_table_field`
**Example:** fields shown as `tuple` or `bits` by `describe_table` (e.g. packed level-up moves, TM compatibility bits).
**What happens:** the tool returns an error telling you to use `hma_edit`.
**Workaround:** `hma_edit` at the row's address, using HMA's text syntax for that field.

### Graphics are not supported
No tools yet for viewing or importing sprites, palettes or tilesets.

### One ROM at a time
`open_rom` replaces the open ROM (and refuses if there are unsaved changes).

## Fixed

_(none yet)_
