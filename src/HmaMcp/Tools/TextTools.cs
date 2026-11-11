using System.ComponentModel;
using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using static HmaMcp.Tools.DataTools;

namespace HmaMcp.Tools;

[McpServerToolType]
public static class TextTools {
   [McpServerTool(Name = "read_text", ReadOnly = true), Description("Read the game text (PCS) at an address, e.g. a msgbox text a script points to. Shows the text in HMA's form (plain line breaks, a blank line for a new text box, [player] etc.), its byte length (including the end byte) and the addresses that point to it.")]
   public static string ReadText(RomSessions sessions,
      [Description("Anchor or hex address where the text starts")] string address) => sessions.With(s => {
      var start = s.ResolveAddress(address);
      var length = PCSString.ReadString(s.Model, start, true);
      if (length < 1) throw new McpException($"There is no text at {start.ToAddress()} (no end-of-text byte FF found where text should be).");
      var run = s.Model.GetNextRun(start);
      var known = run.Start == start;
      return Json.Of(new {
         address = start.ToAddress(),
         anchor = s.Anchor(start),
         text = Display(s.Model, start, length),
         length,
         formatted_as_text = known && run is PCSRun,
         pointed_from = Sources(s.Model, start, run).Take(20).Select(a => a.ToAddress()),
      });
   });

   [McpServerTool(Name = "write_text", Destructive = true), Description("Replace the game text at an address. Use the form read_text returns: plain line breaks (a blank line starts a new text box), or HMA's escapes \\n (new line), \\l (scroll), \\pn (new box); [player], [rival] and so on. Characters the font doesn't have are rejected. If the new text is longer, HMA moves it to free space and updates every pointer to it. Every pointer to it sees the change. One undo step.")]
   public static string WriteText(RomSessions sessions,
      [Description("Anchor or hex address where the text starts")] string address,
      [Description("New text")] string text) => sessions.Edit(s => {
      var model = s.Model;
      var start = s.ResolveAddress(address);
      var oldLength = PCSString.ReadString(model, start, true);
      if (oldLength < 1) throw new McpException($"There is no text at {start.ToAddress()}. Use read_text to check the address.");
      var before = Display(model, start, oldLength);
      text = Escape(text);
      var bytes = model.TextConverter.Convert(text, out var bad);
      if (bad) throw new McpException("The text has characters the game's font doesn't have. Use read_text on existing text to see what's allowed; raw bytes can be written as \\!XX.");
      var existing = model.GetNextRun(start);
      var sources = Sources(model, start, existing);

      if (bytes.Count > oldLength && sources.Count == 0) {
         // with no known pointers the text can't be moved, so it must fit in place (followed by free space)
         for (int i = start + oldLength; i < start + bytes.Count; i++) {
            if (i >= model.Count || model[i] != 0xFF)
               throw new McpException($"The new text is {bytes.Count} bytes but only {oldLength} fit at {start.ToAddress()}, and no pointers to it were found, so it can't be moved. Shorten it, or edit it through whatever points to it (compile_script, write_table_field).");
         }
      }

      if (existing.Start < start && existing.Start + existing.Length > start)
         throw new McpException($"{start.ToAddress()} is inside {RunKind(existing)} data that starts at {existing.Start.ToAddress()}, not the start of a text.");
      if (existing.Start == start && existing is not PCSRun && existing is not NoInfoRun)
         throw new McpException($"HMA has {RunKind(existing)} data at {start.ToAddress()}, not text. Use the tool for that kind of data.");
      var pcs = existing.Start == start && existing is PCSRun run ? run : new PCSRun(model, start, oldLength, sources);
      if (existing is not PCSRun) model.ObserveRunWritten(s.Token, pcs);
      pcs = (PCSRun)model.RelocateForExpansion(s.Token, pcs, bytes.Count);
      var written = (PCSRun)pcs.DeserializeRun(text, s.Token, out _, out _);
      model.ObserveRunWritten(s.Token, written);
      // shorter text: free the old tail, as HMA's editor does
      if (written.Start == start && bytes.Count < oldLength) {
         for (int i = start + bytes.Count; i < start + oldLength; i++) if (model[i] != 0xFF) s.Token.ChangeData(model, i, 0xFF);
      }

      var newLength = PCSString.ReadString(model, written.Start, true);
      return Json.Of(new {
         address = written.Start.ToAddress(),
         moved_from = written.Start != start ? start.ToAddress() : null,
         before,
         after = Display(model, written.Start, newLength),
         old_length = oldLength,
         new_length = newLength,
         pointed_from = written.PointerSources?.Take(20).Select(a => a.ToAddress()),
      });
   });

   /// <summary>Text as HMA shows it, with LF line breaks.</summary>
   internal static string Display(IDataModel model, int start, int length) =>
      Unquote(model.TextConverter.Convert(model, start, length)).Replace("\r\n", "\n");

   /// <summary>
   /// Turn line breaks into HMA's explicit escapes before HMA parses the text. HMA's parser only sees
   /// Environment.NewLine (so LF is dropped on Windows), and ignores a line break in the last few characters.
   /// Same rules as HMA: the first break in a box is \n, later ones \l, a blank line is \pn (new box).
   /// A break right after an explicit escape is just layout, as in the text HMA shows.
   /// </summary>
   internal static string Escape(string text) {
      text = text.Replace("\r\n", "\n").Replace('\r', '\n');
      var sb = new System.Text.StringBuilder();
      bool wrapNext = true, afterEscape = false;
      for (int i = 0; i < text.Length; i++) {
         var c = text[i];
         if (c == '\n') {
            if (afterEscape) { afterEscape = false; continue; }
            if (i + 1 < text.Length && text[i + 1] == '\n') { sb.Append(@"\pn"); i++; wrapNext = true; }
            else { sb.Append(wrapNext ? @"\n" : @"\l"); wrapNext = false; }
            continue;
         }
         afterEscape = false;
         if (c == '\\' && i + 1 < text.Length) {
            if (text[i + 1] == 'p' && i + 2 < text.Length && text[i + 2] == 'n') { sb.Append(@"\pn"); i += 2; wrapNext = true; afterEscape = true; continue; }
            if (text[i + 1] is 'n' or 'l') { sb.Append(c).Append(text[i + 1]); i++; wrapNext = false; afterEscape = true; continue; }
            sb.Append(c).Append(text[i + 1]); i++; continue;
         }
         sb.Append(c);
      }
      return sb.ToString();
   }

   /// <summary>
   /// Addresses that point to a text. HMA often doesn't know them (e.g. a msgbox in a script it hasn't formatted
   /// yet), so fall back to searching the ROM for the 4-byte pointer, as HMA's own "find pointers" does.
   /// </summary>
   internal static SortedSpan<int> Sources(IDataModel model, int start, IFormattedRun? run) {
      if (run != null && run.Start == start && run.PointerSources?.Count > 0) return run.PointerSources;
      var b0 = (byte)start; var b1 = (byte)(start >> 8); var b2 = (byte)(start >> 16); var b3 = (byte)((start >> 24) + 0x08);
      var found = new List<int>();
      var raw = model.RawData;
      for (int i = 0; i + 3 < model.Count; i++) {
         if (raw[i] == b0 && raw[i + 1] == b1 && raw[i + 2] == b2 && raw[i + 3] == b3) found.Add(i);
      }
      return found.Count == 0 ? SortedSpan<int>.None : new SortedSpan<int>(found);
   }
}
