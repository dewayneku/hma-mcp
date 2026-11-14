using System.Reflection;
using System.Security.Cryptography;
using HmaMcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Usage:
//   hma-mcp                                   stdio (Claude Code, Claude Desktop, Cursor, ...)
//   hma-mcp --http --rom-dir <folder>         Streamable HTTP on http://127.0.0.1:3333/mcp, for clients that need a URL
//          [--port 3333] [--token <secret>]   (ChatGPT, through a tunnel). The token is also read from HMA_MCP_TOKEN.
//   --rom-dir <folder> also works in stdio mode, to keep the server to one folder.
var options = Options.Parse(args);
if (options.Help) {
   Console.Error.WriteLine(Options.Usage);
   return 0;
}

// HMA loads its game reference data from ./resources, and MCP clients start us from anywhere.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);
if (options.RomDir != null) Paths.SetRoot(options.RomDir);

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

if (!options.Http) {
   var builder = Host.CreateApplicationBuilder();
   // stdout is the MCP transport: every log line must go to stderr.
   builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
   builder.Services.AddSingleton<RomSessions>();
   builder.Services.AddMcpServer(o => Configure(o, version)).WithStdioServerTransport().WithToolsFromAssembly();
   await builder.Build().RunAsync();
   return 0;
}

// ---------- HTTP ----------
if (options.RomDir == null) {
   Console.Error.WriteLine("HTTP mode needs --rom-dir <folder>: the only folder the server may read ROMs and images from and save to.");
   return 2;
}
var token = options.Token ?? Environment.GetEnvironmentVariable("HMA_MCP_TOKEN");
bool generated = string.IsNullOrWhiteSpace(token);
if (generated) token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
if (token!.Length < 16) {
   Console.Error.WriteLine("The token must be at least 16 characters.");
   return 2;
}

var web = WebApplication.CreateBuilder();
web.Logging.ClearProviders();
web.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
web.Logging.SetMinimumLevel(LogLevel.Warning);
// localhost only: from outside, the server is reachable only through a tunnel you run
web.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
web.Services.AddSingleton<RomSessions>();
web.Services.AddMcpServer(o => Configure(o, version)).WithHttpTransport().WithToolsFromAssembly();
var app = web.Build();

// Every request needs the token: as the last path segment (/mcp/<token>, for clients that only take a URL)
// or as "Authorization: Bearer <token>" on /mcp. Anything else is a plain 404, which reveals nothing.
app.Use(async (context, next) => {
   var path = context.Request.Path.Value ?? "";
   bool ok = false;
   if (path.Equals("/mcp/" + token, StringComparison.Ordinal) || path.StartsWith("/mcp/" + token + "/", StringComparison.Ordinal)) {
      context.Request.Path = "/mcp" + path[("/mcp/" + token).Length..];
      ok = true;
   } else if (path == "/mcp" || path.StartsWith("/mcp/", StringComparison.Ordinal)) {
      var auth = context.Request.Headers.Authorization.ToString();
      ok = auth.StartsWith("Bearer ", StringComparison.Ordinal) &&
         CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(auth[7..].Trim()), System.Text.Encoding.UTF8.GetBytes(token));
   }
   if (!ok) {
      context.Response.StatusCode = StatusCodes.Status404NotFound;
      return;
   }
   await next();
});
// routing after the token check, so the rewritten /mcp/<token> -> /mcp path is what gets matched
app.UseRouting();
app.MapMcp("/mcp");

Console.Error.WriteLine($"hma-mcp {version} (HTTP)");
Console.Error.WriteLine($"  ROM folder: {Paths.Root}");
Console.Error.WriteLine($"  Local URL:  http://127.0.0.1:{options.Port}/mcp/{token}");
Console.Error.WriteLine($"  Tunnel it, e.g.: cloudflared tunnel --url http://127.0.0.1:{options.Port}");
Console.Error.WriteLine($"  then give your client https://<tunnel-host>/mcp/{token}");
if (generated) Console.Error.WriteLine("  (token generated for this run; pass --token or set HMA_MCP_TOKEN to keep one)");
await app.RunAsync();
return 0;

static void Configure(ModelContextProtocol.Server.McpServerOptions o, string version) {
   o.ServerInfo = new() { Name = "hma-mcp", Version = version };
   o.ServerInstructions = RomSessions.Instructions;
}

sealed record Options(bool Http, int Port, string? Token, string? RomDir, bool Help) {
   public const string Usage = """
      hma-mcp: MCP server for GBA Pokémon ROM hacking (HexManiacAdvance's engine).

        hma-mcp                              stdio, for Claude Code, Claude Desktop, Cursor, ...
        hma-mcp --http --rom-dir <folder>    Streamable HTTP on 127.0.0.1, for clients that connect by URL
                [--port 3333]                (e.g. ChatGPT, through a tunnel such as cloudflared)
                [--token <secret>]           at least 16 characters; default: HMA_MCP_TOKEN, else a random one
        --rom-dir <folder>                   only read and write files inside this folder (any mode)
      """;

   public static Options Parse(string[] args) {
      bool http = false, help = false;
      int port = 3333;
      string? token = null, romDir = null;
      for (int i = 0; i < args.Length; i++) {
         string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
         switch (args[i]) {
            case "--http": http = true; break;
            case "--port": port = int.Parse(Next()); break;
            case "--token": token = Next(); break;
            case "--rom-dir": romDir = Next(); break;
            case "-h" or "--help": help = true; break;
            default: throw new ArgumentException($"Unknown option {args[i]}. Use --help.");
         }
      }
      return new Options(http, port, token, romDir, help);
   }
}
