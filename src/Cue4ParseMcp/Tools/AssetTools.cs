using System.ComponentModel;
using Cue4ParseMcp.Core;
using ModelContextProtocol.Server;

namespace Cue4ParseMcp.Tools;

[McpServerToolType]
public sealed class AssetTools(GameManager games, AssetOps ops)
{
    private const string GameParam = "Game id from games.json (e.g. valorant, dbd). Defaults to the most recently loaded game.";
    private const string PathParam = "Asset path as returned by search_assets, e.g. ShooterGame/Content/Characters/Foo/Foo_Mesh. '/Game/...' and 'Pkg.Object' forms also work.";
    private const string OutDirParam = "Folder to write into. Defaults to the game's outputDir from games.json. Files keep the in-game folder structure below it.";

    [McpServerTool(Name = "list_games", ReadOnly = true), Description("List configured games and which are currently mounted.")]
    public object ListGames() => new
    {
        configured = games.Config.Games.Select(g => new
        {
            id = g.Key,
            name = g.Value.DisplayName ?? g.Key,
            ueVersion = g.Value.UeVersion,
            paksDir = g.Value.PaksDir,
        }),
        loaded = games.LoadedGames,
        config = games.Config.SourcePath,
    };

    [McpServerTool(Name = "load_game"), Description(
        "Mount a game's paks (AES keys + .usmap mappings from games.json / configured endpoints). " +
        "Takes 5-60s on first load; later tools reuse the mounted game. Other tools auto-load if needed.")]
    public async Task<LoadSummary> LoadGame(
        [Description("Game id from games.json, e.g. valorant or dbd.")] string game,
        [Description("Re-mount using the startup configuration (e.g. after a game patch). Restart the server after editing games.json.")] bool reload = false,
        CancellationToken ct = default)
    {
        return await games.LoadAsync(game, reload, ct);
    }

    [McpServerTool(Name = "search_assets", ReadOnly = true), Description(
        "Search asset paths. All query terms must appear in the path (case-insensitive, '*' wildcard). " +
        "Optional type filter checks each hit's class: SkeletalMesh, StaticMesh, Mesh, Texture, Texture2D, Material, " +
        "MaterialInstance, Animation, AnimSequence, AnimMontage, Skeleton, or any UE class name. " +
        "Results whose file name matches the terms are ranked first.")]
    public async Task<SearchResult> SearchAssets(
        [Description("Search terms, e.g. 'sarge mesh' or 'T_*_D'. May be empty when 'under' or 'type' narrows enough.")] string? query = null,
        [Description("Class filter, e.g. SkeletalMesh.")] string? type = null,
        [Description("Only search below this folder, e.g. ShooterGame/Content/Characters.")] string? under = null,
        [Description("Max results (1-500).")] int limit = 50,
        [Description(GameParam)] string? game = null,
        CancellationToken ct = default)
    {
        using var lease = await games.AcquireAsync(game, ct);
        var session = lease.Session;
        return session.Search(query, type, under, limit, ct);
    }

    [McpServerTool(Name = "list_folder", ReadOnly = true), Description("List subfolders and assets directly inside a game folder. Empty path lists the root.")]
    public async Task<FolderListing> ListFolder(
        [Description("Folder path, e.g. ShooterGame/Content/Characters.")] string? path = null,
        [Description("Max assets to list.")] int limit = 200,
        [Description(GameParam)] string? game = null,
        CancellationToken ct = default)
    {
        using var lease = await games.AcquireAsync(game, ct);
        var session = lease.Session;
        return session.ListFolder(path, Math.Clamp(limit, 1, 2000));
    }

    [McpServerTool(Name = "inspect_asset", ReadOnly = true), Description(
        "Show an asset's exports and classes. With properties=true also returns the serialized properties as JSON " +
        "(useful to find which mesh/material/anim a character or blueprint references).")]
    public async Task<object> InspectAsset(
        [Description(PathParam)] string path,
        [Description("Include full property JSON.")] bool properties = false,
        [Description("Truncate property JSON to this many characters.")] int maxChars = 40000,
        [Description(GameParam)] string? game = null,
        CancellationToken ct = default)
    {
        using var lease = await games.AcquireAsync(game, ct);
        var session = lease.Session;
        var (pkgPath, _) = session.ParsePath(path);
        return new
        {
            path = pkgPath,
            exports = session.GetExports(pkgPath),
            properties = properties ? session.ToJson(path, Math.Clamp(maxChars, 1000, 500000)) : null,
        };
    }

    [McpServerTool(Name = "export_mesh"), Description(
        "Export a SkeletalMesh or StaticMesh, by default with its materials (JSON parameters) and their textures (PNG). " +
        "Returns the written file paths; rootFiles holds the mesh itself.")]
    public Task<ExportReport> ExportMesh(
        [Description(PathParam)] string path,
        [Description("psk (ActorX psk/pskx), gltf (binary .glb), ueformat (.uemodel) or usd.")] string format = "psk",
        [Description("highest (LOD0 only), lowest, or all (one file per LOD).")] string lods = "highest",
        [Description("Also export materials and their textures.")] bool materials = true,
        [Description(OutDirParam)] string? outDir = null,
        [Description(GameParam)] string? game = null,
        CancellationToken ct = default)
        => ops.ExportMeshAsync(game, path, format, lods, materials, outDir, ct);

    [McpServerTool(Name = "export_textures"), Description(
        "Export textures. Path may be a Texture (exports it), a Material (its parameters + textures), " +
        "or a Mesh (all textures of all its materials, without the mesh).")]
    public Task<ExportReport> ExportTextures(
        [Description(PathParam)] string path,
        [Description("png, tga, jpeg or webp.")] string format = "png",
        [Description(OutDirParam)] string? outDir = null,
        [Description(GameParam)] string? game = null,
        CancellationToken ct = default)
        => ops.ExportTexturesAsync(game, path, format, outDir, ct);

    [McpServerTool(Name = "export_anim"), Description("Export an AnimSequence / AnimMontage / AnimComposite.")]
    public Task<ExportReport> ExportAnim(
        [Description(PathParam)] string path,
        [Description("psa (ActorX), ueformat (.ueanim) or usd.")] string format = "psa",
        [Description(OutDirParam)] string? outDir = null,
        [Description(GameParam)] string? game = null,
        CancellationToken ct = default)
        => ops.ExportAnimAsync(game, path, format, outDir, ct);
}
