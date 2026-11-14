using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Map;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.ViewModels.Map;
using HavenSoft.HexManiac.Core.ViewModels.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HmaMcp.Tools;

/// <summary>
/// Sprites, tilesets, tilemaps, palettes and map pictures, through HMA's sprite tool and map renderer.
/// Images come back as PNG content the assistant can see, and can also be saved to a file.
/// </summary>
[McpServerToolType]
public static class GraphicsTools {
   // ---------- view_image ----------

   [McpServerTool(Name = "view_image", ReadOnly = true), Description(
      "Show a sprite, tileset, tilemap or palette as a PNG image (returned so you can see it), drawn with its palette the way HMA shows it. " +
      "Find graphics with list_anchors (e.g. filter 'graphics.pokemon.sprites.front', 'graphics.trainers', 'graphics.overworld'); " +
      "for a table of sprites (one per Pokémon/trainer), pass the row's pointer address from read_table, or the anchor plus row. " +
      "save_to writes the image at 1x as a PNG file (indexed with the palette when possible), ready to edit and bring back with import_image.")]
   public static CallToolResult ViewImage(RomSessions sessions,
      [Description("Address or anchor of the graphic, or of a table of graphics (then also pass row)")] string address,
      [Description("Row of a graphics table: number or name (e.g. 'CHARMANDER'). Ignored for a single graphic.")] string row = "",
      [Description("Sprite page/frame (overworld frames, front/back pages)")] int page = 0,
      [Description("Palette address or anchor (a palette table uses the same row, e.g. graphics.pokemon.palettes.shiny); empty picks the palette HMA pairs with this graphic")] string palette = "",
      [Description("Palette page (e.g. 1 for a shiny palette when the palette has pages)")] int palette_page = 0,
      [Description("Preview zoom 1-8; 0 picks one so small sprites are easy to see")] int scale = 0,
      [Description("Optional file path to save the image (1x PNG)")] string save_to = "") => sessions.With(s => {
      var start = ResolveGraphic(s, address, row);
      if (s.Model.GetNextRun(start) is IPaletteRun palRun && palRun.Start == start) return PaletteImage(s, palRun, scale, save_to);
      var tool = Prepare(s, start, page, palette, palette_page, row);
      return SpriteImage(s, tool, scale, save_to);
   });

   [McpServerTool(Name = "view_overworld_sprites", ReadOnly = true), Description(
      "A sheet of overworld (NPC) sprites, first frame each, with each sprite's number printed under it: the same number as an NPC's " +
      "'graphics' field in map_events / add_map_event. Use it to pick how a new NPC looks.")]
   public static CallToolResult ViewOverworldSprites(RomSessions sessions,
      [Description("First sprite number")] int first = 0,
      [Description("How many to show (up to 80)")] int count = 40,
      [Description("Zoom 1-4")] int scale = 2) => sessions.With(s => {
      const string table = "graphics.overworld.sprites";
      var run = DataTools.GetTable(s, table);
      if (first < 0 || first >= run.ElementCount) throw new McpException($"first must be 0 to {run.ElementCount - 1}.");
      int last = Math.Min(run.ElementCount, first + Math.Clamp(count, 1, 80));
      var sprites = new List<(int index, short[]? pixels, int w, int h)>();
      for (int i = first; i < last; i++) {
         try {
            var tool = Prepare(s, ResolveGraphic(s, table, i.ToString()), 0, "", 0);
            sprites.Add((i, tool.PixelData?.ToArray(), tool.PixelWidth, tool.PixelHeight));
         } catch (McpException) {
            sprites.Add((i, null, 0, 0));
         }
      }
      int cellW = Math.Clamp(sprites.Max(x => x.w), 16, 64) + 4, spriteH = Math.Clamp(sprites.Max(x => x.h), 16, 64);
      int cellH = spriteH + Labels.Height + 3, perRow = 8, rows = (sprites.Count + perRow - 1) / perRow;
      int width = perRow * cellW, height = rows * cellH;
      var pixels = Enumerable.Repeat(Png.FromRgb(64, 64, 72), width * height).ToArray();
      for (int n = 0; n < sprites.Count; n++) {
         var (index, px, w, h) = sprites[n];
         int ox = (n % perRow) * cellW, oy = (n / perRow) * cellH;
         if (px != null) {
            int dx = ox + (cellW - Math.Min(w, cellW)) / 2, dy = oy + 1 + Math.Max(0, spriteH - h);
            for (int y = 0; y < Math.Min(h, spriteH); y++)
               for (int x = 0; x < Math.Min(w, cellW); x++)
                  pixels[(dy + y) * width + dx + x] = px[y * w + x];
         }
         Labels.Draw(pixels, width, ox + 2, oy + spriteH + 2, index.ToString());
      }
      var info = new Dictionary<string, object?> {
         ["table"] = table,
         ["shown"] = $"{first}-{last - 1} of {run.ElementCount}",
         ["layout"] = "8 per row; the number under each sprite is its graphics number (decimal)",
         ["missing"] = sprites.Where(x => x.pixels == null).Select(x => x.index).ToList() is { Count: > 0 } miss ? miss : null,
      };
      return Result(info, pixels, width, height, Math.Clamp(scale, 1, 4), "");
   });

   // ---------- palettes ----------

   [McpServerTool(Name = "read_palette", ReadOnly = true), Description("A palette's colors as #RRGGBB (GBA colors have 5 bits per channel, so each value is a multiple of 8 plus its top bits). Color 0 is transparent for sprites. Also lists the graphics that use it.")]
   public static string ReadPalette(RomSessions sessions,
      [Description("Palette address or anchor, or a sprite's address (its paired palette is read)")] string address,
      [Description("Row of a palette table (number or name)")] string row = "",
      [Description("Page, for palettes with several 16-color pages")] int page = 0) => sessions.With(s => {
      var run = FindPalette(s, ResolveGraphic(s, address, row));
      CheckPage(page, run.Pages, "page");
      var colors = run.GetPalette(s.Model, page);
      return Json.Of(new {
         address = run.Start.ToAddress(),
         anchor = s.Anchor(run.Start),
         pages = run.Pages,
         page,
         colors = colors.Select(Png.ToHex),
         used_by = run.FindDependentSprites(s.Model).Take(20).Select(d => new { address = d.Start.ToAddress(), anchor = s.Anchor(d.Start) }),
      });
   });

   [McpServerTool(Name = "write_palette"), Description("Replace a palette page's colors (#RRGGBB each; values are rounded to the GBA's 5 bits per channel). Pass all 16 colors, or fewer to change only the first ones. Every graphic using this palette changes. One undo step.")]
   public static string WritePalette(RomSessions sessions,
      [Description("Palette address or anchor, or a sprite's address (its paired palette is written)")] string address,
      [Description("Colors, e.g. ['#000000', '#F8F8F8', ...]")] string[] colors,
      [Description("Row of a palette table (number or name)")] string row = "",
      [Description("Page, for palettes with several 16-color pages")] int page = 0) => sessions.Edit(s => {
      var run = FindPalette(s, ResolveGraphic(s, address, row));
      CheckPage(page, run.Pages, "page");
      var old = run.GetPalette(s.Model, page).ToArray();
      if (colors == null || colors.Length == 0) throw new McpException("Pass at least one color.");
      if (colors.Length > old.Length) throw new McpException($"This palette page has {old.Length} colors; got {colors.Length}.");
      var updated = old.ToArray();
      for (int i = 0; i < colors.Length; i++) updated[i] = Png.ParseHex(colors[i]);
      var after = run.SetPalette(s.Model, s.Token, page, updated);
      s.ViewPort.Refresh();
      return Json.Of(new {
         address = after.Start.ToAddress(),
         moved_from = after.Start != run.Start ? run.Start.ToAddress() : null,
         before = old.Select(Png.ToHex),
         after = after.GetPalette(s.Model, page).Select(Png.ToHex),
      });
   });

   // ---------- import_image ----------

   [McpServerTool(Name = "import_image"), Description(
      "Replace a sprite, tileset or tilemap with a PNG file, the way HMA's image import does. The image must be the graphic's size " +
      "(see view_image), or all its pages side by side / stacked. Compressed graphics that grow are moved and repointed. " +
      "Palette handling when other graphics share the palette: 'smart' (default) balances the palette for all of them, " +
      "'greedy' rewrites the palette for this image (others may look wrong), 'cautious' keeps the palette and matches colors to it. " +
      "An indexed PNG (e.g. one saved by view_image) keeps its exact color indices. One undo step; returns the result as an image.")]
   public static CallToolResult ImportImage(RomSessions sessions,
      [Description("Address or anchor of the graphic, or of a table of graphics (then also pass row)")] string address,
      [Description("Path to a .png file")] string file,
      [Description("Row of a graphics table: number or name")] string row = "",
      [Description("'smart', 'greedy' or 'cautious'")] string mode = "smart",
      [Description("Sprite page to replace, for multi-page sprites when the image is one page")] int page = 0,
      [Description("Palette address or anchor; empty uses the palette HMA pairs with this graphic")] string palette = "",
      [Description("Palette page")] int palette_page = 0) => sessions.Edit(s => {
      if (!Enum.TryParse<ImportType>(mode, true, out var importType) || importType == ImportType.Unknown)
         throw new McpException($"mode must be smart, greedy or cautious, not '{mode}'.");
      var path = Path.GetFullPath(file ?? "");
      if (!File.Exists(path)) throw new McpException($"File not found: {path}");
      var image = Png.Decode(File.ReadAllBytes(path));

      var start = ResolveGraphic(s, address, row);
      var tool = Prepare(s, start, page, palette, palette_page, row);
      var spriteBefore = tool.SpriteAddress;
      // HMA's sprite tool doesn't always follow a graphic it moved, so find it again afterwards through a pointer to it.
      var spriteSource = s.Model.GetNextRun(spriteBefore).PointerSources?.FirstOrDefault(-1) ?? -1;
      var paletteSource = tool.PaletteAddress >= 0 ? s.Model.GetNextRun(tool.PaletteAddress).PointerSources?.FirstOrDefault(-1) ?? -1 : -1;
      var paletteBefore = tool.PaletteAddress;
      var colorsBefore = s.Model.GetNextRun(paletteBefore) is IPaletteRun pb ? pb.AllColors(s.Model).ToArray() : Array.Empty<short>();
      var modeUsed = importType.ToString().ToLowerInvariant();

      // Same size as one page: skip an import that changes nothing (HMA would still recompress, and maybe move, the sprite,
      // and 'smart' would rebalance a shared palette). An indexed PNG that uses this palette as-is is written index for index.
      var spriteRun = (ISpriteRun)s.Model.GetNextRun(start);
      var paletteForPage = s.Model.GetNextRun(tool.PaletteAddress) is IPaletteRun pp && pp.Start == tool.PaletteAddress
         ? (spriteRun.SpriteFormat.BitsPerPixel == 8 ? pp.AllColors(s.Model) : pp.GetPalette(s.Model, tool.PalettePage)) : null;
      if (image.Width == tool.PixelWidth && image.Height == tool.PixelHeight) {
         bool unchanged = false;
         if (spriteRun is ITilemapRun) {
            // tilemap pixels span several palette pages: compare what's drawn
            unchanged = tool.PixelData != null && tool.PixelData.SequenceEqual(image.Pixels);
         } else if (image.Indices != null && image.Palette != null && paletteForPage != null
            && image.Palette.Length <= paletteForPage.Count && image.Palette.SequenceEqual(paletteForPage.Take(image.Palette.Length))) {
            var current = spriteRun.GetPixels(s.Model, tool.SpritePage, -1);
            unchanged = current != null && current.Cast<int>().SequenceEqual(image.Indices.Cast<int>());
            importType = ImportType.Greedy;
            modeUsed = "exact indices (the PNG uses this palette unchanged)";
         } else if (image.Indices == null && tool.PixelData != null) {
            unchanged = tool.PixelData.SequenceEqual(image.Pixels);
         }
         if (unchanged) {
            return SpriteImage(s, tool, 0, "", new Dictionary<string, object?> {
               ["imported"] = path,
               ["changed"] = false,
               ["note"] = "The image is identical to what's in the ROM; nothing was written.",
            });
         }
      }

      var errors = new List<string>();
      void OnError(object? sender, string e) => errors.Add(e);
      s.ViewPort.OnError += OnError;
      try {
         var fs = FileSystemFor(s);
         var paletteRun = s.Model.GetNextRun(tool.PaletteAddress) as IPaletteRun;
         if (image.Indices != null && image.Palette != null && paletteRun != null && image.Palette.Length <= paletteRun.Pages * 16)
            ImportIndexed.Invoke(tool, new object[] { fs, image.Indices, image.Palette, importType });
         else
            ImportColors.Invoke(tool, new object[] { fs, image.Pixels, image.Width, importType });
      } catch (TargetInvocationException ex) when (ex.InnerException != null) {
         throw new McpException($"HMA couldn't import the image: {ex.InnerException.Message}");
      } finally {
         s.ViewPort.OnError -= OnError;
      }
      if (errors.Count > 0) throw new McpException(string.Join(" ", errors));

      s.ViewPort.Refresh();
      var spriteAfter = spriteSource >= 0 ? s.Model.ReadPointer(spriteSource) : tool.SpriteAddress;
      var paletteAfter = paletteSource >= 0 ? s.Model.ReadPointer(paletteSource) : tool.PaletteAddress;
      tool.SpriteAddress = spriteAfter;
      if (paletteAfter >= 0) tool.PaletteAddress = paletteAfter;
      if (s.Model.GetNextRun(tool.PaletteAddress) is IPaletteRun palAfter && palAfter.Start == tool.PaletteAddress && palette_page < palAfter.Pages) tool.PalettePage = palette_page;
      tool.SpritePage = page;
      var colorsAfter = s.Model.GetNextRun(tool.PaletteAddress) is IPaletteRun pa ? pa.AllColors(s.Model).ToArray() : Array.Empty<short>();
      var info = new Dictionary<string, object?> {
         ["imported"] = path,
         ["size"] = $"{image.Width}x{image.Height}",
         ["changed"] = true,
         ["mode"] = modeUsed,
         ["sprite"] = tool.SpriteAddress.ToAddress(),
         ["sprite_moved_from"] = tool.SpriteAddress != spriteBefore ? spriteBefore.ToAddress() : null,
         ["palette"] = tool.PaletteAddress.ToAddress(),
         ["palette_moved_from"] = tool.PaletteAddress != paletteBefore ? paletteBefore.ToAddress() : null,
         ["palette_changed"] = !colorsBefore.SequenceEqual(colorsAfter),
      };
      return SpriteImage(s, tool, 0, "", info);
   });

   // ---------- render_map ----------

   [McpServerTool(Name = "render_map", ReadOnly = true), Description(
      "Draw a map as an image the way HMA's map editor shows it: tiles, and optionally NPCs/objects and warp/trigger/sign markers. " +
      "Each block is 16x16 pixels, so block (x, y) from map_events is at pixel (16x, 16y). Use it to see a map before and after editing it.")]
   public static CallToolResult RenderMap(RomSessions sessions,
      [Description("Map id 'bank.map' (e.g. '3.0'), or a unique map name from find_map")] string map,
      [Description("'all' (objects and event markers), 'objects' (NPCs/items only) or 'none'")] string events = "all",
      [Description("Draw the border blocks around the map")] bool borders = false,
      [Description("Zoom 1-4")] int scale = 1,
      [Description("Optional file path to save the image (1x PNG)")] string save_to = "") => sessions.With(s => {
      var options = events.Trim().ToLowerInvariant() switch {
         "all" or "" => MapDisplayOptions.AllEvents,
         "objects" or "npcs" => MapDisplayOptions.ObjectEvents,
         "none" => MapDisplayOptions.NoEvents,
         _ => throw new McpException($"events must be all, objects or none, not '{events}'."),
      };
      var m = MapTools.FindOne(s, map);
      var view = MapView(s, m.Bank, m.Map);
      view.IncludeBorders = borders;
      view.ShowEvents = options;
      var pixels = view.PixelData;
      int width = view.PixelWidth, height = view.PixelHeight;
      if (pixels == null || width == 0 || height == 0) throw new McpException($"HMA couldn't draw map {m.Id}; its layout or tileset may be invalid.");
      var info = new Dictionary<string, object?> {
         ["map"] = $"{m.Id} {m.Name}",
         ["size_px"] = $"{width}x{height}",
         ["size_blocks"] = $"{width / 16}x{height / 16}",
         ["events"] = options.ToString(),
         ["borders"] = borders,
      };
      return Result(info, pixels, width, height, Math.Clamp(scale, 1, 4), save_to);
   });

   // ---------- helpers ----------

   static readonly MethodInfo ImportColors = typeof(SpriteTool).GetMethod("ImportSpriteAndPalette", BindingFlags.NonPublic | BindingFlags.Instance, null,
      new[] { typeof(IFileSystem), typeof(short[]), typeof(int), typeof(ImportType) }, null)
      ?? throw new InvalidOperationException("HMA's SpriteTool no longer has ImportSpriteAndPalette(IFileSystem, short[], int, ImportType); update GraphicsTools.");
   static readonly MethodInfo ImportIndexed = typeof(SpriteTool).GetMethod("ImportSpriteAndPalette", BindingFlags.NonPublic | BindingFlags.Instance, null,
      new[] { typeof(IFileSystem), typeof(int[,]), typeof(IReadOnlyList<short>), typeof(ImportType) }, null)
      ?? throw new InvalidOperationException("HMA's SpriteTool no longer has ImportSpriteAndPalette(IFileSystem, int[,], IReadOnlyList<short>, ImportType); update GraphicsTools.");

   sealed class MapRenderer {
      public required StubFileSystem FileSystem { get; init; }
      public required Format Format { get; init; }
      public required EventTemplate Templates { get; init; }
      public MapTutorialsViewModel Tutorials { get; } = new();
   }

   /// <summary>HMA's map editor view of one map, headless. Edits through it go into the session's undo history.</summary>
   internal static BlockMapViewModel MapView(RomSession s, int bank, int map) {
      var renderer = MapRendererFor(s);
      return new BlockMapViewModel(renderer.FileSystem, renderer.Tutorials, s.ViewPort, renderer.Format, renderer.Templates, bank, map) {
         AllOverworldSprites = renderer.Templates.OverworldGraphics,
         IncludeBorders = false,
         SpriteScale = 1,
      };
   }

   static readonly ConditionalWeakTable<RomSession, StubFileSystem> FileSystems = new();
   static readonly ConditionalWeakTable<RomSession, MapRenderer> MapRenderers = new();

   // HMA's tools ask an IFileSystem for dialogs and files. Headless, nothing is shown; "Smart"/first option is never chosen
   // silently because every import passes an explicit ImportType.
   static StubFileSystem FileSystemFor(RomSession s) => FileSystems.GetValue(s, _ => new StubFileSystem());

   static MapRenderer MapRendererFor(RomSession s) => MapRenderers.GetValue(s, _ => {
      if (s.Model.GetTable(HardcodeTablesModel.MapBankTable) == null) throw new McpException("This ROM has no map table (data.maps.banks), so maps can't be drawn.");
      var templates = new EventTemplate(InstantDispatch.Instance, s.Model, s.ViewPort.Tools.CodeTool.ScriptParser, BlockMapViewModel.RenderOWs(s.Model));
      return new MapRenderer { FileSystem = FileSystemFor(s), Format = new Format(s.Model), Templates = templates };
   });

   /// <summary>An address or anchor; for a table of graphics plus a row, the graphic that row points to.</summary>
   static int ResolveGraphic(RomSession s, string address, string row) {
      var start = s.ResolveAddress(address);
      var run = s.Model.GetNextRun(start);
      if (run is ITableRun table && table.Start == start && run is not ISpriteRun && run is not IPaletteRun) {
         if (string.IsNullOrWhiteSpace(row)) throw new McpException($"{address} is a table; pass row (number or name) to pick one of its {table.ElementCount} graphics.");
         int index = DataTools.ResolveRow(table, row);
         // Follow pointers from the row, a few levels deep (overworld rows point to a data record that points to the frames).
         var found = FindGraphicIn(s, table.Start + table.ElementLength * index, table.ElementLength, 3);
         if (found >= 0) return found;
         throw new McpException($"Row {row} of {address} doesn't point to a sprite or palette.");
      }
      if (run is not ISpriteRun && run is not IPaletteRun || run.Start != start)
         throw new McpException($"{start.ToAddress()} isn't a sprite, tileset, tilemap or palette HMA knows about. Search graphics with list_anchors (filter 'graphics.'), or use resolve to see what's there.");
      return start;
   }

   static int FindGraphicIn(RomSession s, int elementStart, int length, int depth) {
      var nested = new List<int>();
      for (int offset = 0; offset + 4 <= length; offset += 4) {
         var source = elementStart + offset;
         var dest = s.Model.ReadPointer(source);
         if (dest < 0 || dest >= s.Model.Count || s.Model.GetNextRun(dest) is not { } target || target.Start != dest) continue;
         if (!target.PointerSources?.Contains(source) ?? true) continue;
         if (target is ISpriteRun or IPaletteRun) return dest;
         if (target is ITableRun t && t.ElementCount > 0) nested.Add(dest);
      }
      if (depth <= 1) return -1;
      foreach (var dest in nested) {
         var t = (ITableRun)s.Model.GetNextRun(dest);
         var found = FindGraphicIn(s, t.Start, t.ElementLength, depth - 1);
         if (found >= 0) return found;
      }
      return -1;
   }

   static IPaletteRun FindPalette(RomSession s, int start) {
      var run = s.Model.GetNextRun(start);
      if (run is IPaletteRun p && p.Start == start) return p;
      if (run is ISpriteRun sprite) {
         var paired = SpriteTool.FindMatchingPalette(s.Model, sprite, -1);
         if (paired >= 0 && s.Model.GetNextRun(paired) is IPaletteRun pp) return pp;
         throw new McpException($"HMA doesn't know which palette goes with the sprite at {start.ToAddress()}; pass the palette's address.");
      }
      throw new McpException($"{start.ToAddress()} isn't a palette.");
   }

   static void CheckPage(int page, int pages, string name) {
      if (page < 0 || page >= pages) throw new McpException($"{name} must be 0 to {pages - 1}.");
   }

   /// <summary>Point HMA's sprite tool at a graphic (and optionally a palette), as its Image tab does.</summary>
   static SpriteTool Prepare(RomSession s, int start, int page, string palette, int palettePage, string row = "") {
      var tool = s.ViewPort.Tools.SpriteTool;
      var run = (ISpriteRun)s.Model.GetNextRun(start);
      CheckPage(page, run.Pages, "page");
      tool.SpriteAddress = start;
      tool.SpritePage = page;
      if (!string.IsNullOrWhiteSpace(palette)) tool.PaletteAddress = FindPalette(s, ResolveGraphic(s, palette, row)).Start;
      if (s.Model.GetNextRun(tool.PaletteAddress) is IPaletteRun pal && pal.Start == tool.PaletteAddress) {
         CheckPage(palettePage, pal.Pages, "palette_page");
         tool.PalettePage = palettePage;
      } else if (run.SpriteFormat.BitsPerPixel >= 4) {
         tool.PaletteAddress = -1;
      }
      return tool;
   }

   static CallToolResult SpriteImage(RomSession s, SpriteTool tool, int scale, string saveTo, Dictionary<string, object?>? info = null) {
      var run = (ISpriteRun)s.Model.GetNextRun(tool.SpriteAddress);
      var pal = s.Model.GetNextRun(tool.PaletteAddress) as IPaletteRun;
      if (pal != null && pal.Start != tool.PaletteAddress) pal = null;
      int width = tool.PixelWidth, height = tool.PixelHeight;
      var pixels = tool.PixelData;
      if (pixels == null || width == 0 || height == 0) throw new McpException($"HMA couldn't draw the graphic at {tool.SpriteAddress.ToAddress()}.");

      info ??= new Dictionary<string, object?>();
      info["address"] = run.Start.ToAddress();
      info["anchor"] = s.Anchor(run.Start);
      info["kind"] = run switch { ITilemapRun => "tilemap", ITilesetRun => "tileset", _ => "sprite" };
      info["size"] = $"{width}x{height}";
      info["bits_per_pixel"] = run.SpriteFormat.BitsPerPixel;
      info["compressed"] = run is LZRun;
      info["pages"] = run.Pages;
      info["page"] = tool.SpritePage;
      info["palette"] = pal == null ? (run.SpriteFormat.BitsPerPixel < 4 ? "(grayscale)" : "(none found: drawn with a default palette; pass palette)") : pal.Start.ToAddress();
      if (pal != null) {
         info["palette_anchor"] = s.Anchor(pal.Start);
         info["palette_pages"] = pal.Pages;
         info["palette_page"] = tool.PalettePage;
      }
      var others = run.FindRelatedPalettes(s.Model).Where(p => p.Start != pal?.Start).Take(6).Select(p => p.Start.ToAddress()).ToList();
      if (others.Count > 0) info["other_palettes"] = others;

      // Save an indexed PNG when the pixels are plain indices into one palette page, so a round trip keeps exact indices.
      byte[]? indexed = null;
      if (!string.IsNullOrWhiteSpace(saveTo) && pal != null && run.SpriteFormat.BitsPerPixel is 4 or 8) {
         var indices = run.GetPixels(s.Model, tool.SpritePage, -1);
         // tilemap pixels carry their palette page (page*16 + color): index into all the pages, after any blank leading pages
         bool allPages = run is ITilemapRun || run.SpriteFormat.BitsPerPixel == 8;
         var colors = allPages ? pal.AllColors(s.Model) : pal.GetPalette(s.Model, tool.PalettePage);
         int shift = run is ITilemapRun ? pal.PaletteFormat.InitialBlankPages * 16 : 0;
         if (indices != null && shift > 0)
            for (int x = 0; x < indices.GetLength(0); x++)
               for (int y = 0; y < indices.GetLength(1); y++) indices[x, y] -= shift;
         if (indices != null && colors.Count <= 256 && indices.Cast<int>().All(i => i >= 0 && i < colors.Count)) indexed = Png.EncodeIndexed(indices, colors);
      }
      int zoom = scale > 0 ? Math.Clamp(scale, 1, 8) : Math.Clamp(256 / Math.Max(width, height), 1, 4);
      return Result(info, pixels, width, height, zoom, saveTo, indexed);
   }

   static CallToolResult PaletteImage(RomSession s, IPaletteRun run, int scale, string saveTo) {
      var colors = run.AllColors(s.Model).ToArray();
      int cols = 16, rows = (colors.Length + cols - 1) / cols, cell = 8;
      var pixels = new short[cols * cell * rows * cell];
      for (int i = 0; i < colors.Length; i++)
         for (int y = 0; y < cell; y++)
            for (int x = 0; x < cell; x++)
               pixels[((i / cols) * cell + y) * cols * cell + (i % cols) * cell + x] = colors[i];
      var info = new Dictionary<string, object?> {
         ["address"] = run.Start.ToAddress(),
         ["anchor"] = s.Anchor(run.Start),
         ["kind"] = "palette",
         ["pages"] = run.Pages,
         ["layout"] = "one row of 16 colors per page; color 0 of each page is transparent in sprites",
         ["colors"] = colors.Select(Png.ToHex),
      };
      return Result(info, pixels, cols * cell, rows * cell, scale > 0 ? Math.Clamp(scale, 1, 8) : 3, saveTo);
   }

   internal static CallToolResult Result(Dictionary<string, object?> info, short[] pixels, int width, int height, int scale, string saveTo, byte[]? fileBytes = null) {
      if (!string.IsNullOrWhiteSpace(saveTo)) {
         var path = Path.GetFullPath(saveTo);
         if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new McpException("save_to must be a .png path.");
         Directory.CreateDirectory(Path.GetDirectoryName(path)!);
         File.WriteAllBytes(path, fileBytes ?? Png.Encode(pixels, width, height));
         info["saved_to"] = path;
      }
      info["preview_scale"] = scale;
      return new CallToolResult {
         Content = new List<ContentBlock> {
            new TextContentBlock { Text = Json.Of(info) },
            ImageContentBlock.FromBytes(Png.Encode(pixels, width, height, scale), "image/png"),
         },
      };
   }
}
