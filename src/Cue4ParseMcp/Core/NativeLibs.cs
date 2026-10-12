using CUE4Parse.Compression;
using Cue4ParseMcp.Config;

namespace Cue4ParseMcp.Core;

/// <summary>Oodle and zlib-ng are loaded once per process. Missing dlls are downloaded into the cache dir.</summary>
public static class NativeLibs
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Loads whichever library isn't loaded yet. Failures don't stop a mount (many games use neither); they are
    /// returned as warnings and retried on the next load.
    /// </summary>
    public static async Task<IReadOnlyList<string>> EnsureInitializedAsync(AppConfig app, CancellationToken ct)
    {
        if (OodleHelper.Instance is not null && ZlibHelper.Instance is not null) return [];
        var warnings = new List<string>();

        await Gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(app.ResolvedCacheDir);

            if (OodleHelper.Instance is null)
            {
                var oodle = app.OodlePath is { Length: > 0 } ? app.OodlePath : Path.Combine(app.ResolvedCacheDir, OodleHelper.OodleFileName);
                await TryInitializeAsync("Oodle", "oodlePath", oodle, OodleHelper.InitializeAsync, () => OodleHelper.Instance is not null, warnings, ct);
            }

            if (ZlibHelper.Instance is null)
            {
                var zlib = app.ZlibPath is { Length: > 0 } ? app.ZlibPath : Path.Combine(app.ResolvedCacheDir, ZlibHelper.DllName);
                await TryInitializeAsync("zlib-ng", "zlibPath", zlib, ZlibHelper.InitializeAsync, () => ZlibHelper.Instance is not null, warnings, ct);
            }
        }
        finally
        {
            Gate.Release();
        }
        return warnings;
    }

    /// <summary>The CUE4Parse helpers log and return when the download fails, so success is checked through their Instance.</summary>
    private static async Task TryInitializeAsync(string name, string configKey, string path,
        Func<string?, CancellationToken, Task> initialize, Func<bool> loaded, List<string> warnings, CancellationToken ct)
    {
        string? reason = null;
        try
        {
            await initialize(path, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            reason = ex.Message;
        }
        ct.ThrowIfCancellationRequested();

        if (!loaded())
            warnings.Add($"{name} could not be loaded from {path} ({reason ?? "download failed"}); containers compressed with it will fail to read. " +
                         $"Delete the file if it is corrupt, or set {configKey} in games.json to an existing copy.");
    }
}
