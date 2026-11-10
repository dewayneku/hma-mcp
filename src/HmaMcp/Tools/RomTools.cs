using System.ComponentModel;
using HavenSoft.HexManiac.Core.Models;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace HmaMcp.Tools;

[McpServerToolType]
public static class RomTools {
   [McpServerTool(Name = "open_rom"), Description("Open a GBA ROM with HexManiacAdvance's engine. Loads HMA's .toml metadata next to the ROM if present, otherwise HMA auto-detects tables. Only one ROM is open at a time.")]
   public static string OpenRom(RomSessions sessions,
      [Description("Full path to the .gba file")] string path,
      [Description("Open even if the currently open ROM has unsaved changes (they are lost)")] bool discard_unsaved = false) {
      var session = sessions.Open(path, discard_unsaved);
      return Json.Of(Info(session));
   }

   [McpServerTool(Name = "rom_info", ReadOnly = true), Description("Game code, size, free-space settings, table/anchor counts and save state of the open ROM.")]
   public static string RomInfo(RomSessions sessions) => sessions.With(s => Json.Of(Info(s)));

   static object Info(RomSession s) => new {
      path = s.Path,
      game_code = s.Model.GetGameCode(),
      size = $"0x{s.Model.Count:X}",
      metadata_file = RomSessions.MetadataPath(s.Path),
      metadata_found = File.Exists(RomSessions.MetadataPath(s.Path)),
      anchors = s.Model.Anchors.Count,
      tables = s.Model.Arrays.Count,
      lists = s.Model.ListNames.Count,
      free_space_start = $"0x{s.Model.FreeSpaceStart:X}",
      unsaved_changes = !s.History.IsSaved,
   };

   [McpServerTool(Name = "save", Destructive = true), Description("Write the ROM and its HMA .toml metadata to disk. The first save of a session copies the original ROM to a timestamped backup in a .hma-mcp-backups folder beside it.")]
   public static string Save(RomSessions sessions,
      [Description("Optional new path (save as). Defaults to the opened file.")] string? path = null) => sessions.With(s => {
      var target = string.IsNullOrWhiteSpace(path) ? s.Path : System.IO.Path.GetFullPath(path);
      string? backup = null;
      if (File.Exists(target) && !s.BackupMade) {
         var dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target)!, ".hma-mcp-backups");
         Directory.CreateDirectory(dir);
         backup = System.IO.Path.Combine(dir,
            $"{System.IO.Path.GetFileNameWithoutExtension(target)}.{DateTime.Now:yyyyMMdd-HHmmss}{System.IO.Path.GetExtension(target)}");
         File.Copy(target, backup);
         var oldMeta = RomSessions.MetadataPath(target);
         if (File.Exists(oldMeta)) File.Copy(oldMeta, RomSessions.MetadataPath(backup));
         s.BackupMade = true;
      }

      var metadata = s.Model.ExportMetadata(s.RefTable, s.Singletons.MetadataInfo);
      var tmp = target + ".hma-mcp-tmp";
      File.WriteAllBytes(tmp, s.Model.RawData);
      File.Move(tmp, target, overwrite: true);
      var lines = metadata?.Serialize();
      if (lines != null) File.WriteAllLines(RomSessions.MetadataPath(target), lines);
      s.History.TagAsSaved();
      s.Model.ResetChanges();
      return Json.Of(new { saved = target, metadata = lines != null ? RomSessions.MetadataPath(target) : null, backup });
   });

   [McpServerTool(Name = "undo"), Description("Undo the last edit (each edit tool call is one step).")]
   public static string Undo(RomSessions sessions) => sessions.With(s => {
      if (!s.CanUndo) throw new McpException("Nothing to undo.");
      s.Undo();
      return Json.Of(new { undone = true, can_undo = s.CanUndo, unsaved_changes = !s.History.IsSaved });
   });

   [McpServerTool(Name = "redo"), Description("Redo the last undone edit.")]
   public static string Redo(RomSessions sessions) => sessions.With(s => {
      if (!s.CanRedo) throw new McpException("Nothing to redo.");
      s.Redo();
      return Json.Of(new { redone = true, can_redo = s.CanRedo, unsaved_changes = !s.History.IsSaved });
   });

   [McpServerTool(Name = "close_rom"), Description("Close the open ROM. Fails if there are unsaved changes unless discard_unsaved is true.")]
   public static string CloseRom(RomSessions sessions, bool discard_unsaved = false) {
      var s = sessions.Current;
      if (s == null) return Json.Of(new { closed = false, reason = "no ROM open" });
      if (!s.History.IsSaved && !discard_unsaved) throw new McpException("There are unsaved changes. Call save, or close_rom with discard_unsaved=true.");
      sessions.Close();
      return Json.Of(new { closed = true });
   }
}
