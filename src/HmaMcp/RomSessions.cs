using System.Globalization;
using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels;
using ModelContextProtocol;

namespace HmaMcp;

/// <summary>
/// One ROM opened headlessly through HexManiacAdvance's core library.
/// A ViewPort is used (without any UI) because it owns the undo history and the script/code tools.
/// </summary>
public sealed class RomSession {
   public string Path { get; }
   public IDataModel Model { get; }
   public ViewPort ViewPort { get; }
   public Singletons Singletons { get; }
   public ChangeHistory<ModelDelta> History => ViewPort.ChangeHistory;
   public bool BackupMade { get; set; }

   public RomSession(string path, IDataModel model, ViewPort viewPort, Singletons singletons) =>
      (Path, Model, ViewPort, Singletons) = (path, model, viewPort, singletons);

   public ModelDelta Token => History.CurrentChange;

   public GameReferenceTables? RefTable =>
      Singletons.GameReferenceTables.TryGetValue(Model.GetGameCode(), out var table) ? table : null;

   /// <summary>Accepts an anchor name, "0x08ABCDEF", "08ABCDEF", "0xABCDEF", "ABCDEF" or "&lt;ABCDEF&gt;".</summary>
   public int ResolveAddress(string text) {
      if (string.IsNullOrWhiteSpace(text)) throw new McpException("An address or anchor name is required.");
      text = text.Trim().Trim('<', '>', '@');

      if (!LooksLikeHex(text)) {
         var anchorAddress = Model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, text);
         if (anchorAddress >= 0 && anchorAddress < Model.Count) return anchorAddress;
         throw new McpException($"'{text}' is not a known anchor or a hex address. Use list_anchors to search anchor names.");
      }

      var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
      var value = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
      if (value >= 0x08000000 && value < 0x0A000000) value -= 0x08000000;
      if (value < 0 || value >= Model.Count) throw new McpException($"Address {text} is outside the ROM (size 0x{Model.Count:X}).");
      return value;
   }

   static bool LooksLikeHex(string text) {
      var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
      return hex.Length is > 0 and <= 8 && hex.All(Uri.IsHexDigit);
   }

   public string Anchor(int address) {
      var name = Model.GetAnchorFromAddress(-1, address);
      return string.IsNullOrEmpty(name) ? "" : name;
   }

   // HMA's history can record one logical edit as several steps (e.g. a script compile inserts a
   // separate format-only step). Each tool call is one "edit group" so undo/redo act per tool call.
   readonly Stack<int> undoGroups = new(), redoGroups = new();
   static readonly System.Reflection.FieldInfo UndoStackField =
      typeof(ChangeHistory<ModelDelta>).GetField("undoStack", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
      ?? throw new InvalidOperationException("HMA's ChangeHistory no longer has an undoStack field; update RomSession.Edit.");
   int UndoDepth => ((System.Collections.ICollection)UndoStackField.GetValue(History)!).Count;

   /// <summary>Run an edit and record everything it changed as one undo step.
   /// If the edit throws, whatever it already changed is undone, so a failed tool call leaves the ROM as it was.</summary>
   public T Edit<T>(Func<T> body) {
      History.ChangeCompleted();
      var depth = UndoDepth;
      try {
         var result = body();
         History.ChangeCompleted();
         var steps = UndoDepth - depth;
         if (steps > 0) { undoGroups.Push(steps); redoGroups.Clear(); }
         return result;
      } catch {
         History.ChangeCompleted();
         for (int steps = UndoDepth - depth; steps > 0; steps--) History.Undo.Execute(null);
         throw;
      }
   }

   public bool CanUndo => undoGroups.Count > 0;
   public bool CanRedo => redoGroups.Count > 0;

   public void Undo() {
      var steps = undoGroups.Pop();
      for (int i = 0; i < steps; i++) History.Undo.Execute(null);
      redoGroups.Push(steps);
   }

   public void Redo() {
      var steps = redoGroups.Pop();
      for (int i = 0; i < steps; i++) History.Redo.Execute(null);
      undoGroups.Push(steps);
   }
}

public sealed class RomSessions {
   public const string Instructions = """
      hma-mcp edits GBA Pokemon ROMs (FireRed, LeafGreen, Emerald, Ruby, Sapphire and hacks of them)
      through HexManiacAdvance's core library, the same engine the HMA editor uses.

      Workflow: open_rom -> explore (rom_info, list_anchors, describe_table, read_table, decompile_script,
      read_text, find_map, map_events) -> edit (write_table_field, compile_script, write_text, write_bytes,
      hma_edit) -> save. For anything on a map (NPCs, signs, warps, its scripts), start with find_map.
      Graphics: view_image shows a sprite/tileset/tilemap/palette as an image you can see (graphics tables
      such as graphics.pokemon.sprites.front take a row, e.g. CHARMANDER); render_map draws a map. To change
      art, save it with view_image's save_to, edit the PNG, and import_image it back; read_palette and
      write_palette edit colors. Show the user the image before and after a graphics change.
      Map editing: add_map_event / remove_map_event for NPCs, warps, triggers and signs (move or change
      one with write_table_field on the list map_events gives); read_map_blocks + view_blockset to find
      block ids, paint_map_blocks to place them; resize_map. New maps: create_map (modeled on an existing
      map), then connect_maps / disconnect_maps, warps, add_fly_spot and paint_town_map. NPC looks:
      view_overworld_sprites. Render the map before and after.
      Nothing touches the file on disk until save is called; save makes a timestamped backup of the
      original ROM the first time and writes HMA's .toml metadata next to the ROM, so the HMA GUI
      sees the same anchors. Every edit tool is one undo step (undo / redo).

      Addresses are ROM offsets in hex (ABCDEF, 0xABCDEF or 0x08ABCDEF all work) or HMA anchor names
      such as data.pokemon.stats or scripts.newgame.setflags. Prefer anchors: they survive repoints.
      Before editing, read the current values first and tell the user what will change.
      """;

   readonly object gate = new();
   RomSession? current;
   Singletons? singletons;

   public T With<T>(Func<RomSession, T> action) {
      lock (gate) {
         if (current == null) throw new McpException("No ROM is open. Call open_rom with the path to a .gba file first.");
         try {
            return action(current);
         } catch (Exception ex) when (ex is not McpException) {
            // The MCP SDK hides the message of anything that isn't an McpException ("An error occurred invoking ...").
            throw new McpException($"HMA failed: {ex.GetType().Name}: {ex.Message}", ex);
         }
      }
   }

   /// <summary>Like With, but everything the action changes becomes a single undo step.</summary>
   public T Edit<T>(Func<RomSession, T> action) => With(s => s.Edit(() => action(s)));

   public RomSession? Current { get { lock (gate) return current; } }

   public RomSession Open(string path, bool discardUnsaved) {
      lock (gate) {
         if (current != null && !current.History.IsSaved && !discardUnsaved)
            throw new McpException($"'{current.Path}' has unsaved changes. Call save first, or open_rom again with discard_unsaved=true.");

         path = System.IO.Path.GetFullPath(path);
         if (!File.Exists(path)) throw new McpException($"File not found: {path}");

         singletons ??= new Singletons();
         var data = File.ReadAllBytes(path);
         var metadataText = File.Exists(MetadataPath(path)) ? File.ReadAllLines(MetadataPath(path)) : Array.Empty<string>();
         StoredMetadata metadata;
         try {
            metadata = new StoredMetadata(metadataText);
         } catch (Exception ex) {
            throw new McpException($"Could not read HMA metadata '{MetadataPath(path)}': {ex.Message}");
         }

         IDataModel model = path.EndsWith(".gba", StringComparison.OrdinalIgnoreCase)
            ? new HardcodeTablesModel(singletons, data, metadata)
            : new PokemonModel(data, metadata, singletons);
         model.InitializationWorkload.Wait();

         var viewPort = new ViewPort(path, model, InstantDispatch.Instance, singletons);
         current = new RomSession(path, model, viewPort, singletons);
         return current;
      }
   }

   public void Close() { lock (gate) current = null; }

   /// <summary>Same naming rule as HMA: game.gba -> game.toml, anything else -> name.ext.toml.</summary>
   public static string MetadataPath(string romPath) =>
      romPath.EndsWith(".gba", StringComparison.OrdinalIgnoreCase)
         ? System.IO.Path.ChangeExtension(romPath, ".toml")
         : romPath + ".toml";
}
