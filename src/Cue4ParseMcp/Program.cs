using CUE4Parse;
using Cue4ParseMcp.Config;
using Cue4ParseMcp.Core;
using Cue4ParseMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Serilog;
using Serilog.Events;

const string LogLevelEnvVar = "CUE4PARSE_MCP_LOG_LEVEL";

Invocation invocation;
try
{
    invocation = Cli.Parse(args);
}
catch (UsageException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(Cli.Usage);
    return 2;
}

if (invocation.Command == "help")
{
    Console.WriteLine(Cli.Usage);
    return 0;
}

var isServer = invocation.Command == "serve";

// stdout is the MCP channel (or the CLI's JSON result), so every log line goes to stderr.
var levelText = Environment.GetEnvironmentVariable(LogLevelEnvVar);
var levelFromEnv = Enum.TryParse<LogEventLevel>(levelText, ignoreCase: true, out var level) && Enum.IsDefined(level);
if (!levelFromEnv) level = isServer ? LogEventLevel.Warning : LogEventLevel.Information;
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(level)
    .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose, outputTemplate: "{Timestamp:HH:mm:ss} {Level:u3} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
CUE4ParseLog.UseLogger(Log.Logger);
if (!levelFromEnv && !string.IsNullOrWhiteSpace(levelText))
    Log.Warning("Ignoring {Variable}={Value}: use Verbose, Debug, Information, Warning, Error or Fatal", LogLevelEnvVar, levelText);

try
{
    var config = AppConfig.Load(invocation.Option("config"));
    Log.Debug("Using config {Path}", config.SourcePath);
    return isServer ? await RunServerAsync(config, level) : await Cli.RunAsync(invocation, config);
}
catch (ConfigException ex)
{
    Log.Fatal("{Error}", ex.Message);
    return 1;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Fatal error");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static async Task<int> RunServerAsync(AppConfig config, LogEventLevel level)
{
    // Empty builder: no appsettings.json or other configuration picked up from the MCP client's working directory.
    var builder = Host.CreateEmptyApplicationBuilder(settings: null);
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace);
    // Serilog and Microsoft.Extensions.Logging levels share the same numeric order (Verbose/Trace .. Fatal/Critical).
    builder.Logging.SetMinimumLevel((Microsoft.Extensions.Logging.LogLevel)(int)level);

    builder.Services.AddSingleton(_ => new GameManager(config));
    builder.Services.AddSingleton<AssetOps>();
    builder.Services.AddSingleton<DataOps>();
    builder.Services.AddSingleton<DiagnosticsOps>();
    builder.Services.AddSingleton<DependencyOps>();
    builder.Services.AddSingleton<AudioOps>();
    builder.Services.AddSingleton<BatchJobs>();
    builder.Services
        .AddMcpServer(o => o.ServerInfo = new Implementation { Name = Cli.ServerName, Version = Cli.Version })
        .WithStdioServerTransport()
        .WithTools<AssetTools>()
        .WithTools<DataTools>()
        .WithTools<AnalysisTools>()
        .WithTools<AudioTools>()
        .WithTools<BatchTools>()
        .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) =>
        {
            try
            {
                return await next(context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Surface the real reason (bad path, wrong type, missing key...) so the agent can correct itself.
                Log.Warning(ex, "Tool {Tool} failed", context.Params?.Name);
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"{ex.GetType().Name}: {ex.Message}" }],
                    IsError = true,
                };
            }
        }));

    await builder.Build().RunAsync();
    return 0;
}

