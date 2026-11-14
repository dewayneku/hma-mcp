using System.ComponentModel;
using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.ViewModels.Map;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static HmaMcp.Tools.MapTools;

namespace HmaMcp.Tools;

/// <summary>
/// Blocks (metatiles): what each block of a map's tilesets is made of (eight 8x8 tiles in two layers) and how it behaves.
/// A block id below the primary count is in the primary tileset (shared by many maps); the rest are the map's secondary tileset.
/// </summary>
[McpServerToolType]
public static class BlockTools {
   internal sealed record TilesetRef(bool Secondary, int Record, int Tileset, bool Compressed, int Palettes, int Blockset, int Attributes, int SharedBy);

   internal static int PrimaryBlocks(RomSession s) => s.Model.IsFRLG() ? 0x280 : 0x200;
   internal static int PrimaryTiles(RomSession s) => s.Model.IsFRLG() ? 0x280 : 0x200;
   internal static int PrimaryPalettes(RomSession s) => s.Model.IsFRLG() ? 7 : 6;
   internal static int AttributeSize(RomSession s) => s.Model.IsFRLG() ? 4 : 2;
   static readonly string[] LayerNames = { "normal", "covered", "split" };

   internal static TilesetRef Tileset(RomSession s, MapRef m, bool secondary) {
      var header = TableAt(s, m.Header) ?? throw new McpException($"No map header at {m.Header.ToAddress()}.");
      var layout = Sub(s, header, 0, "layout") ?? throw new McpException($"Map {m.Id} has no layout.");
      var record = Sub(s, layout, 0, secondary ? "blockdata2" : "blockdata1") ?? throw new McpException($"Map {m.Id} has no {(secondary ? "secondary" : "primary")} tileset.");
      return new TilesetRef(secondary, record.Start,
         Pointer(s, record, 0, "tileset"),
         Convert.ToInt32(Value(s, record, 0, "isCompressed")) != 0,
         Pointer(s, record, 0, "pal"),
         Pointer(s, record, 0, "blockset"),
         Pointer(s, record, 0, "attributes"),
         record.PointerSources?.Count ?? 0);
   }

   internal static (TilesetRef ts, int local) BlockLocation(RomSession s, MapRef m, int block) {
      int primary = PrimaryBlocks(s);
      var view = GraphicsTools.MapView(s, m.Bank, m.Map);
      if (block < 0 || block >= view.BlockRenders.Count) throw new McpException($"Block {block:X3} doesn't exist for map {m.Id} (000-{view.BlockRenders.Count - 1:X3}).");
      var ts = Tileset(s, m, block >= primary);
      return (ts, block >= primary ? block - primary : block);
   }

   // ---------- read / write ----------

   [McpServerTool(Name = "read_block", ReadOnly = true), Description(
      "What a block is made of and how it behaves: its behavior (e.g. tall grass, water, door, ledge; by name), layer type (normal: top layer drawn over " +
      "the player; covered: both layers under the player; split), and its eight 8x8 tiles: bottom layer then top layer, each 'tile palette flip' " +
      "(tile id hex, palette 0-12, flip h/v/hv or -). Shows the block enlarged. Use view_tiles to see the tiles, write_block to change it.")]
   public static CallToolResult ReadBlock(RomSessions sessions,
      [Description("Map id 'bank.map' or unique name (the block ids depend on the map's tilesets)")] string map,
      [Description("Block id, hex (as read_map_blocks / view_blockset show)")] string block) => sessions.With(s => {
      var m = FindOne(s, map);
      int b = ParseBlock(block);
      var info = BlockInfo(s, m, b);
      var view = GraphicsTools.MapView(s, m.Bank, m.Map);
      var render = view.BlockRenders[b];
      return GraphicsTools.Result(info, render.PixelData, render.PixelWidth, render.PixelHeight, 8, "");
   });

   [McpServerTool(Name = "write_block", Destructive = true), Description(
      "Change a block: its behavior (a name from read_block/list, e.g. 'MB_TALL_GRASS', or hex), its layer type ('normal', 'covered', 'split'), and/or " +
      "its tiles: bottom and top are 4 entries each, in the order top-left, top-right, bottom-left, bottom-right, written 'tile palette [flip]' " +
      "(e.g. '1A3 7', '1A3 7 h'; '.' keeps an entry). A primary-tileset block (id below the primary count) changes on every map using that tileset; " +
      "the result says how many tileset records share it. One undo step.")]
   public static CallToolResult WriteBlock(RomSessions sessions,
      [Description("Map id 'bank.map' or unique name")] string map,
      [Description("Block id, hex")] string block,
      [Description("Behavior name or hex; empty keeps it")] string behavior = "",
      [Description("'normal', 'covered' or 'split'; empty keeps it")] string layer = "",
      [Description("Bottom layer: 4 entries 'tile palette [flip]'")] string[]? bottom = null,
      [Description("Top layer: 4 entries 'tile palette [flip]'")] string[]? top = null) => sessions.Edit(s => {
      var m = FindOne(s, map);
      int b = ParseBlock(block);
      var (ts, local) = BlockLocation(s, m, b);
      var model = s.Model;

      // tiles
      for (int layerIndex = 0; layerIndex < 2; layerIndex++) {
         var entries = layerIndex == 0 ? bottom : top;
         if (entries == null || entries.Length == 0) continue;
         if (entries.Length != 4) throw new McpException($"{(layerIndex == 0 ? "bottom" : "top")} needs 4 entries (top-left, top-right, bottom-left, bottom-right).");
         for (int i = 0; i < 4; i++) {
            if (entries[i].Trim() == ".") continue;
            var addr = ts.Blockset + local * 16 + (layerIndex * 4 + i) * 2;
            model.WriteMultiByteValue(addr, 2, s.Token, ParseTileEntry(entries[i]));
         }
      }

      // behavior and layer
      if (!string.IsNullOrWhiteSpace(behavior) || !string.IsNullOrWhiteSpace(layer)) {
         int size = AttributeSize(s);
         var addr = ts.Attributes + local * size;
         var bytes = Enumerable.Range(0, size).Select(i => model[addr + i]).ToArray();
         var attr = TileAttribute.Create(bytes);
         if (!string.IsNullOrWhiteSpace(behavior)) attr.Behavior = ParseBehavior(s, behavior);
         if (!string.IsNullOrWhiteSpace(layer)) {
            var li = Array.FindIndex(LayerNames, n => n.Equals(layer.Trim(), StringComparison.OrdinalIgnoreCase));
            attr.Layer = li >= 0 ? li : throw new McpException("layer must be normal, covered or split.");
         }
         s.Token.ChangeData(model, addr, attr.Serialize());
      }
      s.ViewPort.Refresh();
      var info = BlockInfo(s, m, b);
      if (!ts.Secondary) info["note"] = $"This is a primary-tileset block: it changes on every map whose layout uses this primary tileset ({ts.SharedBy} layout pointer(s) to it).";
      else if (ts.SharedBy > 1) info["note"] = $"This secondary tileset is shared by {ts.SharedBy} layouts; they all change.";
      var render = GraphicsTools.MapView(s, m.Bank, m.Map).BlockRenders[b];
      return GraphicsTools.Result(info, render.PixelData, render.PixelWidth, render.PixelHeight, 8, "");
   });

   [McpServerTool(Name = "list_block_behaviors", ReadOnly = true), Description("The block behavior names this game knows (for write_block), optionally filtered, e.g. 'grass', 'water', 'door', 'ledge'.")]
   public static string ListBlockBehaviors(RomSessions sessions, [Description("Part of the name")] string filter = "") => sessions.With(s => {
      var names = BehaviorNames(s);
      var shown = names.Select((n, i) => (n, i)).Where(x => !string.IsNullOrEmpty(x.n) && (filter.Length == 0 || x.n.Contains(filter, StringComparison.OrdinalIgnoreCase)))
         .Select(x => $"{x.i:X2} {x.n}").ToList();
      return Json.Of(new { total = shown.Count, behaviors = shown });
   });

   // ---------- tiles ----------

   [McpServerTool(Name = "view_tiles", ReadOnly = true), Description(
      "The 8x8 tiles of a map's primary or secondary tileset, drawn with one palette, each with its hex tile id printed under it (the ids write_block uses). " +
      "Blocks combine 8 of these (4 per layer), each with its own palette and flip.")]
   public static CallToolResult ViewTiles(RomSessions sessions,
      [Description("Map id 'bank.map' or unique name")] string map,
      [Description("'primary' or 'secondary'")] string tileset = "secondary",
      [Description("Palette 0-12 to draw every tile with; -1 (default) draws each tile in the palette the map's blocks use it with")] int palette = -1,
      [Description("First tile id (hex ok with 0x)")] string first = "0",
      [Description("How many tiles (up to 512)")] int count = 256,
      [Description("Zoom 1-4")] int scale = 2) => sessions.With(s => {
      var m = FindOne(s, map);
      bool secondary = tileset.Trim().StartsWith("s", StringComparison.OrdinalIgnoreCase);
      var ts = Tileset(s, m, secondary);
      var tiles = ReadTiles(s, ts);
      int baseId = secondary ? PrimaryTiles(s) : 0;
      int start = ParseNumber(first) - baseId;
      if (start < 0) start = ParseNumber(first);
      if (start < 0 || start >= tiles.Count) throw new McpException($"This tileset has tiles {baseId:X3}-{baseId + tiles.Count - 1:X3}.");
      // default: draw each tile with the palette the map's blocks use it with most (unused tiles: the tileset's first palette)
      int fallback = palette >= 0 ? palette : secondary ? PrimaryPalettes(s) : 0;
      var usage = palette >= 0 ? new Dictionary<int, int>() : TilePalettes(s, m);
      var paletteCache = new Dictionary<int, short[]>();
      short[] Colors(int p) => paletteCache.TryGetValue(p, out var c) ? c : paletteCache[p] = PaletteColors(s, m, p);
      int n = Math.Min(tiles.Count - start, Math.Clamp(count, 1, 512));
      const int perRow = 16, cellW = 18, cellH = 16 + Labels.Height + 2;
      int rows = (n + perRow - 1) / perRow, width = perRow * cellW, height = rows * cellH;
      var pixels = Enumerable.Repeat(Png.FromRgb(64, 64, 72), width * height).ToArray();
      for (int k = 0; k < n; k++) {
         var tile = tiles[start + k];
         var tileColors = Colors(usage.TryGetValue(baseId + start + k, out var used) ? used : fallback);
         int ox = (k % perRow) * cellW + 1, oy = (k / perRow) * cellH;
         for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
               pixels[(oy + y) * width + ox + x] = tileColors[tile[x / 2, y / 2] & 15];
         Labels.Draw(pixels, width, ox, oy + 16, (baseId + start + k).ToString("X3"));
      }
      var info = new Dictionary<string, object?> {
         ["map"] = $"{m.Id} {m.Name}",
         ["tileset"] = secondary ? "secondary" : "primary",
         ["tiles"] = $"{baseId:X3}-{baseId + tiles.Count - 1:X3}",
         ["shown"] = $"{baseId + start:X3}-{baseId + start + n - 1:X3}",
         ["palette"] = palette >= 0 ? palette.ToString() : "each tile in the palette the map's blocks use it with most",
         ["layout"] = "16 tiles per row, drawn 2x; each tile's hex id is printed under it",
      };
      return GraphicsTools.Result(info, pixels, width, height, Math.Clamp(scale, 1, 4), "");
   });

   // ---------- helpers ----------

   internal static Dictionary<string, object?> BlockInfo(RomSession s, MapRef m, int b) {
      var (ts, local) = BlockLocation(s, m, b);
      var model = s.Model;
      int size = AttributeSize(s);
      var attrBytes = Enumerable.Range(0, size).Select(i => model[ts.Attributes + local * size + i]).ToArray();
      var attr = TileAttribute.Create(attrBytes);
      var names = BehaviorNames(s);
      string Entry(int v) {
         int tile = v & 0x3FF, pal = v >> 12;
         var flip = ((v >> 10) & 3) switch { 1 => "h", 2 => "v", 3 => "hv", _ => "-" };
         return $"{tile:X3} {pal} {flip}";
      }
      var entries = Enumerable.Range(0, 8).Select(i => Entry(model.ReadMultiByteValue(ts.Blockset + local * 16 + i * 2, 2))).ToList();
      var info = new Dictionary<string, object?> {
         ["map"] = $"{m.Id} {m.Name}",
         ["block"] = b.ToString("X3"),
         ["tileset"] = ts.Secondary ? "secondary" : "primary",
         ["behavior"] = attr.Behavior < names.Count && !string.IsNullOrEmpty(names[attr.Behavior]) ? $"{attr.Behavior:X2} {names[attr.Behavior]}" : attr.Behavior.ToString("X2"),
         ["layer"] = attr.Layer < LayerNames.Length ? LayerNames[attr.Layer] : attr.Layer.ToString(),
         ["bottom"] = entries.Take(4),
         ["top"] = entries.Skip(4),
         ["entry_format"] = "tile(hex) palette flip; order top-left, top-right, bottom-left, bottom-right",
      };
      if (attr is AttributeFRLG fr) { info["terrain"] = fr.Terrain; info["encounter"] = fr.Encounter; }
      return info;
   }

   /// <summary>Tile id -> the palette this map's blocks (primary and secondary) use it with most.</summary>
   static Dictionary<int, int> TilePalettes(RomSession s, MapRef m) {
      var counts = new Dictionary<int, Dictionary<int, int>>();
      int total = GraphicsTools.MapView(s, m.Bank, m.Map).BlockRenders.Count, primary = PrimaryBlocks(s);
      foreach (var secondary in new[] { false, true }) {
         var ts = Tileset(s, m, secondary);
         int blocks = secondary ? total - primary : Math.Min(primary, total);
         for (int b = 0; b < blocks; b++) {
            for (int e = 0; e < 8; e++) {
               var v = s.Model.ReadMultiByteValue(ts.Blockset + b * 16 + e * 2, 2);
               int tile = v & 0x3FF, pal = v >> 12;
               if (!counts.TryGetValue(tile, out var c)) counts[tile] = c = new();
               c[pal] = c.GetValueOrDefault(pal) + 1;
            }
         }
      }
      return counts.ToDictionary(kv => kv.Key, kv => kv.Value.OrderByDescending(x => x.Value).First().Key);
   }

   internal static IReadOnlyList<string> BehaviorNames(RomSession s) =>
      s.Model.TryGetList("MapAttributeBehaviors", out var list) ? list.ToList() : Array.Empty<string>();

   static int ParseBehavior(RomSession s, string text) {
      var t = text.Trim();
      var names = BehaviorNames(s);
      static string N(string x) {
         var u = x.Trim().ToUpperInvariant().Replace(' ', '_').Replace('-', '_');
         return u.StartsWith("MB_") ? u[3..] : u;
      }
      var i = names.ToList().FindIndex(n => !string.IsNullOrEmpty(n) && N(n) == N(t));
      if (i >= 0) return i;
      if (int.TryParse(t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t, System.Globalization.NumberStyles.HexNumber, null, out var v) && v is >= 0 and <= 0xFF) return v;
      var close = names.Where(n => !string.IsNullOrEmpty(n) && N(n).Contains(N(t))).Take(8).ToList();
      throw new McpException($"Unknown behavior '{text}'." + (close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : " Use list_block_behaviors."));
   }

   static int ParseTileEntry(string text) {
      var parts = text.Trim().Split(new[] { ' ', ':', ',' }, StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length < 2) throw new McpException($"'{text}' needs a tile id and a palette, e.g. '1A3 7' or '1A3 7 h'.");
      if (!int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var tile) || tile is < 0 or > 0x3FF) throw new McpException($"'{parts[0]}' isn't a tile id (hex 000-3FF).");
      var palText = parts[1].TrimStart('p', 'P');
      if (!int.TryParse(palText, out var pal) || pal is < 0 or > 12) throw new McpException($"'{parts[1]}' isn't a palette (0-12).");
      int flip = parts.Length > 2 ? parts[2].ToLowerInvariant() switch { "h" => 1, "v" => 2, "hv" or "vh" => 3, "-" => 0, _ => throw new McpException($"'{parts[2]}' isn't a flip (h, v, hv or -).") } : 0;
      return tile | (flip << 10) | (pal << 12);
   }

   static int ParseBlock(string text) {
      var t = text.Trim();
      if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
      return int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : throw new McpException($"'{text}' isn't a block id (hex).");
   }

   static int ParseNumber(string text) {
      var t = text.Trim();
      if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return int.TryParse(t[2..], System.Globalization.NumberStyles.HexNumber, null, out var h) ? h : -1;
      return int.TryParse(t, out var d) ? d : -1;
   }

   /// <summary>A tileset's 8x8 tiles as [x, y] color indices (4bpp).</summary>
   internal static List<int[,]> ReadTiles(RomSession s, TilesetRef ts) {
      byte[]? data;
      if (ts.Compressed) data = LZRun.Decompress(s.Model, ts.Tileset);
      else {
         var run = s.Model.GetNextRun(ts.Tileset);
         int length = run.Start == ts.Tileset && run.Length > 1 ? run.Length : (ts.Secondary ? 0x180 : PrimaryTiles(s)) * 32;
         data = Enumerable.Range(0, Math.Min(length, s.Model.Count - ts.Tileset)).Select(i => s.Model[ts.Tileset + i]).ToArray();
      }
      if (data == null) throw new McpException($"The tileset at {ts.Tileset.ToAddress()} couldn't be read.");
      var tiles = new List<int[,]>();
      for (int t = 0; t + 32 <= data.Length; t += 32) {
         var tile = new int[8, 8];
         for (int i = 0; i < 32; i++) {
            tile[(i % 4) * 2, i / 4] = data[t + i] & 15;
            tile[(i % 4) * 2 + 1, i / 4] = data[t + i] >> 4;
         }
         tiles.Add(tile);
      }
      return tiles;
   }

   /// <summary>Palette page 0-12 as the game assembles it for this map: low pages from the primary tileset, the rest from the secondary.</summary>
   internal static short[] PaletteColors(RomSession s, MapRef m, int page) {
      if (page is < 0 or > 12) throw new McpException("palette must be 0 to 12.");
      var ts = Tileset(s, m, page >= PrimaryPalettes(s));
      if (s.Model.GetNextRun(ts.Palettes) is IPaletteRun run && run.Start == ts.Palettes && page < run.Pages)
         return run.GetPalette(s.Model, page).ToArray();
      // not formatted as a palette: read the raw GBA colors (16 per page)
      return Enumerable.Range(0, 16).Select(i => {
         var v = s.Model.ReadMultiByteValue(ts.Palettes + page * 32 + i * 2, 2);
         return Png.FromRgb((v & 31) << 3, ((v >> 5) & 31) << 3, ((v >> 10) & 31) << 3);
      }).ToArray();
   }
}
