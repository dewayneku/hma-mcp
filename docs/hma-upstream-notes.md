# HMA upstream notes

Issues in [HexManiacAdvance](https://github.com/haven1433/HexManiacAdvance) found while building hma-mcp, with fixes. Entries are against upstream `master` at `c8de1e8a` (2026-08-30), which is also the pinned submodule commit.

Status: **open** = not yet worked on · **on fork** = fix branch pushed to [dewayneku/HexManiacAdvance](https://github.com/dewayneku/HexManiacAdvance), no PR opened · **reported** = issue/PR filed (link it) · **merged** = in upstream.

Each fix is on its own branch off `master` in the fork, so it can become a small, separate PR. `local/all-fixes` merges fixes #1–#4, for testing together (#7 is not in it yet). In `external/HexManiacAdvance`, the fork is the `fork` remote (local git config only; run `git remote add fork https://github.com/dewayneku/HexManiacAdvance.git` in a fresh clone).

## How fixes are verified

- HMA's own test suite, with a FireRed (U) 1.0 ROM at `artifacts/HexManiac.Tests/bin/Debug/net6.0/sampleFiles/Pokemon FireRed.gba` (git-ignored). Baseline on upstream `master`: **1633 passed, 16 failed, 921 skipped**. Every fix branch and the combined branch give the same 16 failures and no new ones.
- A targeted check for each fix (below), run both with and without the fix.
- hma-mcp's own `tests/smoke.py`, built against `local/all-fixes`: all checks pass.

---

### 1. Doesn't compile on .NET 8+, and .NET 6 is end-of-life: on fork
**Branch:** [`fix/net8-ascii-ambiguity`](https://github.com/dewayneku/HexManiacAdvance/tree/fix/net8-ascii-ambiguity) (`a8682bb9`). 4 files, +4 lines.
**Problem:** .NET 8 added `System.Text.Ascii`, so `DataFormats.Ascii` is ambiguous (CS0104) in files that also import `System.Text`. HMA targets `net6.0`, which went out of support in November 2024.
**Fix:** `using Ascii = HavenSoft.HexManiac.Core.ViewModels.DataFormats.Ascii;` in `AsciiRun.cs`, `ViewPort.cs`, `ContextItemFactory.cs` and `ToolTipContentVisitor.cs`. No behavior change.
**Verified:** HexManiac.Core now compiles for `net8.0`; it failed before. The actual retarget to net8 is left to the maintainers: it touches WPF, the installer and CI.

### 2. Projects can't be built on their own or referenced from another solution: on fork
**Branch:** [`fix/build-without-solutiondir`](https://github.com/dewayneku/HexManiacAdvance/tree/fix/build-without-solutiondir) (`a4bfe151`). 5 project files.
**Problem:** Paths were built from `$(SolutionDir)`, which is empty or wrong outside HMA's solution, so restore and build failed. Worse, `dotnet build src/HexManiac.Tests` on master writes its output **inside the project folder** (`src/HexManiac.Tests/artifacts/`). The next full solution build then compiles those generated files and fails with `CS0579 Duplicate TargetFrameworkAttribute` until they're deleted by hand. This happened during this work.
**Fix:** Define `HmaRepoRoot` once in `src/Directory.Build.props` from that file's own location (`$([MSBuild]::NormalizeDirectory(...))`) and use it instead of `$(SolutionDir)`. The output layout is unchanged.
**Verified:** the solution builds; `dotnet build src/HexManiac.Core` builds on its own; and a separate solution referencing `HexManiac.Core.csproj` builds and runs. On master, the last two fail.

### 3. Starting HMA from another folder crashes it: on fork
**Branch:** [`fix/resources-relative-to-app`](https://github.com/dewayneku/HexManiacAdvance/tree/fix/resources-relative-to-app) (`ab690341`). New `Models/ResourcePath.cs` plus 6 files.
**Problem:** Reference data (`resources/*.txt`, `*.toml`, `hma.py`, …) was opened relative to the process's current directory. If HMA starts from any other folder (a shortcut with a different "Start in", a command line, another program hosting HexManiac.Core), `new Singletons()` throws `DirectoryNotFoundException: ...\resources\hma.py`.
**Fix:** `ResourcePath.Get("resources/...")` resolves against `AppContext.BaseDirectory`, and falls back to the old relative path when the file isn't there.
**Verified:** a test program started from `C:\Users\dewayne` crashes on master. With the fix, it loads all 10 game reference tables and 323 script commands.

### 4. Invalid enum names silently do nothing: on fork
**Branch:** [`fix/set-enum-value-invalid-name`](https://github.com/dewayneku/HexManiacAdvance/tree/fix/set-enum-value-invalid-name) (`32f05132`). `ModelTable.cs` plus 2 new tests in `TableTests.cs`.
**Problem:** `ModelArrayElement.SetEnumValue` skipped the write when the name didn't match. It backs `element["type1"] = "FIRE"` in HMA's Python automation, so a typo skips the edit with no message.
**Fix:** Throw an `ArgumentException` that names the field and suggests close matches.
**Verified:** the new test `EnumField_SetInvalidName_ThrowsAndLeavesDataUnchanged` fails on master and passes with the fix; `EnumField_SetValidName_WritesValue` passes on both.

### 5. A script edit that has to move the script takes two undos: reproduced, open
**Branch:** [`repro/script-relocate-undo`](https://github.com/dewayneku/HexManiacAdvance/tree/repro/script-relocate-undo) (`7efe275b`). One test, which **currently fails**.
**Where:** `ViewModels/Tools/CodeTool.cs` (`CompileScriptChanges` inserts a separate format-only history entry with `history.InsertCustomChange`), `ViewModels/ChangeHistory.cs`.
**Problem:** Typing a longer script in the code tool moves it to free space and updates its pointers. The first Ctrl+Z reverts only the content: the *original* script stays at the new address, with the pointers still aimed there. A second Ctrl+Z puts it back where it was. The ROM is valid after the first undo (not corrupt), but the result is surprising, and a user who saves then keeps the moved copy.
**Fix idea:** Make the repoint and the compile one history entry: let `InsertCustomChange` join an open `ContinueCurrentTransaction()` (today it throws), or add a public `BeginGroup()`/`EndGroup()`. This changes undo behavior, so it's better raised as an issue with the repro test than sent straight as a PR.
**hma-mcp workaround:** each tool call is one undo group (reads the private `undoStack` through reflection).

### 6. Script compile and repoint logic lives in the editor view-model: open
**Where:** `ViewModels/Tools/CodeTool.cs` (`CompileScriptChanges`)
**Problem:** Compile → expand → relocate → write → reformat → keep orphans is the most useful logic for automation, but it's tied to `CodeBody`, the selection, and the editor caret. Python scripts and other tools can't call it; hma-mcp keeps its own copy in `ScriptTools.Compile`.
**Fix:** Move it into a model-level service, e.g. `ScriptParser.CompileAndWrite(model, token, address, text)`, that `CodeTool` calls. This would also be the natural place to fix #5.
**Size:** medium refactor. High value.

### 7. Script text with LF line endings grows every text/movement block: on fork
**Branch:** [`fix/script-lf-line-endings`](https://github.com/dewayneku/HexManiacAdvance/tree/fix/script-lf-line-endings) (`88c2e0c4`). `ScriptParser.cs` (2 lines) plus 2 new tests in `CodeToolTests.cs`.
**Where:** `Models/Code/ScriptParser.cs`, `InsertMissingClosers`, the checks `text[i + 1] != '\r'` and `text[close + 1] != '\r'` (around lines 464–476).
**Problem:** After a `{`, the code adds `\r\n` unless the next character is `\r`. With LF-only text the next character is `\n`, so it inserts a second line break. The first line of every `{ }` block becomes empty, so each text gets a leading newline (`FE`) and each movement an extra byte. Every block outgrows its slot, is moved to free space, and the original is erased. Recompiling an *unchanged* script moves all of its text. The WPF editor always sends CRLF, so GUI users don't see it, but Python scripts, other hosts and Linux/macOS do.
**Fix:** treat `\n` like `\r` in those checks (or normalize to CRLF at the top of `Compile`). About 2 lines.
**Verified:** the new tests `LfLineEndings_InsertMissingClosers_AddsNoBlankLines` and `LfLineEndings_CompileText_TextHasNoLeadingNewline` fail on master and pass with the fix. Full suite with the ROM: 1635 passed (the baseline 1633 plus the 2 new), and the same 16 failed. `HexManiac.Integration` has 1 failure (`UseTypeSwapScript_UpgradeVersion_TableStillIncluded`), which also fails on master.
**hma-mcp workaround:** `compile_script` normalizes line endings to CRLF before compiling.

### 8. `ModelArrayElement.SetStringValue` only handles text pointers: open
**Where:** `Models/ModelTable.cs`, `SetStringValue`, `var pcsRun = (PCSRun)model.GetNextRun(valueAddress);`
**Problem:** For a pointer to a learnset, trainer team or nested table, this throws `InvalidCastException`. That's the API Python automation uses (`element["movesFromLevel"] = ...`).
**Fix:** for an `IStreamRun` or `ITableRun` destination, do what `TextStreamElementViewModel.Content` does: `DeserializeRun`, `ObserveRunWritten`, then `NotifyChildren` for changed offsets. Small.
**hma-mcp workaround:** `write_table_field` does those steps itself.

### 9. Nested-text parsing silently replaces unknown names: open (related to the tuple note below)
**Where:** table-stream deserialization of tuple/enum cells, e.g. learnset lines `[pair:|t|move::::.data.pokemon.moves.names|level:::.]`.
**Problem:** `(NOTAMOVE 5)` is stored as `(TACKLE 5)` with no error. For a learnset or trainer team that's easy to miss.
**Fix idea:** report unparseable cells through the existing error info, as script compile does.
**hma-mcp workaround:** compares what was written with what reads back and returns `changed_lines`.

### 10. Inline `<auto>` streams after a script: open
**Where:** `ScriptParser.FormatScript` (skips pointer destinations that fall inside an existing run: "we're trying to point into already formatted data") and the deferred `<auto>` placement in `Compile`.
**Problem:** (a) A new `<auto>` text placed right after the script is never formatted as text, so decompiling shows the pointer without the text. (b) `<auto>` blocks are placed at the end of the new code even when an explicit pointer in the same script targets that spot, so one overwrites the other.
**Fix idea:** format deferred streams after writing them; place `<auto>` blocks after the furthest byte the script already uses.

### 11. `ModelArrayElement` setters truncate silently: open
**Where:** `ModelTable.cs`, `SetValue` (`WriteMultiByteValue` keeps the low bytes: 300 → 44) and `SetStringValue` for fixed-length text (cuts the text to fit).
**Problem:** same family as #4: a script that sets `speed = 300` or a too-long name gets something else with no message.
**Fix idea:** throw `ArgumentOutOfRangeException` / `ArgumentException`, like the #4 fix. Small, but it changes behavior, so it's better as an issue first.
**hma-mcp workaround:** range and length checks in `write_table_field`.

### 12. Text parsing drops some line breaks and unknown characters without an error: open
**Where:** `Models/PCSString.cs`, `Convert(string input, ...)`: the check `if (index < input.Length - doubleNewline.Length)` before the newline handling, and the character loop.
**Problem:** (a) Only `Environment.NewLine` counts as a line break, so LF-only text on Windows loses every line break. (b) A line break that starts in the last 4 characters (`doubleNewline.Length`) is skipped: `"A\r\nB"` becomes `AB`. (c) Characters with no PCS byte are dropped; `containsBadCharacters` is set, but `PCSRun.DeserializeRun` writes the text anyway. All three silently change what's written.
**Fix idea:** accept both `\n` and `\r\n`; check the end of the input with `StartsWith` instead of the length guard; have callers report bad characters. Small.
**hma-mcp workaround:** `write_text` and `write_table_field` turn line breaks into explicit `\n`/`\l`/`\pn` codes before calling HMA (the same rules HMA uses), and reject characters the font doesn't have.

### 13. Image import logic is private to the sprite tool view-model, which doesn't follow a moved sprite: open
**Where:** `ViewModels/Tools/SpriteTool.cs`: `ImportSpriteAndPalette(IFileSystem, short[], int, ImportType)` and the `int[,]` overload are private; the public `TryImport` always picks the palette itself and only takes an unindexed image.
**Problem:** (a) A headless caller can't import with a chosen palette, or keep an indexed PNG's exact indices, without reflection. (b) After a `Cautious` import that moved the compressed sprite, `SpriteAddress` still held the old address; reading the sprite there found the next run (a palette) instead.
**Fix idea:** a public import method taking the image, an optional palette address and the import type; have every write path update `SpriteAddress`/`PaletteAddress` when the run moves (as the Greedy path already does). Small to medium. Related to #6.
**hma-mcp workaround:** `import_image` calls the private overloads by reflection, and finds the sprite and palette again afterwards through a pointer to each.

---

## Not yet investigated

- **16 tests fail on upstream master**, mostly trainer-team and table-stream tests (`TableTests.ChangePokemonCount…`, `TrainerTeamTests.*`, with NullReferenceException in `TableTool.DataForCurrentRunChanged`). They fail with or without a ROM. This machine has only the .NET 8 runtime, so the tests ran on net8 through roll-forward. They might pass on a real .NET 6 runtime; check that before reporting. The upstream `failing-tests` branch may be related.
- **Tuple enum fields fuzzy-match silently** (`ModelTupleElement.TryConvertEnumTextToValue` takes the best fuzzy match). This is the same kind of problem as #4, but it may be intentional. #9 is what it looks like in practice.
