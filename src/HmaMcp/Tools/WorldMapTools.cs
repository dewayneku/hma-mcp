using System.ComponentModel;
using System.Text;
using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using static HmaMcp.Tools.DataTools;
using static HmaMcp.Tools.MapTools;

namespace HmaMcp.Tools;

/// <summary>Fly destinations and the Town Map's region grid.</summary>
[McpServerToolType]
public static class WorldMapTools {
   const string Names = "data.maps.names";
   const string FlyConnections = "data.maps.fly.connections";
   const string FlySpawns = "data.maps.fly.spawn";

   [McpServerTool(Name = "add_fly_spot", Destructive = true), Description(
      "Make a map a Fly destination, landing the player at block (x, y), as HMA's map editor does: the map's region (its regionSectionID name) gets a fly " +
      "connection and a spawn point. If that region already flies to this map, only the landing spot moves. A region can have one fly destination, so a " +
      "new map that shares a town's region name needs its own region name first (create_map name, or write_table_field regionSectionID). In the game the " +
      "player still has to have visited the place (its fly flag) before it shows in the Fly menu. One undo step.")]
   public static string AddFlySpot(RomSessions sessions,
      [Description("Map id 'bank.map' or unique name")] string map,
      [Description("Landing block x")] int x,
      [Description("Landing block y")] int y) => sessions.Edit(s => {
      var m = FindOne(s, map);
      var header = TableAt(s, m.Header)!;
      var regionField = Fields(header).FirstOrDefault(f => f.Key == "regionSectionID") ?? throw new McpException("This ROM's map header has no regionSectionID.");
      var regionName = ReadField(s, header, 0, regionField)?.ToString();
      int region = s.Model[m.Header + regionField.Offset] - (s.Model.IsFRLG() ? 88 : 0);
      var connections = GetTable(s, FlyConnections);
      if (region < 0 || region >= connections.ElementCount)
         throw new McpException($"{m.Id}'s region ({regionName}, slot {region}) can't be a Fly destination: this game's Fly table ({FlyConnections}) only covers slots 0-{connections.ElementCount - 1}" +
            (s.Model.IsFRLG() ? "." : " (Emerald's towns and cities). To fly there, give the map one of those names, or rename one of those slots with add_region_name slot=N."));
      var conFields = Fields(connections);
      Field F(ITableRun run, string name) => Fields(run).First(f => f.Key == name);
      int flight = Convert.ToInt32(ReadField(s, connections, region, F(connections, "flight")));

      if (flight != 0) {
         var spawns = GetTable(s, FlySpawns);
         int spawn = flight - 1;
         if (spawn >= spawns.ElementCount) throw new McpException($"{regionName}'s fly entry points at spawn {spawn}, which doesn't exist.");
         int sb = Convert.ToInt32(ReadField(s, spawns, spawn, F(spawns, "bank"))), sm = Convert.ToInt32(ReadField(s, spawns, spawn, F(spawns, "map")));
         if (sb != m.Bank || sm != m.Map)
            throw new McpException($"{regionName} already flies to {sb}.{sm}. Give {m.Id} its own region name first, or move that fly spot.");
         WriteField(s, spawns, spawn, F(spawns, "x"), x.ToString());
         WriteField(s, spawns, spawn, F(spawns, "y"), y.ToString());
         return Json.Of(new { map = $"{m.Id} {m.Name}", region = regionName, spawn, landing = new { x, y }, note = "This region already flew here; the landing spot was moved." });
      }

      var view = GraphicsTools.MapView(s, m.Bank, m.Map);
      if (!view.CanCreateFlyEvent) throw new McpException($"HMA can't add a fly spot for {m.Id} ({regionName}).");
      var fly = view.CreateFlyEvent() ?? throw new McpException($"HMA couldn't add a fly spot for {m.Id}.");
      fly.X = x;
      fly.Y = y;
      var spawnsAfter = GetTable(s, FlySpawns);
      int newFlight = Convert.ToInt32(ReadField(s, GetTable(s, FlyConnections), region, F(connections, "flight")));
      return Json.Of(new { map = $"{m.Id} {m.Name}", region = regionName, spawn = newFlight - 1, spawns = spawnsAfter.ElementCount, landing = new { x, y } });
   });

   [McpServerTool(Name = "read_town_map", ReadOnly = true), Description(
      "The Town Map's region grid: which region name (data.maps.names) each square of the Town Map belongs to, as rows of numbers ('--' = nothing). " +
      "The legend gives each number's name. With no region, lists the Town Map grids this game has (FireRed: kanto and the Sevii islands; Emerald: hoenn). " +
      "See the Town Map picture with view_image on graphics.townmap.map.tilemap.")]
   public static string ReadTownMap(RomSessions sessions,
      [Description("Region grid, e.g. 'kanto' or 'hoenn' (or the full anchor); empty lists them")] string region = "") => sessions.With(s => {
      if (string.IsNullOrWhiteSpace(region)) return Json.Of(new { regions = Regions(s).Select(r => r.Replace("graphics.townmap.annotations.", "")) });
      var (run, fields, none) = Grid(s, region);
      var rows = new List<string>();
      var used = new SortedDictionary<int, string>();
      var names = GetTable(s, Names);
      for (int r = 0; r < run.ElementCount; r++) {
         var sb = new StringBuilder();
         foreach (var f in fields) {
            int v = Cell(s, run, r, f);
            if (sb.Length > 0) sb.Append(' ');
            if (v >= none || v < 0) sb.Append("--");
            else {
               sb.Append(v.ToString("D3"));
               used.TryAdd(v, ReadField(s, names, v, Fields(names).First(x => x.Key == "name"))?.ToString() ?? "");
            }
         }
         rows.Add(sb.ToString());
      }
      return Json.Of(new {
         region = s.Anchor(run.Start),
         size = $"{fields.Count}x{run.ElementCount}",
         rows,
         legend = used.ToDictionary(kv => kv.Key.ToString("D3"), kv => kv.Value),
      });
   });

   [McpServerTool(Name = "paint_town_map", Destructive = true), Description(
      "Set a rectangle of the Town Map's region grid to a region name (e.g. the name a new map uses), or 'none' to clear it. Coordinates are grid squares " +
      "as read_town_map shows them (column x, row y). In Emerald, a region's position on the Town Map is also stored in data.maps.names (x, y, width, height); " +
      "change those with write_table_field. One undo step.")]
   public static string PaintTownMap(RomSessions sessions,
      [Description("Region grid, e.g. 'kanto' or 'hoenn'")] string region,
      [Description("Column")] int x,
      [Description("Row")] int y,
      [Description("Region name from data.maps.names (e.g. 'ROUTE 22'), a slot number '#34' (for names that repeat), or 'none'")] string name,
      [Description("Width in squares")] int width = 1,
      [Description("Height in squares")] int height = 1) => sessions.Edit(s => {
      var (run, fields, none) = Grid(s, region);
      if (x < 0 || y < 0 || width < 1 || height < 1 || x + width > fields.Count || y + height > run.ElementCount)
         throw new McpException($"The rectangle doesn't fit in the {fields.Count}x{run.ElementCount} grid.");
      int slot = name.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? none : Slot(s, name);
      for (int r = y; r < y + height; r++)
         for (int c = x; c < x + width; c++)
            s.Token.ChangeData(s.Model, run.Start + r * run.ElementLength + fields[c].Offset, (byte)(slot + SectionOffset(s)));
      return Json.Of(new { region = s.Anchor(run.Start), x, y, width, height, name = slot == none ? "none" : $"{slot:D3} {SlotName(s, slot)}" });
   });

   static IEnumerable<string> Regions(RomSession s) =>
      s.Model.GetAutoCompleteAnchorNameOptions("graphics.townmap.annotations.", int.MaxValue)
         .Where(a => a.StartsWith("graphics.townmap.annotations.") && s.Model.GetNextRun(s.Model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, a)) is ITableRun);

   static (ITableRun run, List<Field> fields, int none) Grid(RomSession s, string region) {
      var anchor = region.Contains('.') ? region : "graphics.townmap.annotations." + region.Trim().ToLowerInvariant();
      ITableRun run;
      try { run = GetTable(s, anchor); } catch (McpException) {
         throw new McpException($"No Town Map grid '{region}'. This game has: {string.Join(", ", Regions(s).Select(r => r.Replace("graphics.townmap.annotations.", "")))}.");
      }
      var fields = Fields(run).Where(f => f.Key.StartsWith("worldmap")).ToList();
      if (fields.Count == 0) throw new McpException($"{anchor} isn't a Town Map grid.");
      return (run, fields, GetTable(s, Names).ElementCount);
   }

   // ---------- region names ----------

   [McpServerTool(Name = "list_region_names", ReadOnly = true), Description(
      "The region names (data.maps.names: the name a map shows when you enter it, on the Town Map, and as a Pokémon's met location), one per slot, " +
      "with how many maps use each, whether it has a Fly spot and Town Map squares. 'free' slots are used by none of those: add_region_name reuses them. " +
      "The list's size is fixed by the game's code, so new names go into free slots.")]
   public static string ListRegionNames(RomSessions sessions,
      [Description("Part of a name")] string filter = "",
      [Description("Only free slots")] bool free_only = false) => sessions.With(s => {
      var usage = Usage(s);
      var rows = usage.Where(u => (filter.Length == 0 || u.name.Contains(filter, StringComparison.OrdinalIgnoreCase)) && (!free_only || u.free))
         .Select(u => new { u.slot, u.name, u.maps, u.fly, town_map_squares = u.squares, u.free, u.note }).ToList();
      return Json.Of(new { total = usage.Count, free = usage.Count(u => u.free), shown = rows.Count, names = rows.Take(250) });
   });

   [McpServerTool(Name = "add_region_name", Destructive = true), Description(
      "Give a new region name (e.g. 'MISTY HOLLOW') to a free slot of data.maps.names, since the list itself can't grow. Picks a free slot (no maps, " +
      "no Fly spot, no Town Map squares) unless you pass one. Then use the name with create_map's name, write_table_field regionSectionID, " +
      "add_fly_spot and paint_town_map. One undo step.")]
   public static string AddRegionName(RomSessions sessions,
      [Description("The new name, in capitals as the game shows names (up to 16 characters fit the map name popup)")] string name,
      [Description("Slot to use (from list_region_names); -1 picks a free one")] int slot = -1) => sessions.Edit(s => {
      var text = (name ?? "").Trim();
      if (text.Length == 0) throw new McpException("Pass a name.");
      var usage = Usage(s);
      if (slot < 0) {
         // Emerald: its leftover Kanto names (slots 88+) are only shown as traded Pokémon's met location, while spare Hoenn
         // slots (a second METEOR FALLS, MIRAGE ISLAND, ...) can still be shown by special game code. FireRed: any free slot.
         var pick = usage.Where(u => u.free).OrderBy(u => !s.Model.IsFRLG() && u.slot < 88 ? 1 : 0).ThenBy(u => u.slot).FirstOrDefault();
         slot = pick?.slot ?? throw new McpException("No free slot: every region name is used by a map, a Fly spot or the Town Map. Pass slot to reuse one anyway (list_region_names).");
      } else if (slot >= usage.Count) throw new McpException($"slot must be 0 to {usage.Count - 1}.");
      var before = usage[slot];
      var names = GetTable(s, Names);
      WriteField(s, names, slot, Fields(names).First(f => f.Key == "name"), text);
      return Json.Of(new {
         slot,
         name = SlotName(s, slot),
         replaced = before.name,
         warning = !before.free ? $"Slot {slot} ({before.name}) was in use ({before.maps} map(s), fly {before.fly}, {before.squares} Town Map square(s)); those now show the new name." : before.note,
         length_note = text.Length > 16 ? "Longer than 16 characters: it may not fit the map name popup." : null,
         next = $"Use it: create_map name '{text}', or write_table_field regionSectionID; add_fly_spot; paint_town_map name '#{slot}'.",
      });
   });

   sealed record SlotUse(int slot, string name, int maps, bool fly, int squares, bool free, string? note);

   /// <summary>Per slot: maps whose header uses it, a Fly connection, Town Map squares.</summary>
   static List<SlotUse> Usage(RomSession s) {
      var names = GetTable(s, Names);
      int offset = SectionOffset(s);
      var maps = new int[names.ElementCount];
      foreach (var m in AllMaps(s)) {
         var header = TableAt(s, m.Header);
         var f = header == null ? null : Fields(header).FirstOrDefault(x => x.Key == "regionSectionID");
         if (f == null) continue;
         int slot = s.Model[m.Header + f.Offset] - offset;
         if (slot >= 0 && slot < maps.Length) maps[slot]++;
      }
      var fly = new bool[names.ElementCount];
      var connections = GetTable(s, FlyConnections);
      var flight = Fields(connections).First(x => x.Key == "flight");
      for (int i = 0; i < Math.Min(connections.ElementCount, fly.Length); i++) fly[i] = Convert.ToInt32(ReadField(s, connections, i, flight)) != 0;
      var squares = new int[names.ElementCount];
      foreach (var region in Regions(s)) {
         var (run, fields, none) = Grid(s, region);
         for (int r = 0; r < run.ElementCount; r++)
            foreach (var f in fields) { int v = Cell(s, run, r, f); if (v >= 0 && v < none) squares[v]++; }
      }
      bool emerald = !s.Model.IsFRLG();
      return Enumerable.Range(0, names.ElementCount).Select(i => {
         bool free = maps[i] == 0 && !fly[i] && squares[i] == 0;
         // Emerald keeps FireRed's Kanto names (slots 88+) for the met location of Pokémon traded from FireRed/LeafGreen
         string? note = !free || !emerald ? null
            : i >= 88 ? "Kanto name kept for traded Pokémon's met location; renaming changes what those show. Can't be a Fly destination."
            : "Spare Hoenn slot; special game code may still show it (e.g. MIRAGE ISLAND, the hideouts). Prefer slots 88+.";
         return new SlotUse(i, SlotName(s, i), maps[i], fly[i], squares[i], free, note);
      }).ToList();
   }

   /// <summary>Header and Town Map bytes store a slot plus this offset (FireRed: 88, where its region numbers start).</summary>
   static int SectionOffset(RomSession s) => s.Model.IsFRLG() ? 88 : 0;

   static int Cell(RomSession s, ITableRun run, int row, Field f) => s.Model[run.Start + row * run.ElementLength + f.Offset] - SectionOffset(s);

   static string SlotName(RomSession s, int slot) {
      var names = GetTable(s, Names);
      return slot >= 0 && slot < names.ElementCount ? ReadField(s, names, slot, Fields(names).First(x => x.Key == "name"))?.ToString() ?? "" : "";
   }

   /// <summary>A region name or '#slot' to its slot (a repeated name: its first slot).</summary>
   static int Slot(RomSession s, string text) {
      var t = text.Trim();
      var names = GetTable(s, Names);
      if (t.StartsWith('#') && int.TryParse(t[1..], out var n)) return n >= 0 && n < names.ElementCount ? n : throw new McpException($"Slot must be 0 to {names.ElementCount - 1}.");
      for (int i = 0; i < names.ElementCount; i++) if (NormalizeName(SlotName(s, i)) == NormalizeName(t)) return i;
      throw new McpException($"No region name '{text}'. See list_region_names, or add one with add_region_name.");
   }
}
