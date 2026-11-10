using HmaMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// HMA loads its game reference data from ./resources, and MCP clients start us from anywhere.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP transport: every log line must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<RomSessions>();
builder.Services
   .AddMcpServer(o => {
      o.ServerInfo = new() { Name = "hma-mcp", Version = "0.1.0" };
      o.ServerInstructions = RomSessions.Instructions;
   })
   .WithStdioServerTransport()
   .WithToolsFromAssembly();

await builder.Build().RunAsync();
