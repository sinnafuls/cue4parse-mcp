using System.ComponentModel;
using Cue4ParseMcp.Core;
using ModelContextProtocol.Server;

namespace Cue4ParseMcp.Tools;

[McpServerToolType]
public sealed class BatchTools(BatchJobs jobs)
{
    [McpServerTool(Name = "preview_batch", ReadOnly = true),
     Description("Preview an explicit selection or bounded search without writing files. Returns the exact selection, output options and a previewId valid for 10 minutes. Start with start_batch. Search truncation is explicit; only the shown selection will be exported.")]
    public Task<BatchPreview> PreviewBatch(
        [Description("mesh, textures, anim, json, table or audio.")] string kind,
        [Description("Explicit asset paths; mutually exclusive with query/type/under.")] string[]? paths = null,
        [Description("Search terms, all required; same case-insensitive wildcard semantics as search_assets.")] string? query = null,
        [Description("Optional class filter for search selection.")] string? type = null,
        [Description("Optional folder restriction for search selection.")] string? under = null,
        [Description("Search selection cap, 1..500. Explicit paths have an independent maximum of 500.")] int limit = 100,
        [Description("Format for the selected kind; defaults mesh=psk, textures=png, anim=psa, json/table=json. Audio uses audioMode instead.")] string? format = null,
        [Description("Mesh LODs: highest, lowest or all.")] string lods = "highest",
        [Description("Include a mesh's materials and textures.")] bool materials = true,
        [Description("Audio: original preserves the encoded payload; wav requires supported WAV decoding.")] string audioMode = "original",
        [Description("Output directory; defaults to this operation's game/config snapshot.")] string? outDir = null,
        [Description("Configured game id; defaults to the current or sole game.")] string? game = null,
        CancellationToken ct = default) => jobs.PreviewAsync(game, kind, paths, query, type, under, limit,
        format, lods, materials, audioMode, outDir, ct);

    [McpServerTool(Name = "start_batch"),
     Description("Start a previously previewed batch as a background job in this MCP server process. A remount or changed output directory invalidates the preview. Poll get_batch_job or cancel_batch. One batch executes at a time; at most eight unfinished jobs.")]
    public Task<BatchJobStatus> StartBatch(string previewId, CancellationToken ct = default) => jobs.StartAsync(previewId, ct);

    [McpServerTool(Name = "get_batch_job", ReadOnly = true),
     Description("Get batch progress and paged individual results. offset>=0, limit=1..200. The complete JSON manifest is available on disk once terminal. Shared dependency files are bounded in this response; total and truncation are explicit. Jobs belong to this server process; up to 32 are retained.")]
    public BatchJobStatus GetBatchJob(string jobId, int offset = 0, int limit = 50) => jobs.Get(jobId, offset, limit);

    [McpServerTool(Name = "cancel_batch"),
     Description("Request cooperative cancellation of a queued or running batch. Poll until cancelled/completed; finalization writes a manifest and releases its game lease. Confirmed exports remain; a cancelled native converter may leave partial files. Completed jobs are unchanged.")]
    public BatchJobStatus CancelBatch(string jobId) => jobs.Cancel(jobId);
}
