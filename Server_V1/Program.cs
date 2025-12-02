using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Server_V1.Models;
using Server_V1.Tools;
using Server_V1.Repos;

var builder = Host.CreateApplicationBuilder(args);

// configure sql server connection string from environment variable
var connectionString = "Data Source=DESKTOP-DUIJGIM;Initial Catalog=MCPTrial;Integrated Security=True;Trust Server Certificate=True";
builder.Services.AddSqlServer<ApplicationDbContext>(connectionString);

// register repositories so tools can receive them via DI
builder.Services.AddScoped<CategoriesRepo>();
builder.Services.AddScoped<SubcategoriesRepo>();

// Configure all logs to go to stderr (stdout is used for the MCP protocol messages).
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Add the MCP services: the transport to use (stdio) and the tools to register.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<CategoryTools>()
    .WithTools<IdentityTools>()
    .WithTools<RandomNumberTools>()
    ;

await builder.Build().RunAsync();
