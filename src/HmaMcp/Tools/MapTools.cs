using System.ComponentModel;
using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using static HmaMcp.Tools.DataTools;

namespace HmaMcp.Tools;

[McpServerToolType]
public static class MapTools {
   const string Banks = "data.maps.banks";

   [McpServerTool(Name = "find_map", ReadOnly = true), Description("Find maps by name (e.g. 'PALLET TOWN', 'route 1', 'viridian'). Returns each map's bank.map id and header address. Many indoor maps share their town's name. Pass the id to map_events.")]
   public static string FindMap(RomSessions sessions,
      [Description("Part of the map's name (case-insensitive); empty lists every map")] string name = "",
      [Description("Max results")] int limit = 100) => sessions.With(s => {
      var wanted = NormalizeName(name ?? "");
      var all = AllMaps(s);
      // "ROUTE 1" should not also match ROUTE 10..19: prefer exact name matches when there are any
      var exact = all.Where(m => NormalizeName(m.Name) == wanted).ToList();
      var matches = wanted.Length == 0 ? all : exact.Count > 0 ? exact : all.Where(m => NormalizeName(m.Name).Contains(wanted)).ToList();
      if (matches.Count == 0) throw new McpException($"No map named like '{name}'. Call find_map with an empty name to list them all.");
      return Json.Of(new {
         total = matches.Count,
         maps = matches.Take(Math.Clamp(limit, 1, 1000)).Select(m => new { id = m.Id, name = m.Name, header = m.Header.ToAddress() }),
      });
   });

   [McpServerTool(Name = "map_events", ReadOnly = true), Description("Everything on one map: header (music, weather, size), NPCs/objects with their scripts, warps (with where they lead), trigger scripts, signs, map scripts and connections. Each list comes with its table address, so a field can be changed with write_table_field (table = that address, row = the item's index).")]
   public static string MapEvents(RomSessions sessions,
      [Description("Map id 'bank.map' (e.g. '3.0'), or a unique map name from find_map")] string map) => sessions.With(s => {
      var m = FindOne(s, map);
      var model = s.Model;
      var header = TableAt(s, m.Header) ?? throw new McpException($"No map header at {m.Header.ToAddress()}.");
      var headerRow = Row(s, header, 0, skipPointers: true);

      object? layout = null;
      if (Sub(s, header, 0, "layout") is { } layoutRun)
         layout = new { table = layoutRun.Start.ToAddress(), width = Value(s, layoutRun, 0, "width"), height = Value(s, layoutRun, 0, "height") };

      object? events = null;
      if (Sub(s, header, 0, "events") is { } ev) {
         object? List(string field, Func<ITableRun, int, Dictionary<string, object?>, Dictionary<string, object?>>? extra = null) {
            var run = Sub(s, ev, 0, field);
            if (run == null) return new { table = (string?)null, items = Array.Empty<object>() };
            var items = Enumerable.Range(0, run.ElementCount).Select(i => {
               var row = Row(s, run, i, skipPointers: false);
               return extra != null ? extra(run, i, row) : row;
            }).ToList();
            return new { table = run.Start.ToAddress(), items };
         }
         var maps = AllMaps(s);
         events = new {
            table = ev.Start.ToAddress(),
            objects = List("objects"),
            warps = List("warps", (_, _, row) => {
               row["to"] = Destination(maps, row, "bank", "map");
               return row;
            }),
            scripts = List("scripts"),
            signposts = List("signposts"),
         };
      }

      object? mapScripts = null;
      if (Sub(s, header, 0, "mapscripts") is { } ms)
         mapScripts = new { table = ms.Start.ToAddress(), items = Enumerable.Range(0, ms.ElementCount).Select(i => Row(s, ms, i, skipPointers: false)).ToList() };

      object? connections = null;
      if (Sub(s, header, 0, "connections") is { } con && Sub(s, con, 0, "connections") is { } list) {
         var maps = AllMaps(s);
         connections = new {
            table = list.Start.ToAddress(),
            items = Enumerable.Range(0, list.ElementCount).Select(i => {
               var row = Row(s, list, i, skipPointers: false);
               row["to"] = Destination(maps, row, "mapGroup", "mapNum");
               // connection lists HMA creates for new maps store direction as a plain number; show the name either way
               if (row.TryGetValue("direction", out var d) && d is int or long && Enum.IsDefined(typeof(HavenSoft.HexManiac.Core.ViewModels.Map.MapDirection), Convert.ToInt32(d)))
                  row["direction"] = ((HavenSoft.HexManiac.Core.ViewModels.Map.MapDirection)Convert.ToInt32(d)).ToString();
               return row;
            }).ToList(),
         };
      }

      return Json.Of(new {
         id = m.Id, name = m.Name, header = m.Header.ToAddress(),
         settings = headerRow, layout, events, map_scripts = mapScripts, connections,
      });
   });

   static string? Destination(List<MapRef> maps, Dictionary<string, object?> row, string bankField, string mapField) {
      if (!row.TryGetValue(bankField, out var b) || !row.TryGetValue(mapField, out var m)) return null;
      if (b is not (int or long) || m is not (int or long)) return null;
      int bank = Convert.ToInt32(b), map = Convert.ToInt32(m);
      var target = maps.FirstOrDefault(x => x.Bank == bank && x.Map == map);
      return target != null ? $"{target.Id} {target.Name}" : $"{bank}.{map}";
   }

   internal sealed record MapRef(int Bank, int Map, string Name, int Header) {
      public string Id => $"{Bank}.{Map}";
   }

   static List<MapRef> AllMaps(RomSession s) {
      var model = s.Model;
      var banks = GetTable(s, Banks);
      var result = new List<MapRef>();
      for (int b = 0; b < banks.ElementCount; b++) {
         var maps = Sub(s, banks, b, "maps");
         if (maps == null) continue;
         for (int m = 0; m < maps.ElementCount; m++) {
            var headerAddress = Pointer(s, maps, m, "map");
            if (headerAddress < 0) continue;
            var header = TableAt(s, headerAddress);
            var name = header == null ? "" : NameField(header) is { } nameField ? ReadField(s, header, 0, nameField)?.ToString() ?? "" : "";
            result.Add(new MapRef(b, m, name, headerAddress));
         }
      }
      return result;
   }

   static Field? NameField(ITableRun header) =>
      Fields(header).FirstOrDefault(f => f.Seg is ArrayRunEnumSegment e && e.EnumName == "data.maps.names")
      ?? Fields(header).FirstOrDefault(f => f.Key == "regionSectionID");

   internal static MapRef FindOne(RomSession s, string map) {
      var all = AllMaps(s);
      var parts = map.Trim().Split('.', ',', '/', ' ');
      if (parts.Length == 2 && int.TryParse(parts[0], out var bank) && int.TryParse(parts[1], out var num)) {
         return all.FirstOrDefault(m => m.Bank == bank && m.Map == num)
            ?? throw new McpException($"There is no map {bank}.{num}.");
      }
      var wanted = NormalizeName(map);
      var named = all.Where(m => NormalizeName(m.Name) == wanted).ToList();
      if (named.Count == 1) return named[0];
      if (named.Count > 1) throw new McpException($"{named.Count} maps are named '{named[0].Name}': {string.Join(", ", named.Select(m => m.Id))}. Pass one of these ids; indoor maps often share their town's name, and the first is usually the outdoor one.");
      throw new McpException($"No map named '{map}'. Use find_map to search by part of the name, or pass 'bank.map'.");
   }

   internal static ITableRun? TableAt(RomSession s, int address) =>
      address >= 0 && address < s.Model.Count && s.Model.GetNextRun(address) is ITableRun run && run.Start == address ? run : null;

   internal static int Pointer(RomSession s, ITableRun run, int index, string field) {
      var f = Fields(run).FirstOrDefault(x => x.Key == field);
      if (f == null || f.Seg.Type != ElementContentType.Pointer) return -1;
      var p = s.Model.ReadPointer(run.Start + run.ElementLength * index + f.Offset);
      return p >= 0 && p < s.Model.Count ? p : -1;
   }

   internal static ITableRun? Sub(RomSession s, ITableRun run, int index, string field) => TableAt(s, Pointer(s, run, index, field));

   internal static object? Value(RomSession s, ITableRun run, int index, string field) =>
      Fields(run).FirstOrDefault(x => x.Key == field) is { } f ? ReadField(s, run, index, f) : null;

   /// <summary>One row as field -> value. Pointers become '&lt;ABCDEF&gt;' (or null), not their nested text.</summary>
   internal static Dictionary<string, object?> Row(RomSession s, ITableRun run, int index, bool skipPointers) {
      var row = new Dictionary<string, object?> { ["_index"] = index };
      foreach (var f in Fields(run)) {
         if (f.Seg.Length == 0 && f.Seg is not ArrayRunCalculatedSegment) continue;
         if (f.Key.StartsWith("padding") || f.Key.StartsWith("unused")) continue;
         if (f.Seg.Type == ElementContentType.Pointer && f.Seg is not ArrayRunRecordSegment) {
            if (skipPointers) continue;
            var p = s.Model.ReadPointer(run.Start + run.ElementLength * index + f.Offset);
            row[f.Key] = p >= 0 && p < s.Model.Count ? $"<{p:X6}>" : null;
            var anchor = p >= 0 && p < s.Model.Count ? s.Anchor(p) : "";
            if (!string.IsNullOrEmpty(anchor)) row[f.Key + "_anchor"] = anchor;
            continue;
         }
         row[f.Key] = ReadField(s, run, index, f);
      }
      return row;
   }
}
