using ModelContextProtocol;

namespace HmaMcp;

/// <summary>
/// Every file a tool reads or writes goes through Resolve. With a ROM folder set (--rom-dir; required in HTTP mode),
/// paths are relative to it and must stay inside it, so a server reachable over a tunnel can't touch other files.
/// </summary>
public static class Paths {
   public static string? Root { get; private set; }

   public static void SetRoot(string folder) {
      var full = Path.GetFullPath(folder);
      if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"ROM folder not found: {full}");
      Root = Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar;
   }

   /// <summary>A full path for a tool's file argument; relative paths are inside the ROM folder when one is set.</summary>
   public static string Resolve(string path) {
      if (string.IsNullOrWhiteSpace(path)) throw new McpException("A file path is required.");
      var full = Root == null ? Path.GetFullPath(path) : Path.GetFullPath(path, Root);
      if (Root != null) {
         var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
         if (!full.StartsWith(Root, comparison))
            throw new McpException($"'{path}' is outside the ROM folder this server allows ({Root}). Use a path inside it, e.g. a file name relative to it.");
      }
      return full;
   }

   /// <summary>Show a path to the client: relative to the ROM folder when one is set, so the server's own folders aren't revealed.</summary>
   public static string Show(string full) =>
      Root != null && full.StartsWith(Root, StringComparison.OrdinalIgnoreCase) ? full[Root.Length..].Replace('\\', '/') : full;
}
