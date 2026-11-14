using System.ComponentModel;
using System.Reflection;
using System.Text;
using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using HavenSoft.HexManiac.Core.ViewModels.Map;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static HmaMcp.Tools.DataTools;
using static HmaMcp.Tools.MapTools;

namespace HmaMcp.Tools;

/// <summary>
/// Map editing through HMA's map editor: add/remove events, read and paint blocks, the blockset, resizing.
/// Moving or changing an existing event is write_table_field on the list map_events returns.
/// </summary>
[McpServerToolType]
public static class MapEditTools {
   sealed record EventKind(string Name, string CountField, string ListField, Func<BlockMapViewModel, EventGroupModel, IReadOnlyList<IEventViewModel>> Existing);

   static readonly EventKind[] Kinds = {
      new("npc", "objectCount", "objects", (_, g) => g.Objects),
      new("warp", "warpCount", "warps", (_, g) => g.Warps),
      new("trigger", "scriptCount", "scripts", (_, g) => g.Scripts),
      new("sign", "signpostCount", "signposts", (_, g) => g.Signposts),
   };

   static EventKind Kind(string kind) {
      var k = (kind ?? "").Trim().ToLowerInvariant() switch {
         "npc" or "object" or "objects" or "person" or "item" => "npc",
         "warp" or "warps" or "door" => "warp",
         "trigger" or "script" or "scripts" or "coord" => "trigger",
         "sign" or "signpost" or "signposts" or "bg" => "sign",
         _ => throw new McpException($"kind must be npc, warp, trigger or sign, not '{kind}'."),
      };
      return Kinds.First(x => x.Name == k);
   }

   // ---------- events ----------

   [McpServerTool(Name = "add_map_event", Destructive = true), Description(
      "Add an NPC/object, warp, trigger (step-on script) or sign to a map, the way HMA's map editor does (it creates the event list if the map has none, " +
      "picks a free NPC id, and moves the list if it grows). Set its position with x/y (blocks, as in map_events) and any other fields by name, " +
      "e.g. npc: {graphics: '5', script: '<165838>', moveType: '8'}; warp: {bank: '4', map: '1', warpID: '0'}; sign: {kind: '0', arg: '<165838>'}; " +
      "trigger: {trigger: '0x4050', index: '0', script: '<...>'}. Field names are the ones map_events shows. elevation defaults to the one most of this map's events of that kind use. One undo step. Check the result with render_map.")]
   public static string AddMapEvent(RomSessions sessions,
      [Description("Map id 'bank.map' or a unique map name")] string map,
      [Description("'npc', 'warp', 'trigger' or 'sign'")] string kind,
      [Description("Block x")] int x,
      [Description("Block y")] int y,
      [Description("Other fields to set, by name (values as write_table_field takes them)")] Dictionary<string, string>? fields = null) => sessions.Edit(s => {
      var k = Kind(kind);
      var m = FindOne(s, map);
      var (width, height) = MapSize(s, m);
      if (x < 0 || y < 0 || x >= width || y >= height) throw new McpException($"({x}, {y}) is outside the map ({width}x{height} blocks).");
      var header = TableAt(s, m.Header)!;
      if (Sub(s, header, 0, "events") == null && k.Name != "npc")
         throw new McpException($"Map {m.Id} has no events table yet. Add an npc first (HMA creates the table), then other events.");

      // HMA creates events at elevation 0; default to what this map's other events of the same kind use (usually 3).
      string? elevation = null;
      if (fields == null || !fields.ContainsKey("elevation")) {
         var existing = EventList(s, m, k);
         if (existing != null && existing.ElementCount > 0 && Fields(existing).FirstOrDefault(f => f.Key == "elevation") is { } ef) {
            elevation = Enumerable.Range(0, existing.ElementCount).Select(i => ReadField(s, existing, i, ef)?.ToString() ?? "0")
               .GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
         }
      }

      var view = GraphicsTools.MapView(s, m.Bank, m.Map);
      object? created = k.Name switch {
         "npc" => view.CreateObjectEvent(0, HavenSoft.HexManiac.Core.ViewModels.DataFormats.Pointer.NULL),
         "warp" => view.CreateWarpEvent(m.Bank, m.Map),
         "trigger" => view.CreateScriptEvent(),
         _ => view.CreateSignpostEvent(),
      };
      if (created == null) throw new McpException($"HMA couldn't add a {k.Name} to map {m.Id}.");

      var list = EventList(s, m, k) ?? throw new McpException("HMA added the event but its list can't be found.");
      int index = list.ElementCount - 1;
      var values = new List<(string, string)> { ("x", x.ToString()), ("y", y.ToString()) };
      if (elevation != null) values.Add(("elevation", elevation));
      if (fields != null) values.AddRange(fields.Select(kv => (kv.Key, kv.Value)));
      foreach (var (name, value) in values) {
         list = EventList(s, m, k)!;
         var f = Fields(list).FirstOrDefault(fl => fl.Key == name)
            ?? throw new McpException($"A {k.Name} has no field '{name}'. Fields: {string.Join(", ", Fields(list).Select(fl => fl.Key))}");
         WriteField(s, list, index, f, value);
      }
      list = EventList(s, m, k)!;
      return Json.Of(new {
         map = $"{m.Id} {m.Name}",
         kind = k.Name,
         table = list.Start.ToAddress(),
         index,
         count = list.ElementCount,
         @event = Row(s, list, index, skipPointers: false),
      });
   });

   [McpServerTool(Name = "remove_map_event", Destructive = true), Description(
      "Remove an NPC/object, warp, trigger or sign from a map by its index in map_events. Later events of that kind shift down one index. " +
      "Removing a warp can break warps on other maps that lead to this map by warp number; removing an NPC can break scripts that move it by its local id. One undo step.")]
   public static string RemoveMapEvent(RomSessions sessions,
      [Description("Map id 'bank.map' or a unique map name")] string map,
      [Description("'npc', 'warp', 'trigger' or 'sign'")] string kind,
      [Description("Index in that event list (from map_events)")] int index) => sessions.Edit(s => {
      var k = Kind(kind);
      var m = FindOne(s, map);
      var list = EventList(s, m, k);
      if (list == null || list.ElementCount == 0) throw new McpException($"Map {m.Id} has no {k.Name} events.");
      if (index < 0 || index >= list.ElementCount) throw new McpException($"index must be 0 to {list.ElementCount - 1}.");
      var removed = Row(s, list, index, skipPointers: false);
      var view = GraphicsTools.MapView(s, m.Bank, m.Map);
      var events = k.Existing(view, view.EventGroup);
      if (index >= events.Count || !events[index].Delete()) throw new McpException($"HMA couldn't remove {k.Name} {index}.");
      var after = EventList(s, m, k);
      return Json.Of(new {
         map = $"{m.Id} {m.Name}",
         kind = k.Name,
         removed,
         remaining = after?.ElementCount ?? 0,
         warning = k.Name switch {
            "warp" => "Warps on other maps that lead here by warp number may now point at a different warp.",
            "npc" => "Scripts that move or hide NPCs by local id may refer to a different NPC now.",
            _ => null,
         },
      });
   });

   // ---------- blocks ----------

   [McpServerTool(Name = "read_map_blocks", ReadOnly = true), Description(
      "The blocks of a map (or a rectangle of it) as grids: 'blocks' are block ids in hex (see view_blockset), 'movement' is the movement permission in hex " +
      "as AdvanceMap shows it (elevation*4 + collision: 0C = walkable at elevation 3, 01 = blocked, 00 = walkable at any height/transition, 04 = water/surf). " +
      "Row r, column c is block (x + c, y + r).")]
   public static string ReadMapBlocks(RomSessions sessions,
      [Description("Map id 'bank.map' or a unique map name")] string map,
      [Description("Left block")] int x = 0,
      [Description("Top block")] int y = 0,
      [Description("Width in blocks; 0 = to the right edge")] int width = 0,
      [Description("Height in blocks; 0 = to the bottom edge")] int height = 0) => sessions.With(s => {
      var m = FindOne(s, map);
      var (mapWidth, mapHeight, blockmap) = Blockmap(s, m);
      if (width <= 0) width = mapWidth - x;
      if (height <= 0) height = mapHeight - y;
      CheckRect(x, y, width, height, mapWidth, mapHeight);
      var blocks = new List<string>();
      var movement = new List<string>();
      var counts = new Dictionary<int, int>();
      for (int r = 0; r < height; r++) {
         var b = new StringBuilder();
         var mv = new StringBuilder();
         for (int c = 0; c < width; c++) {
            var v = s.Model.ReadMultiByteValue(blockmap + ((y + r) * mapWidth + x + c) * 2, 2);
            b.Append(c > 0 ? " " : "").Append((v & 0x3FF).ToString("X3"));
            mv.Append(c > 0 ? " " : "").Append((v >> 10).ToString("X2"));
            counts[v & 0x3FF] = counts.GetValueOrDefault(v & 0x3FF) + 1;
         }
         blocks.Add(b.ToString());
         movement.Add(mv.ToString());
      }
      return Json.Of(new {
         map = $"{m.Id} {m.Name}",
         map_size = $"{mapWidth}x{mapHeight}",
         x, y, width, height,
         blocks,
         movement,
         most_used = counts.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key:X3} x{kv.Value}"),
      });
   });

   [McpServerTool(Name = "paint_map_blocks", Destructive = true), Description(
      "Write blocks onto a map starting at block (x, y). blocks: rows of hex block ids separated by spaces, e.g. ['1A0 1A1', '1A8 1A9'] " +
      "(copy rows from read_map_blocks to reuse a building or path); use '.' to leave a cell alone. movement: optional rows of hex movement " +
      "permissions in the same shape ('.' keeps), or one value for every painted cell (e.g. '01' to make it all blocked). One undo step; check with render_map.")]
   public static string PaintMapBlocks(RomSessions sessions,
      [Description("Map id 'bank.map' or a unique map name")] string map,
      [Description("Left block")] int x,
      [Description("Top block")] int y,
      [Description("Rows of hex block ids, '.' to keep")] string[] blocks,
      [Description("Optional rows of hex movement permissions, '.' to keep; or a single value for all painted cells")] string[]? movement = null) => sessions.Edit(s => {
      var m = FindOne(s, map);
      var (mapWidth, mapHeight, blockmap) = Blockmap(s, m);
      if (blocks == null || blocks.Length == 0) throw new McpException("blocks needs at least one row.");
      var blockRows = blocks.Select(Cells).ToList();
      int width = blockRows.Max(r => r.Count), height = blockRows.Count;
      CheckRect(x, y, width, height, mapWidth, mapHeight);
      List<List<string>>? moveRows = null;
      string? moveAll = null;
      if (movement != null && movement.Length > 0) {
         if (movement.Length == 1 && Cells(movement[0]).Count == 1 && (height > 1 || width > 1)) moveAll = Cells(movement[0])[0];
         else {
            moveRows = movement.Select(Cells).ToList();
            if (moveRows.Count != height || moveRows.Any(r => r.Count > width)) throw new McpException("movement must have the same shape as blocks, or be a single value.");
         }
      }
      int blockCount = GraphicsTools.MapView(s, m.Bank, m.Map).BlockRenders.Count;
      int changed = 0;
      for (int r = 0; r < height; r++) {
         for (int c = 0; c < width; c++) {
            var addr = blockmap + ((y + r) * mapWidth + x + c) * 2;
            var old = s.Model.ReadMultiByteValue(addr, 2);
            int block = old & 0x3FF, move = old >> 10;
            var bText = c < blockRows[r].Count ? blockRows[r][c] : ".";
            if (bText != ".") {
               block = ParseHex(bText, "block", 0x3FF);
               if (block >= blockCount) throw new McpException($"Block {block:X3} doesn't exist in this map's tilesets ({blockCount} blocks, 000-{blockCount - 1:X3}). See view_blockset.");
            }
            var mText = moveAll ?? (moveRows != null && c < moveRows[r].Count ? moveRows[r][c] : ".");
            if (mText != "." && (bText != "." || moveRows != null)) move = ParseHex(mText, "movement", 0x3F);
            var value = (move << 10) | block;
            if (value != old) {
               s.Model.WriteMultiByteValue(addr, 2, s.Token, value);
               changed++;
            }
         }
      }
      s.ViewPort.Refresh();
      return Json.Of(new { map = $"{m.Id} {m.Name}", x, y, width, height, cells_changed = changed });
   });

   [McpServerTool(Name = "view_blockset", ReadOnly = true), Description(
      "The blocks a map can use (its primary and secondary tilesets), as an image 8 blocks wide: the block at row r, column c is id first_block + r*8 + c. " +
      "Use with read_map_blocks (to see which ids an existing building or path uses) and paint_map_blocks.")]
   public static CallToolResult ViewBlockset(RomSessions sessions,
      [Description("Map id 'bank.map' or a unique map name")] string map,
      [Description("First block id to show (rounded down to a multiple of 8); hex ok, e.g. 0x280 for FireRed's secondary tileset")] string first_block = "0",
      [Description("How many blocks to show")] int count = 256,
      [Description("Zoom 1-4")] int scale = 2) => sessions.With(s => {
      var m = FindOne(s, map);
      var view = GraphicsTools.MapView(s, m.Bank, m.Map);
      var all = view.BlockPixels;
      int total = view.BlockRenders.Count;
      int first = ParseHex(first_block.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? first_block[2..] : first_block, "first_block", 0x3FF, allowDecimal: !first_block.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) / 8 * 8;
      if (first >= total) throw new McpException($"This map has {total} blocks (000-{total - 1:X3}).");
      int rows = (Math.Min(total, first + Math.Clamp(count, 8, 1024)) - first + 7) / 8;
      int width = 8 * 16, height = rows * 16, top = first / 8 * 16;
      var pixels = new short[width * height];
      for (int yy = 0; yy < height && top + yy < all.PixelHeight; yy++)
         Array.Copy(all.PixelData, (top + yy) * all.PixelWidth, pixels, yy * width, width);
      int primary = s.Model.IsFRLG() ? 0x280 : 0x200;
      var info = new Dictionary<string, object?> {
         ["map"] = $"{m.Id} {m.Name}",
         ["total_blocks"] = total,
         ["primary_tileset_blocks"] = $"000-{primary - 1:X3}",
         ["secondary_tileset_blocks"] = total > primary ? $"{primary:X3}-{total - 1:X3}" : null,
         ["shown"] = $"{first:X3}-{Math.Min(total, first + rows * 8) - 1:X3}",
         ["layout"] = $"8 blocks per row, 16x16 px each: block id = {first:X3} + row*8 + column (hex)",
      };
      return GraphicsTools.Result(info, pixels, width, height, Math.Clamp(scale, 1, 4), "");
   });

   // ---------- resize ----------

   static readonly MethodInfo ResizeMapData = typeof(BlockMapViewModel).GetMethod("ResizeMapData", BindingFlags.NonPublic | BindingFlags.Instance)
      ?? throw new InvalidOperationException("HMA's BlockMapViewModel no longer has ResizeMapData; update MapEditTools.");

   [McpServerTool(Name = "resize_map", Destructive = true), Description(
      "Grow or shrink a map by adding (positive) or removing (negative) block columns/rows on each side, as dragging the edges in HMA's map editor does. " +
      "Events and connections are shifted to stay in place, and the map data is moved if it grows. New cells repeat the edge blocks; paint them afterwards. One undo step.")]
   public static string ResizeMap(RomSessions sessions,
      [Description("Map id 'bank.map' or a unique map name")] string map,
      [Description("Columns to add (+) or remove (-) on the left")] int left = 0,
      [Description("Columns to add (+) or remove (-) on the right")] int right = 0,
      [Description("Rows to add (+) or remove (-) at the top")] int top = 0,
      [Description("Rows to add (+) or remove (-) at the bottom")] int bottom = 0) => sessions.Edit(s => {
      var m = FindOne(s, map);
      var (oldWidth, oldHeight) = MapSize(s, m);
      int newWidth = oldWidth + left + right, newHeight = oldHeight + top + bottom;
      if (newWidth < 1 || newHeight < 1) throw new McpException($"That would make the map {newWidth}x{newHeight}.");
      if (!BlockMapViewModel.IsMapWithinSizeLimit(newWidth, newHeight)) throw new McpException($"{newWidth}x{newHeight} is larger than the game can load ((width+15)*(height+14) must be at most 10240).");
      var view = GraphicsTools.MapView(s, m.Bank, m.Map);
      foreach (var (dir, amount) in new[] { (MapDirection.Left, left), (MapDirection.Right, right), (MapDirection.Up, top), (MapDirection.Down, bottom) }) {
         if (amount == 0) continue;
         try {
            ResizeMapData.Invoke(view, new object[] { dir, amount });
         } catch (TargetInvocationException ex) when (ex.InnerException != null) {
            throw new McpException($"HMA couldn't resize the map: {ex.InnerException.Message}");
         }
      }
      var (width, height) = MapSize(s, m);
      if ((width, height) != (newWidth, newHeight)) throw new McpException($"HMA resized the map to {width}x{height} instead of {newWidth}x{newHeight}; nothing was kept.");
      s.ViewPort.Refresh();
      return Json.Of(new { map = $"{m.Id} {m.Name}", before = $"{oldWidth}x{oldHeight}", after = $"{width}x{height}" });
   });

   // ---------- helpers ----------

   static ITableRun? EventList(RomSession s, MapRef m, EventKind k) {
      var header = TableAt(s, m.Header);
      var events = header == null ? null : Sub(s, header, 0, "events");
      return events == null ? null : Sub(s, events, 0, k.ListField);
   }

   static (int width, int height) MapSize(RomSession s, MapRef m) {
      var (w, h, _) = Blockmap(s, m);
      return (w, h);
   }

   static (int width, int height, int blockmap) Blockmap(RomSession s, MapRef m) {
      var header = TableAt(s, m.Header) ?? throw new McpException($"No map header at {m.Header.ToAddress()}.");
      var layout = Sub(s, header, 0, "layout") ?? throw new McpException($"Map {m.Id} has no layout.");
      var width = Convert.ToInt32(Value(s, layout, 0, "width"));
      var height = Convert.ToInt32(Value(s, layout, 0, "height"));
      var blockmap = Pointer(s, layout, 0, "blockmap");
      if (blockmap < 0) throw new McpException($"Map {m.Id}'s layout has no block data.");
      return (width, height, blockmap);
   }

   static void CheckRect(int x, int y, int width, int height, int mapWidth, int mapHeight) {
      if (x < 0 || y < 0 || width < 1 || height < 1 || x + width > mapWidth || y + height > mapHeight)
         throw new McpException($"The rectangle ({x}, {y}) size {width}x{height} doesn't fit in the map ({mapWidth}x{mapHeight} blocks).");
   }

   static List<string> Cells(string row) =>
      (row ?? "").Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).ToList();

   static int ParseHex(string text, string what, int max, bool allowDecimal = false) {
      var t = text.Trim();
      if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
      bool ok = allowDecimal ? int.TryParse(t, out var v) : int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out v);
      if (!ok || v < 0 || v > max) throw new McpException($"'{text}' isn't a valid {what} (hex 0-{max:X}).");
      return v;
   }
}
