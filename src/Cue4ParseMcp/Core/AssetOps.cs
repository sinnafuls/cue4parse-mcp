using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Options;

namespace Cue4ParseMcp.Core;

/// <summary>Tool-level operations shared by the MCP tools and the CLI. Parameters are the user-facing strings.</summary>
public sealed class AssetOps(GameManager games)
{
    public async Task<ExportReport> ExportMeshAsync(string? game, string path, string format, string lods, bool materials, string? outDir, CancellationToken ct)
    {
        var options = new ExportOptions(
            meshFormat: ParseMeshFormat(format),
            meshQuality: ParseLods(lods),
            exportMaterials: materials);

        using var lease = await games.AcquireAsync(game, ct);
        var session = lease.Session;
        var obj = session.LoadMain(path);
        if (obj is not (USkinnedAsset or UStaticMesh))
            throw new InvalidOperationException($"{path} is a {obj.ExportType}, not a SkeletalMesh/StaticMesh. Use inspect_asset to find the mesh it references.");

        return await session.ExportAsync([obj], options, games.ResolveOutputDir(lease, outDir), ct);
    }

    public async Task<ExportReport> ExportTexturesAsync(string? game, string path, string format, string? outDir, CancellationToken ct)
    {
        var options = new ExportOptions(textureFormat: ParseTextureFormat(format));

        using var lease = await games.AcquireAsync(game, ct);
        var session = lease.Session;
        var obj = session.LoadMain(path);

        IReadOnlyList<UObject> roots;
        IReadOnlyList<string> materialFailures = [];
        switch (obj)
        {
            case UTexture or UMaterialInterface:
                roots = [obj];
                break;
            case USkinnedAsset or UStaticMesh:
                (var materials, materialFailures) = GameSession.GetMeshMaterials(obj);
                roots = materials;
                break;
            default:
                throw new InvalidOperationException($"{path} is a {obj.ExportType}; expected a Texture, Material or Mesh.");
        }
        if (roots.Count == 0)
            throw new InvalidOperationException(materialFailures.Count > 0
                ? $"{path} has no loadable materials: {string.Join("; ", materialFailures)}"
                : $"{path} has no materials.");

        var report = await session.ExportAsync(roots, options, games.ResolveOutputDir(lease, outDir), ct);
        return materialFailures.Count == 0 ? report : report with { Failures = [.. materialFailures, .. report.Failures] };
    }

    public async Task<ExportReport> ExportAnimAsync(string? game, string path, string format, string? outDir, CancellationToken ct)
    {
        var meshFormat = ParseAnimFormat(format);

        using var lease = await games.AcquireAsync(game, ct);
        var session = lease.Session;
        var obj = session.LoadMain(path);
        if (obj is not UAnimationAsset)
            throw new InvalidOperationException($"{path} is a {obj.ExportType}, not an animation (AnimSequence/AnimMontage/AnimComposite).");

        return await session.ExportAsync([obj], new ExportOptions(meshFormat: meshFormat), games.ResolveOutputDir(lease, outDir), ct);
    }

    public static EMeshFormat ParseAnimFormat(string format) => format.ToLowerInvariant() switch
    {
        "psa" or "actorx" => EMeshFormat.ActorX,
        "ueformat" or "ueanim" => EMeshFormat.UEFormat,
        "usd" => EMeshFormat.USD,
        _ => throw new ArgumentException($"Unsupported animation format '{format}'. Use psa, ueformat or usd.")
    };

    public static EMeshFormat ParseMeshFormat(string format) => format.ToLowerInvariant() switch
    {
        "psk" or "pskx" or "actorx" => EMeshFormat.ActorX,
        "gltf" or "glb" or "gltf2" => EMeshFormat.Gltf2,
        "ueformat" or "uemodel" => EMeshFormat.UEFormat,
        "usd" or "usda" => EMeshFormat.USD,
        _ => throw new ArgumentException($"Unsupported mesh format '{format}'. Use psk, gltf, ueformat or usd.")
    };

    public static EMeshQuality ParseLods(string lods) => lods.ToLowerInvariant() switch
    {
        "highest" or "first" or "lod0" or "0" => EMeshQuality.Highest,
        "lowest" or "last" => EMeshQuality.Lowest,
        "all" => EMeshQuality.All,
        _ => throw new ArgumentException($"Unsupported lods value '{lods}'. Use highest, lowest or all.")
    };

    public static ETextureFormat ParseTextureFormat(string format) => format.ToLowerInvariant() switch
    {
        "png" => ETextureFormat.Png,
        "tga" => ETextureFormat.Tga,
        "jpg" or "jpeg" => ETextureFormat.Jpeg,
        "webp" => ETextureFormat.Webp,
        _ => throw new ArgumentException($"Unsupported texture format '{format}'. Use png, tga, jpeg or webp.")
    };
}
