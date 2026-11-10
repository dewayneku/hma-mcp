# HMA upstream notes

Issues in [HexManiacAdvance](https://github.com/haven1433/HexManiacAdvance) found while building hma-mcp, with fixes. Entries are against upstream `master` at `c8de1e8a` (2026-08-30), which is also the pinned submodule commit.

Status: **open** = not yet worked on · **on fork** = fix branch pushed to [dewayneku/HexManiacAdvance](https://github.com/dewayneku/HexManiacAdvance), no PR opened · **reported** = issue/PR filed (link it) · **merged** = in upstream.

Each fix is on its own branch off `master` in the fork, so it can become a small, separate PR. `local/all-fixes` merges all four, for testing together. In `external/HexManiacAdvance`, the fork is the `fork` remote (local git config only; run `git remote add fork https://github.com/dewayneku/HexManiacAdvance.git` in a fresh clone).

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

---

## Not yet investigated

- **16 tests fail on upstream master**, mostly trainer-team and table-stream tests (`TableTests.ChangePokemonCount…`, `TrainerTeamTests.*`, with NullReferenceException in `TableTool.DataForCurrentRunChanged`). They fail with or without a ROM. This machine has only the .NET 8 runtime, so the tests ran on net8 through roll-forward. They might pass on a real .NET 6 runtime; check that before reporting. The upstream `failing-tests` branch may be related.
- **Tuple enum fields fuzzy-match silently** (`ModelTupleElement.TryConvertEnumTextToValue` takes the best fuzzy match). This is the same kind of problem as #4, but it may be intentional.
