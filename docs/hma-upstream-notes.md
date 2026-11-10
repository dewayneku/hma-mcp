# HMA upstream notes

Issues in [HexManiacAdvance](https://github.com/haven1433/HexManiacAdvance) found while building hma-mcp, with suggested fixes. Each could become an issue or PR upstream. Entries are against the pinned submodule commit (`c8de1e8a`, 2026-08-30).

Status: **open** = not yet reported · **reported** = issue/PR filed (link it) · **fixed** = merged upstream.

---

### 1. `HexManiac.Core` can't be referenced from outside HMA's own solution (open)
**Where:** `src/HexManiac.Core/HexManiac.Core.csproj`, `src/Directory.Build.props`
**Problem:** Paths are built from `$(SolutionDir)`, including `SharedAssemblyInfo.cs`, the output folders, and the `AutoImplement` tool path. Referenced from any other solution, or built on its own, `$(SolutionDir)` is wrong or empty, and restore and build fail. Tools, plugins, and test harnesses can't build on HMA's core. hma-mcp works around this with its own wrapper project, `src/HexManiac.Core.Build`.
**Fix:** Use `$(MSBuildThisFileDirectory)`-relative paths. Optionally also publish `HexManiac.Core` as a NuGet package.
**Size:** small, low risk.

### 2. Doesn't compile on .NET 8+, and .NET 6 is end-of-life (open)
**Where:** `ViewModels/Visitors/ContextItemFactory.cs:219`, `ViewModels/Visitors/ToolTipContentVisitor.cs:95`
**Problem:** .NET 8 added `System.Text.Ascii`, which makes HMA's `DataFormats.Ascii` ambiguous (CS0104). HMA still targets `net6.0`, which went out of support in November 2024, so users run an unpatched runtime.
**Fix:** Qualify the name or add an alias (`using Ascii = HavenSoft.HexManiac.Core.ViewModels.DataFormats.Ascii;`) in those two files, then retarget to `net8.0`.
**Size:** small. The retarget needs a full test pass.

### 3. Reference data is loaded relative to the working directory (open)
**Where:** `Models/Singletons.cs:22-31`, `Models/PCSString.cs:65`, `Models/IDataModel.cs:205-211`
**Problem:** Paths like `"resources/tableReference.txt"` resolve against the process's current directory, not the app's folder. If HMA starts from a different working directory (a shortcut with a different "Start in", a command line, another program hosting the core), the reference tables, scripts, and constants fail to load. hma-mcp works around this by changing directory at startup.
**Fix:** Build the paths from `AppContext.BaseDirectory`.
**Size:** small.

### 4. Setting an enum field to an invalid name silently does nothing (open)
**Where:** `Models/ModelTable.cs` (`ModelArrayElement.SetEnumValue`)
**Problem:** `SetEnumValue("type1", "NOTATYPE")` doesn't change anything and doesn't report an error. HMA's own Python automation uses this API (`element["type1"] = "FIER"`), so a typo in a script silently skips the edit.
**Fix:** Throw an exception (or return `false`) that names the closest valid options. hma-mcp now validates with `ArrayRunEnumSegment.TryParse` first.
**Size:** small. Check any callers that rely on the silent behavior.

### 5. One script edit can take several undo steps (open, verify in GUI)
**Where:** `ViewModels/Tools/CodeTool.cs` (`CompileScriptChanges` calls `history.InsertCustomChange`), `ViewModels/ChangeHistory.cs`
**Problem:** Compiling a script that grows records at least two history entries: a format-only change and the data change. In hma-mcp, one undo restored only part of a relocated script, which left the old location filled with `FF` bytes. The GUI likely needs several Ctrl+Z presses for one script edit. Also, `InsertCustomChange` throws inside `ContinueCurrentTransaction()`, so callers can't group the steps themselves, and there's no public undo-depth or grouping API. hma-mcp reads the private `undoStack` through reflection.
**Fix:** Let `InsertCustomChange` join the current transaction, or add a public `BeginGroup()`/`EndGroup()` API.
**Size:** medium. Reproduce in the GUI first.

### 6. Script compile and repoint logic lives in the editor view-model (open)
**Where:** `ViewModels/Tools/CodeTool.cs` (`CompileScriptChanges`)
**Problem:** Compile → expand → relocate → write → reformat → keep orphans is the most valuable logic for automation, but it's tied to `CodeBody`, the selection, and the editor caret. Python scripts and other tools can't call it, so hma-mcp keeps its own copy in `ScriptTools.Compile`, which can drift out of date.
**Fix:** Move it into a model-level service, such as `ScriptParser.CompileAndWrite(model, token, address, text)`, that `CodeTool` calls. HMA's Python tool would gain safe script editing too.
**Size:** medium refactor. High value.
