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
         throw new McpException($"{m.Id}'s region ({regionName}) has no entry in {FlyConnections}, so it can't be a Fly destination in this game.");
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
      var index = NameIndex(s);
      for (int r = 0; r < run.ElementCount; r++) {
         var sb = new StringBuilder();
         foreach (var f in fields) {
            int v = Value(s, run, r, f, index);
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
      [Description("Region name from data.maps.names (e.g. 'ROUTE 22'), or 'none'")] string name,
      [Description("Width in squares")] int width = 1,
      [Description("Height in squares")] int height = 1) => sessions.Edit(s => {
      var (run, fields, none) = Grid(s, region);
      if (x < 0 || y < 0 || width < 1 || height < 1 || x + width > fields.Count || y + height > run.ElementCount)
         throw new McpException($"The rectangle doesn't fit in the {fields.Count}x{run.ElementCount} grid.");
      var value = name.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? none.ToString() : name;
      for (int r = y; r < y + height; r++)
         for (int c = x; c < x + width; c++)
            WriteField(s, run, r, fields[c], value);
      return Json.Of(new { region = s.Anchor(run.Start), x, y, width, height, name = value == none.ToString() ? "none" : name });
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

   /// <summary>Region name -> index into data.maps.names (first index when a name repeats).</summary>
   static Dictionary<string, int> NameIndex(RomSession s) {
      var names = GetTable(s, Names);
      var nameField = Fields(names).First(x => x.Key == "name");
      var result = new Dictionary<string, int>();
      for (int i = 0; i < names.ElementCount; i++) result.TryAdd(NormalizeName(ReadField(s, names, i, nameField)?.ToString() ?? ""), i);
      return result;
   }

   /// <summary>The cell's region index into data.maps.names (the enum's stored offset, e.g. FireRed's +88, undone).</summary>
   static int Value(RomSession s, ITableRun run, int row, Field f, Dictionary<string, int> index) {
      var text = ReadField(s, run, row, f)?.ToString() ?? "";
      return index.TryGetValue(NormalizeName(text), out var i) ? i : int.TryParse(text, out var n) ? n : -1;
   }
}
