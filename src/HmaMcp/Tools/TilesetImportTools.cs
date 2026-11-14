using System.ComponentModel;
using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.ViewModels.Map;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static HmaMcp.Tools.DataTools;
using static HmaMcp.Tools.MapTools;

namespace HmaMcp.Tools;

/// <summary>One rectangle of the source image to turn into blocks.</summary>
public sealed record TilesetPart {
   [Description("Left edge in pixels (after downscale); usually a multiple of 16 on a tileset grid")] public int x { get; init; }
   [Description("Top edge in pixels")] public int y { get; init; }
   [Description("Width in pixels; rounded up to 16 with transparency")] public int width { get; init; }
   [Description("Height in pixels; rounded up to 16 with transparency")] public int height { get; init; }
   [Description("'ground': solid blocks (bottom layer; any transparent pixels are filled). 'object': drawn on the top layer, with transparency, over the 'under' block")] public string layer { get; init; } = "ground";
   [Description("object parts: what to draw them over: a primary-tileset block id (hex, e.g. FireRed grass 00D), or 'part:N' for the most common block of imported ground part N; default: the map's most used block")] public string under { get; init; } = "";
   [Description("Behavior for these blocks (e.g. Tall_Grass, a water behavior); default Normal")] public string behavior { get; init; } = "";
   [Description("Optional name to report this part by")] public string name { get; init; } = "";
   [Description("A different .png for this part (default: the tool's file), so one tileset can mix several images")] public string file { get; init; } = "";
}

/// <summary>
/// Build a map's secondary tileset from an image: cut into 16x16 blocks, colors fitted to the GBA's palettes,
/// duplicate 8x8 tiles (including flipped copies) shared, written to free space, and the map switched to it.
/// </summary>
[McpServerToolType]
public static class TilesetImportTools {
   [McpServerTool(Name = "import_tileset", Destructive = true), Description(
      "Give a map a new secondary tileset made from parts of a PNG (e.g. a top-down 16x16 tileset): each 16x16 cell of each part becomes a block " +
      "(empty cells are skipped, identical cells share a block). 'ground' parts are solid; 'object' parts (trees, rocks, ...) go on the top layer, with " +
      "transparency, over a ground block. Colors are fitted to the GBA's limits (FireRed: 6 palettes, Emerald: 7, of 15 colors each) and duplicate " +
      "8x8 tiles, including mirrored ones, are shared; the result says how close it is to the limits (FireRed: 384 tiles and 384 blocks, Emerald: 512 each). " +
      "The map's primary tileset (grass, paths, ...) stays usable. Returns each part's new block ids as rows ready for paint_map_blocks, and an image of " +
      "the new blocks. If other maps share this map's layout, it gets its own copy first. Best used on a map made with create_map. One undo step.")]
   public static CallToolResult ImportTileset(RomSessions sessions,
      [Description("Map id 'bank.map' or unique name")] string map,
      [Description("Path to a .png (parts can also name their own file)")] string file,
      [Description("Rectangles of the image to import, e.g. [{x:0, y:0, width:128, height:64, layer:'ground'}, {x:0, y:64, width:32, height:32, layer:'object'}]; omit to import the whole image (one object, if it has transparency)")] TilesetPart[]? parts = null,
      [Description("1 for 16x16-pixel art, 2 to halve 32x32-pixel art")] int downscale = 1,
      [Description("A color to treat as transparent, #RRGGBB (for images without transparency)")] string transparent_color = "") => sessions.Edit(s => {
      var m = FindOne(s, map);
      if (downscale is not (1 or 2)) throw new McpException("downscale must be 1 or 2.");
      // each part can come from its own file; images are loaded once (downscaled, with their transparency mask)
      var images = new Dictionary<string, (Png.Image image, bool[] clear)>(StringComparer.OrdinalIgnoreCase);
      (Png.Image image, bool[] clear) Load(string? partFile) {
         var path = Path.GetFullPath(string.IsNullOrWhiteSpace(partFile) ? file ?? "" : partFile);
         if (images.TryGetValue(path, out var loaded)) return loaded;
         if (!File.Exists(path)) throw new McpException($"File not found: {path}");
         var img = Png.Decode(File.ReadAllBytes(path));
         if (downscale == 2) img = Halve(img);
         var mask = img.Transparent ?? new bool[img.Width * img.Height];
         if (!string.IsNullOrWhiteSpace(transparent_color)) {
            var key = Png.ParseHex(transparent_color);
            for (int i = 0; i < mask.Length; i++) if (img.Pixels[i] == key) mask[i] = true;
         }
         return images[path] = (img, mask);
      }
      // no parts: the whole image, as an object if it has transparency (e.g. a single house or tree image), else as ground
      if (parts == null || parts.Length == 0) {
         var (whole, wholeClear) = Load(file);
         parts = new[] { new TilesetPart { x = 0, y = 0, width = whole.Width, height = whole.Height, layer = wholeClear.Any(c => c) ? "object" : "ground" } };
      }

      // game limits
      int primaryBlocks = BlockTools.PrimaryBlocks(s), primaryTiles = BlockTools.PrimaryTiles(s), paletteBase = BlockTools.PrimaryPalettes(s);
      int tileCapacity = 0x400 - primaryTiles, blockCapacity = 0x400 - primaryBlocks, paletteCapacity = 13 - paletteBase;

      // the block objects are drawn over, by default the map's most used block (must be in the primary tileset, which stays)
      var (mw, mh, blockmap) = MapBlockmap(s, m);
      int mostUsed = Enumerable.Range(0, mw * mh).Select(i => s.Model.ReadMultiByteValue(blockmap + i * 2, 2) & 0x3FF).GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;

      // 1. cut the parts into cells and 8x8 tiles
      var cells = new List<Cell>();
      for (int p = 0; p < parts.Length; p++) {
         var part = parts[p];
         var (image, clear) = Load(part.file);
         if (part.width <= 0 || part.height <= 0 || part.x < 0 || part.y < 0 || part.x >= image.Width || part.y >= image.Height)
            throw new McpException($"Part {p} ({part.x},{part.y} {part.width}x{part.height}) must start inside the image ({image.Width}x{image.Height}{(downscale == 2 ? " after downscale" : "")}) and have a size.");
         bool isObject = part.layer.Trim().Equals("object", StringComparison.OrdinalIgnoreCase);
         if (!isObject && !part.layer.Trim().Equals("ground", StringComparison.OrdinalIgnoreCase)) throw new McpException($"Part {p}: layer must be 'ground' or 'object'.");
         int under = -1;
         if (isObject) {
            var u = part.under.Trim();
            if (u.StartsWith("part:", StringComparison.OrdinalIgnoreCase)) {
               // over an imported ground part: encoded as -(index+2), resolved once its blocks exist
               if (!int.TryParse(u[5..], out var up) || up < 0 || up >= parts.Length || up == p || !parts[up].layer.Trim().Equals("ground", StringComparison.OrdinalIgnoreCase))
                  throw new McpException($"Part {p}: under '{part.under}' must name another ground part, e.g. 'part:0'.");
               under = -(up + 2);
            } else {
               under = string.IsNullOrWhiteSpace(u) ? mostUsed : ParseHex(u, $"part {p} under");
               if (under >= primaryBlocks) throw new McpException($"Part {p}: 'under' block {under:X3} is in the secondary tileset this import replaces; use a primary-tileset block (000-{primaryBlocks - 1:X3}) or an imported ground part ('part:0').");
            }
         }
         // cells cover the part rounded up to 16 px; pixels past the part or the image are transparent
         for (int cy = 0; cy < (part.height + 15) / 16; cy++) {
            for (int cx = 0; cx < (part.width + 15) / 16; cx++) {
               var colors = new short[256];
               var transparent = new bool[256];
               int opaque = 0;
               for (int yy = 0; yy < 16; yy++)
                  for (int xx = 0; xx < 16; xx++) {
                     int px = part.x + cx * 16 + xx, py = part.y + cy * 16 + yy;
                     if (px >= image.Width || py >= image.Height || cx * 16 + xx >= part.width || cy * 16 + yy >= part.height) { transparent[yy * 16 + xx] = true; continue; }
                     int src = py * image.Width + px;
                     colors[yy * 16 + xx] = image.Pixels[src];
                     transparent[yy * 16 + xx] = clear[src];
                     if (!clear[src]) opaque++;
                  }
               if (opaque == 0) continue; // empty cell
               if (!isObject && opaque < 256) {
                  // ground is solid: fill holes with the cell's most common color
                  var fill = colors.Where((c, i) => !transparent[i]).GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;
                  for (int i = 0; i < 256; i++) if (transparent[i]) { colors[i] = fill; transparent[i] = false; }
               }
               cells.Add(new Cell(p, cx, cy, isObject, under, colors, transparent));
            }
         }
      }
      if (cells.Count == 0) throw new McpException("The parts are empty (all transparent).");

      // 2. fit colors: every 8x8 tile uses one palette of up to 15 colors (index 0 is transparent)
      var tiles = cells.SelectMany(c => c.Tiles()).ToList();
      var palettes = FitPalettes(tiles, paletteCapacity);

      // 3. index tiles, share duplicates (and flipped duplicates); a fully transparent top tile is primary tile 0
      var unique = new List<byte[]>();
      var lookup = new Dictionary<string, (int index, int flip)>();
      int TileEntry(Tile t) {
         var (pal, indices) = (t.Palette, t.Indices);
         if (indices.All(i => i == 0)) return 0;
         var key = Key(indices);
         if (!lookup.TryGetValue(key, out var found)) {
            found = (unique.Count, 0);
            unique.Add(indices);
            lookup[key] = found;
            lookup.TryAdd(Key(Flip(indices, true, false)), (found.index, 1));
            lookup.TryAdd(Key(Flip(indices, false, true)), (found.index, 2));
            lookup.TryAdd(Key(Flip(indices, true, true)), (found.index, 3));
         }
         return (primaryTiles + found.index) | (found.flip << 10) | ((paletteBase + pal) << 12);
      }
      foreach (var t in tiles) t.Indices = IndexTile(t, palettes[t.Palette]);

      // 4. blocks: 4 bottom + 4 top entries, plus attributes; identical blocks are shared
      int attrSize = BlockTools.AttributeSize(s);
      var blocks = new List<(int[] entries, int behavior)>();
      var blockIndex = new Dictionary<string, int>();
      var underEntries = new Dictionary<int, int[]>();
      int[] Under(int block) {
         if (underEntries.TryGetValue(block, out var e)) return e;
         var (ts, local) = BlockTools.BlockLocation(s, m, block);
         return underEntries[block] = Enumerable.Range(0, 4).Select(i => s.Model.ReadMultiByteValue(ts.Blockset + local * 16 + i * 2, 2)).ToArray();
      }
      var partBehaviors = parts.Select((part, i) => string.IsNullOrWhiteSpace(part.behavior) ? 0 : ParseBehavior(s, part.behavior, i)).ToArray();
      foreach (var cell in cells) cell.Own = cell.TileList.Select(TileEntry).ToArray();
      // a ground part used as 'under': its most common cell
      int[] UnderPart(int part) => cells.Where(c => c.Part == part && !c.IsObject).GroupBy(c => string.Join(",", c.Own))
         .OrderByDescending(g => g.Count()).First().First().Own;
      foreach (var cell in cells) {
         var own = cell.Own;
         var entries = cell.IsObject ? (cell.Under >= 0 ? Under(cell.Under) : UnderPart(-cell.Under - 2)).Concat(own).ToArray() : own.Concat(new int[4]).ToArray();
         var key = string.Join(",", entries) + "|" + partBehaviors[cell.Part];
         if (!blockIndex.TryGetValue(key, out var id)) {
            id = blocks.Count;
            blocks.Add((entries, partBehaviors[cell.Part]));
            blockIndex[key] = id;
         }
         cell.Block = primaryBlocks + id;
      }
      if (unique.Count > tileCapacity || blocks.Count > blockCapacity)
         throw new McpException($"Too big for one tileset: {unique.Count} unique 8x8 tiles (limit {tileCapacity}) and {blocks.Count} blocks (limit {blockCapacity}). " +
            "Import fewer or smaller parts; repeated textures and mirrored tiles are shared automatically.");

      // 5. write: tiles (LZ-compressed), palettes, blocks, attributes, then a tileset record pointing at them
      var model = s.Model;
      int Write(byte[] data) {
         var at = model.FindFreeSpace(model.FreeSpaceStart, data.Length + 4);
         if (at < 0) throw new McpException("Not enough free space in the ROM.");
         at = (at + 3) / 4 * 4;
         s.Token.ChangeData(model, at, data);
         return at;
      }
      var tileBytes = new byte[unique.Count * 32];
      for (int t = 0; t < unique.Count; t++)
         for (int i = 0; i < 32; i++) {
            int x = (i % 4) * 2, y = i / 4;
            tileBytes[t * 32 + i] = (byte)(unique[t][y * 8 + x] | (unique[t][y * 8 + x + 1] << 4));
         }
      int tilesAt = Write(LZRun.Compress(tileBytes).ToArray());

      var oldSecondary = BlockTools.Tileset(s, m, true);
      var palBytes = new byte[0x200];
      if (oldSecondary.Palettes >= 0) for (int i = 0; i < 0x200; i++) palBytes[i] = model[oldSecondary.Palettes + i];
      for (int p = 0; p < palettes.Count; p++) {
         for (int c = 0; c < 16; c++) {
            short color = c == 0 ? (short)0 : c - 1 < palettes[p].Count ? palettes[p][c - 1] : (short)0;
            int raw = ((color >> 10) & 31) | (((color >> 5) & 31) << 5) | ((color & 31) << 10);
            palBytes[(paletteBase + p) * 32 + c * 2] = (byte)raw;
            palBytes[(paletteBase + p) * 32 + c * 2 + 1] = (byte)(raw >> 8);
         }
      }
      int palAt = Write(palBytes);

      var blockBytes = new byte[blocks.Count * 16];
      var attrBytes = new byte[blocks.Count * attrSize];
      for (int b = 0; b < blocks.Count; b++) {
         for (int e = 0; e < 8; e++) { blockBytes[b * 16 + e * 2] = (byte)blocks[b].entries[e]; blockBytes[b * 16 + e * 2 + 1] = (byte)(blocks[b].entries[e] >> 8); }
         var attr = TileAttribute.Create(new byte[attrSize]);
         attr.Behavior = blocks[b].behavior;
         attr.Serialize().CopyTo(attrBytes, b * attrSize);
      }
      int blocksAt = Write(blockBytes);
      int attrsAt = Write(attrBytes);

      // the map gets its own layout first if others share it, so only this map changes
      var view = GraphicsTools.MapView(s, m.Bank, m.Map);
      var header = TableAt(s, m.Header)!;
      var layoutRun = Sub(s, header, 0, "layout")!;
      string? layoutNote = null;
      if ((layoutRun.PointerSources?.Count ?? 0) > 1 && view.MapRepointer.RepointLayout.CanExecute(null)) {
         view.MapRepointer.RepointLayout.Execute(null);
         layoutNote = $"Other maps shared this map's layout, so {m.Id} got its own copy first.";
         header = TableAt(s, m.Header)!;
         layoutRun = Sub(s, header, 0, "layout")!;
      }
      var recordRun = Sub(s, layoutRun, 0, "blockdata2") ?? throw new McpException($"Map {m.Id} has no secondary tileset record to model the new one on.");
      var record = new byte[recordRun.ElementLength];
      for (int i = 0; i < record.Length; i++) record[i] = model[recordRun.Start + i];
      int recordAt = Write(record);
      var recordFields = Fields(recordRun);
      void Set(string name, int value) { var f = recordFields.First(x => x.Key == name); model.WriteMultiByteValue(recordAt + f.Offset, f.Seg.Length, s.Token, value); }
      void Point(string name, int dest) { var f = recordFields.First(x => x.Key == name); model.WritePointer(s.Token, recordAt + f.Offset, dest); }
      Set("isCompressed", 1);
      Set("isSecondary", 1);
      Point("tileset", tilesAt);
      Point("pal", palAt);
      Point("blockset", blocksAt);
      Point("attributes", attrsAt);
      if (recordFields.Any(x => x.Key == "animation")) model.WriteMultiByteValue(recordAt + recordFields.First(x => x.Key == "animation").Offset, 4, s.Token, 0);
      WriteField(s, layoutRun, 0, Fields(layoutRun).First(x => x.Key == "blockdata2"), "@" + recordAt.ToAddress());
      s.ViewPort.Refresh();

      // 6. report: each part's block ids as rows for paint_map_blocks ('.' = an empty cell)
      var partResults = parts.Select((part, p) => {
         var rows = Enumerable.Range(0, (part.height + 15) / 16).Select(cy => string.Join(" ", Enumerable.Range(0, (part.width + 15) / 16).Select(cx =>
            cells.FirstOrDefault(c => c.Part == p && c.X == cx && c.Y == cy)?.Block.ToString("X3") ?? "."))).ToList();
         return new { part = string.IsNullOrWhiteSpace(part.name) ? p.ToString() : part.name, part.layer, blocks = rows };
      }).ToList();
      var info = new Dictionary<string, object?> {
         ["map"] = $"{m.Id} {m.Name}",
         ["tileset"] = recordAt.ToAddress(),
         ["tiles"] = $"{unique.Count} of {tileCapacity}",
         ["blocks"] = $"{blocks.Count} of {blockCapacity} ({primaryBlocks:X3}-{primaryBlocks + blocks.Count - 1:X3})",
         ["palettes"] = $"{palettes.Count} of {paletteCapacity} (pages {paletteBase}-{paletteBase + palettes.Count - 1})",
         ["parts"] = partResults,
         ["layout_note"] = layoutNote,
         ["note"] = "Blocks from the map's old secondary tileset are replaced; repaint any of them on this map. New blocks are walkable unless you set movement with paint_map_blocks.",
      };
      return MapEditTools.BlocksetImage(s, m, primaryBlocks, blocks.Count, 2, info);
   });

   // ---------- color fitting ----------

   sealed class Cell {
      public int Part, X, Y, Under, Block;
      public int[] Own = Array.Empty<int>();
      public bool IsObject;
      public List<Tile> TileList = new();
      public Cell(int part, int x, int y, bool isObject, int under, short[] colors, bool[] transparent) {
         (Part, X, Y, IsObject, Under) = (part, x, y, isObject, under);
         for (int ty = 0; ty < 2; ty++)
            for (int tx = 0; tx < 2; tx++) {
               var c = new short[64]; var t = new bool[64];
               for (int py = 0; py < 8; py++)
                  for (int px = 0; px < 8; px++) {
                     c[py * 8 + px] = colors[(ty * 8 + py) * 16 + tx * 8 + px];
                     t[py * 8 + px] = transparent[(ty * 8 + py) * 16 + tx * 8 + px];
                  }
               TileList.Add(new Tile(c, t));
            }
      }
      public IEnumerable<Tile> Tiles() => TileList;
   }

   sealed class Tile {
      public short[] Colors; public bool[] Transparent; public int Palette; public byte[] Indices = Array.Empty<byte>();
      public Tile(short[] colors, bool[] transparent) => (Colors, Transparent) = (colors, transparent);
      public Dictionary<short, int> Counts() {
         var d = new Dictionary<short, int>();
         for (int i = 0; i < 64; i++) if (!Transparent[i]) d[Colors[i]] = d.GetValueOrDefault(Colors[i]) + 1;
         return d;
      }
   }

   static int Dist(short a, short b) {
      int dr = ((a >> 10) & 31) - ((b >> 10) & 31), dg = ((a >> 5) & 31) - ((b >> 5) & 31), db = (a & 31) - (b & 31);
      return dr * dr * 3 + dg * dg * 4 + db * db * 2;
   }

   /// <summary>Merge the closest colors (weighted by use) until at most max remain.</summary>
   static Dictionary<short, int> Reduce(Dictionary<short, int> colors, int max) {
      var list = colors.Select(kv => (color: kv.Key, weight: kv.Value)).ToList();
      while (list.Count > max) {
         int bi = 0, bj = 1; long best = long.MaxValue;
         for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++) {
               long cost = (long)Dist(list[i].color, list[j].color) * Math.Min(list[i].weight, list[j].weight);
               if (cost < best) (best, bi, bj) = (cost, i, j);
            }
         var (a, b) = (list[bi], list[bj]);
         int w = a.weight + b.weight;
         int Mix(int shift) => (((a.color >> shift) & 31) * a.weight + ((b.color >> shift) & 31) * b.weight + w / 2) / w;
         var merged = (short)((Mix(10) << 10) | (Mix(5) << 5) | Mix(0));
         list.RemoveAt(bj);
         list[bi] = (merged, w);
      }
      var result = new Dictionary<short, int>();
      foreach (var (c, w) in list) result[c] = result.GetValueOrDefault(c) + w;
      return result;
   }

   /// <summary>Group tiles into at most maxPalettes palettes of 15 colors, then give each tile the palette that draws it best.</summary>
   static List<List<short>> FitPalettes(List<Tile> tiles, int maxPalettes) {
      var sets = tiles.Select(t => Reduce(t.Counts(), 15)).ToList();
      var groups = new List<Dictionary<short, int>>();
      foreach (var i in Enumerable.Range(0, tiles.Count).OrderByDescending(i => sets[i].Count)) {
         var set = sets[i];
         if (set.Count == 0) continue;
         int best = -1, bestGrowth = int.MaxValue;
         for (int g = 0; g < groups.Count; g++) {
            int growth = set.Keys.Count(c => !groups[g].ContainsKey(c));
            if (groups[g].Count + growth <= 15 && growth < bestGrowth) (best, bestGrowth) = (g, growth);
         }
         if (best < 0 && groups.Count < maxPalettes) { groups.Add(new()); best = groups.Count - 1; }
         if (best < 0) best = Enumerable.Range(0, groups.Count).OrderBy(g => set.Keys.Count(c => !groups[g].ContainsKey(c))).First();
         foreach (var (c, w) in set) groups[best][c] = groups[best].GetValueOrDefault(c) + w;
      }
      if (groups.Count == 0) groups.Add(new() { [0] = 1 });
      var palettes = groups.Select(g => Reduce(g, 15).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList()).ToList();
      foreach (var t in tiles) {
         long Error(List<short> pal) {
            long e = 0;
            for (int i = 0; i < 64; i++) if (!t.Transparent[i]) e += pal.Min(c => Dist(c, t.Colors[i]));
            return e;
         }
         t.Palette = Enumerable.Range(0, palettes.Count).OrderBy(p => Error(palettes[p])).First();
      }
      return palettes;
   }

   static byte[] IndexTile(Tile t, List<short> palette) {
      var result = new byte[64];
      for (int i = 0; i < 64; i++) {
         if (t.Transparent[i]) continue;
         int best = 0, bestDist = int.MaxValue;
         for (int c = 0; c < palette.Count; c++) { int d = Dist(palette[c], t.Colors[i]); if (d < bestDist) (best, bestDist) = (c, d); }
         result[i] = (byte)(best + 1);
      }
      return result;
   }

   static string Key(byte[] indices) => Convert.ToHexString(indices);

   static byte[] Flip(byte[] tile, bool h, bool v) {
      var result = new byte[64];
      for (int y = 0; y < 8; y++)
         for (int x = 0; x < 8; x++) result[y * 8 + x] = tile[(v ? 7 - y : y) * 8 + (h ? 7 - x : x)];
      return result;
   }

   // ---------- small helpers ----------

   static Png.Image Halve(Png.Image image) {
      int w = image.Width / 2, h = image.Height / 2;
      var pixels = new short[w * h];
      var clear = new bool[w * h];
      for (int y = 0; y < h; y++)
         for (int x = 0; x < w; x++) {
            var src = new[] { (2 * y) * image.Width + 2 * x, (2 * y) * image.Width + 2 * x + 1, (2 * y + 1) * image.Width + 2 * x, (2 * y + 1) * image.Width + 2 * x + 1 };
            var opaque = src.Where(i => image.Transparent == null || !image.Transparent[i]).ToList();
            if (opaque.Count < 2) { clear[y * w + x] = true; continue; }
            int Avg(int shift) => opaque.Sum(i => (image.Pixels[i] >> shift) & 31) / opaque.Count;
            pixels[y * w + x] = (short)((Avg(10) << 10) | (Avg(5) << 5) | Avg(0));
         }
      return new Png.Image(w, h, pixels, null, null, clear);
   }

   static (int width, int height, int blockmap) MapBlockmap(RomSession s, MapRef m) {
      var header = TableAt(s, m.Header)!;
      var layout = Sub(s, header, 0, "layout") ?? throw new McpException($"Map {m.Id} has no layout.");
      return (Convert.ToInt32(Value(s, layout, 0, "width")), Convert.ToInt32(Value(s, layout, 0, "height")), Pointer(s, layout, 0, "blockmap"));
   }

   static int ParseHex(string text, string what) {
      var t = text.Trim();
      if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
      return int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v) && v is >= 0 and < 0x400 ? v : throw new McpException($"'{text}' isn't a valid {what} (hex block id).");
   }

   static int ParseBehavior(RomSession s, string text, int part) {
      static string N(string x) { var u = x.Trim().ToUpperInvariant().Replace(' ', '_'); return u.StartsWith("MB_") ? u[3..] : u; }
      var names = BlockTools.BehaviorNames(s).ToList();
      var i = names.FindIndex(n => !string.IsNullOrEmpty(n) && N(n) == N(text));
      if (i >= 0) return i;
      throw new McpException($"Part {part}: unknown behavior '{text}'. Use list_block_behaviors.");
   }
}
