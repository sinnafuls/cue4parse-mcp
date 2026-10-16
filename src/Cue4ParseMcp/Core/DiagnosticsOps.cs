using CUE4Parse.Compression;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.Utils;
using Cue4ParseMcp.Config;

namespace Cue4ParseMcp.Core;

public sealed record GameDiagnostics(
    string Game,
    EngineDiagnostics Engine,
    ConfigurationDiagnostics Configuration,
    MountDiagnostics Mount,
    EncryptionDiagnostics? Encryption,
    MappingsDiagnostics? Mappings,
    CodecDiagnostics Codecs,
    IReadOnlyList<string> Warnings,
    ProbeDiagnostics? Probe,
    IReadOnlyList<string> Notes);

public sealed record EngineDiagnostics(string Configured, bool Recognized, string? Effective);

/// <summary>What games.json asks for. Endpoints are reduced to scheme://host; no key or path values.</summary>
/// <param name="ConfigFile">File name of the selected configuration (no directory), or &lt;in-memory&gt;.</param>
public sealed record ConfigurationDiagnostics(
    string ConfigFile,
    bool PaksDirConfigured,
    bool PaksDirExists,
    bool MainKeyConfigured,
    int DynamicKeysConfigured,
    string? AesEndpoint,
    bool MappingsFileConfigured,
    bool MappingsFileExists,
    string? MappingsEndpoint);

/// <param name="Status">
/// mounted (every container), partial (some unmounted), none-mounted (containers found, none mounted),
/// no-containers (no pak/utoc found) or failed (the game could not be loaded).
/// </param>
public sealed record MountDiagnostics(
    string Status,
    string? FailureCategory,
    string? FailureMessage,
    double? Seconds,
    int MountedContainers,
    int UnmountedContainers,
    int Files,
    int Packages,
    int LooseFiles,
    IReadOnlyList<ContainerTypeCount> ContainerTypes,
    IReadOnlyList<UnmountedContainer> Unmounted);

public sealed record ContainerTypeCount(string Type, int Mounted, int Unmounted);

public sealed record UnmountedContainer(string Name, string Type, string Reason);

/// <param name="RequiredKeyGuids">GUIDs of encrypted-index containers that still have no accepted key.</param>
public sealed record EncryptionDiagnostics(IReadOnlyList<string> RequiredKeyGuids, IReadOnlyList<KeyDiagnostics> Keys);

/// <summary>
/// One encryption key GUID (all zeros = main key). Counters are containers; <see cref="EncryptedEntries"/> counts
/// encrypted files in mounted containers. <see cref="Configured"/>: games.json, endpoint, endpoint-unknown (an
/// endpoint is configured but its dynamic keys are not retained after mounting) or none. <see cref="State"/> is the
/// worst container state: rejected, missing, untested, unverified or verified.
/// </summary>
public sealed record KeyDiagnostics(
    string Guid,
    string Configured,
    int Containers,
    int Mounted,
    int Unmounted,
    int IndexEncrypted,
    int EntryEncryptedContainers,
    long EncryptedEntries,
    int Assigned,
    int Verified,
    int Rejected,
    int Missing,
    int Unverified,
    int Untested,
    string State,
    string Evidence);

public sealed record MappingsDiagnostics(bool Loaded, string? FileName, string? Source, int Types);

/// <param name="NativeDetexLoaded">BC6H/BC7 texture decoding (Detex) is loaded.</param>
/// <param name="NativesLoaded">The CUE4Parse-Natives library is loaded.</param>
/// <param name="AclAvailable">CUE4Parse-Natives reports the ACL animation-compression feature.</param>
/// <param name="CompressionMethods">Methods declared by the mounted containers.</param>
public sealed record CodecDiagnostics(
    bool NativeOodleLoaded,
    bool NativeZlibNgLoaded,
    bool NativeDetexLoaded,
    bool NativesLoaded,
    bool AclAvailable,
    IReadOnlyList<string> CompressionMethods);

/// <param name="ExercisedEncryption">True only when the probed entry itself is encrypted.</param>
public sealed record ProbeDiagnostics(
    string Path,
    string Kind,
    string? Container,
    bool? ContainerIndexEncrypted,
    bool? EntryEncrypted,
    bool ExercisedEncryption,
    string KeyEvidence,
    IReadOnlyList<ProbeStage> Stages);

/// <param name="Stage">locate, read, header or deserialize.</param>
/// <param name="Status">ok, partial, failed or skipped.</param>
/// <param name="Category">not-found, decryption, decompression, read, header, mappings-required, properties or properties-no-mappings.</param>
public sealed record ProbeStage(string Stage, string Status, string? Category = null, string? Message = null, string? Detail = null);

/// <summary>
/// Sanitized game health report: configuration, mount, per-GUID key evidence, mappings, codecs and an optional
/// real asset probe. Key values, endpoint tokens, local paths and stack traces never appear in the result.
/// </summary>
public sealed class DiagnosticsOps(GameManager games)
{
    private const int MaxUnmountedListed = 50;
    private const int ProbeExportBudget = 64;

    // PakFileReader.MountPointCheckBytes moves the shared archive position when the reader isn't concurrent.
    // Only unmounted readers are tested (no other operation reads them); this serializes concurrent diagnoses.
    private static readonly object KeyTestGate = new();

    private enum KeyState { Verified, Unverified, Untested, Missing, Rejected }

    public async Task<GameDiagnostics> DiagnoseAsync(string? game, string? probe, CancellationToken ct)
    {
        var id = games.ResolveGameId(game);
        var (config, _) = games.GetState();
        var key = config.Games.Keys.FirstOrDefault(k => k.Equals(id, StringComparison.OrdinalIgnoreCase))
                  ?? throw new ArgumentException($"Unknown game '{id}'. Configured: {string.Join(", ", config.Games.Keys)}");
        var gameConfig = config.Games[key];

        GameLease lease;
        try
        {
            lease = await games.AcquireAsync(key, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            return MountFailure(key, gameConfig, config, ex, probe);
        }

        using (lease)
            return Diagnose(lease, probe, ct);
    }

    private static GameDiagnostics MountFailure(string id, GameConfig gameConfig, AppConfig app, Exception ex, string? probe)
    {
        var clean = CreateSanitizer(gameConfig, app);
        var engineRecognized = Enum.TryParse<EGame>(gameConfig.UeVersion, ignoreCase: true, out _);
        var category = ex switch
        {
            DirectoryNotFoundException => "paks-dir-not-found",
            ArgumentException when !engineRecognized => "unknown-engine-version",
            UnauthorizedAccessException or IOException => "io-error",
            _ => "mount-error"
        };
        var notes = new List<string> { "The game did not mount; container, key, mappings and probe checks need a successful mount." };
        if (probe is { Length: > 0 }) notes.Add("Probe skipped because the game did not mount.");

        return new GameDiagnostics(
            id,
            new EngineDiagnostics(gameConfig.UeVersion, engineRecognized, null),
            Configuration(gameConfig, app),
            new MountDiagnostics("failed", category, clean.Describe(ex), null, 0, 0, 0, 0, 0, [], []),
            null,
            null,
            Codecs([]),
            [],
            null,
            notes);
    }

    private static GameDiagnostics Diagnose(GameLease lease, string? probe, CancellationToken ct)
    {
        var session = lease.Session;
        var provider = session.Provider;
        var summary = session.Summary;
        var clean = CreateSanitizer(session.Config, lease.Configuration);
        foreach (var reader in provider.MountedVfs.Concat(provider.UnloadedVfs))
            clean.AddSecret(reader.AesKey?.KeyString);
        foreach (var providerKey in provider.Keys.Values)
            clean.AddSecret(providerKey.KeyString);

        // The probe runs first: an encrypted package it decrypts is evidence for its container's key.
        var probeVerified = new HashSet<IAesVfsReader>(ReferenceEqualityComparer.Instance);
        var probeReport = probe is { Length: > 0 } ? Probe(session, probe, clean, probeVerified, ct) : null;
        ct.ThrowIfCancellationRequested();

        var mounted = provider.MountedVfs.ToArray();
        var unmounted = provider.UnloadedVfs.ToArray();
        var iniKey = IniVerifiedKey(session);
        var states = new Dictionary<IAesVfsReader, KeyState?>(ReferenceEqualityComparer.Instance);
        foreach (var reader in mounted) states[reader] = Classify(reader, mounted: true, probeVerified, iniKey);
        foreach (var reader in unmounted)
        {
            ct.ThrowIfCancellationRequested();
            states[reader] = Classify(reader, mounted: false, probeVerified, iniKey);
        }

        var containerTypes = mounted.Select(r => (Type: ContainerType(r), Mounted: true))
            .Concat(unmounted.Select(r => (Type: ContainerType(r), Mounted: false)))
            .GroupBy(x => x.Type)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ContainerTypeCount(g.Key, g.Count(x => x.Mounted), g.Count(x => !x.Mounted)))
            .ToList();
        var unmountedList = unmounted
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxUnmountedListed)
            .Select(r => new UnmountedContainer(clean.Clean(Path.GetFileName(r.Name)), ContainerType(r), UnmountedReason(r, states[r])))
            .ToList();

        var notes = new List<string>
        {
            "Keys are only reported verified when CUE4Parse validated a decrypted container index at mount, when an encrypted DefaultGame.ini decrypted into valid project settings, or when a probe decrypted an encrypted entry that parsed as a package. Mounting a plain-index container or reading unencrypted files never verifies a key.",
            "Containers without a key for their GUID are given the main key as a fallback, so 'assigned' can reflect that fallback."
        };
        if (unmounted.Length > MaxUnmountedListed)
            notes.Add($"Only the first {MaxUnmountedListed} of {unmounted.Length} unmounted containers are listed.");

        var mount = new MountDiagnostics(
            MountStatus(mounted.Length, unmounted.Length), null, null, summary.Seconds,
            mounted.Length, unmounted.Length, summary.Files, summary.Packages, provider.LooseFileCount,
            containerTypes, unmountedList);
        if (mounted.Length + unmounted.Length == 0)
            notes.Add("No .pak or .utoc containers were found under the configured paksDir.");

        return new GameDiagnostics(
            session.Id,
            new EngineDiagnostics(session.Config.UeVersion, Enum.TryParse<EGame>(session.Config.UeVersion, ignoreCase: true, out _), summary.EGame),
            Configuration(session.Config, lease.Configuration),
            mount,
            Encryption(session, states, probeVerified),
            Mappings(session),
            Codecs(mounted.SelectMany(r => r.CompressionMethods ?? [])
                .Where(m => m != CompressionMethod.None)
                .Select(m => m.ToString())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList()),
            summary.Warnings.Select(clean.Clean).ToList(),
            probeReport,
            notes);
    }

    private static CodecDiagnostics Codecs(IReadOnlyList<string> methods) => new(
        OodleHelper.Instance is not null,
        ZlibHelper.Instance is not null,
        NativeLibs.DetexLoaded,
        CUE4ParseNatives.IsInitialized,
        CUE4ParseNatives.IsFeatureAvailable("ACL\0"u8),
        methods);

    private static ConfigurationDiagnostics Configuration(GameConfig game, AppConfig app) => new(
        app.SourcePath.Length > 0 ? Path.GetFileName(app.SourcePath) : "<in-memory>",
        game.PaksDir is { Length: > 0 },
        game.PaksDir is { Length: > 0 } && Directory.Exists(game.PaksDir),
        game.AesKey is { Length: > 0 },
        game.DynamicKeys?.Count ?? 0,
        ReportSanitizer.EndpointLabel(game.AesUrl) ?? (game.AesUrl is { Length: > 0 } ? "[invalid url]" : null),
        game.Mappings is { Length: > 0 },
        game.Mappings is { Length: > 0 } && File.Exists(game.Mappings),
        ReportSanitizer.EndpointLabel(game.MappingsUrl) ?? (game.MappingsUrl is { Length: > 0 } ? "[invalid url]" : null));

    /// <summary>Redacts the configured keys, endpoints and local paths of one game.</summary>
    internal static ReportSanitizer CreateSanitizer(GameConfig game, AppConfig app)
    {
        var clean = new ReportSanitizer();
        clean.AddSecret(game.AesKey);
        foreach (var dynamicKey in game.DynamicKeys?.Values ?? Enumerable.Empty<string>())
            clean.AddSecret(dynamicKey);
        clean.AddSecret(game.AesUrl);
        clean.AddSecret(game.MappingsUrl);

        clean.AddPath(game.PaksDir, "<paksDir>");
        clean.AddPath(game.Mappings, "<mappings>");
        clean.AddPath(game.OutputDir, "<outputDir>");
        clean.AddPath(app.ResolvedCacheDir, "<cacheDir>");
        clean.AddPath(app.OodlePath, "<oodlePath>");
        clean.AddPath(app.ZlibPath, "<zlibPath>");
        clean.AddPath(app.DefaultOutputDir, "<defaultOutputDir>");
        if (app.SourcePath.Length > 0) clean.AddPath(Path.GetDirectoryName(app.SourcePath), "<configDir>");
        clean.AddPath(AppContext.BaseDirectory, "<appDir>");
        clean.AddPath(Path.GetTempPath(), "<temp>");
        clean.AddPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<home>");
        return clean;
    }

    #region Keys

    /// <summary>
    /// The key of the container holding an encrypted DefaultGame.ini that CUE4Parse decrypted into a config with
    /// project settings during mount. Garbage from a wrong key never contains that section.
    /// </summary>
    private static byte[]? IniVerifiedKey(GameSession session)
    {
        try
        {
            var provider = session.Provider;
            if (!provider.TryGetGameFile("/Game/Config/DefaultGame.ini", out var ini) || !ini.IsEncrypted ||
                ini is not VfsEntry { Vfs: IAesVfsReader { AesKey: { } key } }) return null;
            return provider.DefaultGame.Sections.Any(s => s.Name == "/Script/EngineSettings.GeneralProjectSettings") ? key.Key : null;
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static KeyState? Classify(IAesVfsReader reader, bool mounted, HashSet<IAesVfsReader> probeVerified, byte[]? iniKey)
    {
        if (mounted)
        {
            // An encrypted index (pak) or directory index (IoStore) only mounts once it decrypted and parsed.
            if (reader.IsEncrypted) return KeyState.Verified;
            if (reader.EncryptedFileCount == 0) return null;
            if (reader.AesKey is not { } assigned) return KeyState.Missing;
            return probeVerified.Contains(reader) || (iniKey is not null && assigned.Key.AsSpan().SequenceEqual(iniKey))
                ? KeyState.Verified
                : KeyState.Unverified;
        }

        if (!reader.IsEncrypted)
        {
            // A plain index with encrypted entries mounted, then CUE4Parse's DefaultGame.ini check unmounted it
            // (PostMount is the only place a mounted container is unloaded again).
            if (reader.EncryptedFileCount > 0) return reader.AesKey is null ? KeyState.Missing : KeyState.Rejected;
            return null;
        }
        if (reader.AesKey is not { } key) return KeyState.Missing;
        try
        {
            lock (KeyTestGate)
                return reader.TestAesKey(key) ? KeyState.Verified : KeyState.Rejected;
        }
        catch (Exception)
        {
            return KeyState.Untested;
        }
    }

    private static string UnmountedReason(IAesVfsReader reader, KeyState? state) => (state, reader.IsEncrypted) switch
    {
        (KeyState.Missing, true) => "encrypted index: no key assigned for its GUID",
        (KeyState.Rejected, true) => "encrypted index: the assigned key failed CUE4Parse's index check",
        (KeyState.Verified, true) => "the assigned key passed the index check, but mounting failed (see warnings)",
        (KeyState.Untested, true) => "encrypted index: the key could not be tested",
        (KeyState.Missing, false) => "encrypted entries: no key assigned, and CUE4Parse's DefaultGame.ini check unmounted the container",
        (KeyState.Rejected, false) => "encrypted entries: CUE4Parse's DefaultGame.ini check failed with the assigned key and unmounted the container",
        _ when !reader.HasDirectoryIndex => "no directory index: the container cannot be listed",
        _ => "mount failed: the container could not be read"
    };

    private static EncryptionDiagnostics Encryption(GameSession session, Dictionary<IAesVfsReader, KeyState?> states, HashSet<IAesVfsReader> probeVerified)
    {
        var provider = session.Provider;
        var mounted = new HashSet<IAesVfsReader>(provider.MountedVfs, ReferenceEqualityComparer.Instance);
        var dynamicGuids = new HashSet<FGuid>();
        foreach (var guid in session.Config.DynamicKeys?.Keys ?? Enumerable.Empty<string>())
        {
            var hex = guid.Trim().Replace("-", "");
            if (hex.Length == 32 && hex.All(Uri.IsHexDigit)) dynamicGuids.Add(new FGuid(hex));
        }
        var aesSource = session.Summary.AesSource;
        var mainFromEndpoint = aesSource is not ("games.json" or "none");
        var endpointConfigured = session.Config.AesUrl is { Length: > 0 };

        var keys = states
            .Where(s => s.Value is not null)
            .GroupBy(s => s.Key.EncryptionKeyGuid)
            .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
            .Select(g =>
            {
                var readers = g.Select(s => s.Key).ToList();
                var counts = g.GroupBy(s => s.Value!.Value).ToDictionary(x => x.Key, x => x.Count());
                int Count(KeyState state) => counts.GetValueOrDefault(state);
                var isMain = g.Key == new FGuid();
                var configured = isMain
                    ? aesSource == "games.json" ? "games.json" : mainFromEndpoint ? "endpoint" : "none"
                    : dynamicGuids.Contains(g.Key) ? "games.json" : endpointConfigured ? "endpoint-unknown" : "none";
                var state = new[] { KeyState.Rejected, KeyState.Missing, KeyState.Untested, KeyState.Unverified, KeyState.Verified }
                    .First(s => Count(s) > 0);
                var evidence = state switch
                {
                    KeyState.Rejected => "The assigned key failed a CUE4Parse check (encrypted index validation or the DefaultGame.ini check) for at least one container.",
                    KeyState.Missing => "At least one container needs this key but none is assigned.",
                    KeyState.Untested => "At least one encrypted container could not be tested.",
                    KeyState.Unverified => "A key is assigned to containers with a plain index and encrypted entries; mounting does not test it. Probe an encrypted package from this group to verify.",
                    _ => "Verified for every container: " + string.Join("; ", new[]
                    {
                        readers.Any(r => r.IsEncrypted) ? "encrypted indexes validated by CUE4Parse with the assigned key" : null,
                        readers.Any(r => !r.IsEncrypted && probeVerified.Contains(r)) ? "the probe decrypted an encrypted entry that parsed as a package" : null,
                        readers.Any(r => !r.IsEncrypted && !probeVerified.Contains(r)) ? "an encrypted DefaultGame.ini decrypted into valid project settings" : null
                    }.OfType<string>()) + "."
                };
                return new KeyDiagnostics(
                    g.Key.ToString(),
                    configured,
                    readers.Count,
                    readers.Count(mounted.Contains),
                    readers.Count(r => !mounted.Contains(r)),
                    readers.Count(r => r.IsEncrypted),
                    readers.Count(r => mounted.Contains(r) && !r.IsEncrypted && r.EncryptedFileCount > 0),
                    readers.Where(mounted.Contains).Sum(r => (long) r.EncryptedFileCount),
                    readers.Count(r => r.AesKey is not null),
                    Count(KeyState.Verified),
                    Count(KeyState.Rejected),
                    Count(KeyState.Missing),
                    Count(KeyState.Unverified),
                    Count(KeyState.Untested),
                    state.ToString().ToLowerInvariant(),
                    evidence);
            })
            .ToList();

        return new EncryptionDiagnostics(
            provider.RequiredKeys.Select(g => g.ToString()).Order(StringComparer.Ordinal).ToList(),
            keys);
    }

    #endregion

    private static MappingsDiagnostics Mappings(GameSession session)
    {
        var types = session.Provider.MappingsForGame?.Types.Count ?? 0;
        // LoadSummary.Mappings is "<file name> (<source>)"; the source may be a full download URL.
        if (session.Summary.Mappings is not { Length: > 0 } text)
            return new MappingsDiagnostics(false, null, null, types);
        var open = text.LastIndexOf(" (", StringComparison.Ordinal);
        var fileName = Path.GetFileName(open > 0 ? text[..open] : text);
        var source = open > 0 ? text[(open + 2)..].TrimEnd(')') : null;
        source = source switch
        {
            null => null,
            "games.json" => "games.json",
            _ when source.StartsWith("cache", StringComparison.Ordinal) => source,
            _ => ReportSanitizer.EndpointLabel(source) is { } endpoint ? $"endpoint {endpoint}" : "endpoint"
        };
        return new MappingsDiagnostics(session.Provider.MappingsContainer is not null, fileName, source, types);
    }

    #region Probe

    private static ProbeDiagnostics Probe(GameSession session, string input, ReportSanitizer clean, HashSet<IAesVfsReader> probeVerified, CancellationToken ct)
    {
        var provider = session.Provider;
        var stages = new List<ProbeStage>();
        var shown = clean.Clean(input);

        GameFile? file = null;
        var raw = input.Trim().Trim('"').Replace('\\', '/');
        if (raw.Length > 0 && provider.TryGetGameFile(raw, out var direct)) file = direct;
        if (file is null)
        {
            try
            {
                var (packagePath, _) = session.ParsePath(input);
                foreach (var ext in GameFile.UePackageExtensions)
                    if (provider.TryGetGameFile(packagePath + "." + ext, out var found)) { file = found; break; }
            }
            catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
            {
                // Reported below as not-found.
            }
        }
        if (file is null)
        {
            stages.Add(new ProbeStage("locate", "failed", "not-found", "No file or package with this path is in the mounted containers."));
            stages.Add(new ProbeStage("read", "skipped"));
            stages.Add(new ProbeStage("header", "skipped"));
            stages.Add(new ProbeStage("deserialize", "skipped"));
            return new ProbeDiagnostics(shown, "unknown", null, null, null, false,
                "Nothing was read; the probe says nothing about AES keys.", stages);
        }

        var isPackage = file.IsUePackage;
        var reader = (file as VfsEntry)?.Vfs as IAesVfsReader;
        var entryEncrypted = file.IsEncrypted;
        stages.Add(new ProbeStage("locate", "ok", Detail: reader is null ? "loose file" : $"in {clean.Clean(Path.GetFileName(reader.Name))}"));

        var readOk = false;
        try
        {
            var bytes = file.Read();
            readOk = true;
            stages.Add(new ProbeStage("read", "ok", Detail: $"{bytes.Length} bytes{(entryEncrypted ? ", decrypted" : "")}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stages.Add(new ProbeStage("read", "failed", Category(ex, "read"), clean.Describe(ex)));
        }
        ct.ThrowIfCancellationRequested();

        var headerOk = false;
        if (!isPackage || !readOk)
        {
            var why = isPackage ? "the read failed" : "not a UE package";
            stages.Add(new ProbeStage("header", "skipped", Detail: why));
            stages.Add(new ProbeStage("deserialize", "skipped", Detail: why));
        }
        else
        {
            IPackage? package = null;
            try
            {
                package = provider.LoadPackage(file);
                headerOk = true;
                stages.Add(new ProbeStage("header", "ok", Detail:
                    $"{package.ExportMapLength} exports, {package.ImportMapLength} imports, {(package is IoPackage ? "zen" : "legacy")} format, " +
                    $"{(package.Summary.bUnversioned ? "unversioned" : "tagged")} properties"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stages.Add(new ProbeStage("header", "failed", Category(ex, "header"), clean.Describe(ex)));
                stages.Add(new ProbeStage("deserialize", "skipped", Detail: "the header did not load"));
            }
            if (package is not null) stages.Add(Deserialize(package, session, clean, ct));
        }

        string evidence;
        if (reader is null || !entryEncrypted)
        {
            evidence = "The probed entry is not encrypted; this read says nothing about configured AES keys.";
        }
        else if (!readOk)
        {
            evidence = "Reading the encrypted entry failed; see the read stage category.";
        }
        else if (headerOk)
        {
            probeVerified.Add(reader);
            evidence = "Verified: the encrypted entry decrypted and parsed as a valid package header.";
        }
        else if (isPackage)
        {
            evidence = "The encrypted entry decrypted without error but did not parse as a package: the key may be wrong, or the package format is unsupported.";
        }
        else
        {
            evidence = "The encrypted entry decrypted without error, but non-package contents cannot be validated; the key stays unverified.";
        }

        return new ProbeDiagnostics(
            clean.Clean(file.Path), isPackage ? "package" : "file",
            reader is null ? null : clean.Clean(Path.GetFileName(reader.Name)),
            reader?.IsEncrypted, reader is null ? null : entryEncrypted,
            reader is not null && entryEncrypted, evidence, stages);
    }

    private static ProbeStage Deserialize(IPackage package, GameSession session, ReportSanitizer clean, CancellationToken ct)
    {
        var total = package.ExportsLazy.Length;
        var budget = Math.Min(total, ProbeExportBudget);
        int ok = 0, failed = 0, absent = 0;
        Exception? first = null;
        for (var i = 0; i < budget; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (package.ExportsLazy[i] is not { } export)
            {
                absent++;
                continue;
            }
            try
            {
                _ = export.Value;
                ok++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                first ??= ex;
            }
        }

        var detail = $"{ok} of {budget} export(s) deserialized" +
                     (budget < total ? $" (first {budget} of {total} tried)" : "") +
                     (absent > 0 ? $", {absent} without serialized data" : "");
        if (first is null) return new ProbeStage("deserialize", "ok", Detail: detail);

        var category = package.Summary.bUnversioned && session.Provider.MappingsForGame is null
            ? "properties-no-mappings"
            : Category(first, "properties");
        return new ProbeStage("deserialize", ok == 0 ? "failed" : "partial", category, clean.Describe(first), detail);
    }

    private static string Category(Exception ex, string fallback) => ex switch
    {
        InvalidAesKeyException => "decryption",
        MappingException => "mappings-required",
        OodleException or ZlibException or UnknownCompressionMethodException => "decompression",
        FileLoadException when ex.Message.Contains("decompress", StringComparison.OrdinalIgnoreCase) => "decompression",
        _ => fallback
    };

    #endregion

    private static string MountStatus(int mounted, int unmounted) => (mounted, unmounted) switch
    {
        (0, 0) => "no-containers",
        (0, _) => "none-mounted",
        (_, 0) => "mounted",
        _ => "partial"
    };

    private static string ContainerType(IAesVfsReader reader) => reader switch
    {
        IoStoreOnDemandReader => "iostore-ondemand",
        IoStoreReader => "iostore",
        PakFileReader => "pak",
        _ => reader.GetType().Name
    };
}
