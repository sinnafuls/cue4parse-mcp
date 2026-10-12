using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cue4ParseMcp.Config;

/// <summary>A games.json problem the user has to fix (missing file, invalid JSON...).</summary>
public sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class AppConfig
{
    public const string ConfigEnvVar = "CUE4PARSE_MCP_CONFIG";

    /// <summary>Where exports go when a tool call doesn't pass out_dir and the game has no outputDir.</summary>
    public string? DefaultOutputDir { get; set; }

    /// <summary>Downloaded mappings / native dlls. Defaults to %LOCALAPPDATA%\Cue4ParseMcp.</summary>
    public string? CacheDir { get; set; }

    /// <summary>Optional path to an existing oodle-data-shared.dll (e.g. FModel's Output\.data copy).</summary>
    public string? OodlePath { get; set; }

    /// <summary>Optional path to an existing zlib-ng2.dll.</summary>
    public string? ZlibPath { get; set; }

    public Dictionary<string, GameConfig> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Absolute path of the loaded games.json.</summary>
    [JsonIgnore] public string SourcePath { get; private set; } = "";

    public string ResolvedCacheDir => CacheDir
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cue4ParseMcp");

    public string ResolvedDefaultOutputDir => DefaultOutputDir
        ?? Path.Combine(Path.GetDirectoryName(SourcePath)!, "exports");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Config lookup order: explicit path (--config), CUE4PARSE_MCP_CONFIG, then games.json in the working
    /// directory, executable's directory, or one of its parents.
    /// Relative local paths inside the file resolve against the file's directory and %VARS% are expanded,
    /// so the result does not depend on the caller's working directory.
    /// </summary>
    public static AppConfig Load(string? explicitPath = null)
    {
        var path = explicitPath is { Length: > 0 } ? FromUser(explicitPath, "--config")
            : Environment.GetEnvironmentVariable(ConfigEnvVar) is { Length: > 0 } env ? FromUser(env, ConfigEnvVar)
            : FindDefault() ?? throw new ConfigException(
                "No games.json found in the working directory, next to the executable, or in its parent directories. Copy games.example.json " +
                $"to games.json, or pass --config PATH / set {ConfigEnvVar}.");

        AppConfig config;
        try
        {
            config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions)
                     ?? throw new ConfigException($"Config {path} is empty (null).");
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"Config {path} is not valid JSON: {ex.Message}" +
                                      " Backslashes in JSON strings must be doubled (C:\\\\Games) or written as forward slashes.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"Cannot read config {path}: {ex.Message}", ex);
        }

        var baseDir = Path.GetDirectoryName(path)!;
        config.SourcePath = path;
        config.DefaultOutputDir = ResolvePath(config.DefaultOutputDir, baseDir);
        config.CacheDir = ResolvePath(config.CacheDir, baseDir);
        config.OodlePath = ResolvePath(config.OodlePath, baseDir);
        config.ZlibPath = ResolvePath(config.ZlibPath, baseDir);

        var games = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, game) in config.Games ?? new Dictionary<string, GameConfig>())
        {
            if (game is null) throw new ConfigException($"Config {path}: game '{id}' has no settings.");
            if (!games.TryAdd(id, game)) throw new ConfigException($"Config {path}: game id '{id}' is defined twice (ids are case-insensitive).");
            game.PaksDir = ResolvePath(game.PaksDir, baseDir) ?? "";
            game.Mappings = ResolvePath(game.Mappings, baseDir);
            game.OutputDir = ResolvePath(game.OutputDir, baseDir);
        }
        config.Games = games;
        return config;
    }

    /// <summary>A config path given on the command line or in the environment: relative to the working directory.</summary>
    private static string FromUser(string value, string source)
    {
        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
        return File.Exists(path) ? path : throw new ConfigException($"Config file from {source} not found: {path}");
    }

    private static string? FindDefault()
    {
        var cwd = Path.Combine(Environment.CurrentDirectory, "games.json");
        if (File.Exists(cwd)) return cwd;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "games.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? ResolvePath(string? value, string baseDir) => string.IsNullOrWhiteSpace(value)
        ? null
        : Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()), baseDir);
}

public sealed class GameConfig
{
    public string? DisplayName { get; set; }

    /// <summary>The game's Content\Paks directory (absolute after <see cref="AppConfig.Load"/>).</summary>
    public string PaksDir { get; set; } = "";

    /// <summary>CUE4Parse EGame name, e.g. GAME_Valorant or GAME_DeadByDaylight.</summary>
    public string UeVersion { get; set; } = "";

    /// <summary>Main AES key (hex, 0x-prefixed or not). Used when AesUrl is unset or unreachable.</summary>
    public string? AesKey { get; set; }

    /// <summary>Extra keys for encrypted chunks: pak/utoc encryption GUID -> key.</summary>
    public Dictionary<string, string>? DynamicKeys { get; set; }

    /// <summary>Endpoint returning FModel-style JSON {"mainKey": "...", "dynamicKeys": [...]}, e.g. uedb.dev.</summary>
    public string? AesUrl { get; set; }

    /// <summary>Local .usmap file (absolute after <see cref="AppConfig.Load"/>). Used when MappingsUrl is unset or unreachable.</summary>
    public string? Mappings { get; set; }

    /// <summary>Endpoint returning a mappings descriptor (uedb.dev or FModel-style); the .usmap is cached.</summary>
    public string? MappingsUrl { get; set; }

    /// <summary>Per-game default export folder (absolute after <see cref="AppConfig.Load"/>).</summary>
    public string? OutputDir { get; set; }
}
