using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Options;
using Cue4ParseMcp.Config;
using Serilog;

namespace Cue4ParseMcp.Core;

public sealed record ExportInfo(string Name, string Class, bool IsMain);

public sealed record AssetHit(string Path, string? Class);

public sealed record SearchResult(int NameMatches, int Scanned, bool Truncated, IReadOnlyList<AssetHit> Results, string? Note);

public sealed record FolderListing(string Path, IReadOnlyList<string> Folders, IReadOnlyList<string> Assets, int TotalAssets);

public sealed record ExportReport(
    string OutputDir,
    IReadOnlyList<string> RootFiles,
    IReadOnlyList<string> DependencyFiles,
    IReadOnlyList<string> Failures);

public sealed record LoadSummary(
    string Game,
    string EGame,
    int MountedContainers,
    int UnmountedContainers,
    int Files,
    int Packages,
    string? Mappings,
    string AesSource,
    double Seconds,
    IReadOnlyList<string> Warnings);

/// <summary>One mounted game: a CUE4Parse provider plus a package-path index for searching.</summary>
/// <remarks>Reference counted: the <see cref="GameManager"/> holds one reference and every <see cref="GameLease"/> another.</remarks>
public sealed class GameSession
{
    public string Id { get; }
    public GameConfig Config { get; }
    public DefaultFileProvider Provider { get; }
    public LoadSummary Summary { get; private set; } = null!;

    /// <summary>Package paths without extension, sorted. Excludes .uexp/.ubulk payloads and non-package files.</summary>
    private string[] _packages = [];

    /// <summary>Header reads per package; failures are cached too so broad type scans don't retry broken packages.</summary>
    private readonly ConcurrentDictionary<string, (ExportInfo[] Exports, Exception? Error)> _exportCache = new(StringComparer.OrdinalIgnoreCase);

    private int _refs = 1; // the GameManager's reference

    private GameSession(string id, GameConfig config, DefaultFileProvider provider)
    {
        Id = id;
        Config = config;
        Provider = provider;
    }

    internal void AddRef() => Interlocked.Increment(ref _refs);

    internal void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0) Provider.Dispose();
    }

    public static async Task<GameSession> LoadAsync(string id, GameConfig config, AppConfig app, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();

        if (!Directory.Exists(config.PaksDir))
            throw new DirectoryNotFoundException($"Paks directory not found: {config.PaksDir}");
        if (!Enum.TryParse<EGame>(config.UeVersion, ignoreCase: true, out var egame))
            throw new ArgumentException($"Unknown ueVersion '{config.UeVersion}'. Use a CUE4Parse EGame name such as GAME_Valorant.");

        warnings.AddRange(await NativeLibs.EnsureInitializedAsync(app, ct));

        var aes = await RemoteResources.ResolveAesAsync(config, warnings, ct);
        var mappings = await RemoteResources.ResolveMappingsAsync(id, config, app.ResolvedCacheDir, warnings, ct);
        if (mappings is null)
            warnings.Add("No mappings available: unversioned assets (most UE5 games) will fail to parse properties.");

        var provider = new DefaultFileProvider(config.PaksDir, SearchOption.AllDirectories,
            new VersionContainer(egame, ETexturePlatform.DesktopMobile), StringComparer.OrdinalIgnoreCase);
        try
        {
            if (mappings != null)
            {
                try
                {
                    provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappings.Path);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Mappings {mappings.Path} could not be parsed ({ex.Message}); continuing without them.");
                    mappings = null;
                }
            }

            provider.Initialize();
            ct.ThrowIfCancellationRequested();
            await provider.MountAsync(); // unencrypted containers; encrypted ones mount when their key is submitted
            ct.ThrowIfCancellationRequested();

            var keys = new List<KeyValuePair<FGuid, FAesKey>>();
            if (aes.MainKey is { Length: > 0 })
                AddKey(keys, warnings, null, aes.MainKey);
            foreach (var (guid, key) in aes.DynamicKeys)
                AddKey(keys, warnings, guid, key);
            if (keys.Count > 0)
                await provider.SubmitKeysAsync(keys);
            else
                warnings.Add("No usable AES key: encrypted containers will stay unmounted.");
            ct.ThrowIfCancellationRequested();

            provider.PostMount();
            provider.LoadVirtualPaths();

            var session = new GameSession(id, config, provider);
            session._packages = provider.Files.Values
                .Where(f => f.IsUePackage)
                .Select(f => f.PathWithoutExtension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (provider.UnloadedVfs.Count > 0)
            {
                var encrypted = provider.UnloadedVfs.Count(v => v.IsEncrypted);
                if (encrypted > 0)
                    warnings.Add($"{encrypted} encrypted container(s) did not mount - AES key missing or wrong.");
            }

            session.Summary = new LoadSummary(
                id, egame.ToString(), provider.MountedVfs.Count, provider.UnloadedVfs.Count,
                provider.Files.Count, session._packages.Length,
                mappings is null ? null : $"{Path.GetFileName(mappings.Path)} ({mappings.Source})",
                aes.Source, Math.Round(sw.Elapsed.TotalSeconds, 1), warnings);
            return session;
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    /// <summary>Invalid keys are reported and skipped so one bad dynamic key doesn't block the rest of the game.</summary>
    private static void AddKey(List<KeyValuePair<FGuid, FAesKey>> keys, List<string> warnings, string? guid, string key)
    {
        var label = guid is null ? "main AES key" : $"AES key for {guid}";
        try
        {
            var hex = key.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            var aesKey = new FAesKey(Convert.FromHexString(hex));

            var fguid = new FGuid();
            if (guid is not null)
            {
                var guidHex = guid.Trim().Replace("-", "");
                if (guidHex.Length != 32) throw new FormatException("the GUID must have 32 hex digits");
                fguid = new FGuid(guidHex);
            }
            keys.Add(new(fguid, aesKey));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            warnings.Add($"Ignored invalid {label}: {ex.Message}");
        }
    }

    #region Search

    /// <summary>Friendly type names -> UE class names matched anywhere in an export's class hierarchy.</summary>
    private static readonly Dictionary<string, string[]> TypeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mesh"] = ["SkinnedAsset", "StaticMesh"],
        ["Anim"] = ["AnimationAsset"],
        ["Animation"] = ["AnimationAsset"],
        ["Material"] = ["MaterialInterface"],
        ["Texture"] = ["Texture"],
    };

    /// <param name="query">Space-separated terms, all must appear in the path. '*' is a wildcard.</param>
    /// <param name="type">Class filter: an alias above or any UE class name (matched against the class hierarchy).</param>
    /// <param name="under">Restrict to a folder prefix, e.g. ShooterGame/Content/Characters.</param>
    public SearchResult Search(string? query, string? type, string? under, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 500);
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(BuildMatcher).ToArray();
        var prefix = under is { Length: > 0 } && NormalizeFolder(under) is { Length: > 0 } folder ? folder + "/" : null;
        ct.ThrowIfCancellationRequested();

        var candidates = _packages
            .Where(p => prefix is null || p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(p => terms.All(t => t(p)))
            .Select(p => (Path: p, Score: Score(p, terms)))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Path.Length).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Path)
            .ToList();

        if (type is not { Length: > 0 })
        {
            var hits = candidates.Take(limit).Select(p => new AssetHit(p, null)).ToList();
            return new SearchResult(candidates.Count, 0, candidates.Count > limit, hits,
                candidates.Count > limit ? "Results truncated; refine the query or pass 'under'." : null);
        }

        // Type filtering needs each package's export map. Headers are cheap but not free, so cap the scan.
        const int scanBudget = 20000;
        var matcher = BuildTypeMatcher(type);
        var found = new ConcurrentBag<(int Order, AssetHit Hit)>();
        var scanned = 0;
        var unreadable = 0;
        var toScan = candidates.Take(scanBudget).Select((p, i) => (p, i)).ToList();

        // Scan in ordered chunks so that we can stop once the best-ranked `limit` hits are known.
        const int chunk = 512;
        for (var start = 0; start < toScan.Count && found.Count < limit; start += chunk)
        {
            ct.ThrowIfCancellationRequested();
            Parallel.ForEach(toScan.Skip(start).Take(chunk), new ParallelOptions { CancellationToken = ct }, item =>
            {
                Interlocked.Increment(ref scanned);
                var (exports, error) = ReadExports(item.p);
                if (error != null)
                {
                    Interlocked.Increment(ref unreadable);
                    return;
                }
                var main = exports.FirstOrDefault(e => e.IsMain && matcher(e)) ?? exports.FirstOrDefault(matcher);
                if (main != null) found.Add((item.i, new AssetHit(item.p, main.Class)));
            });
        }

        var results = found.OrderBy(x => x.Order).Take(limit).Select(x => x.Hit).ToList();
        var truncated = found.Count > limit || candidates.Count > scanned;
        string? note = null;
        if (candidates.Count > scanBudget && found.Count < limit)
            note = $"Only the first {scanBudget} of {candidates.Count} name matches were type-checked; narrow the query or pass 'under'.";
        else if (truncated)
            note = "Stopped after reaching the limit; more matches may exist.";
        if (unreadable > 0)
            note = (note is null ? "" : note + " ") + $"{unreadable} package header(s) could not be read and were skipped; inspect_asset shows the error.";

        return new SearchResult(candidates.Count, scanned, truncated, results, note);
    }

    private static Func<string, bool> BuildMatcher(string term)
    {
        if (!term.Contains('*'))
            return p => p.Contains(term, StringComparison.OrdinalIgnoreCase);
        // NonBacktracking: a pattern like "*a*a*a*b" over 300k paths must not blow up.
        var regex = new Regex(Regex.Escape(term).Replace("\\*", ".*"),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        return regex.IsMatch;
    }

    /// <summary>Prefer hits where the terms are in the asset name rather than only in its folder.</summary>
    private static int Score(string path, Func<string, bool>[] terms)
    {
        var leaf = path.SubstringAfterLast('/');
        return terms.Count(t => t(leaf));
    }

    private static Func<ExportInfo, bool> BuildTypeMatcher(string type)
    {
        var name = type.Length > 1 && type[0] == 'U' && char.IsUpper(type[1]) ? type[1..] : type;
        var wanted = TypeAliases.TryGetValue(name, out var alias) ? alias : [name];
        return e => e.Class.Split(" : ").Any(c => wanted.Contains(c, StringComparer.OrdinalIgnoreCase));
    }

    public FolderListing ListFolder(string? path, int limit)
    {
        var folder = path is { Length: > 0 } ? NormalizeFolder(path) : "";
        var prefix = folder.Length > 0 ? folder + "/" : "";
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var assets = new List<string>();
        foreach (var p in _packages)
        {
            if (!p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = p[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash >= 0) folders.Add(rest[..slash]);
            else assets.Add(rest);
        }
        if (folder.Length > 0 && folders.Count == 0 && assets.Count == 0)
            throw new DirectoryNotFoundException($"No assets under '{folder}'. List a parent folder or use search_assets to find the path.");
        return new FolderListing(prefix.TrimEnd('/'), folders.ToList(), assets.Take(limit).ToList(), assets.Count);
    }

    #endregion

    #region Assets

    /// <summary>
    /// Top-level exports of a package with their class hierarchy, e.g. "SkeletalMesh : SkinnedAsset : StreamableRenderAsset : Object".
    /// Uses only the package header, so it's safe to call on thousands of packages. Throws if the header can't be read.
    /// </summary>
    public IReadOnlyList<ExportInfo> GetExports(string packagePath)
    {
        var (exports, error) = ReadExports(packagePath);
        return error is null ? exports : throw new InvalidDataException($"Could not read the header of {packagePath}: {error.Message}", error);
    }

    private (ExportInfo[] Exports, Exception? Error) ReadExports(string packagePath)
    {
        return _exportCache.GetOrAdd(packagePath, p =>
        {
            try
            {
                var file = ResolvePackageFile(p) ?? throw new FileNotFoundException($"Asset not found: {p}");
                var pkg = Provider.LoadPackage(file);

                var leaf = p.SubstringAfterLast('/');
                var list = new List<ExportInfo>();
                for (var i = 0; i < pkg.ExportMapLength; i++)
                {
                    var resolved = pkg.ResolvePackageIndex(new FPackageIndex(pkg, i + 1));
                    if (resolved is null) continue;
                    if (resolved.Outer is { } outer && outer is not ResolvedPackageObject) continue; // subobject

                    var name = resolved.Name.Text;
                    list.Add(new ExportInfo(name, ClassChain(resolved, pkg), name.Equals(leaf, StringComparison.OrdinalIgnoreCase)));
                }
                return (list.ToArray(), null);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to read header of {Path}", p);
                return ([], ex);
            }
        });
    }

    private static string ClassChain(ResolvedObject obj, IPackage pkg)
    {
        var names = new List<string>();
        for (var cls = obj.Class; cls != null && names.Count < 16; cls = cls.Super)
            names.Add(cls.Name.Text);

        // Script classes don't carry their supers in cooked data; the mappings do.
        var last = names.LastOrDefault();
        while (last != null && names.Count < 16 && pkg.Mappings?.Types.TryGetValue(last, out var struc) == true
               && struc.SuperType is { Length: > 0 } super && super != last)
        {
            names.Add(super);
            last = super;
        }
        return names.Count == 0 ? "Unknown" : string.Join(" : ", names);
    }

    /// <summary>
    /// Accepts "Game/Content/X/Y", "/Game/X/Y", "Y.uasset" paths, "Pkg.Object" and "Class'/Game/X/Y.Y'" references.
    /// </summary>
    public (string PackagePath, string? ObjectName) ParsePath(string input)
    {
        var s = input.Trim().Trim('"').Replace('\\', '/');
        if (s.Trim('/').Length == 0) throw new ArgumentException("Asset path is empty.");
        var quote = s.IndexOf('\'');
        if (quote >= 0 && s.EndsWith('\'')) s = s[(quote + 1)..^1];

        string? objectName = null;
        var leafStart = s.LastIndexOf('/') + 1;
        var dot = s.IndexOf('.', leafStart);
        if (dot > 0)
        {
            var after = s[(dot + 1)..];
            if (!PackageFileExtensions.Contains(after))
                objectName = after.Contains(':') ? after.SubstringBefore(':') : after;
            s = s[..dot];
        }

        var file = ResolvePackageFile(s)
                   ?? throw new FileNotFoundException($"Asset not found: {input}. Use search_assets to find the exact path.");
        return (file.PathWithoutExtension, objectName);
    }

    /// <summary>Extensions that name the package file itself rather than an object inside it.</summary>
    private static readonly HashSet<string> PackageFileExtensions = new(["uasset", "umap", "uexp", "ubulk", "uptnl"], StringComparer.OrdinalIgnoreCase);

    private GameFile? ResolvePackageFile(string pathWithoutExtension)
    {
        foreach (var ext in GameFile.UePackageExtensions)
        {
            if (Provider.TryGetGameFile(pathWithoutExtension + "." + ext, out var file)) return file;
        }
        return null;
    }

    private IPackage LoadPackage(string packagePath) =>
        Provider.LoadPackage(ResolvePackageFile(packagePath) ?? throw new FileNotFoundException($"Asset not found: {packagePath}"));

    /// <summary>Loads the asset's primary object: the named export, else the export matching the package name, else the first top-level export.</summary>
    public UObject LoadMain(string input)
    {
        var (pkgPath, objectName) = ParsePath(input);
        var pkg = LoadPackage(pkgPath);

        var exports = GetExports(pkgPath);
        var name = objectName ?? exports.FirstOrDefault(e => e.IsMain)?.Name
                               ?? exports.FirstOrDefault()?.Name
                               ?? throw new InvalidDataException($"{pkgPath} has no exports");
        return pkg.GetExportOrNull(name, StringComparison.OrdinalIgnoreCase)
               ?? throw new InvalidDataException($"{pkgPath} has no export named '{name}'. Exports: {string.Join(", ", exports.Select(e => e.Name))}");
    }

    public string ToJson(string input, int maxChars)
    {
        var (pkgPath, objectName) = ParsePath(input);
        var pkg = LoadPackage(pkgPath);
        object payload = objectName != null
            ? pkg.GetExport(objectName, StringComparison.OrdinalIgnoreCase)
            : pkg.GetExports().ToArray();
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload, Newtonsoft.Json.Formatting.Indented);
        return json.Length <= maxChars ? json : json[..maxChars] + $"\n... [truncated, {json.Length} chars total]";
    }

    #endregion

    #region Export

    /// <summary>Exports sharing an output folder run one at a time so two meshes with a common texture don't race on the file.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> OutputLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ExportReport> ExportAsync(IReadOnlyList<UObject> roots, ExportOptions options, string outDir, CancellationToken ct)
    {
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        var session = new ExportSession { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        foreach (var root in roots) session.Add(root);

        // Same formula ExporterBase uses for the ObjectPath of a top-level export.
        var rootPaths = new HashSet<string>(
            roots.Select(r => Provider.FixPath(r.Owner?.Name ?? r.GetPathName()).SubstringBeforeLast('.') + "." + r.Name),
            StringComparer.OrdinalIgnoreCase);

        var gate = OutputLocks.GetOrAdd(outDir, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        IReadOnlyList<ExportResult> results;
        try
        {
            results = await session.RunAsync(outDir, options, null, ct);
        }
        finally
        {
            gate.Release();
        }

        var rootFiles = new List<string>();
        var depFiles = new List<string>();
        var failures = new List<string>();
        foreach (var r in results)
        {
            if (!r.Success)
            {
                // A parse failure upstream usually surfaces later as a null reference in the converter.
                var hint = r.Error is NullReferenceException or CUE4Parse.UE4.Exceptions.ParserException
                    ? " (asset failed to deserialize - usually the .usmap mappings don't match this game build)"
                    : "";
                failures.Add($"{r.ObjectPath}: {r.Error?.Message}{hint}");
                continue;
            }
            var target = rootPaths.Contains(r.ObjectPath) ? rootFiles : depFiles;
            target.AddRange(r.DiskFilePaths ?? []);
        }

        // Every root failing would otherwise look like an empty but successful export.
        if (rootFiles.Count == 0)
            throw new InvalidOperationException(failures.Count > 0
                ? $"Export failed: {string.Join("; ", failures)}"
                : "Export wrote no files.");

        return new ExportReport(outDir, rootFiles, depFiles.Order().ToList(), failures);
    }

    /// <summary>Loads a mesh's material slots. Empty slots are skipped; slots that fail to load are reported, not dropped.</summary>
    public static (IReadOnlyList<UMaterialInterface> Materials, IReadOnlyList<string> Failures) GetMeshMaterials(UObject mesh)
    {
        FPackageIndex?[] slots = mesh switch
        {
            USkinnedAsset skinned => skinned.Materials,
            UStaticMesh staticMesh => staticMesh.Materials,
            _ => []
        };
        var materials = new List<UMaterialInterface>();
        var failures = new List<string>();
        foreach (var slot in slots)
        {
            if (slot is null || slot.IsNull) continue;
            try
            {
                switch (slot.Load())
                {
                    case UMaterialInterface material:
                        materials.Add(material);
                        break;
                    case null:
                        failures.Add($"{slot}: material could not be resolved");
                        break;
                    case var other:
                        failures.Add($"{slot}: is a {other.ExportType}, not a material");
                        break;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{slot}: {ex.Message}");
            }
        }
        return (materials, failures);
    }

    #endregion

    /// <summary>Folder in provider form ("ShooterGame/Content/X"); also accepts "/Game/X" and plugin mount roots.</summary>
    public string NormalizeFolder(string path)
    {
        var trimmed = path.Replace('\\', '/').Trim().Trim('/');
        return trimmed.Length == 0 ? "" : Provider.FixPath(trimmed + "/x").SubstringBeforeLast('/');
    }
}
