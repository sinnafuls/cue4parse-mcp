using Cue4ParseMcp.Config;
using Serilog;

namespace Cue4ParseMcp.Core;

/// <summary>Owns mounted games and refreshes the selected config file before each operation.</summary>
public sealed class GameManager(AppConfig config) : IDisposable
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly Dictionary<string, GameSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly FileInfo? _configFile = config.SourcePath.Length == 0 ? null : new FileInfo(config.SourcePath);
    private AppConfig _config = config;
    private (DateTime WriteTime, DateTime CreationTime, long Length)? _configStamp;
    private string? _lastReloadError;
    private string? _current;
    private bool _disposed;

    /// <summary>A consistent snapshot for list_games; no mutable configuration is updated in place.</summary>
    public (AppConfig Config, IReadOnlyCollection<string> LoadedGames) GetState()
    {
        lock (_sessions)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshConfig();
            return (_config, _sessions.Keys.ToArray());
        }
    }

    public async Task<LoadSummary> LoadAsync(string gameId, bool reload, CancellationToken ct)
    {
        using var lease = await LoadAndAcquireAsync(gameId, reload, ct);
        return lease.Session.Summary;
    }

    /// <summary>Resolves the configured game an operation would select, without mounting it.</summary>
    public string ResolveGameId(string? gameId)
    {
        lock (_sessions)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshConfig();
            return SelectGameId(gameId);
        }
    }

    // Called under _sessions after refreshing the configuration.
    private string SelectGameId(string? gameId)
    {
        var id = gameId is { Length: > 0 } ? gameId : _current
            ?? (_config.Games.Count == 1 ? _config.Games.Keys.First()
                : throw new InvalidOperationException($"No game loaded. Call load_game first (configured: {string.Join(", ", _config.Games.Keys)})."));
        if (!_config.Games.ContainsKey(id)) throw UnknownGame(id);
        return id;
    }

    /// <summary>Holds both the provider and the operation's configuration snapshot until the lease ends.</summary>
    public async Task<GameLease> AcquireAsync(string? gameId, CancellationToken ct)
    {
        string id;
        lock (_sessions)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshConfig();
            id = SelectGameId(gameId);
            if (_sessions.TryGetValue(id, out var session))
                return new GameLease(session, _config);
        }
        return await LoadAndAcquireAsync(id, reload: false, ct);
    }

    private async Task<GameLease> LoadAndAcquireAsync(string gameId, bool reload, CancellationToken ct)
    {
        lock (_sessions)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshConfig();
            if (!_config.Games.ContainsKey(gameId)) throw UnknownGame(gameId);
        }
        await _loadGate.WaitAsync(ct);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                AppConfig snapshot;
                GameConfig gameConfig;
                string key;
                lock (_sessions)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    RefreshConfig();
                    snapshot = _config;
                    if (!snapshot.Games.TryGetValue(gameId, out gameConfig!))
                        throw UnknownGame(gameId);
                    key = snapshot.Games.Keys.First(k => k.Equals(gameId, StringComparison.OrdinalIgnoreCase));
                    if (!reload && _sessions.TryGetValue(key, out var existing))
                    {
                        _current = key;
                        return new GameLease(existing, snapshot);
                    }
                }

                var session = await GameSession.LoadAsync(key, gameConfig, snapshot, ct);
                GameSession? replaced;
                GameLease lease;
                lock (_sessions)
                {
                    if (_disposed)
                    {
                        session.Release();
                        throw new ObjectDisposedException(nameof(GameManager));
                    }
                    RefreshConfig();
                    if (!_config.Games.TryGetValue(key, out var latestGame))
                    {
                        session.Release();
                        throw UnknownGame(key);
                    }
                    // A save can arrive during a slow mount. Never publish a provider built from obsolete keys/paths.
                    if (!SameProviderSettings(snapshot, _config) || !SameMountSettings(gameConfig, latestGame))
                    {
                        session.Release();
                        continue;
                    }
                    _sessions.TryGetValue(key, out replaced);
                    _sessions[key] = session;
                    _current = key;
                    lease = new GameLease(session, _config);
                }
                replaced?.Release();
                return lease;
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private ArgumentException UnknownGame(string gameId) =>
        new($"Unknown game '{gameId}'. Configured: {string.Join(", ", _config.Games.Keys)}");

    // Called only under _sessions. Checking on demand also handles editor rename/replace saves
    // without a watcher, background parsing, or a delay before the next MCP call sees the new games.
    private void RefreshConfig()
    {
        if (_configFile is not { } file) return; // Programmatic configurations have no backing file.
        AppConfig next;
        (DateTime WriteTime, DateTime CreationTime, long Length) stamp;
        try
        {
            file.Refresh();
            if (!file.Exists) throw new FileNotFoundException("The configuration file is temporarily missing.", file.FullName);
            stamp = (file.LastWriteTimeUtc, file.CreationTimeUtc, file.Length);
            if (_configStamp == stamp) return;
            next = AppConfig.Load(file.FullName);
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Do not cache a failed read: a sharing violation can clear without changing the file timestamp.
            if (_lastReloadError != ex.Message)
                Log.Warning("Could not reload config {Path}; keeping the last valid configuration: {Reason}", file.FullName, ex.Message);
            _lastReloadError = ex.Message;
            return;
        }

        var providerChanged = !SameProviderSettings(_config, next);
        List<string>? retired = null;
        foreach (var (id, session) in _sessions)
        {
            if (providerChanged || !next.Games.TryGetValue(id, out var game) || !SameMountSettings(session.Config, game))
                (retired ??= []).Add(id);
        }
        _config = next;
        _configStamp = stamp;
        _lastReloadError = null;
        if (_current != null && !next.Games.ContainsKey(_current)) _current = null;
        if (retired != null)
        {
            foreach (var id in retired)
            {
                var session = _sessions[id];
                _sessions.Remove(id);
                // Active leases keep the old provider alive. New calls mount from the new snapshot.
                session.Release();
            }
        }
    }

    private static bool SameProviderSettings(AppConfig left, AppConfig right) =>
        string.Equals(left.ResolvedCacheDir, right.ResolvedCacheDir, PathComparison) &&
        string.Equals(left.OodlePath, right.OodlePath, PathComparison) &&
        string.Equals(left.ZlibPath, right.ZlibPath, PathComparison);

    private static bool SameMountSettings(GameConfig left, GameConfig right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (!string.Equals(left.PaksDir, right.PaksDir, PathComparison) ||
            !string.Equals(left.UeVersion, right.UeVersion, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(left.AesKey, right.AesKey, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(left.AesUrl, right.AesUrl, StringComparison.Ordinal) ||
            !string.Equals(left.Mappings, right.Mappings, PathComparison) ||
            !string.Equals(left.MappingsUrl, right.MappingsUrl, StringComparison.Ordinal) ||
            (left.DynamicKeys?.Count ?? 0) != (right.DynamicKeys?.Count ?? 0))
            return false;
        if (left.DynamicKeys != null)
        {
            foreach (var (guid, key) in left.DynamicKeys)
            {
                if (right.DynamicKeys == null || !right.DynamicKeys.TryGetValue(guid, out var otherKey) ||
                    !string.Equals(otherKey, key, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        return true;
    }

    public string ResolveOutputDir(GameLease lease, string? outDir)
    {
        if (outDir is { Length: > 0 }) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(outDir));
        var snapshot = lease.Configuration;
        if (snapshot.Games[lease.Session.Id].OutputDir is { Length: > 0 } gameOutput) return gameOutput;
        return Path.Combine(snapshot.ResolvedDefaultOutputDir, lease.Session.Id);
    }

    public void Dispose()
    {
        List<GameSession> sessions;
        lock (_sessions)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
            _current = null;
        }
        foreach (var session in sessions) session.Release();
    }
}

/// <summary>Keeps a provider and its configuration snapshot alive for one operation.</summary>
public sealed class GameLease : IDisposable
{
    private int _released;

    internal GameLease(GameSession session, AppConfig configuration)
    {
        session.AddRef();
        Session = session;
        Configuration = configuration;
    }

    public GameSession Session { get; }
    internal AppConfig Configuration { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) Session.Release();
    }
}
