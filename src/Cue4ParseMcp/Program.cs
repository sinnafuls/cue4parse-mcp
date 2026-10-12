using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton<GameManager>();
    builder.Services.AddSingleton<AssetOps>();
    builder.Services
        .AddMcpServer(o => o.ServerInfo = new Implementation { Name = Cli.ServerName, Version = Cli.Version })
        .WithStdioServerTransport()
        .WithTools<AssetTools>()
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

/// <summary>A command line that does not match <see cref="Cli.Usage"/>; reported with exit code 2.</summary>
sealed class UsageException(string message) : Exception(message);

/// <summary>A parsed command line. <see cref="Tail"/> is every word after the game id joined by spaces.</summary>
sealed record Invocation(string Command, string? Game, string? Tail, IReadOnlyDictionary<string, string?> Options)
{
    public string? Option(string name) => Options.GetValueOrDefault(name);
    public bool Flag(string name) => Options.ContainsKey(name);
}

static class Cli
{
    public const string ServerName = "cue4parse-mcp";

    public static readonly string Version =
        (typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const string Usage = """
        Usage:
          Cue4ParseMcp [serve] [--config PATH]
              Run the MCP server on stdio (the default when no command is given).
          Cue4ParseMcp load <game> [--config PATH]
          Cue4ParseMcp search <game> [query...] [--type CLASS] [--under FOLDER] [--limit N] [--config PATH]
          Cue4ParseMcp ls <game> [folder] [--config PATH]
          Cue4ParseMcp inspect <game> <path> [--props] [--config PATH]
          Cue4ParseMcp export-mesh <game> <path> [--format psk|gltf|ueformat|usd] [--lods highest|lowest|all] [--no-materials] [--out DIR] [--config PATH]
          Cue4ParseMcp export-textures <game> <path> [--format png|tga|jpeg|webp] [--out DIR] [--config PATH]
          Cue4ParseMcp export-anim <game> <path> [--format psa|ueformat|usd] [--out DIR] [--config PATH]
          Cue4ParseMcp help | -h | --help

        Options may appear anywhere; every option except --props and --no-materials takes a value.
        Words after <game> are joined with spaces, so asset paths with spaces need no quoting.
        CLI results are JSON on stdout; logs go to stderr.

          --config PATH   games.json to use (relative to the current directory). Default: the
                          CUE4PARSE_MCP_CONFIG environment variable, else games.json in the working
                          directory, next to the executable, or in one of its parent directories.

        Environment: CUE4PARSE_MCP_LOG_LEVEL = Verbose|Debug|Information|Warning|Error|Fatal
                     (default: Warning for the server, Information for CLI commands).
        """;

    private enum TailKind { None, Optional, Required }

    private sealed record CommandSpec(bool TakesGame, TailKind Tail, string TailName, string[] Options);

    private static readonly Dictionary<string, CommandSpec> Commands = new(StringComparer.Ordinal)
    {
        ["serve"] = new(false, TailKind.None, "", ["config"]),
        ["load"] = new(true, TailKind.None, "", ["config"]),
        ["search"] = new(true, TailKind.Optional, "query", ["config", "type", "under", "limit"]),
        ["ls"] = new(true, TailKind.Optional, "folder", ["config"]),
        ["inspect"] = new(true, TailKind.Required, "<path>", ["config", "props"]),
        ["export-mesh"] = new(true, TailKind.Required, "<path>", ["config", "format", "lods", "no-materials", "out"]),
        ["export-textures"] = new(true, TailKind.Required, "<path>", ["config", "format", "out"]),
        ["export-anim"] = new(true, TailKind.Required, "<path>", ["config", "format", "out"]),
    };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.Ordinal) { "props", "no-materials" };
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal) { "config", "type", "under", "limit", "format", "lods", "out" };

    private static readonly Invocation Help = new("help", null, null, new Dictionary<string, string?>());

    public static Invocation Parse(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help") return Help;
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            string? value = null;
            if (ValueOptions.Contains(name))
            {
                if (i + 1 >= args.Length || args[i + 1].Length == 0 || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new UsageException($"Option {arg} requires a value.");
                value = args[++i];
            }
            else if (!FlagOptions.Contains(name))
                throw new UsageException($"Unknown option '{arg}'.");

            if (!options.TryAdd(name, value))
                throw new UsageException($"Option {arg} was given more than once.");
        }

        var command = positional.Count > 0 ? positional[0] : "serve";
        if (command == "help") return Help;
        if (!Commands.TryGetValue(command, out var spec))
            throw new UsageException($"Unknown command '{command}'.");
        foreach (var name in options.Keys)
            if (!spec.Options.Contains(name))
                throw new UsageException($"Command '{command}' does not accept --{name}.");

        var words = positional.Skip(1).ToList();
        string? game = null;
        if (spec.TakesGame)
        {
            if (words.Count == 0) throw new UsageException($"Command '{command}' needs a <game> id from games.json.");
            game = words[0];
            words.RemoveAt(0);
        }

        var tail = words.Count > 0 ? string.Join(' ', words) : null;
        if (spec.Tail == TailKind.None && tail != null)
            throw new UsageException($"Unexpected argument '{words[0]}' for command '{command}'.");
        if (spec.Tail == TailKind.Required && tail == null)
            throw new UsageException($"Command '{command}' needs an asset {spec.TailName}.");

        if (options.TryGetValue("limit", out var limit) && !(int.TryParse(limit, out var n) && n > 0))
            throw new UsageException($"--limit must be a positive integer, got '{limit}'.");

        return new Invocation(command, game, tail, options);
    }

    public static async Task<int> RunAsync(Invocation inv, AppConfig config)
    {
        using var games = new GameManager(config);
        var ops = new AssetOps(games);
        var ct = CancellationToken.None;
        var game = inv.Game!;

        if (inv.Command == "load")
        {
            Console.WriteLine(JsonSerializer.Serialize(await games.LoadAsync(game, reload: false, ct), Json));
            return 0;
        }

        // Hold the lease until the result is written so the provider stays mounted for the whole command.
        using var lease = await games.AcquireAsync(game, ct);
        var session = lease.Session;
        object result = inv.Command switch
        {
            "search" => session.Search(inv.Tail ?? "", inv.Option("type"), inv.Option("under"),
                int.Parse(inv.Option("limit") ?? "50"), ct),
            "ls" => session.ListFolder(inv.Tail, 500),
            "inspect" => new
            {
                exports = session.GetExports(session.ParsePath(inv.Tail!).PackagePath),
                properties = inv.Flag("props") ? session.ToJson(inv.Tail!, 200000) : null,
            },
            "export-mesh" => await ops.ExportMeshAsync(game, inv.Tail!, inv.Option("format") ?? "psk",
                inv.Option("lods") ?? "highest", !inv.Flag("no-materials"), inv.Option("out"), ct),
            "export-textures" => await ops.ExportTexturesAsync(game, inv.Tail!, inv.Option("format") ?? "png",
                inv.Option("out"), ct),
            "export-anim" => await ops.ExportAnimAsync(game, inv.Tail!, inv.Option("format") ?? "psa",
                inv.Option("out"), ct),
            _ => throw new UnreachableException($"Unhandled command '{inv.Command}'."),
        };

        Console.WriteLine(JsonSerializer.Serialize(result, Json));
        return 0;
    }
}
