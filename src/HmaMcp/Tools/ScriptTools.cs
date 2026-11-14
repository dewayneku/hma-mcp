using System.ComponentModel;
using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.Models.Runs;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace HmaMcp.Tools;

[McpServerToolType]
public static class ScriptTools {
   const string KindHelp = "Script kind: event (XSE overworld scripts, the default), battle, animation, or trainer_ai";

   [McpServerTool(Name = "decompile_script", ReadOnly = true), Description("Decompile a script into HMA's script language, including the sub-scripts, text and movement it points to.")]
   public static string DecompileScript(RomSessions sessions,
      [Description("Anchor or hex address of the script")] string address,
      [Description(KindHelp)] string kind = "event") => sessions.With(s => {
      var start = s.ResolveAddress(address);
      var parser = Parser(s, kind);
      // HMA shows a text inline only once it has marked that address as text. Its editor formats a script when it
      // opens it; do the same (format only, no data change, not an undo step) when a script is known to start here, or something points here.
      if (kind.Equals("event", StringComparison.OrdinalIgnoreCase) && s.Model.GetNextRun(start) is { } known && known.Start == start && (known is XSERun || (known is NoInfoRun && known.PointerSources?.Count > 0))) {
         try {
            using (ModelCacheScope.CreateScope(s.Model)) parser.FormatScript<XSERun>(new NoDataChangeDeltaModel(), s.Model, start);
         } catch (Exception) {
            // formatting is only a display aid
         }
      }
      using (ModelCacheScope.CreateScope(s.Model)) {
         var length = parser.FindLength(s.Model, start);
         int sections = 0;
         var text = parser.Parse(s.Model, start, length, ref sections);
         return Json.Of(new { address = start.ToAddress(), anchor = s.Anchor(start), kind, compiled_length = length, script = text });
      }
   });

   [McpServerTool(Name = "compile_script", Destructive = true), Description("Compile HMA script text and write it at an address, replacing the script there. If the new script is longer, HMA's engine moves it to free space and updates every pointer to it, like the HMA code editor does. Use '<auto>' for new text/movement/sub-script pointers to let HMA place them. One undo step.")]
   public static string CompileScript(RomSessions sessions,
      [Description("Anchor or hex address of the script to replace")] string address,
      [Description("Full script text, in the form decompile_script returns")] string script,
      [Description(KindHelp)] string kind = "event") => sessions.Edit(s => {
      var start = s.ResolveAddress(address);
      var parser = Parser(s, kind);
      // HMA's parser (InsertMissingClosers) assumes CRLF: with LF-only text it adds a blank line after
      // every '{', so each text/movement block gains a leading newline and gets moved. Normalize first.
      script = script.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
      var errors = new List<string>();
      void OnError(object? sender, ScriptErrorInfo e) => errors.Add(e.Message);
      using (ModelCacheScope.CreateScope(s.Model)) {
         return kind.ToLowerInvariant() switch {
            "battle" => Compile<BSERun>(s, parser, start, script, errors, OnError),
            "animation" => Compile<ASERun>(s, parser, start, script, errors, OnError),
            "trainer_ai" or "ai" => Compile<TSERun>(s, parser, start, script, errors, OnError),
            _ => Compile<XSERun>(s, parser, start, script, errors, OnError),
         };
      }
   });

   /// <summary>Compile a new event script into free space and return its address (one undo step is the caller's).</summary>
   internal static int CompileNew(RomSession s, string script) {
      script = script.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
      var start = s.Model.FindFreeSpace(s.Model.FreeSpaceStart, 0x40);
      if (start < 0) throw new McpException("No free space for the new script.");
      var parser = Parser(s, "event");
      var errors = new List<string>();
      void OnError(object? sender, ScriptErrorInfo e) => errors.Add(e.Message);
      using (ModelCacheScope.CreateScope(s.Model)) Compile<XSERun>(s, parser, start, script, errors, OnError);
      return start;
   }

   /// <summary>Mirrors HexManiac.Core CodeTool.CompileScriptChanges, minus the editor/selection bookkeeping.</summary>
   static string Compile<TRun>(RomSession s, ScriptParser parser, int start, string script, List<string> errors, EventHandler<ScriptErrorInfo> onError)
      where TRun : IScriptStartRun {
      var model = s.Model;
      var originalStart = start;
      var run = model.GetNextRun(start);
      if (run != null && run.Start != start) run = null;
      var sources = run?.PointerSources;
      var length = parser.FindLength(model, start);
      var oldScripts = parser.CollectScripts(model, start);

      parser.CompileError += onError;
      byte[]? code;
      try {
         code = parser.Compile(s.Token, model, start, ref script, out _, out _);
      } finally {
         parser.CompileError -= onError;
      }
      if (code == null || errors.Count > 0)
         throw new McpException("Script did not compile:\n" + string.Join("\n", errors.DefaultIfEmpty("unknown error")));

      if (code.Length > length) {
         model.ExpandData(s.Token, start + code.Length - 1);
         if (run == null) {
            // no run to relocate: only allowed if the bytes after the script are free space
            for (int i = start + length; i < start + code.Length; i++) {
               if (model[i] != 0xFF) throw new McpException($"Script is {code.Length} bytes, but only {i - start} bytes are available at {start.ToAddress()} and there is no anchor/pointer to repoint.");
            }
         } else {
            if (run is NoInfoRun) run = Construct<TRun>(run.Start, run.PointerSources);
            run = model.RelocateForExpansion(s.Token, run, length, code.Length);
            if (run.Start != start) {
               start = run.Start;
               parser.CompileError += onError;
               try {
                  code = parser.Compile(s.Token, model, start, ref script, out _, out _);
               } finally {
                  parser.CompileError -= onError;
               }
               if (code == null || errors.Count > 0)
                  throw new McpException("Script did not compile after repointing:\n" + string.Join("\n", errors));
               sources = run.PointerSources;
            }
         }
      }

      // clear stale formats over the changed region (keep the anchor at byte 0)
      var changeStart = code.Length;
      for (int i = 0; i < code.Length; i++) {
         if (model[start + i] == code[i]) continue;
         changeStart = Math.Max(1, i);
         break;
      }
      if (changeStart < code.Length) {
         var noData = s.History.InsertCustomChange(new NoDataChangeDeltaModel());
         model.ClearFormat(noData, start + changeStart, code.Length - changeStart);
      }

      s.Token.ChangeData(model, start, code);
      if (originalStart == start && length > code.Length) {
         // The old length can include data stored right after the script (e.g. inline movement). If the new
         // script still points into that tail, clear only up to the first byte it points at.
         var tailStart = start + code.Length;
         var tailEnd = start + length;
         for (int i = 0; i + 4 <= code.Length; i++) {
            var target = code[i] | code[i + 1] << 8 | code[i + 2] << 16 | (code[i + 3] - 0x08) << 24;
            if (code[i + 3] is 0x08 or 0x09 && target >= tailStart && target < tailEnd) tailEnd = target;
         }
         if (tailEnd > tailStart) model.ClearFormatAndData(s.Token, tailStart, tailEnd - tailStart);
      }
      var formatted = parser.FormatScript<TRun>(s.Token, model, start, code.Length);
      if (sources != null) {
         foreach (var source in sources) {
            if (formatted.Any(kvp => source.InRange(kvp.Key, kvp.Key + kvp.Value))) continue;
            var existing = model.GetNextRun(source);
            if (existing.Start > source || existing is not ITableRun) model.ObserveRunWritten(s.Token, new PointerRun(source));
         }
      }

      // keep scripts this edit orphaned, so they are not lost
      var orphans = oldScripts.Except(parser.CollectScripts(model, start)).ToList();
      var keptOrphans = new List<string>();
      foreach (var orphan in orphans) {
         var orphanRun = model.GetNextRun(orphan);
         if (orphanRun.Start == orphan && orphanRun.PointerSources.IsNullOrEmpty() && string.IsNullOrEmpty(model.GetAnchorFromAddress(-1, orphan))) {
            parser.FormatScript<TRun>(s.Token, model, orphan);
            var prefix = typeof(TRun) == typeof(XSERun) ? "xse" : typeof(TRun) == typeof(BSERun) ? "bse" : typeof(TRun) == typeof(ASERun) ? "ase" : "tse";
            var name = $"orphans.{prefix}{orphan:X6}";
            model.ObserveAnchorWritten(s.Token, name, Construct<TRun>(orphan, SortedSpan<int>.None));
            keptOrphans.Add(name);
         }
      }

      return Json.Of(new {
         address = start.ToAddress(),
         moved_from = start != originalStart ? originalStart.ToAddress() : null,
         old_length = length,
         new_length = code.Length,
         pointers_updated = start != originalStart ? sources?.Count ?? 0 : 0,
         orphaned_scripts_kept = keptOrphans.Count > 0 ? keptOrphans : null,
      });
   }

   static TRun Construct<TRun>(int start, SortedSpan<int> sources) where TRun : IScriptStartRun {
      IScriptStartRun run =
         typeof(TRun) == typeof(XSERun) ? new XSERun(start, sources) :
         typeof(TRun) == typeof(BSERun) ? new BSERun(start, sources) :
         typeof(TRun) == typeof(ASERun) ? new ASERun(start, sources) :
         new TSERun(start, sources);
      return (TRun)run;
   }

   static ScriptParser Parser(RomSession s, string kind) {
      var tool = s.ViewPort.Tools.CodeTool;
      return kind.ToLowerInvariant() switch {
         "event" or "xse" or "" => tool.ScriptParser,
         "battle" or "bse" => tool.BattleScriptParser,
         "animation" or "ase" => tool.AnimationScriptParser,
         "trainer_ai" or "ai" or "tse" => tool.BattleAIScriptParser,
         _ => throw new McpException($"Unknown script kind '{kind}'. Use event, battle, animation or trainer_ai."),
      };
   }
}
