using Cue4ParseMcp.Config;

namespace Cue4ParseMcp.Core;

/// <summary>Holds mounted games. Several can be loaded at once; tools default to the most recently loaded one.</summary>
public sealed class GameManager(AppConfig config) : IDisposable
{
    private readonly Dictionary<string, GameSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private string? _current;
    private bool _disposed;

    public AppConfig Config => config;

    public IReadOnlyCollection<string> LoadedGames
    {
        get { lock (_sessions) return _sessions.Keys.ToList(); }
    }

    /// <summary>Mounts a game (or returns the already mounted one) and makes it the default for later calls.</summary>
    public async Task<LoadSummary> LoadAsync(string gameId, bool reload, CancellationToken ct)
    {
        using var lease = await LoadAndAcquireAsync(gameId, reload, ct);
        return lease.Session.Summary;
    }

    /// <summary>
    /// Returns a lease on the requested (or current) game, loading it on first use so tools work without an explicit load_game.
    /// Hold the lease for the whole operation: a reload only disposes the replaced provider once every lease on it is released.
    /// </summary>
    public async Task<GameLease> AcquireAsync(string? gameId, CancellationToken ct)
    {
        string? id;
        lock (_sessions)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            id = gameId is { Length: > 0 } ? gameId : _current;
            if (id != null && _sessions.TryGetValue(id, out var session))
                return new GameLease(session);
        }

        if (id is null)
        {
            if (config.Games.Count == 1) id = config.Games.Keys.First();
            else throw new InvalidOperationException($"No game loaded. Call load_game first (configured: {string.Join(", ", config.Games.Keys)}).");
        }
        return await LoadAndAcquireAsync(id, reload: false, ct);
    }

    private async Task<GameLease> LoadAndAcquireAsync(string gameId, bool reload, CancellationToken ct)
    {
        if (!config.Games.TryGetValue(gameId, out var gameConfig))
            throw new ArgumentException($"Unknown game '{gameId}'. Configured: {string.Join(", ", config.Games.Keys)}");
        var key = config.Games.Keys.First(k => k.Equals(gameId, StringComparison.OrdinalIgnoreCase));

        await _loadGate.WaitAsync(ct);
        try
        {
            if (!reload)
            {
                lock (_sessions)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_sessions.TryGetValue(key, out var existing))
                    {
                        _current = key;
                        return new GameLease(existing);
                    }
                }
            }

            var session = await GameSession.LoadAsync(key, gameConfig, config, ct);
            GameSession? replaced;
            GameLease lease;
            lock (_sessions)
            {
                if (_disposed)
                {
                    session.Release();
                    throw new ObjectDisposedException(nameof(GameManager));
                }
                _sessions.TryGetValue(key, out replaced);
                _sessions[key] = session;
                _current = key;
                lease = new GameLease(session);
            }
            // Calls still holding the old session keep it alive; its provider is disposed when the last of them finishes.
            replaced?.Release();
            return lease;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public string ResolveOutputDir(GameSession game, string? outDir)
    {
        if (outDir is { Length: > 0 }) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(outDir));
        if (game.Config.OutputDir is { Length: > 0 } g) return g;
        return Path.Combine(config.ResolvedDefaultOutputDir, game.Id);
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
        foreach (var s in sessions) s.Release();
    }
}

/// <summary>Keeps a game's provider alive while an operation uses it.</summary>
public sealed class GameLease : IDisposable
{
    private int _released;

    internal GameLease(GameSession session)
    {
        session.AddRef();
        Session = session;
    }

    public GameSession Session { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) Session.Release();
    }
}
