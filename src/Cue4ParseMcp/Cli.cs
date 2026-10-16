using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cue4ParseMcp.Config;
using Cue4ParseMcp.Core;

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

    /// <summary>Reads user-supplied JSON (--filters, --request files): case-insensitive names, unknown properties rejected.</summary>
    private static readonly JsonSerializerOptions InputJson = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
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
          Cue4ParseMcp export-json <game> <path> [--out DIR] [--config PATH]
              Write the complete property JSON of a package (all objects) or of one Package.Object to a file.
          Cue4ParseMcp query-table <game> <path> [--columns A,B] [--filters JSON] [--offset N] [--limit N] [--config PATH]
              Return one page of DataTable rows (default --offset 0 --limit 50; the service accepts limit 1..500).
          Cue4ParseMcp export-table <game> <path> [--format json|csv] [--columns A,B] [--filters JSON] [--out DIR] [--config PATH]
              Write every matching DataTable row (not just one page) to a file.
          Cue4ParseMcp diagnose <game> [--probe PATH] [--config PATH]
              Sanitized mount/key/mappings/codec report; --probe also tries reading one asset.
          Cue4ParseMcp dependencies <game> <path> [--depth N] [--limit N] [--config PATH]
              Outgoing package references (default --depth 1 --limit 200; depth 1..8, limit 1..2000).
          Cue4ParseMcp export-audio <game> <path> [--mode original|wav] [--out DIR] [--config PATH]
          Cue4ParseMcp batch <game> --request PATH [--run] [--config PATH]
              Preview a batch export described by a JSON request file (writes nothing). --run starts the
              previewed job and waits for it in this process; Ctrl+C cancels it. Jobs do not outlive the command.
          Cue4ParseMcp help | -h | --help

        Options may appear anywhere; every option except --props, --no-materials and --run takes a value.
        Words after <game> are joined with spaces, so asset paths with spaces need no quoting.
        Option values (e.g. --filters JSON, --probe PATH) are single arguments and must be quoted if they contain spaces.
        CLI results are JSON on stdout; logs go to stderr.
        Exit codes: 0 success, 2 invalid command line or request file, 1 runtime error or a batch that did
        not fully succeed (failed, partially failed or cancelled).

          --config PATH   games.json to use (relative to the current directory). Default: the
                          CUE4PARSE_MCP_CONFIG environment variable, else games.json in the working
                          directory, next to the executable, or in one of its parent directories.
          --columns A,B   Comma-separated top-level row fields to return; $rowName is the row key.
          --filters JSON  JSON array of {"column":..,"op":"eq|ne|contains|gt|gte|lt|lte","value":..}, all
                          of which must match.
          --request PATH  Batch request JSON file (relative to the current directory): {"kind":"mesh|textures|
                          anim|json|table|audio", then "paths":[..] or "query"/"type"/"under", optionally
                          "limit", "format", "lods", "materials", "audioMode", "outDir"}.

        Environment: CUE4PARSE_MCP_LOG_LEVEL = Verbose|Debug|Information|Warning|Error|Fatal
                     (default: Warning for the server, Information for CLI commands).
        """;

    private enum TailKind { None, Optional, Required }

    private sealed record CommandSpec(bool TakesGame, TailKind Tail, string TailName, string[] Options, string[]? Required = null);

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
        ["export-json"] = new(true, TailKind.Required, "<path>", ["config", "out"]),
        ["query-table"] = new(true, TailKind.Required, "<path>", ["config", "columns", "filters", "offset", "limit"]),
        ["export-table"] = new(true, TailKind.Required, "<path>", ["config", "format", "columns", "filters", "out"]),
        ["diagnose"] = new(true, TailKind.None, "", ["config", "probe"]),
        ["dependencies"] = new(true, TailKind.Required, "<path>", ["config", "depth", "limit"]),
        ["export-audio"] = new(true, TailKind.Required, "<path>", ["config", "mode", "out"]),
        ["batch"] = new(true, TailKind.None, "", ["config", "request", "run"], Required: ["request"]),
    };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.Ordinal) { "props", "no-materials", "run" };

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "config", "type", "under", "limit", "format", "lods", "out",
        "columns", "filters", "offset", "depth", "probe", "mode", "request",
    };

    private const int DefaultQueryLimit = 50;
    private const int DefaultDependencyDepth = 1;
    private const int DefaultDependencyLimit = 200;

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
        foreach (var name in spec.Required ?? [])
            if (!options.ContainsKey(name))
                throw new UsageException($"Command '{command}' needs --{name}.");

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

        var invocation = new Invocation(command, game, tail, options);
        // Syntax only; ranges (offset >= 0, limit <= 500, depth 1..8, ...) are enforced by the services.
        IntOption(invocation, "offset", 0);
        IntOption(invocation, "depth", 0);
        Columns(invocation);
        Filters(invocation);
        return invocation;
    }

    public static async Task<int> RunAsync(Invocation inv, AppConfig config)
    {
        using var games = new GameManager(config);
        var ct = CancellationToken.None;
        var game = inv.Game!;

        switch (inv.Command)
        {
            case "load":
                Write(await games.LoadAsync(game, reload: false, ct));
                return 0;
            case "search" or "ls" or "inspect":
            {
                // Hold the lease until the result is written so the provider stays mounted for the whole command.
                using var lease = await games.AcquireAsync(game, ct);
                Write(SessionCommand(inv, lease.Session, ct));
                return 0;
            }
            case "batch":
                return await RunBatchAsync(inv, games);
        }

        // Every service below acquires (and releases) its own lease for the whole operation. Diagnostics must not be
        // preceded by a lease here: it reports mount failures instead of failing on them.
        var assets = new AssetOps(games);
        object result = inv.Command switch
        {
            "export-mesh" => await assets.ExportMeshAsync(game, inv.Tail!, inv.Option("format") ?? "psk",
                inv.Option("lods") ?? "highest", !inv.Flag("no-materials"), inv.Option("out"), ct),
            "export-textures" => await assets.ExportTexturesAsync(game, inv.Tail!, inv.Option("format") ?? "png",
                inv.Option("out"), ct),
            "export-anim" => await assets.ExportAnimAsync(game, inv.Tail!, inv.Option("format") ?? "psa",
                inv.Option("out"), ct),
            "export-json" => await new DataOps(games).ExportJsonAsync(game, inv.Tail!, inv.Option("out"), ct),
            "query-table" => await new DataOps(games).QueryTableAsync(game, inv.Tail!, Columns(inv), Filters(inv),
                IntOption(inv, "offset", 0), IntOption(inv, "limit", DefaultQueryLimit), ct),
            "export-table" => await new DataOps(games).ExportTableAsync(game, inv.Tail!, inv.Option("format") ?? "json",
                Columns(inv), Filters(inv), inv.Option("out"), ct),
            "diagnose" => await new DiagnosticsOps(games).DiagnoseAsync(game, inv.Option("probe"), ct),
            "dependencies" => await new DependencyOps(games).GetAsync(game, inv.Tail!,
                IntOption(inv, "depth", DefaultDependencyDepth), IntOption(inv, "limit", DefaultDependencyLimit), ct),
            "export-audio" => await new AudioOps(games).ExportAudioAsync(game, inv.Tail!, inv.Option("mode") ?? "original",
                inv.Option("out"), ct),
            _ => throw new UnreachableException($"Unhandled command '{inv.Command}'."),
        };

        Write(result);
        return 0;
    }

    private static object SessionCommand(Invocation inv, GameSession session, CancellationToken ct) => inv.Command switch
    {
        "search" => session.Search(inv.Tail ?? "", inv.Option("type"), inv.Option("under"),
            IntOption(inv, "limit", 50), ct),
        "ls" => session.ListFolder(inv.Tail, 500),
        "inspect" => new
        {
            exports = session.GetExports(session.ParsePath(inv.Tail!).PackagePath),
            properties = inv.Flag("props") ? session.ToJson(inv.Tail!, 200000) : null,
        },
        _ => throw new UnreachableException($"Unhandled command '{inv.Command}'."),
    };

    /// <summary>
    /// Previews by default. With --run, starts the preview and waits for the job in this process; the job (and its lease)
    /// ends with the process, so jobs cannot be resumed or polled from a later invocation.
    /// </summary>
    private static async Task<int> RunBatchAsync(Invocation inv, GameManager games)
    {
        BatchRequest request;
        try
        {
            request = JsonSerializer.Deserialize<BatchRequest>(File.ReadAllText(inv.Option("request")!), InputJson)
                      ?? throw new JsonException("the request must be a JSON object, not null.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"error: Batch request file '{inv.Option("request")}' is not usable: {ex.Message}");
            return 2;
        }

        using var jobs = new BatchJobs(games, new DataOps(games), new AudioOps(games));
        var preview = await jobs.PreviewAsync(inv.Game, request, CancellationToken.None);
        if (!inv.Flag("run"))
        {
            Write(preview);
            return 0;
        }

        // First Ctrl+C cancels the job cooperatively and still prints its final status; a second one terminates.
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            if (cts.IsCancellationRequested) return;
            e.Cancel = true;
            Console.Error.WriteLine("Cancelling batch job; press Ctrl+C again to terminate immediately.");
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            BatchJobStatus started;
            try
            {
                started = await jobs.StartAsync(preview.PreviewId, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                Console.Error.WriteLine("error: Batch cancelled before the job started; nothing was exported.");
                return 1;
            }

            // On cancellation WaitAsync cancels the job itself and returns its final status once the manifest is
            // written and the lease released.
            var status = await jobs.WaitAsync(started.JobId, cts.Token);
            Write(status);
            return status.State == "completed" ? 0 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static void Write(object? value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));

    private static int IntOption(Invocation inv, string name, int fallback)
    {
        var text = inv.Option(name);
        if (text == null) return fallback;
        return int.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new UsageException($"--{name} must be an integer, got '{text}'.");
    }

    private static string[]? Columns(Invocation inv)
    {
        var text = inv.Option("columns");
        if (text == null) return null;
        var columns = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return columns.Length > 0 ? columns : throw new UsageException("--columns must name at least one column.");
    }

    private static TableFilter[]? Filters(Invocation inv)
    {
        var text = inv.Option("filters");
        if (text == null) return null;
        try
        {
            var filters = JsonSerializer.Deserialize<TableFilter[]>(text, InputJson);
            if (filters == null || filters.Any(f => f is null))
                throw new UsageException("--filters must be a JSON array of filter objects.");
            return filters;
        }
        catch (JsonException ex)
        {
            throw new UsageException($"--filters is not a valid JSON filter array: {ex.Message}");
        }
    }
}
