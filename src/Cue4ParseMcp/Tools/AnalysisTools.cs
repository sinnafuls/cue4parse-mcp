using System.ComponentModel;
using Cue4ParseMcp.Core;
using ModelContextProtocol.Server;

namespace Cue4ParseMcp.Tools;

[McpServerToolType]
public sealed class AnalysisTools(DiagnosticsOps diagnostics, DependencyOps dependencies)
{
    private const string GameParam = "Game id from games.json (e.g. valorant, dbd). Defaults to the most recently loaded game.";

    [McpServerTool(Name = "diagnose_game", ReadOnly = true), Description(
        "Sanitized health report for a game: engine version, container mount counts/types and unmounted reasons, " +
        "per-GUID AES key state (configured / assigned / verified / rejected / missing), mappings and codec availability, plus warnings. " +
        "Mounts the game if needed; a mount failure is reported, not thrown. Keys count as verified only when CUE4Parse validated a " +
        "decrypted index at mount or the probe decrypted an encrypted package; unencrypted reads never verify keys. " +
        "Never contains AES key values, endpoint tokens, local paths or stack traces.")]
    public Task<GameDiagnostics> DiagnoseGame(
        [Description(GameParam)] string? game = null,
        [Description("Optional asset or file to probe, e.g. ShooterGame/Content/UI/Foo or a .ini path. Reports locate, read (decryption/decompression), " +
                     "header and property deserialization stages separately.")] string? probe = null,
        CancellationToken ct = default)
        => diagnostics.DiagnoseAsync(game, probe, ct);

    [McpServerTool(Name = "get_dependencies", ReadOnly = true), Description(
        "Outgoing package references of an asset: hard imports (import map / IoStore store entry) and typed soft object paths " +
        "(package header list and deserialized property values), traversed breadth-first. Each reference has its kind, source, " +
        "whether it resolves to a mounted package, whether it was scanned and whether it closes a cycle. Reports truncation, " +
        "depth limits, per-package errors and completeness. Outgoing only: no reverse lookup or whole-game index; runtime-built, " +
        "string-based and native-serialized references are omitted.")]
    public Task<DependencyReport> GetDependencies(
        [Description("Asset path as returned by search_assets; '/Game/...' and 'Pkg.Object' forms also work (references are package-level).")] string path,
        [Description("Package levels whose references are listed (1-8). 1 = only the asset's direct references.")] int depth = 1,
        [Description("Maximum number of reference entries in the report (1-2000); traversal stops when reached.")] int limit = 200,
        [Description(GameParam)] string? game = null,
        CancellationToken ct = default)
        => dependencies.GetAsync(game, path, depth, limit, ct);
}
