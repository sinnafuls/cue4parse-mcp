using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Options;

namespace Cue4ParseMcp.Core;

public sealed record BatchRequest
{
    public string Kind { get; init; } = "mesh";
    public string[]? Paths { get; init; }
    public string? Query { get; init; }
    public string? Type { get; init; }
    public string? Under { get; init; }
    public int Limit { get; init; } = 100;
    public string? Format { get; init; }
    public string Lods { get; init; } = "highest";
    public bool Materials { get; init; } = true;
    public string AudioMode { get; init; } = "original";
    public string? OutDir { get; init; }
}

public sealed record BatchAsset(string Path, string? Class, string? Error);
public sealed record BatchPreview(string PreviewId, string Game, BatchRequest Options, string OutputDir,
    DateTimeOffset ExpiresAt, IReadOnlyList<BatchAsset> Assets, bool Truncated, string? Note);
public sealed record BatchItemResult(string Path, string State, IReadOnlyList<string> RootFiles,
    IReadOnlyList<string> Failures, AudioExportReport? Audio = null);
public sealed record BatchJobStatus(string JobId, string PreviewId, string Game, string Kind, string State,
    int CompletedItems, int TotalItems, int ExportedObjects, int QueuedObjects,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    string OutputDir, string? ManifestPath, int ItemOffset, int? NextItemOffset, bool ItemsTruncated,
    IReadOnlyList<BatchItemResult> Items, int TotalDependencyFiles, bool DependencyFilesTruncated,
    IReadOnlyList<string> DependencyFiles, int TotalDependencyFailures, bool DependencyFailuresTruncated,
    IReadOnlyList<string> Notes, string? Error);

/// <summary>Bounded, process-local jobs. Each accepted job owns a provider/config lease until its manifest is final.</summary>
public sealed class BatchJobs(GameManager games, DataOps data, AudioOps audio) : IDisposable
{
    private const int MaxPreviews = 32;
    private const int MaxJobs = 32;
    private const int MaxUnfinishedJobs = 8;
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] ManifestNotes =
    [
        "Root files belong to individual selections; dependency files are shared across the whole batch and deduplicated.",
        "Only files confirmed by completed exporter results are listed. A failed or cancelled native exporter may leave partial files on disk."
    ];
    private readonly object _store = new();
    private readonly Dictionary<string, Plan> _previews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _runner = new(1, 1);
    private bool _disposed;

    public async Task<BatchPreview> PreviewAsync(string? game, BatchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = Validate(request);
        using var lease = await games.AcquireAsync(game, ct);
        var session = lease.Session;
        var output = games.ResolveOutputDir(lease, request.OutDir);
        IEnumerable<string> paths;
        var truncated = false;
        string? note = null;
        if (options.Paths is { Length: > 0 } explicitPaths) paths = explicitPaths;
        else
        {
            var selection = session.Search(request.Query, request.Type, request.Under, request.Limit, ct);
            paths = selection.Results.Select(hit => hit.Path);
            truncated = selection.Truncated;
            note = selection.Note;
            if (truncated) note = "Only the bounded selection shown here will be exported; narrow the search or increase limit. " + note;
        }
        var assets = new List<BatchAsset>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var canonical = path;
            try
            {
                if (options.Kind == "audio" && AudioOps.FindLooseFile(session, path) is { } loose)
                {
                    canonical = loose.Path;
                    _ = ExportFiles.WithinDirectory(output, canonical);
                    if (seen.Add(canonical)) assets.Add(new BatchAsset(canonical, "LooseFile (." + loose.Extension + ")", null));
                    continue;
                }
                var (package, objectName) = session.ParsePath(path);
                canonical = objectName is null ? package : package + "." + objectName;
                _ = ExportFiles.WithinDirectory(output, canonical + ".export");
                if (!seen.Add(canonical)) continue;
                var exports = session.GetExports(package);
                var chosen = objectName is null
                    ? exports.FirstOrDefault(export => export.IsMain) ?? exports.FirstOrDefault()
                    : exports.FirstOrDefault(export => export.Name.Equals(objectName, StringComparison.OrdinalIgnoreCase));
                assets.Add(new BatchAsset(canonical, chosen?.Class, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!seen.Add(canonical) && assets.Any(asset => asset.Path.Equals(canonical, StringComparison.OrdinalIgnoreCase))) continue;
                assets.Add(new BatchAsset(canonical, null, ex.Message));
            }
        }
        if (assets.Count == 0) throw new InvalidOperationException("The batch selection is empty; refine the search or supply paths.");
        var preview = new BatchPreview(Guid.NewGuid().ToString("N"), session.Id, options, output,
            DateTimeOffset.UtcNow + PreviewLifetime, assets.ToArray(), truncated, note);
        lock (_store)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RemoveExpiredPreviews();
            if (_previews.Count >= MaxPreviews) _previews.Remove(_previews.Values.MinBy(plan => plan.Preview.ExpiresAt)!.Preview.PreviewId);
            _previews.Add(preview.PreviewId, new Plan(preview, new WeakReference<GameSession>(session)));
        }
        return preview;
    }

    public Task<BatchPreview> PreviewAsync(string? game, string kind, string[]? paths, string? query,
        string? type, string? under, int limit, string? format, string lods, bool materials,
        string audioMode, string? outDir, CancellationToken ct) => PreviewAsync(game, new BatchRequest
        {
            Kind = kind, Paths = paths, Query = query, Type = type, Under = under, Limit = limit,
            Format = format, Lods = lods, Materials = materials, AudioMode = audioMode, OutDir = outDir
        }, ct);

    public async Task<BatchJobStatus> StartAsync(string previewId, CancellationToken ct)
    {
        Plan plan;
        lock (_store)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RemoveExpiredPreviews();
            if (!_previews.TryGetValue(previewId, out plan!)) throw new KeyNotFoundException("Preview not found or expired; create a new preview.");
        }
        var lease = await games.AcquireAsync(plan.Preview.Game, ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var output = games.ResolveOutputDir(lease, plan.Preview.Options.OutDir);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!plan.Session.TryGetTarget(out var expectedSession) || !ReferenceEquals(lease.Session, expectedSession)
                || !output.Equals(plan.Preview.OutputDir, comparison))
                throw new InvalidOperationException("The game was reloaded or its output directory changed; preview this batch again.");
            lock (_store)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_jobs.Values.Count(job => !job.Completion.Task.IsCompleted) >= MaxUnfinishedJobs)
                    throw new InvalidOperationException("Eight batches are already unfinished; wait for or cancel a job before starting another.");
                RemoveExpiredPreviews();
                if (!_previews.Remove(previewId)) throw new InvalidOperationException("This preview was already started or expired; create a new preview.");
                if (_jobs.Count >= MaxJobs)
                {
                    var oldest = _jobs.Values.Where(job => job.Completion.Task.IsCompleted).MinBy(job => job.CreatedAt)!;
                    _jobs.Remove(oldest.Id);
                    oldest.Cancellation.Dispose();
                }
                var job = new Job(plan.Preview);
                _jobs.Add(job.Id, job);
                job.Work = Task.Run(() => RunAsync(job, lease));
                return Snapshot(job, 0, 50);
            }
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public BatchJobStatus Get(string jobId, int offset = 0, int limit = 50)
    {
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit), "Offset must be nonnegative and limit must be 1..200.");
        return Snapshot(Find(jobId), offset, limit);
    }

    public BatchJobStatus Cancel(string jobId)
    {
        var job = Find(jobId);
        lock (job.Sync)
        {
            if (!job.Completion.Task.IsCompleted && job.State != "finalizing")
            {
                job.State = "cancelling";
                job.Cancellation.Cancel();
            }
        }
        return Snapshot(job, 0, 50);
    }

    public async Task<BatchJobStatus> WaitAsync(string jobId, CancellationToken ct)
    {
        var job = Find(jobId);
        using var registration = ct.Register(() =>
        {
            lock (job.Sync)
            {
                if (job.Completion.Task.IsCompleted || job.State == "finalizing") return;
                job.State = "cancelling";
                job.Cancellation.Cancel();
            }
        });
        return await job.Completion.Task.ConfigureAwait(false);
    }

    private async Task RunAsync(Job job, GameLease lease)
    {
        var token = job.Cancellation.Token;
        var acquired = false;
        string? fatal = null;
        try
        {
            await _runner.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            lock (job.Sync)
            {
                token.ThrowIfCancellationRequested();
                job.State = "running";
                job.StartedAt = DateTimeOffset.UtcNow;
            }
            if (job.Preview.Options.Kind is "mesh" or "textures" or "anim")
                await ExportNativeAsync(job, lease, token).ConfigureAwait(false);
            else
                await ExportFilesAsync(job, lease, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { fatal = ex.Message; }
        finally
        {
            if (acquired) _runner.Release();
            BatchJobStatus final;
            lock (job.Sync)
            {
                for (var index = 0; index < job.Items.Length; index++)
                {
                    var item = job.Items[index];
                    if (item.State is not ("pending" or "running")) continue;
                    job.Items[index] = item with
                    {
                        State = token.IsCancellationRequested ? "cancelled" : "failed",
                        Failures = [.. item.Failures, token.IsCancellationRequested ? "Cancelled before this item completed." : fatal ?? "Export did not complete."]
                    };
                }
                var state = token.IsCancellationRequested ? "cancelled"
                    : fatal is not null ? "failed"
                    : job.Items.All(item => item.State == "completed") && job.DependencyFailures.Count == 0 ? "completed"
                    : job.Items.Any(item => item.RootFiles.Count > 0) ? "completed_with_errors" : "failed";
                job.State = "finalizing";
                final = Snapshot(job, 0, int.MaxValue) with
                {
                    State = state, FinishedAt = DateTimeOffset.UtcNow, ManifestPath = job.Manifest, Error = fatal
                };
            }
            try
            {
                await ExportFiles.WriteAsync(job.Manifest, (stream, cancellation) =>
                    JsonSerializer.SerializeAsync(stream, final, Json, cancellation), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                final = final with { State = "failed", ManifestPath = null, Error = "The manifest could not be written: " + ex.Message };
            }
            lease.Dispose();
            lock (job.Sync)
            {
                job.State = final.State;
                job.FinishedAt = final.FinishedAt;
                job.ManifestWritten = final.ManifestPath is not null;
                job.Error = final.Error;
                job.Completion.TrySetResult(Snapshot(job, 0, 50));
            }
        }
    }

    private async Task ExportFilesAsync(Job job, GameLease lease, CancellationToken ct)
    {
        for (var index = 0; index < job.Items.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            BatchItemResult item;
            lock (job.Sync)
            {
                item = job.Items[index];
                if (item.State == "failed") continue;
                job.Items[index] = item with { State = "running" };
            }
            try
            {
                ExportReport report;
                AudioExportReport? audioReport = null;
                switch (job.Preview.Options.Kind)
                {
                    case "json":
                        report = await data.ExportJsonAsync(lease, item.Path, job.Preview.OutputDir, ct).ConfigureAwait(false);
                        break;
                    case "table":
                        report = await data.ExportTableAsync(lease, item.Path, job.Preview.Options.Format!, null, null, job.Preview.OutputDir, ct).ConfigureAwait(false);
                        break;
                    case "audio":
                        audioReport = await audio.ExportAudioAsync(lease, item.Path, job.Preview.Options.AudioMode, job.Preview.OutputDir, ct).ConfigureAwait(false);
                        report = audioReport.Files;
                        break;
                    default: throw new InvalidOperationException("Unknown batch operation.");
                }
                lock (job.Sync)
                {
                    job.Items[index] = new BatchItemResult(item.Path, report.Failures.Count == 0 ? "completed" : "completed_with_errors",
                        report.RootFiles, report.Failures, audioReport);
                    job.Dependencies.UnionWith(report.DependencyFiles);
                    job.ExportedObjects++;
                    job.QueuedObjects = job.Items.Length;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (job.Sync) job.Items[index] = item with { State = "failed", Failures = [ex.Message] };
            }
        }
    }

    private static async Task ExportNativeAsync(Job job, GameLease lease, CancellationToken ct)
    {
        var session = lease.Session;
        var roots = new Dictionary<string, UObject>(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var results = new Dictionary<string, ExportResult>(StringComparer.OrdinalIgnoreCase);
        var itemRoots = new List<string>[job.Items.Length];
        var initialFailures = new IReadOnlyList<string>[job.Items.Length];
        for (var index = 0; index < job.Items.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            itemRoots[index] = [];
            initialFailures[index] = [];
            var item = job.Items[index];
            if (item.State == "failed") continue;
            try
            {
                var obj = session.LoadMain(item.Path);
                IReadOnlyList<UObject> selected;
                IReadOnlyList<string> failures = [];
                switch (job.Preview.Options.Kind)
                {
                    case "mesh" when obj is USkinnedAsset or UStaticMesh:
                    case "anim" when obj is UAnimationAsset:
                        selected = [obj];
                        break;
                    case "textures" when obj is UTexture or UMaterialInterface:
                        selected = [obj];
                        break;
                    case "textures" when obj is USkinnedAsset or UStaticMesh:
                        var materials = GameSession.GetMeshMaterials(obj);
                        selected = materials.Materials;
                        failures = materials.Failures;
                        if (selected.Count == 0) throw new InvalidOperationException("This mesh has no loadable materials. " + string.Join("; ", failures));
                        break;
                    default: throw new InvalidOperationException($"{item.Path} is {obj.ExportType}, which cannot be exported as {job.Preview.Options.Kind}.");
                }
                foreach (var root in selected)
                {
                    var objectPath = session.ExportObjectPath(root);
                    roots.TryAdd(objectPath, root);
                    itemRoots[index].Add(objectPath);
                    if (!owners.TryGetValue(objectPath, out var indices)) owners.Add(objectPath, indices = []);
                    if (!indices.Contains(index)) indices.Add(index);
                }
                initialFailures[index] = failures;
                lock (job.Sync) job.Items[index] = item with { State = "running", Failures = failures };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                initialFailures[index] = [ex.Message];
                lock (job.Sync) job.Items[index] = item with { State = "failed", Failures = [ex.Message] };
            }
        }
        if (roots.Count == 0) return;
        var options = job.Preview.Options;
        var nativeOptions = options.Kind switch
        {
            "mesh" => new ExportOptions(meshFormat: AssetOps.ParseMeshFormat(options.Format!),
                meshQuality: AssetOps.ParseLods(options.Lods), exportMaterials: options.Materials),
            "textures" => new ExportOptions(textureFormat: AssetOps.ParseTextureFormat(options.Format!)),
            "anim" => new ExportOptions(meshFormat: AssetOps.ParseAnimFormat(options.Format!)),
            _ => throw new InvalidOperationException("Unknown native batch operation.")
        };
        var progress = new InlineProgress(value =>
        {
            lock (job.Sync)
            {
                job.ExportedObjects = value.Completed;
                job.QueuedObjects = value.Total;
                if (value.LastResult is not { } result) return;
                results[result.ObjectPath] = result;
                if (owners.TryGetValue(result.ObjectPath, out var indices))
                    foreach (var index in indices) UpdateItem(index);
                else if (result.Success) job.Dependencies.UnionWith(result.DiskFilePaths ?? []);
                else job.DependencyFailures.Add($"{result.ObjectPath}: {result.Error?.Message ?? "Dependency export failed."}");
            }
        });
        try
        {
            await session.ExportAsync(roots.Values.ToArray(), nativeOptions, job.Preview.OutputDir, ct, progress).ConfigureAwait(false);
        }
        finally
        {
            lock (job.Sync)
                for (var index = 0; index < job.Items.Length; index++)
                    if (itemRoots[index] is { Count: > 0 }) UpdateItem(index);
        }

        // Called under job.Sync, including from synchronous progress callbacks.
        void UpdateItem(int index)
        {
            var item = job.Items[index];
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failures = new List<string>();
            var complete = true;
            foreach (var objectPath in itemRoots[index])
            {
                if (!results.TryGetValue(objectPath, out var result)) { complete = false; continue; }
                if (result.Success) files.UnionWith(result.DiskFilePaths ?? []);
                else failures.Add($"{objectPath}: {result.Error?.Message ?? "Export failed."}");
            }
            // Material-resolution failures survive later progress callbacks without duplicating export failures.
            failures.InsertRange(0, initialFailures[index]);
            if (complete && files.Count == 0 && failures.Count == 0) failures.Add("Export wrote no root files.");
            var state = !complete ? "running" : failures.Count == 0 ? "completed" : files.Count > 0 ? "completed_with_errors" : "failed";
            job.Items[index] = item with { State = state, RootFiles = files.Order(StringComparer.OrdinalIgnoreCase).ToArray(), Failures = failures.ToArray() };
        }
    }

    private Job Find(string id)
    {
        lock (_store)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _jobs.TryGetValue(id, out var job) ? job : throw new KeyNotFoundException("Batch job not found in this server process or no longer retained.");
        }
    }

    private static BatchJobStatus Snapshot(Job job, int offset, int limit)
    {
        lock (job.Sync)
        {
            var count = Math.Min(limit, Math.Max(0, job.Items.Length - offset));
            var next = offset + count;
            var dependencies = job.Dependencies.Order(StringComparer.OrdinalIgnoreCase).Take(limit).ToArray();
            var notes = job.DependencyFailures.Count == 0 ? ManifestNotes : [.. ManifestNotes, .. job.DependencyFailures.Take(limit)];
            return new BatchJobStatus(job.Id, job.Preview.PreviewId, job.Preview.Game, job.Preview.Options.Kind, job.State,
                job.Items.Count(item => item.State is "completed" or "completed_with_errors" or "failed"), job.Items.Length,
                job.ExportedObjects, job.QueuedObjects, job.CreatedAt, job.StartedAt, job.FinishedAt, job.Preview.OutputDir,
                job.ManifestWritten ? job.Manifest : null, offset, next < job.Items.Length ? next : null, next < job.Items.Length,
                job.Items.Skip(offset).Take(count).ToArray(), job.Dependencies.Count, dependencies.Length < job.Dependencies.Count,
                dependencies, job.DependencyFailures.Count, job.DependencyFailures.Count > limit, notes, job.Error);
        }
    }

    private static BatchRequest Validate(BatchRequest request)
    {
        if (request.Limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(request), "Batch limit must be 1..500.");
        if (request.Paths is { Length: 0 } || request.Paths?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ArgumentException("Explicit paths must be a nonempty array of nonblank asset paths.");
        if (request.Paths?.Length > 500) throw new ArgumentException("At most 500 explicit paths are supported per batch.");
        var hasSearch = !string.IsNullOrWhiteSpace(request.Query) || !string.IsNullOrWhiteSpace(request.Type) || !string.IsNullOrWhiteSpace(request.Under);
        if ((request.Paths is not null) == hasSearch) throw new ArgumentException("Supply explicit paths OR a nonempty query/type/under search selection.");
        if (request.Kind is null || request.Lods is null || request.AudioMode is null) throw new ArgumentException("Kind, lods and audioMode cannot be null.");
        return request with
        {
            Kind = request.Kind.ToLowerInvariant(),
            Format = ResolveFormat(request),
            AudioMode = AudioOps.ParseMode(request.AudioMode) ? "wav" : "original",
            Paths = request.Paths?.ToArray()
        };
    }

    private static string? ResolveFormat(BatchRequest request)
    {
        var format = request.Format?.ToLowerInvariant();
        switch (request.Kind.ToLowerInvariant())
        {
            case "mesh":
                format ??= "psk";
                _ = AssetOps.ParseMeshFormat(format);
                _ = AssetOps.ParseLods(request.Lods);
                break;
            case "textures": format ??= "png"; _ = AssetOps.ParseTextureFormat(format); break;
            case "anim": format ??= "psa"; _ = AssetOps.ParseAnimFormat(format); break;
            case "json":
                format ??= "json";
                if (format != "json") throw new ArgumentException("JSON batch format must be json.");
                break;
            case "table":
                format ??= "json";
                if (format is not ("json" or "csv")) throw new ArgumentException("Table batch format must be json or csv.");
                break;
            case "audio":
                if (format is not null) throw new ArgumentException("Audio batches use audioMode original|wav rather than format.");
                break;
            default: throw new ArgumentException("Batch kind must be mesh, textures, anim, json, table or audio.");
        }
        return format;
    }

    private void RemoveExpiredPreviews()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var id in _previews.Where(pair => pair.Value.Preview.ExpiresAt <= now).Select(pair => pair.Key).ToArray()) _previews.Remove(id);
    }

    public void Dispose()
    {
        Job[] jobs;
        lock (_store)
        {
            if (_disposed) return;
            _disposed = true;
            _previews.Clear();
            jobs = _jobs.Values.ToArray();
        }
        foreach (var job in jobs) job.Cancellation.Cancel();
        Task.WhenAll(jobs.Select(job => job.Work)).GetAwaiter().GetResult();
        foreach (var job in jobs) job.Cancellation.Dispose();
        _runner.Dispose();
    }

    private sealed record Plan(BatchPreview Preview, WeakReference<GameSession> Session);
    private sealed class InlineProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }
    private sealed class Job
    {
        public object Sync { get; } = new();
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public BatchPreview Preview { get; }
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? FinishedAt;
        public string State = "queued";
        public string? Error;
        public bool ManifestWritten;
        public string Manifest { get; }
        public BatchItemResult[] Items { get; }
        public HashSet<string> Dependencies { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> DependencyFailures { get; } = [];
        public int ExportedObjects;
        public int QueuedObjects;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<BatchJobStatus> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Work = Task.CompletedTask;
        public Job(BatchPreview preview)
        {
            Preview = preview;
            Manifest = ExportFiles.WithinDirectory(preview.OutputDir, ".cue4parse-batches/" + Id + ".json");
            Items = preview.Assets.Select(asset => new BatchItemResult(asset.Path, asset.Error is null ? "pending" : "failed", [],
                asset.Error is null ? [] : [asset.Error])).ToArray();
        }
    }
}
