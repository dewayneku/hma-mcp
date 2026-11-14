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
         fields = Fields(run).Select(f => new {
            name = f.Key,
            type = SegmentType(f.Seg),
            length = f.Seg.Length,
            @enum = (f.Seg as ArrayRunEnumSegment)?.EnumName ?? (f.Seg as ArrayRunBitArraySegment)?.SourceArrayName,
            points_to = (f.Seg as ArrayRunPointerSegment)?.InnerFormat,
            depends_on = (f.Seg as ArrayRunRecordSegment)?.MatchField,
            parts = (f.Seg as ArrayRunTupleSegment)?.Elements.Where(e => !string.IsNullOrEmpty(e.Name)).Select(e => e.Name),
            note = FieldNote(f.Seg),
         }),
      });
   });

   static string? FieldNote(ArrayRunElementSegment seg) => seg switch {
      ArrayRunCalculatedSegment => "calculated from other fields; read-only",
      ArrayRunRecordSegment r => $"type depends on '{r.MatchField}' (enum or pointer for some values, else int)",
      ArrayRunTupleSegment => "packed fields: read as an object; write 'part=value, part=value' or a JSON object",
      ArrayRunBitArraySegment => "set of flags: read as a list of names; write a list ('A, B'), '+A, -B' to add/remove, or 0x hex",
      ArrayRunPointerSegment p when p.InnerFormat != "\"\"" => "pointer to nested data: write the full text form read_table returns (HMA repoints if it grows), or '@ABCDEF'",
      _ => null,
   };

   [McpServerTool(Name = "read_table", ReadOnly = true), Description("Read rows of a table as field -> value. Enums come back as names (e.g. species, moves, types), text as text, pointers to known data as HMA's text form.")]
   public static string ReadTable(RomSessions sessions,
      [Description("Table anchor or address")] string table,
      [Description("First row index")] int start = 0,
      [Description("Number of rows (max 200)")] int count = 10,
      [Description("Only these fields (comma separated); empty = all")] string fields = "",
      [Description("Only rows matching all of these 'field=value' conditions (comma separated), e.g. 'bank=3, map=19' or 'type1=FIRE'. Scans from start; count limits the matches returned.")] string where = "") => sessions.With(s => {
      var run = GetTable(s, table);
      var all = Fields(run);
      var wanted = SplitFields(fields);
      var unknown = wanted.Where(w => all.All(f => f.Key != w)).ToList();
      if (unknown.Count > 0) throw new McpException($"Table has no field {string.Join(", ", unknown.Select(u => $"'{u}'"))}. Fields: {string.Join(", ", all.Select(f => f.Key))}");
      var conditions = ParseWhere(where, all);
      var shown = wanted.Count == 0 ? all.Where(f => !IsHidden(f.Seg)).ToList() : wanted.Select(w => all.First(f => f.Key == w)).ToList();
      var rows = new List<Dictionary<string, object?>>();
      var limit = Math.Clamp(count, 1, 200);
      for (int i = Math.Max(0, start); i < run.ElementCount && rows.Count < limit; i++) {
         if (conditions.Count > 0 && !conditions.All(c => Matches(ReadField(s, run, i, c.field), c.value))) continue;
         var row = new Dictionary<string, object?> { ["_index"] = i };
         if (run.ElementNames?.Count > i) row["_name"] = Unquote(run.ElementNames[i]);
         foreach (var f in shown) row[f.Key] = ReadField(s, run, i, f);
         rows.Add(row);
      }
      return Json.Of(new { table = s.Anchor(run.Start), element_count = run.ElementCount, rows });
   });

   [McpServerTool(Name = "write_table_field", Destructive = true), Description("Set one field of one table row. Values are checked against the field's size. Integers: decimal or 0x hex. Enums: a name (e.g. 'FIRE', 'THUNDERBOLT') or number. Text: text (fixed-length text must fit). Packed (tuple) fields: 'part=value, part=value' or a JSON object. Flag sets (bits): 'A, B' to replace, '+A, -B' to add/remove, or 0x hex. Pointers: '@ABCDEF' to repoint, or the full text form read_table shows (text, learnsets, trainer teams, nested tables); HMA repoints the data if it grows. Returns the old and new value, plus changed_lines if HMA stored nested text differently from what was written.")]
   public static string WriteTableField(RomSessions sessions,
      [Description("Table anchor or address")] string table,
      [Description("Row index, or the row's name for named tables (e.g. 'CHARMANDER')")] string row,
      [Description("Field name from describe_table")] string field,
      [Description("New value")] string value) => sessions.Edit(s => {
      var run = GetTable(s, table);
      var anchor = s.Anchor(run.Start);
      var index = ResolveRow(run, row);
      var all = Fields(run);
      var f = all.FirstOrDefault(x => x.Key == field)
         ?? throw new McpException($"Table has no field '{field}'. Fields: {string.Join(", ", all.Select(x => x.Key))}");
      var before = ReadField(s, run, index, f);
      WriteField(s, run, index, f, value);

      // re-read from the (possibly relocated) table
      var after = anchor.Length > 0 ? GetTable(s, anchor) : (s.Model.GetNextRun(run.Start) as ITableRun ?? run);
      var afterValue = ReadField(s, after, index, Fields(after).First(x => x.Key == field));

      // HMA parses nested text leniently: an unknown name becomes some default value without an error.
      // Report every line that doesn't read back the way it was written.
      List<object>? changedLines = null;
      if (f.Seg.Type == ElementContentType.Pointer && afterValue is string stored && !value.TrimStart().StartsWith('@') && (value.Contains('\n') || value.Contains('('))) {
         var wrote = MeaningfulLines(value);
         var got = MeaningfulLines(stored);
         changedLines = new();
         for (int i = 0; i < Math.Max(wrote.Count, got.Count); i++) {
            var w = i < wrote.Count ? wrote[i] : default((string key, string text)?);
            var g = i < got.Count ? got[i] : default((string key, string text)?);
            if (w?.key != g?.key) changedLines.Add(new { line = i + 1, wrote = w?.text, stored = g?.text });
         }
         if (changedLines.Count == 0) changedLines = null;
      }
      return Json.Of(new {
         table = anchor, row = index, field, before, after = afterValue,
         warning = changedLines != null ? "HMA stored some lines differently from what was written (unknown names become a default). Check them; call undo if this is wrong." : null,
         changed_lines = changedLines?.Take(20),
      });
   });

   static List<(string key, string text)> MeaningfulLines(string text) =>
      text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
         .Select(l => (string.Join(" ", l.Replace("\"", "").Replace(" :", ":").Replace(": ", ":").ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)), l))
         .ToList();

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

   /// <summary>A table field. Key is unique within the row: repeated names (e.g. two 'unused') get _2, _3 suffixes.</summary>
   internal sealed record Field(string Key, ArrayRunElementSegment Seg, int Offset, int Index);

   internal static List<Field> Fields(ITableRun run) {
      var list = new List<Field>();
      var seen = new Dictionary<string, int>();
      int offset = 0;
      for (int i = 0; i < run.ElementContent.Count; i++) {
         var seg = run.ElementContent[i];
         var key = string.IsNullOrEmpty(seg.Name) ? $"_field{i}" : seg.Name;
         if (seen.TryGetValue(key, out var n)) { seen[key] = n + 1; key = $"{key}_{n + 1}"; } else seen[key] = 1;
         list.Add(new Field(key, seg, offset, i));
         offset += seg.Length;
      }
      return list;
   }

   // zero-length segments are comments/markers in HMA formats; calculated fields are the exception
   static bool IsHidden(ArrayRunElementSegment seg) => seg.Length == 0 && seg is not ArrayRunCalculatedSegment;

   internal static int ResolveRow(ITableRun run, string row) {
      if (TryParseInt(row, out var index)) {
         if (index < 0 || index >= run.ElementCount) throw new McpException($"Row {index} is out of range (0-{run.ElementCount - 1}).");
         return index;
      }
      var names = run.ElementNames ?? Array.Empty<string>();
      var wanted = NormalizeName(row);
      for (int i = 0; i < names.Count; i++) if (NormalizeName(names[i]) == wanted) return i;
      throw new McpException($"No row named '{row}'. Use a row index, or read_table to see row names.");
   }

   /// <summary>Case-, quote- and accent-insensitive name, so POKE BALL, "POKé BALL" and poké ball all match.</summary>
   internal static string NormalizeName(string name) {
      var decomposed = Unquote(name.Trim()).Normalize(System.Text.NormalizationForm.FormD);
      var plain = new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
      return plain.ToUpperInvariant();
   }

   internal static string Unquote(string text) =>
      text.Length >= 2 && text[0] == '"' && text[^1] == '"' && text.IndexOf('"', 1) == text.Length - 1 ? text[1..^1] : text;

   internal static object? ReadField(RomSession s, ITableRun run, int index, Field f) {
      var model = s.Model;
      var address = run.Start + run.ElementLength * index + f.Offset;
      var seg = f.Seg;
      try {
         if (seg is ArrayRunRecordSegment record) seg = record.CreateConcrete(model, run, address);
         switch (seg) {
            case ArrayRunCalculatedSegment calc: {
               var v = calc.CalculatedValue(address);
               if (calc.Enum != null) {
                  var options = ArrayRunEnumSegment.GetOptions(model, calc.Enum);
                  if (options != null && (int)v >= 0 && (int)v < options.Count) return Unquote(options[(int)v]);
               }
               return Math.Abs(v - Math.Round(v)) < 1e-9 ? (object)(long)Math.Round(v) : Math.Round(v, 2);
            }
            case ArrayRunTupleSegment tuple: {
               var result = new Dictionary<string, object>();
               int bit = 0;
               foreach (var e in tuple.Elements) {
                  if (!string.IsNullOrEmpty(e.Name)) {
                     var v = e.Read(model, address, bit);
                     if (!string.IsNullOrEmpty(e.SourceName)) {
                        var options = model.GetOptions(e.SourceName);
                        result[e.Name] = options != null && v < options.Count ? Unquote(options[v]) : v;
                     } else if (e.BitWidth == 1) result[e.Name] = v == 1;
                     else result[e.Name] = v;
                  }
                  bit += e.BitWidth;
               }
               return result;
            }
            case ArrayRunBitArraySegment bits: {
               var options = model.GetBitOptions(bits.SourceArrayName);
               var set = new List<string>();
               for (int b = 0; b < bits.Length * 8; b++) {
                  if ((model[address + b / 8] & (1 << (b % 8))) == 0) continue;
                  if (options == null || b >= options.Count) return Hex(model, address, seg.Length);
                  set.Add(Unquote(options[b]));
               }
               return set;
            }
            case ArrayRunEnumSegment:
               return Unquote(seg.ToText(model, address, 0));
            case ArrayRunSignedSegment signed:
               return signed.ReadValue(model, address);
            case ArrayRunHexSegment:
               return Hex(model, address, seg.Length);
         }
         if (seg.Type == ElementContentType.Integer) return model.ReadMultiByteValue(address, seg.Length);
         if (seg.Type == ElementContentType.PCS) return Unquote(model.TextConverter.Convert(model, address, seg.Length));
         if (seg.Type == ElementContentType.Pointer) {
            // ModelArrayElement gives the nested text form (text, learnsets, teams...), but looks fields up by name
            if (f.Key == f.Seg.Name && f.Seg is not ArrayRunRecordSegment) return new ModelTable(model, run, () => s.Token)[index].Serialize(f.Seg.Name);
            return seg.ToText(model, address, 0);
         }
         return Hex(model, address, seg.Length);
      } catch (Exception) {
         return Hex(model, address, f.Seg.Length);
      }
   }

   static string Hex(IDataModel model, int address, int length) =>
      "0x" + string.Concat(Enumerable.Range(address, length).Select(a => model[a].ToString("X2")));

   static void WriteField(RomSession s, ITableRun run, int index, Field f, string value) {
      var model = s.Model;
      var address = run.Start + run.ElementLength * index + f.Offset;
      var seg = f.Seg;
      if (seg is ArrayRunCalculatedSegment) throw new McpException($"Field '{f.Key}' is calculated from other fields and can't be written.");
      if (seg.Length == 0) throw new McpException($"Field '{f.Key}' is a comment/marker with no data.");
      if (seg is ArrayRunRecordSegment record) seg = record.CreateConcrete(model, run, address);

      void WriteInt(int n) {
         model.WriteMultiByteValue(address, seg.Length, s.Token, n);
         run.NotifyChildren(model, s.Token, index, f.Index);
      }

      switch (seg) {
         case ArrayRunEnumSegment enumSeg: {
            if (TryParseInt(value, out var n)) n += enumSeg.ValueOffset;
            else if (!enumSeg.TryParse(model, value, out n)) {
               var close = enumSeg.GetBestOptions(model, value).Take(8).ToList();
               if (close.Count == 0) close = enumSeg.GetOptions(model).Take(20).ToList();
               throw new McpException($"'{value}' is not a valid {enumSeg.EnumName} value. Closest matches: {string.Join(", ", close)}");
            }
            CheckRange(f.Key, n, seg.Length, signed: false);
            WriteInt(n);
            return;
         }
         case ArrayRunTupleSegment tuple:
            WriteTuple(model, s.Token, address, f.Key, tuple, value);
            run.NotifyChildren(model, s.Token, index, f.Index);
            return;
         case ArrayRunBitArraySegment bits:
            WriteBits(model, s.Token, address, f.Key, bits, value);
            run.NotifyChildren(model, s.Token, index, f.Index);
            return;
      }

      switch (seg.Type) {
         case ElementContentType.Integer: {
            if (!TryParseInt(value, out var n)) throw new McpException($"'{value}' is not a number.");
            CheckRange(f.Key, n, seg.Length, signed: seg is ArrayRunSignedSegment);
            WriteInt(n);
            return;
         }
         case ElementContentType.PCS: {
            var bytes = model.TextConverter.Convert(value, out var bad);
            if (bad) throw new McpException($"'{value}' has characters the game's font doesn't have.");
            if (bytes.Count > seg.Length)
               throw new McpException($"'{value}' is too long for '{f.Key}': it encodes to {bytes.Count - 1} bytes, but the field holds at most {seg.Length - 1} (plus the end marker).");
            while (bytes.Count < seg.Length) bytes.Add(0);
            s.Token.ChangeData(model, address, bytes);
            return;
         }
         case ElementContentType.Pointer:
            WritePointerField(s, run, index, f, seg, address, value);
            return;
      }
      throw new McpException($"Field '{f.Key}' is a {SegmentType(seg)} field; edit it with hma_edit for now.");
   }

   static void CheckRange(string field, long n, int length, bool signed) {
      long min = signed ? -(1L << (length * 8 - 1)) : 0;
      long max = signed ? (1L << (length * 8 - 1)) - 1 : (1L << (length * 8)) - 1;
      if (n < min || n > max) throw new McpException($"{n} doesn't fit in '{field}' ({length} byte{(length == 1 ? "" : "s")}, {(signed ? "signed" : "unsigned")}): allowed range is {min} to {max}.");
   }

   static void WritePointerField(RomSession s, ITableRun run, int index, Field f, ArrayRunElementSegment seg, int address, string value) {
      var model = s.Model;
      var text = value.Trim();
      if (text.StartsWith('@') || (text.StartsWith('<') && text.EndsWith('>') && !text.Contains('\n'))) {
         var target = text.Equals("<null>", StringComparison.OrdinalIgnoreCase) ? HavenSoft.HexManiac.Core.ViewModels.DataFormats.Pointer.NULL : s.ResolveAddress(text.TrimStart('@'));
         if (f.Key == f.Seg.Name) new ModelTable(model, run, () => s.Token)[index].SetAddress(f.Seg.Name, target);
         else model.UpdateArrayPointer(s.Token, seg, run.ElementContent, index, address, target);
         return;
      }

      var destination = model.ReadPointer(address);
      if (destination < 0 || destination >= model.Count)
         throw new McpException($"'{f.Key}' is a null pointer, so there is no data to edit. Point it at existing data with '@ABCDEF' first, or create the data with hma_edit.");
      var destinationRun = model.GetNextRun(destination);
      if (destinationRun.Start != destination) destinationRun = null;

      if (destinationRun is PCSRun || (destinationRun == null && seg is ArrayRunPointerSegment { InnerFormat: "\"\"" })) {
         value = TextTools.Escape(value);
         var bytes = model.TextConverter.Convert(value, out var bad);
         if (bad) throw new McpException($"The text for '{f.Key}' has characters the game's font doesn't have.");
         var pcs = destinationRun as PCSRun ?? new PCSRun(model, destination, Math.Max(1, PCSString.ReadString(model, destination, true)), new SortedSpan<int>(address));
         pcs = model.RelocateForExpansion(s.Token, pcs, bytes.Count);
         pcs = (PCSRun)pcs.DeserializeRun(value, s.Token, out _, out _);
         model.ObserveRunWritten(s.Token, pcs);
         return;
      }

      IStreamRun stream;
      if (destinationRun is IStreamRun streamRun) stream = streamRun;
      else if (destinationRun is ITableRun tableRun)
         stream = new TableStreamRun(model, tableRun.Start, tableRun.PointerSources, tableRun.FormatString, tableRun.ElementContent, new FixedLengthStreamStrategy(tableRun.ElementCount));
      else throw new McpException($"HMA doesn't know the format of the data '{f.Key}' points to ({destination.ToAddress()}), so it can't be written as text. Use '@ABCDEF' to repoint it, or hma_edit/write_bytes at that address.");

      // same steps as HMA's TextStreamElementViewModel (the GUI's text box for nested data)
      var newRun = stream.DeserializeRun(value.Replace("\r\n", "\n").Replace("\n", Environment.NewLine), s.Token, out var changedOffsets, out _);
      model.ObserveRunWritten(s.Token, newRun);
      if (model.GetNextRun(newRun.Start) is ITableRun written && written.Start == newRun.Start) {
         foreach (var change in changedOffsets) {
            var offsets = written.ConvertByteOffsetToArrayOffset(change);
            written.NotifyChildren(model, s.Token, offsets.ElementIndex, offsets.SegmentIndex);
         }
      }
   }

   static void WriteTuple(IDataModel model, ModelDelta token, int address, string field, ArrayRunTupleSegment tuple, string value) {
      var named = tuple.Elements.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
      var assignments = ParseAssignments(value);
      if (assignments == null) {
         // HMA's own positional form: (a b c)
         var data = value.Trim();
         if (!data.StartsWith('(')) data = $"({data})";
         var parts = data.Split(new[] { "(", ")", " " }, StringSplitOptions.RemoveEmptyEntries).ToList();
         TableStreamRun.Recombine(parts, "\"", "\"");
         if (parts.Count != named.Count)
            throw new McpException($"'{field}' has {named.Count} parts ({string.Join(", ", named.Select(e => e.Name))}). Write 'part=value, ...' to change some of them, or all {named.Count} values in order.");
         assignments = named.Select((e, i) => (e.Name, parts[i])).ToList();
      }

      foreach (var (name, raw) in assignments) {
         var element = named.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException($"'{field}' has no part '{name}'. Parts: {string.Join(", ", named.Select(e => e.Name))}");
         int bit = tuple.Elements.TakeWhile(e => e != element).Sum(e => e.BitWidth);
         int n;
         if (!string.IsNullOrEmpty(element.SourceName)) {
            if (!TryParseInt(raw, out n) && !ArrayRunEnumSegment.TryParse(element.SourceName, model, raw, out n)) {
               var close = ArrayRunEnumSegment.GetBestOptions(ArrayRunEnumSegment.GetOptions(model, element.SourceName), raw).Take(8);
               throw new McpException($"'{raw}' is not a valid {element.SourceName} value for {field}.{element.Name}. Closest matches: {string.Join(", ", close)}");
            }
         } else if (bool.TryParse(raw, out var flag)) n = flag ? 1 : 0;
         else if (!TryParseInt(raw, out n)) throw new McpException($"'{raw}' is not a number for {field}.{element.Name}.");
         if (n < 0 || n >= (1L << element.BitWidth)) throw new McpException($"{n} doesn't fit in {field}.{element.Name} ({element.BitWidth} bits): allowed range is 0 to {(1L << element.BitWidth) - 1}.");
         element.Write(model, token, address, bit, n);
      }
   }

   /// <summary>'a=1, b=2' or a JSON object -> assignments; null if the text is neither.</summary>
   static List<(string name, string value)>? ParseAssignments(string value) {
      var text = value.Trim();
      if (text.StartsWith('{')) {
         try {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            return doc.RootElement.EnumerateObject().Select(p => (p.Name, p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText())).ToList();
         } catch (System.Text.Json.JsonException ex) {
            throw new McpException($"Couldn't parse '{value}' as JSON: {ex.Message}");
         }
      }
      if (!text.Contains('=')) return null;
      return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(part => {
         var eq = part.IndexOf('=');
         if (eq <= 0) throw new McpException($"Expected 'part=value' but got '{part}'.");
         return (part[..eq].Trim(), Unquote(part[(eq + 1)..].Trim()));
      }).ToList();
   }

   static void WriteBits(IDataModel model, ModelDelta token, int address, string field, ArrayRunBitArraySegment bits, string value) {
      var text = value.Trim();
      var bytes = Enumerable.Range(address, bits.Length).Select(a => model[a]).ToArray();
      if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
         var hex = text[2..];
         if (hex.Length != bits.Length * 2 || !hex.All(Uri.IsHexDigit)) throw new McpException($"'{field}' is {bits.Length} bytes: give exactly {bits.Length * 2} hex digits after 0x (byte order as read_table shows it).");
         for (int i = 0; i < bits.Length; i++) bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber);
      } else {
         var options = model.GetBitOptions(bits.SourceArrayName)
            ?? throw new McpException($"HMA has no names for the bits of '{field}' ({bits.SourceArrayName}); write it as 0x hex.");
         List<string> items;
         if (text.StartsWith('[')) {
            try { items = System.Text.Json.JsonSerializer.Deserialize<List<string>>(text) ?? new(); }
            catch (System.Text.Json.JsonException ex) { throw new McpException($"Couldn't parse '{value}' as a JSON list of names: {ex.Message}"); }
         } else items = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
         items = items.Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
         var relative = items.Count > 0 && items.All(i => i[0] is '+' or '-');
         if (!relative) Array.Clear(bytes);
         var normalized = options.Select(o => NormalizeName(o)).ToList();
         foreach (var item in items) {
            var on = !relative || item[0] == '+';
            var name = relative ? item[1..].Trim() : item;
            var bit = normalized.IndexOf(NormalizeName(name));
            if (bit < 0 || bit >= bits.Length * 8) {
               var close = ArrayRunEnumSegment.GetBestOptions(options, name).Take(8);
               throw new McpException($"'{name}' is not one of the flags of '{field}'. Closest matches: {string.Join(", ", close)}");
            }
            if (on) bytes[bit / 8] |= (byte)(1 << (bit % 8));
            else bytes[bit / 8] &= (byte)~(1 << (bit % 8));
         }
      }
      token.ChangeData(model, address, bytes);
   }

   static List<(Field field, string value)> ParseWhere(string where, List<Field> fields) {
      var result = new List<(Field, string)>();
      if (string.IsNullOrWhiteSpace(where)) return result;
      foreach (var part in where.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
         var eq = part.IndexOf('=');
         if (eq <= 0) throw new McpException($"where: expected 'field=value' but got '{part}'.");
         var name = part[..eq].Trim();
         var f = fields.FirstOrDefault(x => x.Key == name) ?? throw new McpException($"where: table has no field '{name}'. Fields: {string.Join(", ", fields.Select(x => x.Key))}");
         result.Add((f, part[(eq + 1)..].Trim()));
      }
      return result;
   }

   static bool Matches(object? actual, string wanted) {
      switch (actual) {
         case null: return false;
         case int or long:
            return TryParseInt(wanted, out var n) && Convert.ToInt64(actual) == n;
         case List<string> list:
            return list.Any(item => NormalizeName(item) == NormalizeName(wanted));
         default:
            return NormalizeName(actual.ToString() ?? "") == NormalizeName(wanted);
      }
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
      ArrayRunCalculatedSegment => "calculated",
      ArrayRunRecordSegment => "switch",
      ArrayRunSignedSegment => "signed int",
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
