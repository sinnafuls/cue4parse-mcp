using System.Text.Json;
using Cue4ParseMcp.Config;
using Serilog;

namespace Cue4ParseMcp.Core;

public sealed record AesKeySet(string? MainKey, IReadOnlyDictionary<string, string> DynamicKeys, string Source);

public sealed record MappingsFile(string Path, string Source);

/// <summary>
/// Resolves AES keys and .usmap mappings for a game: remote endpoint first (so new patches are picked up
/// without editing config), falling back to the values in games.json / the last cached download.
/// Fallbacks are added to the caller's warnings so a load summary never hides a failed endpoint.
/// </summary>
public static class RemoteResources
{
    private const ushort UsmapMagic = 0x30C4;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static RemoteResources()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Cue4ParseMcp/1.0.0");
    }

    public static async Task<AesKeySet> ResolveAesAsync(GameConfig game, List<string> warnings, CancellationToken ct)
    {
        var dynamicKeys = new Dictionary<string, string>(game.DynamicKeys ?? [], StringComparer.OrdinalIgnoreCase);

        if (game.AesUrl is { Length: > 0 } url)
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
                var root = doc.RootElement;
                var main = root.TryGetProperty("mainKey", out var mk) ? mk.GetString() : null;
                if (root.TryGetProperty("dynamicKeys", out var dk) && dk.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in dk.EnumerateArray())
                    {
                        var guid = entry.TryGetProperty("guid", out var g) ? g.GetString() : null;
                        var key = entry.TryGetProperty("key", out var k) ? k.GetString() : null;
                        if (guid is { Length: > 0 } && key is { Length: > 0 }) dynamicKeys[guid] = key;
                    }
                }

                if (main is { Length: > 0 })
                    return new AesKeySet(main, dynamicKeys, url);
                warnings.Add($"AES endpoint {url} returned no mainKey; using the configured key.");
            }
            // HttpClient timeouts surface as TaskCanceledException; only the caller's token means "stop".
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Warning(ex, "AES endpoint {Url} failed, falling back to configured key", url);
                warnings.Add($"AES endpoint {url} failed ({ex.Message}); using the configured key.");
            }
        }

        return new AesKeySet(game.AesKey, dynamicKeys, game.AesKey is { Length: > 0 } ? "games.json" : "none");
    }

    public static async Task<MappingsFile?> ResolveMappingsAsync(string gameId, GameConfig game, string cacheDir, List<string> warnings, CancellationToken ct)
    {
        var mappingsCache = Path.Combine(cacheDir, "mappings", gameId);

        if (game.MappingsUrl is { Length: > 0 } url)
        {
            try
            {
                var (fileUrl, fileName) = await ResolveMappingsDescriptorAsync(url, ct);
                var target = Path.Combine(mappingsCache, fileName);
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(mappingsCache);
                    var bytes = await Http.GetByteArrayAsync(fileUrl, ct);
                    if (bytes.Length < 2 || BitConverter.ToUInt16(bytes, 0) != UsmapMagic)
                        throw new InvalidDataException($"{fileUrl} did not return a .usmap file");
                    var tmp = target + ".part";
                    await File.WriteAllBytesAsync(tmp, bytes, ct);
                    File.Move(tmp, target, overwrite: true);
                    Log.Information("Downloaded mappings {File}", fileName);
                }
                return new MappingsFile(target, fileUrl);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Warning(ex, "Mappings endpoint {Url} failed, falling back", url);
                warnings.Add($"Mappings endpoint {url} failed ({ex.Message}); falling back to configured or cached mappings.");
            }
        }

        if (game.Mappings is { Length: > 0 } local)
        {
            if (File.Exists(local)) return new MappingsFile(local, "games.json");
            warnings.Add($"Configured mappings file not found: {local}");
        }

        // Nothing else available: the newest previously downloaded file beats nothing.
        if (Directory.Exists(mappingsCache))
        {
            var newest = new DirectoryInfo(mappingsCache).GetFiles("*.usmap").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest != null)
                return new MappingsFile(newest.FullName, game.MappingsUrl is { Length: > 0 } ? "cache (endpoint unreachable)" : "cache");
        }

        return null;
    }

    /// <summary>Accepts uedb.dev's {"mappings": {"ZStandard": url, ...}} and FModel's [{"url", "fileName"}] shapes.</summary>
    private static async Task<(string Url, string FileName)> ResolveMappingsDescriptorAsync(string url, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        var root = doc.RootElement;

        string? fileUrl = null, fileName = null;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            var first = root[0];
            fileUrl = first.TryGetProperty("url", out var u) ? u.GetString() : null;
            fileName = first.TryGetProperty("fileName", out var f) ? f.GetString() : null;
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("mappings", out var m))
        {
            if (m.ValueKind == JsonValueKind.Object)
            {
                foreach (var preferred in new[] { "ZStandard", "Brotli", "Oodle", "None" })
                {
                    if (m.TryGetProperty(preferred, out var p) && p.GetString() is { Length: > 0 } s) { fileUrl = s; break; }
                }
                fileUrl ??= m.EnumerateObject().Select(p => p.Value.GetString()).FirstOrDefault(s => !string.IsNullOrEmpty(s));
            }
            else if (m.ValueKind == JsonValueKind.String)
            {
                fileUrl = m.GetString();
            }
        }

        if (string.IsNullOrEmpty(fileUrl))
            throw new InvalidDataException($"Unrecognised mappings descriptor from {url}");

        // Never trust a remote name as a path: keep only the leaf.
        fileName = Path.GetFileName(fileName ?? new Uri(fileUrl).AbsolutePath);
        if (fileName.Length == 0) fileName = "mappings";
        if (!fileName.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase)) fileName += ".usmap";
        return (fileUrl, fileName);
    }
}
