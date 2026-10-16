using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.UObject;

namespace Cue4ParseMcp.Core;

/// <summary>
/// Outgoing package references from a root package, traversed breadth-first. <see cref="Complete"/> is relative to
/// the requested depth and the documented sources only: it never claims whole-game coverage or reverse lookups.
/// </summary>
/// <param name="RootObject">Object part of the input; references are always package-level.</param>
/// <param name="Truncated">The reference limit was reached; later references and packages are omitted.</param>
/// <param name="DepthLimited">Resolvable references at the final depth were not scanned.</param>
/// <param name="Complete">Not truncated, no errors, and every scanned package's hard and soft sources were fully read.</param>
public sealed record DependencyReport(
    string Game,
    string Root,
    string? RootObject,
    int Depth,
    int Limit,
    int PackagesScanned,
    int ReferencesReported,
    bool Truncated,
    bool DepthLimited,
    int ErrorCount,
    bool Complete,
    IReadOnlyList<DependencyPackage> Packages,
    IReadOnlyList<string> Omitted);

/// <param name="Depth">0 for the root.</param>
/// <param name="Format">legacy (.uasset/.uexp) or zen (IoStore).</param>
/// <param name="HardReferencesComplete">The import data was read completely and every reference was reported.</param>
/// <param name="SoftReferencesComplete">Every export was deserialized and scanned, and every soft reference was reported.</param>
/// <param name="ScriptPackages">Native /Script packages imported; code, not files, so never traversed.</param>
public sealed record DependencyPackage(
    string Path,
    int Depth,
    string Format,
    bool HardReferencesComplete,
    bool SoftReferencesComplete,
    int ExportCount,
    int ExportsScanned,
    IReadOnlyList<string> ScriptPackages,
    IReadOnlyList<DependencyReference> References,
    IReadOnlyList<string> Errors);

/// <param name="Target">Canonical package path when resolved; otherwise the engine package name or "packageId:&lt;id&gt;".</param>
/// <param name="Kind">hard (import) or soft (soft object path).</param>
/// <param name="Source">import-map, store-entry, imported-packages, soft-object-path-list or property.</param>
/// <param name="Resolved">The target package exists in the mounted containers.</param>
/// <param name="Scanned">The target's own references are listed in this report.</param>
/// <param name="Cycle">The target is an ancestor of this package in the traversal.</param>
/// <param name="Objects">Referenced objects inside the target (up to 16), when the source names them.</param>
public sealed record DependencyReference(
    string Target,
    string Kind,
    string Source,
    bool Resolved,
    bool Scanned,
    bool Cycle,
    IReadOnlyList<string>? Objects);

public sealed class DependencyOps(GameManager games)
{
    public const int MaxDepth = 8;
    public const int MaxLimit = 2000;
    private const int MaxExportsPerPackage = 512;
    private const int MaxObjectsPerReference = 16;
    private const int MaxErrorsPerPackage = 8;
    private const int MaxValueDepth = 64;

    private static readonly string[] OmittedNotes =
    [
        "Outgoing references only: no reverse (referencer) lookup and no whole-game index.",
        "Hard references come from the legacy import map or the IoStore container store entry (falling back to the package's resolved imported packages).",
        "Soft references come from the package header soft object path list and from typed soft object path values in deserialized properties (nested arrays, sets, maps, structs, optionals, instanced structs and DataTable rows), up to 512 exports per package.",
        "Not included: references built at runtime or from plain strings/names, soft references inside native (non-property) serialized data other than DataTable rows, IoStore container-header soft package lists, Blueprint bytecode, and asset-registry-only dependencies.",
        "Unresolved targets are not in the mounted containers: missing, inside an unmounted or encrypted container, or not shipped. IoStore targets that cannot be resolved only have their package id."
    ];

    public async Task<DependencyReport> GetAsync(string? game, string path, int depth, int limit, CancellationToken ct)
    {
        if (depth is < 1 or > MaxDepth)
            throw new ArgumentOutOfRangeException(nameof(depth), depth, $"depth must be between 1 and {MaxDepth}.");
        if (limit is < 1 or > MaxLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"limit must be between 1 and {MaxLimit}.");

        using var lease = await games.AcquireAsync(game, ct);
        var clean = DiagnosticsOps.CreateSanitizer(lease.Session.Config, lease.Configuration);
        return new Traversal(lease.Session, depth, limit, clean, ct).Run(path);
    }

    private sealed record Candidate(string Target, string Kind, string Source, bool Resolved, List<string>? Objects);

    private sealed class Node(string path, int depth, Node? parent)
    {
        public string Path { get; } = path;
        public int Depth { get; } = depth;
        public Node? Parent { get; } = parent;
        public DependencyPackage? Result { get; set; }
        public List<(DependencyReference Reference, Node? Target)> References { get; } = [];
    }

    private sealed class Traversal(GameSession session, int depth, int limit, ReportSanitizer clean, CancellationToken ct)
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
        private readonly Dictionary<string, Node> _discovered = new(PathComparer);
        private readonly HashSet<Node> _scanned = [];
        private readonly Dictionary<IoStoreReader, Dictionary<FPackageId, int>> _storeIndexes = new(ReferenceEqualityComparer.Instance);
        private int _referenceCount;
        private bool _truncated;
        private bool _depthLimited;

        public DependencyReport Run(string input)
        {
            var (rootPath, rootObject) = session.ParsePath(input);
            var root = new Node(rootPath, 0, null);
            _discovered[rootPath] = root;
            var queue = new Queue<Node>();
            queue.Enqueue(root);
            var order = new List<Node>();

            while (queue.Count > 0 && !_truncated)
            {
                ct.ThrowIfCancellationRequested();
                var node = queue.Dequeue();
                order.Add(node);
                _scanned.Add(node);
                Scan(node, queue);
            }

            // Targets queued before the limit stopped traversal were never scanned; report that, not "scanned".
            var packages = order.Select(n => n.Result! with
            {
                References = n.References
                    .Select(r => r.Target is { } target ? r.Reference with { Scanned = _scanned.Contains(target) } : r.Reference)
                    .ToList()
            }).ToList();
            var errorCount = packages.Sum(p => p.Errors.Count);
            var complete = !_truncated && errorCount == 0 && packages.All(p => p.HardReferencesComplete && p.SoftReferencesComplete);

            return new DependencyReport(session.Id, rootPath, rootObject, depth, limit, packages.Count, _referenceCount,
                _truncated, _depthLimited, errorCount, complete, packages, OmittedNotes);
        }

        private void Scan(Node node, Queue<Node> queue)
        {
            var errors = new List<string>();
            var scripts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<Candidate>();
            var hardComplete = true;
            var softComplete = true;
            string format = "unknown";
            int exportCount = 0, exportsScanned = 0;

            IPackage? package = null;
            try
            {
                var file = PackageFile(node.Path) ?? throw new FileNotFoundException($"Asset not found: {node.Path}");
                package = session.Provider.LoadPackage(file);
                format = package is IoPackage ? "zen" : "legacy";
                exportCount = package.ExportMapLength;
                hardComplete = package switch
                {
                    Package legacy => LegacyImports(legacy, candidates, scripts),
                    IoPackage zen => ZenImports(zen, file, candidates, scripts, errors),
                    _ => false
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddError(errors, "header", ex);
                hardComplete = false;
                softComplete = false;
            }

            if (package is not null)
            {
                ct.ThrowIfCancellationRequested();
                var soft = new List<(string AssetPath, string Source)>();
                if (package is Package { SoftObjectPaths: { Length: > 0 } listed })
                {
                    foreach (var p in listed) soft.Add((SoftPathText(p), "soft-object-path-list"));
                }
                (exportsScanned, softComplete) = ScanExports(package, soft, errors);
                AddSoftCandidates(soft, candidates, scripts);
            }

            // A package referencing its own objects is not a dependency. Hard references first, then soft;
            // each group in target order for stable reports.
            var ordered = candidates
                .Where(c => !PathComparer.Equals(c.Target, node.Path))
                .OrderBy(c => c.Kind == "hard" ? 0 : 1)
                .ThenBy(c => c.Target, PathComparer)
                .ThenBy(c => c.Source, StringComparer.Ordinal);
            foreach (var candidate in ordered)
            {
                if (_referenceCount >= limit)
                {
                    _truncated = true;
                    if (candidate.Kind == "hard") hardComplete = false;
                    else softComplete = false;
                    continue;
                }
                _referenceCount++;
                node.References.Add(Link(node, candidate, queue));
            }

            node.Result = new DependencyPackage(node.Path, node.Depth, format, hardComplete, softComplete,
                exportCount, exportsScanned, scripts.ToList(), [], errors);
        }

        private (DependencyReference, Node?) Link(Node node, Candidate candidate, Queue<Node> queue)
        {
            var objects = candidate.Objects is { Count: > 0 } list ? list : null;
            if (!candidate.Resolved)
                return (new DependencyReference(candidate.Target, candidate.Kind, candidate.Source, false, false, false, objects), null);

            if (_discovered.TryGetValue(candidate.Target, out var existing))
            {
                var cycle = false;
                for (var ancestor = node; ancestor != null; ancestor = ancestor.Parent)
                {
                    if (ReferenceEquals(ancestor, existing)) { cycle = true; break; }
                }
                return (new DependencyReference(candidate.Target, candidate.Kind, candidate.Source, true, false, cycle, objects), existing);
            }

            if (node.Depth + 1 >= depth)
            {
                _depthLimited = true;
                return (new DependencyReference(candidate.Target, candidate.Kind, candidate.Source, true, false, false, objects), null);
            }

            var target = new Node(candidate.Target, node.Depth + 1, node);
            _discovered[candidate.Target] = target;
            queue.Enqueue(target);
            return (new DependencyReference(candidate.Target, candidate.Kind, candidate.Source, true, false, false, objects), target);
        }

        #region Hard references

        /// <summary>Every import belongs to the package at the end of its outer chain, or to its explicit PackageName.</summary>
        private bool LegacyImports(Package package, List<Candidate> candidates, SortedSet<string> scripts)
        {
            var map = package.ImportMap;
            var byPackage = new Dictionary<string, List<string>>(PathComparer);
            for (var i = 0; i < map.Length; i++)
            {
                if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
                var import = map[i];
                var owner = ImportPackage(map, i);
                if (owner.Length == 0) continue;
                if (!byPackage.TryGetValue(owner, out var objects)) byPackage[owner] = objects = [];
                // The package import itself names no object.
                if (!import.ObjectName.Text.Equals(owner, StringComparison.OrdinalIgnoreCase) && objects.Count < MaxObjectsPerReference)
                    objects.Add(import.ObjectName.Text);
            }

            foreach (var (name, objects) in byPackage)
            {
                if (IsScript(name))
                {
                    scripts.Add(name);
                    continue;
                }
                var resolved = ResolvePackageName(name);
                candidates.Add(new Candidate(resolved ?? name, "hard", "import-map", resolved is not null, objects));
            }
            return true;
        }

        private static string ImportPackage(FObjectImport[] map, int index)
        {
            var original = map[index];
            var import = original;
            for (var steps = 0; steps < map.Length && import.OuterIndex is { IsImport: true } outer; steps++)
            {
                var next = -outer.Index - 1;
                if (next < 0 || next >= map.Length) return "";
                import = map[next];
            }
            if (import.OuterIndex is null or { IsNull: true }
                && import.ClassName.Text.Equals("Package", StringComparison.OrdinalIgnoreCase))
                return import.ObjectName.Text;
            // Some cooks repeat ObjectName in PackageName; that is not an owning package.
            var explicitPackage = original.PackageName.Text;
            return explicitPackage is { Length: > 0 } && explicitPackage != "None"
                && !explicitPackage.Equals(original.ObjectName.Text, StringComparison.OrdinalIgnoreCase)
                ? explicitPackage : "";
        }

        /// <summary>IoStore lists a package's imported packages in its container's store entry.</summary>
        private bool ZenImports(IoPackage package, GameFile file, List<Candidate> candidates, SortedSet<string> scripts, List<string> errors)
        {
            for (var i = 0; i < package.ImportMap.Length; i++)
            {
                var index = package.ImportMap[i];
                if (!index.IsScriptImport) continue;
                var resolved = package.ResolveObjectIndex(index);
                for (var steps = 0; resolved?.Outer is { } outer && steps < 64; steps++) resolved = outer;
                if (resolved is not null) scripts.Add(resolved.Name.Text);
            }

            if (StoreEntry(package, file) is { } entry)
            {
                foreach (var id in entry.ImportedPackages ?? [])
                {
                    ct.ThrowIfCancellationRequested();
                    if (session.Provider.FilesById.TryGetValue(id, out var target))
                        candidates.Add(new Candidate(target.PathWithoutExtension, "hard", "store-entry", true, null));
                    else
                        candidates.Add(new Candidate($"packageId:{id.id}", "hard", "store-entry", false, null));
                }
                return true;
            }

            // No store entry (e.g. editor-only data): CUE4Parse's own resolution names the packages it found.
            try
            {
                var imported = package.ImportedPackages.Value;
                for (var i = 0; i < imported.Length; i++)
                {
                    if (imported[i] is { } target && ResolvePackageName(target.Name) is { } resolved)
                        candidates.Add(new Candidate(resolved, "hard", "imported-packages", true, null));
                    else
                        candidates.Add(new Candidate(imported[i]?.Name ?? $"importedPackage:{i}", "hard", "imported-packages", false, null));
                }
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddError(errors, "imports", ex);
                return false;
            }
        }

        private FFilePackageStoreEntry? StoreEntry(IoPackage package, GameFile file)
        {
            if (file is not FIoStoreEntry { IoStoreReader: var reader } || reader.ContainerHeader is not { } header) return null;
            if (!_storeIndexes.TryGetValue(reader, out var index))
            {
                index = new Dictionary<FPackageId, int>();
                for (var i = 0; i < header.PackageIds.Length; i++) index.TryAdd(header.PackageIds[i], i);
                // Optional-segment entries are stored after the regular ones, offset by their count.
                var optionalIds = header.OptionalSegmentPackageIds ?? [];
                for (var i = 0; i < optionalIds.Length; i++) index.TryAdd(optionalIds[i], header.PackageIds.Length + i);
                _storeIndexes[reader] = index;
            }
            if (!index.TryGetValue(FPackageId.FromName(package.Name), out var position)) return null;
            return position < header.PackageIds.Length
                ? header.StoreEntries.ElementAtOrDefault(position)
                : header.OptionalSegmentStoreEntries?.ElementAtOrDefault(position - header.PackageIds.Length);
        }

        #endregion

        #region Soft references

        private (int Scanned, bool Complete) ScanExports(IPackage package, List<(string AssetPath, string Source)> soft, List<string> errors)
        {
            var exports = package.ExportsLazy;
            var budget = Math.Min(exports.Length, MaxExportsPerPackage);
            var complete = budget == exports.Length;
            var scanned = 0;
            for (var i = 0; i < budget; i++)
            {
                ct.ThrowIfCancellationRequested();
                // Create-only exports have no serialized data and therefore nothing to scan.
                if (exports[i] is not { } lazy)
                {
                    scanned++;
                    continue;
                }
                try
                {
                    var obj = lazy.Value;
                    var paths = new List<string>();
                    if (!Collect(obj, paths, 0)) complete = false;
                    if (obj is UDataTable { RowMap: { } rows })
                    {
                        foreach (var row in rows.Values)
                            if (!Collect(row, paths, 0)) complete = false;
                    }
                    else if (obj is UDataTable)
                    {
                        complete = false;
                        AddError(errors, "rows", new InvalidDataException("DataTable rows could not be read; its row struct could not be resolved."));
                    }
                    soft.AddRange(paths.Select(p => (p, "property")));
                    scanned++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    complete = false;
                    AddError(errors, "export", ex);
                }
            }
            return (scanned, complete);
        }

        /// <summary>Typed soft object path values only; plain strings and names are never treated as references.</summary>
        private bool Collect(object? value, List<string> paths, int level)
        {
            ct.ThrowIfCancellationRequested();
            if (value is null) return true;
            if (level > MaxValueDepth) return false;
            var complete = true;
            switch (value)
            {
                case FSoftObjectPath path:
                    paths.Add(SoftPathText(path));
                    break;
                case FPropertyTag tag:
                    complete &= Collect(tag.Tag, paths, level + 1);
                    break;
                case SoftObjectProperty property:
                    complete &= Collect(property.Value, paths, level + 1);
                    break;
                case AssetObjectProperty { Value: { Length: > 0 } assetPath }:
                    paths.Add(assetPath);
                    break;
                case ArrayProperty { Value: { } array }:
                    foreach (var item in array.Properties) complete &= Collect(item, paths, level + 1);
                    break;
                case SetProperty { Value: { } set }:
                    foreach (var item in set.Properties) complete &= Collect(item, paths, level + 1);
                    break;
                case MapProperty { Value: { } map }:
                    foreach (var (key, item) in map.Properties)
                    {
                        complete &= Collect(key, paths, level + 1);
                        complete &= Collect(item, paths, level + 1);
                    }
                    break;
                case StructProperty { Value: { } scriptStruct }:
                    complete &= Collect(scriptStruct.StructType, paths, level + 1);
                    break;
                case OptionalProperty optional:
                    complete &= Collect(optional.Value, paths, level + 1);
                    break;
                case FInstancedStruct instanced:
                    complete &= Collect(instanced.ScriptStruct?.StructType, paths, level + 1);
                    break;
                case IPropertyHolder holder:
                    foreach (var property in holder.Properties) complete &= Collect(property, paths, level + 1);
                    break;
            }
            return complete;
        }

        private void AddSoftCandidates(List<(string AssetPath, string Source)> soft, List<Candidate> candidates, SortedSet<string> scripts)
        {
            foreach (var group in soft
                         .Where(s => s.AssetPath.Length > 0 && s.AssetPath != "None")
                         .GroupBy(s => (Package: SoftPackage(s.AssetPath), s.Source), s => s.AssetPath))
            {
                var name = group.Key.Package;
                if (name.Length == 0) continue;
                if (IsScript(name))
                {
                    scripts.Add(name);
                    continue;
                }
                var objects = group.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxObjectsPerReference).ToList();
                var resolved = ResolvePackageName(name);
                candidates.Add(new Candidate(resolved ?? name, "soft", group.Key.Source, resolved is not null, objects));
            }
        }

        private static string SoftPathText(FSoftObjectPath path)
        {
            var asset = path.AssetPathName.Text;
            return path.SubPathString is { Length: > 0 } sub ? $"{asset}:{sub}" : asset;
        }

        /// <summary>"/Game/X/Y.Y:Sub" -> "/Game/X/Y".</summary>
        private static string SoftPackage(string assetPath)
        {
            var s = assetPath.Trim();
            var colon = s.IndexOf(':');
            if (colon >= 0) s = s[..colon];
            var leaf = s.LastIndexOf('/') + 1;
            var dot = s.IndexOf('.', leaf);
            return dot >= 0 ? s[..dot] : s;
        }

        #endregion

        private static bool IsScript(string packageName) =>
            packageName.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase);

        private GameFile? PackageFile(string path)
        {
            foreach (var ext in GameFile.UePackageExtensions)
            {
                if (session.Provider.TryGetGameFile(path + "." + ext, out var file)) return file;
            }
            return null;
        }

        /// <summary>Engine package name ("/Game/X/Y") to canonical provider path, or null when not mounted.</summary>
        private string? ResolvePackageName(string name)
        {
            if (name.Length == 0 || name == "None") return null;
            try
            {
                return PackageFile(name)?.PathWithoutExtension;
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
            {
                return null;
            }
        }

        private void AddError(List<string> errors, string stage, Exception ex)
        {
            if (errors.Count >= MaxErrorsPerPackage) return;
            errors.Add($"{stage}: {clean.Describe(ex)}");
        }
    }
}
