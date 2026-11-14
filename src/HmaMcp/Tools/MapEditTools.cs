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
      "trigger: {trigger: '0x4050', index: '0', script: '<...>'}. Or pass message to have a talk script written for an npc or sign. Field names are the ones map_events shows. elevation defaults to the one most of this map's events of that kind use. One undo step. Check the result with render_map.")]
   public static string AddMapEvent(RomSessions sessions,
      [Description("Map id 'bank.map' or a unique map name")] string map,
      [Description("'npc', 'warp', 'trigger' or 'sign'")] string kind,
      [Description("Block x")] int x,
      [Description("Block y")] int y,
      [Description("Other fields to set, by name (values as write_table_field takes them)")] Dictionary<string, string>? fields = null,
      [Description("npc or sign only: text it says. A simple script is written for it (npc: faces the player and shows the text; sign: shows the text). Line breaks as in write_text.")] string message = "") => sessions.Edit(s => {
      var k = Kind(kind);
      string? scriptField = null, scriptValue = null;
      if (!string.IsNullOrWhiteSpace(message)) {
         if (k.Name is not ("npc" or "sign")) throw new McpException("message works for an npc or a sign. For a trigger, write its script with compile_script and pass script.");
         scriptField = k.Name == "npc" ? "script" : "arg";
         if (fields != null && fields.ContainsKey(scriptField)) throw new McpException($"Pass either message or {scriptField}, not both.");
         scriptValue = $"<{TalkScript(s, message, k.Name == "sign").ToAddress()}>";
      }
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
      if (scriptField != null) values.Add((scriptField, scriptValue!));
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
      "Removing a warp renumbers warps on any map that lead to this map's later warps, and lists any that led to the removed one. One undo step.")]
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

      // Warps anywhere that lead to this map by warp number: later numbers move down one; ones that led to the removed warp are reported.
      var renumbered = new List<string>();
      var dangling = new List<string>();
      if (k.Name == "warp") {
         var warpKind = Kinds.First(x => x.Name == "warp");
         foreach (var other in AllMaps(s)) {
            var warps = EventList(s, other, warpKind);
            if (warps == null) continue;
            var fields = Fields(warps);
            Field? F(string name) => fields.FirstOrDefault(f => f.Key == name);
            if (F("bank") is not { } fb || F("map") is not { } fm || F("warpID") is not { } fw) continue;
            for (int i = 0; i < warps.ElementCount; i++) {
               if (Convert.ToInt32(ReadField(s, warps, i, fb)) != m.Bank || Convert.ToInt32(ReadField(s, warps, i, fm)) != m.Map) continue;
               int id = Convert.ToInt32(ReadField(s, warps, i, fw));
               if (id == index) dangling.Add($"{other.Id} warp {i}");
               else if (id > index && id < 0xFF) {
                  WriteField(s, warps, i, fw, (id - 1).ToString());
                  renumbered.Add($"{other.Id} warp {i}: {id} -> {id - 1}");
               }
            }
         }
      }
      return Json.Of(new {
         map = $"{m.Id} {m.Name}",
         kind = k.Name,
         removed,
         remaining = after?.ElementCount ?? 0,
         warps_renumbered = renumbered.Count > 0 ? renumbered : null,
         warps_now_leading_nowhere = dangling.Count > 0 ? dangling : null,
         warning = k.Name switch {
            "warp" when dangling.Count > 0 => "These warps led to the removed warp. Point them at another warp of this map (write_table_field warpID) or remove them.",
            "npc" => $"Other NPCs keep their local ids. Scripts that refer to the removed NPC's id ({removed.GetValueOrDefault("id")}) no longer find it.",
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
      "The blocks a map can use (its primary and secondary tilesets), as an image 8 blocks wide with each block's hex id printed under it. " +
      "Use with read_map_blocks (to see which ids an existing building or path uses) and paint_map_blocks.")]
   public static CallToolResult ViewBlockset(RomSessions sessions,
      [Description("Map id 'bank.map' or a unique map name")] string map,
      [Description("First block id to show (rounded down to a multiple of 8); hex ok, e.g. 0x280 for FireRed's secondary tileset")] string first_block = "0",
      [Description("How many blocks to show")] int count = 256,
      [Description("Zoom 1-4")] int scale = 2) => sessions.With(s => {
      var m = FindOne(s, map);
      int first = ParseHex(first_block.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? first_block[2..] : first_block, "first_block", 0x3FF, allowDecimal: !first_block.StartsWith("0x", StringComparison.OrdinalIgnoreCase));
      return BlocksetImage(s, m, first, count, scale);
   });

   /// <summary>A labeled image of a map's blocks from first (rounded down to 8) for count blocks.</summary>
   internal static CallToolResult BlocksetImage(RomSession s, MapRef m, int first, int count, int scale, Dictionary<string, object?>? extra = null) {
      var view = GraphicsTools.MapView(s, m.Bank, m.Map);
      var all = view.BlockPixels;
      int total = view.BlockRenders.Count;
      first = first / 8 * 8;
      if (first >= total) throw new McpException($"This map has {total} blocks (000-{total - 1:X3}).");
      int rows = (Math.Min(total, first + Math.Clamp(count, 8, 1024)) - first + 7) / 8;
      // each block with its hex id printed under it
      const int cellW = 18, cellH = 16 + Labels.Height + 2;
      int width = 8 * cellW, height = rows * cellH, top = first / 8 * 16;
      var pixels = Enumerable.Repeat(Png.FromRgb(64, 64, 72), width * height).ToArray();
      for (int r = 0; r < rows; r++) {
         for (int c = 0; c < 8; c++) {
            int id = first + r * 8 + c;
            if (id >= total) break;
            for (int yy = 0; yy < 16 && top + r * 16 + yy < all.PixelHeight; yy++)
               Array.Copy(all.PixelData, (top + r * 16 + yy) * all.PixelWidth + c * 16, pixels, (r * cellH + yy) * width + c * cellW + 1, 16);
            Labels.Draw(pixels, width, c * cellW + 2, r * cellH + 16, id.ToString("X3"));
         }
      }
      int primary = s.Model.IsFRLG() ? 0x280 : 0x200;
      var info = extra ?? new Dictionary<string, object?>();
      info["map"] = $"{m.Id} {m.Name}";
      info["total_blocks"] = total;
      info["primary_tileset_blocks"] = $"000-{primary - 1:X3}";
      info["secondary_tileset_blocks"] = total > primary ? $"{primary:X3}-{total - 1:X3}" : null;
      info["shown"] = $"{first:X3}-{Math.Min(total, first + rows * 8) - 1:X3}";
      info["layout"] = "8 blocks per row; each block's hex id is printed under it";
      return GraphicsTools.Result(info, pixels, width, height, Math.Clamp(scale, 1, 4), "");
   }

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

   // ---------- new maps and connections ----------

   static readonly MethodInfo CreateNewMapMethod = Private("CreateNewMap", typeof(ModelDelta), typeof(int), typeof(int), typeof(int));
   static readonly MethodInfo AddConnectionMethod = Private("AddConnection", typeof(ConnectionInfo));
   static readonly MethodInfo RemoveConnectionsMethod = Private("RemoveConnections", typeof(IReadOnlyList<int>));

   static MethodInfo Private(string name, params Type[] args) =>
      typeof(BlockMapViewModel).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance, null, args, null)
      ?? throw new InvalidOperationException($"HMA's BlockMapViewModel no longer has {name}; update MapEditTools.");

   static object? Call(MethodInfo method, object target, params object[] args) {
      try {
         return method.Invoke(target, args);
      } catch (TargetInvocationException ex) when (ex.InnerException != null) {
         throw new McpException($"HMA failed in {method.Name}: {ex.InnerException.Message}");
      }
   }

   [McpServerTool(Name = "create_map", Destructive = true), Description(
      "Create a new map, as HMA's map editor does: it copies the header (music, weather, map type, name) and the tilesets/border of an existing " +
      "map you pick as a template, and starts with no events and no connections. The block area is filled with one block (default: the template's " +
      "most-used block and movement). Then: paint_map_blocks to draw it, add_map_event for a warp in and out, connect_maps to join it to a neighbor. " +
      "Returns the new map's id. One undo step.")]
   public static string CreateMap(RomSessions sessions,
      [Description("Template map id 'bank.map' or unique name (e.g. a route for an outdoor map, a house for an indoor one)")] string like,
      [Description("Width in blocks")] int width,
      [Description("Height in blocks")] int height,
      [Description("Map bank to add it to (number), or 'new' for a new bank. Banks are only for organization; default: the template's bank")] string bank = "",
      [Description("Block to fill with, hex (default: the template's most-used block)")] string fill_block = "",
      [Description("Movement permission for the fill, hex (default: what the template uses with that block)")] string fill_movement = "",
      [Description("Map name from the region names (regionSectionID / data.maps.names), e.g. 'ROUTE 1'; default: the template's")] string name = "") => sessions.Edit(s => {
      var template = FindOne(s, like);
      if (width < 1 || height < 1 || !BlockMapViewModel.IsMapWithinSizeLimit(width, height))
         throw new McpException($"{width}x{height} isn't a valid map size ((width+15)*(height+14) must be at most 10240).");
      int bankCount = TableAt(s, s.Model.GetTable(HardcodeTablesModel.MapBankTable).Start)!.ElementCount;
      int bankIndex = string.IsNullOrWhiteSpace(bank) ? template.Bank
         : bank.Trim().Equals("new", StringComparison.OrdinalIgnoreCase) ? bankCount
         : int.TryParse(bank, out var b) && b >= 0 && b <= bankCount ? b
         : throw new McpException($"bank must be 0 to {bankCount - 1}, or 'new'.");

      // the fill value: a block plus its movement bits, by default the template's most common cell
      var (tw, th, tmap) = Blockmap(s, template);
      var cells = Enumerable.Range(0, tw * th).Select(i => s.Model.ReadMultiByteValue(tmap + i * 2, 2)).ToList();
      int fillBlock = string.IsNullOrWhiteSpace(fill_block) ? cells.GroupBy(v => v & 0x3FF).OrderByDescending(g => g.Count()).First().Key : ParseHex(fill_block, "block", 0x3FF);
      int fillMove = string.IsNullOrWhiteSpace(fill_movement)
         ? cells.Where(v => (v & 0x3FF) == fillBlock).Select(v => v >> 10).GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).DefaultIfEmpty(0).First()
         : ParseHex(fill_movement, "movement", 0x3F);

      var view = GraphicsTools.MapView(s, template.Bank, template.Map);
      if (fillBlock >= view.BlockRenders.Count) throw new McpException($"Block {fillBlock:X3} isn't in the template's tilesets (000-{view.BlockRenders.Count - 1:X3}).");
      var created = Call(CreateNewMapMethod, view, s.Token, bankIndex, width, height) as BlockMapViewModel
         ?? throw new McpException(s.Model.GetTable(HardcodeTablesModel.MapLayoutTable) == null
            ? "This ROM has no map layout table, so HMA can't add maps."
            : $"Bank {bankIndex} is full (127 maps). Use another bank or 'new'.");
      // its own copy of the border blocks, so editing them later doesn't change the template
      if (created.MapRepointer.RepointBorderBlock.CanExecute(null)) created.MapRepointer.RepointBorderBlock.Execute(null);

      var m = FindOne(s, $"{created.MapID / 1000}.{created.MapID % 1000}");
      var (_, _, blockmap) = Blockmap(s, m);
      for (int i = 0; i < width * height; i++) s.Model.WriteMultiByteValue(blockmap + i * 2, 2, s.Token, (fillMove << 10) | fillBlock);
      if (!string.IsNullOrWhiteSpace(name)) {
         var header = TableAt(s, m.Header)!;
         var f = Fields(header).FirstOrDefault(x => x.Key == "regionSectionID") ?? throw new McpException("This ROM's map header has no regionSectionID field.");
         WriteField(s, header, 0, f, name);
      }
      s.ViewPort.Refresh();
      m = FindOne(s, m.Id);
      return Json.Of(new {
         id = m.Id,
         name = m.Name,
         header = m.Header.ToAddress(),
         size = $"{width}x{height}",
         like = $"{template.Id} {template.Name}",
         fill = $"block {fillBlock:X3}, movement {fillMove:X2}",
         next = "paint_map_blocks to draw it, add_map_event for warps, connect_maps for neighbors; check with render_map.",
      });
   });

   static readonly Dictionary<string, MapDirection> Directions = new(StringComparer.OrdinalIgnoreCase) {
      ["up"] = MapDirection.Up, ["north"] = MapDirection.Up,
      ["down"] = MapDirection.Down, ["south"] = MapDirection.Down,
      ["left"] = MapDirection.Left, ["west"] = MapDirection.Left,
      ["right"] = MapDirection.Right, ["east"] = MapDirection.Right,
      ["dive"] = MapDirection.Dive, ["emerge"] = MapDirection.Emerge,
   };

   static MapDirection Direction(string text) => Directions.TryGetValue((text ?? "").Trim(), out var d) ? d
      : throw new McpException($"direction must be up, down, left, right (or north/south/west/east), dive or emerge, not '{text}'.");

   [McpServerTool(Name = "connect_maps", Destructive = true), Description(
      "Connect two maps so the player walks from one into the other (or dives/emerges), adding the matching connection on both maps, as HMA's map editor does. " +
      "direction is where to_map lies as seen from map: 'up' means to_map is north of map. offset is in blocks: for up/down, how far to_map is shifted right " +
      "relative to map's left edge (negative = left); for left/right, how far it is shifted down relative to map's top edge. Default: centered. " +
      "Check with render_map on both. One undo step.")]
   public static string ConnectMaps(RomSessions sessions,
      [Description("Map id 'bank.map' or unique name")] string map,
      [Description("'up', 'down', 'left', 'right' (or north/south/west/east), 'dive' or 'emerge'")] string direction,
      [Description("The neighboring map, id or unique name")] string to_map,
      [Description("Offset in blocks; omit to center")] int? offset = null) => sessions.Edit(s => {
      var a = FindOne(s, map);
      var bMap = FindOne(s, to_map);
      if (a.Bank == bMap.Bank && a.Map == bMap.Map) throw new McpException("A map can't connect to itself.");
      var dir = Direction(direction);
      var (aw, ah) = MapSize(s, a);
      var (bw, bh) = MapSize(s, bMap);
      bool vertical = dir is MapDirection.Up or MapDirection.Down;
      bool z = dir is MapDirection.Dive or MapDirection.Emerge;
      int off = z ? 0 : offset ?? (vertical ? (aw - bw) / 2 : (ah - bh) / 2);

      var existing = Connections(s, a);
      if (existing.Any(c => c.mapGroup == bMap.Bank && c.mapNum == bMap.Map && c.direction == dir))
         throw new McpException($"{a.Id} is already connected to {bMap.Id} going {dir.ToString().ToLowerInvariant()}.");
      CheckConnectionSpace(s, a, dir, off, vertical ? bw : bh, vertical ? aw : ah, bMap);
      CheckConnectionSpace(s, bMap, dir.Reverse(), z ? 0 : -off, vertical ? aw : ah, vertical ? bw : bh, a);

      var viewA = GraphicsTools.MapView(s, a.Bank, a.Map);
      var viewB = GraphicsTools.MapView(s, bMap.Bank, bMap.Map);
      var there = Call(AddConnectionMethod, viewA, new ConnectionInfo(0, off, dir)) as ConnectionModel
         ?? throw new McpException($"HMA couldn't add a connection to {a.Id}.");
      there.Offset = off;
      there.Direction = dir;
      there.MapGroup = bMap.Bank;
      there.MapNum = bMap.Map;
      there.Unused = 0;
      var back = Call(AddConnectionMethod, viewB, new ConnectionInfo(0, -off, dir.Reverse())) as ConnectionModel
         ?? throw new McpException($"HMA couldn't add a connection to {bMap.Id}.");
      back.Offset = z ? 0 : -off;
      back.Direction = dir.Reverse();
      back.MapGroup = a.Bank;
      back.MapNum = a.Map;
      back.Unused = 0;
      s.ViewPort.Refresh();
      return Json.Of(new {
         map = $"{a.Id} {a.Name}",
         to = $"{bMap.Id} {bMap.Name}",
         direction = dir.ToString().ToLowerInvariant(),
         offset = off,
         reverse = $"{bMap.Id} -> {a.Id} going {dir.Reverse().ToString().ToLowerInvariant()}, offset {(z ? 0 : -off)}",
      });
   });

   [McpServerTool(Name = "disconnect_maps", Destructive = true), Description(
      "Remove a map's connection(s) in one direction, and the matching connection on the other map, as HMA's map editor does. " +
      "Pass to_map to remove only the connection to that map when several share a side. One undo step.")]
   public static string DisconnectMaps(RomSessions sessions,
      [Description("Map id 'bank.map' or unique name")] string map,
      [Description("'up', 'down', 'left', 'right' (or north/south/west/east), 'dive' or 'emerge'")] string direction,
      [Description("Only the connection to this map (id or unique name)")] string to_map = "") => sessions.Edit(s => {
      var a = FindOne(s, map);
      var dir = Direction(direction);
      MapRef? other = string.IsNullOrWhiteSpace(to_map) ? null : FindOne(s, to_map);
      var list = Connections(s, a);
      var indices = list.Select((c, i) => (c, i))
         .Where(x => x.c.direction == dir && (other == null || (x.c.mapGroup == other.Bank && x.c.mapNum == other.Map)))
         .Select(x => x.i).ToList();
      if (indices.Count == 0) throw new McpException($"{a.Id} has no connection going {dir.ToString().ToLowerInvariant()}{(other != null ? $" to {other.Id}" : "")}.");
      var removed = indices.Select(i => $"{list[i].mapGroup}.{list[i].mapNum}").ToList();
      var view = GraphicsTools.MapView(s, a.Bank, a.Map);
      Call(RemoveConnectionsMethod, view, (IReadOnlyList<int>)indices);
      s.ViewPort.Refresh();
      return Json.Of(new { map = $"{a.Id} {a.Name}", direction = dir.ToString().ToLowerInvariant(), removed, remaining = Connections(s, a).Count });
   });

   /// <summary>
   /// The new neighbor's span [offset, offset+length) along this map's edge must touch the edge and not overlap a neighbor
   /// already on that side (the same rule HMA's map editor uses). Dive/emerge: one per map.
   /// </summary>
   static void CheckConnectionSpace(RomSession s, MapRef m, MapDirection dir, int offset, int length, int edgeLength, MapRef neighbor) {
      var side = Connections(s, m).Where(c => c.direction == dir).ToList();
      var name = dir.ToString().ToLowerInvariant();
      if (dir is MapDirection.Dive or MapDirection.Emerge) {
         if (side.Count > 0) throw new McpException($"{m.Id} already has a {name} connection (to {side[0].mapGroup}.{side[0].mapNum}).");
         return;
      }
      if (offset + length <= 0 || offset >= edgeLength)
         throw new McpException($"With offset {offset}, {neighbor.Id} wouldn't touch {m.Id}'s {name} edge ({edgeLength} blocks long). Use an offset from {1 - length} to {edgeLength - 1}.");
      bool vertical = dir is MapDirection.Up or MapDirection.Down;
      foreach (var c in side) {
         var other = new MapRef(c.mapGroup, c.mapNum, "", 0);
         int otherLength;
         try {
            var (ow, oh) = MapSize(s, FindOne(s, other.Id));
            otherLength = vertical ? ow : oh;
         } catch (McpException) { continue; }
         if (offset < c.offset + otherLength && c.offset < offset + length)
            throw new McpException($"{m.Id}'s {name} edge already has {other.Id} at blocks {c.offset} to {c.offset + otherLength - 1}; {neighbor.Id} would cover {offset} to {offset + length - 1}. " +
               "Pick an offset that doesn't overlap, or disconnect_maps first.");
      }
   }

   static List<(MapDirection direction, int offset, int mapGroup, int mapNum)> Connections(RomSession s, MapRef m) {
      var header = TableAt(s, m.Header);
      var con = header == null ? null : Sub(s, header, 0, "connections");
      var list = con == null ? null : Sub(s, con, 0, "connections");
      if (list == null) return new();
      return Enumerable.Range(0, list.ElementCount).Select(i => (
         (MapDirection)s.Model.ReadMultiByteValue(list.Start + i * list.ElementLength, 4),
         s.Model.ReadMultiByteValue(list.Start + i * list.ElementLength + 4, 4),
         (int)s.Model[list.Start + i * list.ElementLength + 8],
         (int)s.Model[list.Start + i * list.ElementLength + 9])).ToList();
   }

   /// <summary>Write the text and a standard talk script for it into free space; returns the script's address.</summary>
   static int TalkScript(RomSession s, string message, bool sign) {
      var text = TextTools.Escape(message);
      var bytes = s.Model.TextConverter.Convert(text, out var bad);
      if (bad) throw new McpException("The message has characters the game's font doesn't have. Use read_text on existing text to see what's allowed.");
      var textStart = s.Model.FindFreeSpace(s.Model.FreeSpaceStart, bytes.Count);
      if (textStart < 0) throw new McpException("No free space for the message.");
      s.Token.ChangeData(s.Model, textStart, bytes.ToArray());
      s.Model.ObserveRunWritten(s.Token, new PCSRun(s.Model, textStart, bytes.Count));
      var lines = sign
         ? new[] { $"msgbox.sign <{textStart.ToAddress()}>", "end" }
         : new[] { "lock", "faceplayer", $"msgbox.default <{textStart.ToAddress()}>", "release", "end" };
      return ScriptTools.CompileNew(s, string.Concat(lines.Select(line => "  " + line + Environment.NewLine)));
   }

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
