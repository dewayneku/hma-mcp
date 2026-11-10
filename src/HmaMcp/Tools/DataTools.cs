using System.ComponentModel;
using System.Globalization;
using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace HmaMcp.Tools;

[McpServerToolType]
public static class DataTools {
   [McpServerTool(Name = "list_anchors", ReadOnly = true), Description("Search HMA's named anchors (tables, scripts, text, graphics). Matching is a case-insensitive substring, e.g. 'pokemon' or 'data.items'.")]
   public static string ListAnchors(RomSessions sessions,
      [Description("Substring to match; empty lists everything")] string filter = "",
      [Description("Max results")] int limit = 100) => sessions.With(s => {
      var matches = s.Model.Anchors
         .Where(a => a.Contains(filter ?? "", StringComparison.OrdinalIgnoreCase))
         .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
         .ToList();
      var shown = matches.Take(Math.Clamp(limit, 1, 2000)).Select(a => {
         var address = s.Model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, a);
         var run = address >= 0 ? s.Model.GetNextRun(address) : null;
         return new {
            anchor = a,
            address = address >= 0 ? address.ToAddress() : null,
            kind = run != null && run.Start == address ? RunKind(run) : null,
            format = run is ITableRun t ? t.FormatString : null,
         };
      }).ToList();
      return Json.Of(new { total = matches.Count, shown = shown.Count, anchors = shown });
   });

   [McpServerTool(Name = "resolve", ReadOnly = true), Description("Turn an anchor name or hex address into an address, and say what HMA thinks is there (anchor, run type, format, which table element it falls in, and how many pointers point to it).")]
   public static string Resolve(RomSessions sessions, [Description("Anchor name or hex address")] string target) => sessions.With(s => {
      var address = s.ResolveAddress(target);
      var run = s.Model.GetNextRun(address);
      var inside = run != null && run.Start <= address && address < run.Start + run.Length;
      object? element = null;
      if (inside && run is ITableRun table) {
         var offset = table.ConvertByteOffsetToArrayOffset(address);
         element = new { index = offset.ElementIndex, field = table.ElementContent[offset.SegmentIndex].Name };
      }
      return Json.Of(new {
         address = address.ToAddress(),
         gba_pointer = $"0x{address + 0x08000000:X8}",
         run = inside ? new {
            start = run!.Start.ToAddress(),
            length = run.Length,
            kind = RunKind(run),
            anchor = s.Anchor(run.Start),
            format = (run as ITableRun)?.FormatString,
            pointer_sources = run.PointerSources?.Count ?? 0,
            element,
         } : null,
      });
   });

   [McpServerTool(Name = "describe_table", ReadOnly = true), Description("Show a table's layout: element count and size, and every field with its type, byte length, and the enum/list it uses. Call this before read_table or write_table_field.")]
   public static string DescribeTable(RomSessions sessions, [Description("Table anchor (e.g. data.pokemon.stats) or address")] string table) => sessions.With(s => {
      var run = GetTable(s, table);
      return Json.Of(new {
         anchor = s.Anchor(run.Start),
         address = run.Start.ToAddress(),
         format = run.FormatString,
         element_count = run.ElementCount,
         element_length = run.ElementLength,
         element_names = run.ElementNames?.Count > 0 ? "yes (rows can be addressed by name)" : null,
         fields = run.ElementContent.Select(seg => new {
            name = seg.Name,
            type = SegmentType(seg),
            length = seg.Length,
            @enum = (seg as ArrayRunEnumSegment)?.EnumName,
            points_to = (seg as ArrayRunPointerSegment)?.InnerFormat,
         }),
      });
   });

   [McpServerTool(Name = "read_table", ReadOnly = true), Description("Read rows of a table as field -> value. Enums come back as names (e.g. species, moves, types), text as text, pointers to known data as HMA's text form.")]
   public static string ReadTable(RomSessions sessions,
      [Description("Table anchor or address")] string table,
      [Description("First row index")] int start = 0,
      [Description("Number of rows (max 200)")] int count = 10,
      [Description("Only these fields (comma separated); empty = all")] string fields = "") => sessions.With(s => {
      var run = GetTable(s, table);
      var model = new ModelTable(s.Model, run, () => s.Token);
      var wanted = SplitFields(fields);
      var rows = new List<Dictionary<string, object?>>();
      var end = Math.Min(run.ElementCount, start + Math.Clamp(count, 1, 200));
      for (int i = Math.Max(0, start); i < end; i++) {
         var element = model[i];
         var row = new Dictionary<string, object?> { ["_index"] = i };
         if (run.ElementNames?.Count > i) row["_name"] = run.ElementNames[i];
         foreach (var seg in run.ElementContent) {
            if (wanted.Count > 0 && !wanted.Contains(seg.Name)) continue;
            row[seg.Name] = ReadField(s, run, i, seg, element);
         }
         rows.Add(row);
      }
      return Json.Of(new { table = s.Anchor(run.Start), element_count = run.ElementCount, rows });
   });

   [McpServerTool(Name = "write_table_field", Destructive = true), Description("Set one field of one table row. Integers accept decimal or 0x hex; enum fields accept a name (e.g. 'FIRE', 'THUNDERBOLT') or number; text fields accept text; pointer fields accept '@ABCDEF' for an address or text for a text pointer (HMA repoints if the text grows). Returns the old and new value.")]
   public static string WriteTableField(RomSessions sessions,
      [Description("Table anchor or address")] string table,
      [Description("Row index, or the row's name for named tables (e.g. 'CHARMANDER')")] string row,
      [Description("Field name from describe_table")] string field,
      [Description("New value")] string value) => sessions.Edit(s => {
      var run = GetTable(s, table);
      var model = new ModelTable(s.Model, run, () => s.Token);
      var index = ResolveRow(run, row);
      var element = model[index];
      var seg = run.ElementContent.FirstOrDefault(f => f.Name == field)
         ?? throw new McpException($"Table has no field '{field}'. Fields: {string.Join(", ", run.ElementContent.Select(f => f.Name))}");
      var before = ReadField(s, run, index, seg, element);

      {
         if (seg is ArrayRunEnumSegment enumSeg) {
            if (!TryParseInt(value, out var n) && !ArrayRunEnumSegment.TryParse(enumSeg.EnumName, s.Model, value, out n)) {
               var close = enumSeg.GetBestOptions(s.Model, value).Take(8).ToList();
               if (close.Count == 0) close = enumSeg.GetOptions(s.Model).Take(20).ToList();
               throw new McpException($"'{value}' is not a valid {enumSeg.EnumName} value. Closest matches: {string.Join(", ", close)}");
            }
            element.SetValue(field, n);
         } else if (seg.Type == ElementContentType.Pointer) {
            if (value.StartsWith('@')) element.SetAddress(field, s.ResolveAddress(value[1..]));
            else element.SetStringValue(field, value);
         } else if (seg.Type == ElementContentType.PCS) {
            element.SetStringValue(field, value);
         } else if (seg.Type == ElementContentType.Integer && seg is not ArrayRunTupleSegment) {
            if (!TryParseInt(value, out var n)) throw new McpException($"'{value}' is not a number.");
            element.SetValue(field, n);
         } else {
            throw new McpException($"Field '{field}' is a {SegmentType(seg)} field; edit it with hma_edit for now.");
         }
      }

      // re-read from the (possibly relocated) table
      var after = GetTable(s, s.Anchor(run.Start) is { Length: > 0 } a ? a : run.Start.ToAddress());
      var afterValue = ReadField(s, after, index, seg, new ModelTable(s.Model, after, () => s.Token)[index]);
      return Json.Of(new { table = s.Anchor(run.Start), row = index, field, before, after = afterValue });
   });

   [McpServerTool(Name = "read_bytes", ReadOnly = true), Description("Hex dump of raw bytes.")]
   public static string ReadBytes(RomSessions sessions,
      [Description("Anchor or hex address")] string address,
      [Description("Byte count (max 4096)")] int length = 64) => sessions.With(s => {
      var start = s.ResolveAddress(address);
      var len = Math.Clamp(length, 1, Math.Min(4096, s.Model.Count - start));
      var lines = new List<string>();
      for (int i = 0; i < len; i += 16) {
         var chunk = Enumerable.Range(start + i, Math.Min(16, len - i)).Select(a => s.Model[a].ToString("X2"));
         lines.Add($"{start + i:X6}: {string.Join(" ", chunk)}");
      }
      return string.Join("\n", lines);
   });

   [McpServerTool(Name = "write_bytes", Destructive = true), Description("Write raw bytes (hex string like 'FF 00 1A' or 'FF001A'). Low level: does not update HMA's formats or repoint anything. Prefer write_table_field, compile_script or hma_edit.")]
   public static string WriteBytes(RomSessions sessions,
      [Description("Anchor or hex address")] string address,
      [Description("Hex bytes")] string hex) => sessions.Edit(s => {
      var start = s.ResolveAddress(address);
      var clean = new string(hex.Where(Uri.IsHexDigit).ToArray());
      if (clean.Length == 0 || clean.Length % 2 != 0) throw new McpException("hex must be an even number of hex digits.");
      var bytes = Enumerable.Range(0, clean.Length / 2).Select(i => byte.Parse(clean.AsSpan(i * 2, 2), NumberStyles.HexNumber)).ToArray();
      if (start + bytes.Length > s.Model.Count) throw new McpException("Write would run past the end of the ROM.");
      var before = string.Join(" ", bytes.Select((_, i) => s.Model[start + i].ToString("X2")));
      s.Token.ChangeData(s.Model, start, bytes);
      return Json.Of(new { address = start.ToAddress(), length = bytes.Length, before, after = string.Join(" ", bytes.Select(b => b.ToString("X2"))) });
   });

   [McpServerTool(Name = "find_free_space", ReadOnly = true), Description("Find free space (0xFF bytes) big enough for new data, searching from HMA's free-space start.")]
   public static string FindFreeSpace(RomSessions sessions,
      [Description("Bytes needed")] int length,
      [Description("Search start (hex); defaults to HMA's free-space start")] string? start = null) => sessions.With(s => {
      var from = string.IsNullOrWhiteSpace(start) ? s.Model.FreeSpaceStart : s.ResolveAddress(start);
      var found = s.Model.FindFreeSpace(from, length);
      if (found < 0) throw new McpException($"No free block of {length} bytes found after {from.ToAddress()}.");
      return Json.Of(new { address = found.ToAddress(), gba_pointer = $"0x{found + 0x08000000:X8}", length });
   });

   [McpServerTool(Name = "hma_edit", Destructive = true), Description("Type text into HMA's hex view at an address, exactly like typing in the editor. Supports HMA's full edit language: '^anchor.name`format`' to create anchors/tables, '<ABCDEF>' pointers, '\"text\"' strings, plain hex bytes, and table cell values separated by spaces. One undo step.")]
   public static string HmaEdit(RomSessions sessions,
      [Description("Anchor or hex address to start typing at")] string address,
      [Description("Text to type, as you would in HMA")] string text) => sessions.Edit(s => {
      var start = s.ResolveAddress(address);
      s.ViewPort.Goto.Execute(start);
      s.ViewPort.Edit(text);
      var run = s.Model.GetNextRun(start);
      return Json.Of(new {
         address = start.ToAddress(),
         typed = text,
         run_here = run != null && run.Start <= start ? RunKind(run) : null,
         anchor = s.Anchor(start),
      });
   });

   // ---- helpers ----

   internal static ITableRun GetTable(RomSession s, string table) {
      var address = s.ResolveAddress(table);
      if (s.Model.GetNextRun(address) is ITableRun run && run.Start == address) return run;
      throw new McpException($"No table starts at '{table}'. Use list_anchors to find table anchors.");
   }

   static int ResolveRow(ITableRun run, string row) {
      if (TryParseInt(row, out var index)) {
         if (index < 0 || index >= run.ElementCount) throw new McpException($"Row {index} is out of range (0-{run.ElementCount - 1}).");
         return index;
      }
      var names = run.ElementNames ?? Array.Empty<string>();
      for (int i = 0; i < names.Count; i++) if (string.Equals(names[i], row, StringComparison.OrdinalIgnoreCase)) return i;
      throw new McpException($"No row named '{row}'. Use a row index, or read_table to see row names.");
   }

   static object? ReadField(RomSession s, ITableRun run, int index, ArrayRunElementSegment seg, ModelArrayElement element) {
      try {
         if (seg is ArrayRunTupleSegment or ArrayRunBitArraySegment) return Hex(s, run, index, seg);
         if (seg.Type == ElementContentType.Integer && seg is not ArrayRunEnumSegment) return element.GetValue(seg.Name);
         return element.Serialize(seg.Name);
      } catch (Exception) {
         return Hex(s, run, index, seg);
      }
   }

   static string Hex(RomSession s, ITableRun run, int index, ArrayRunElementSegment seg) {
      var offset = run.Start + run.ElementLength * index + run.ElementContent.TakeWhile(x => x != seg).Sum(x => x.Length);
      return "0x" + string.Concat(Enumerable.Range(offset, seg.Length).Select(a => s.Model[a].ToString("X2")));
   }

   static HashSet<string> SplitFields(string fields) =>
      (fields ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

   static bool TryParseInt(string text, out int value) {
      text = text.Trim();
      if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
         return int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
      return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
   }

   static string SegmentType(ArrayRunElementSegment seg) => seg switch {
      ArrayRunEnumSegment => "enum",
      ArrayRunTupleSegment => "tuple",
      ArrayRunBitArraySegment => "bits",
      ArrayRunPointerSegment => "pointer",
      _ => seg.Type switch {
         ElementContentType.PCS => "text",
         ElementContentType.Integer => "int",
         ElementContentType.Pointer => "pointer",
         _ => seg.Type.ToString().ToLowerInvariant(),
      },
   };

   internal static string RunKind(IFormattedRun run) => run switch {
      ArrayRun => "table",
      ITableRun => "table-stream",
      PCSRun => "text",
      XSERun => "event-script",
      BSERun => "battle-script",
      ASERun => "animation-script",
      TSERun => "trainer-ai-script",
      ISpriteRun => "sprite",
      IPaletteRun => "palette",
      PointerRun => "pointer",
      NoInfoRun => "unformatted",
      _ => run.GetType().Name,
   };
}
