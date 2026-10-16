using System.ComponentModel;
using Cue4ParseMcp.Core;
using ModelContextProtocol.Server;

namespace Cue4ParseMcp.Tools;

[McpServerToolType]
public sealed class AudioTools(AudioOps ops)
{
    [McpServerTool(Name = "export_audio"), Description(
        "Export one sound: a SoundWave, SoundNodeWave or AkMediaAssetData asset, or a loose .wem/.wav/.ogg/.opus/.flac file given by its exact archive path " +
        "(loose files are not listed by search_assets). mode=original writes the cooked payload unchanged with an extension matching its real header " +
        "(.wav only for RIFF/WAVE, .ogg, .wem, .binka, .rada, .ueopus for Unreal Opus streams, ...). mode=wav succeeds only when that payload already is " +
        "uncompressed PCM WAVE: nothing is transcoded, and OPUS/BINKA/RADA/OGG/WEM/ADPCM payloads return an unsupported-format error. " +
        "The result reports the declared codec, detected container, WAVE header and notes. SoundCues/banks are refused; find their waves with get_dependencies.")]
    public Task<AudioExportReport> ExportAudio(
        [Description("Sound asset path as returned by search_assets ('Pkg.Object' selects one export), or a loose audio file path including its extension.")] string path,
        [Description("original (cooked payload as is) or wav (only for payloads that already are PCM WAVE).")] string mode = "original",
        [Description("Folder to write into. Defaults to the game's outputDir from games.json. Files keep the in-game folder structure below it.")] string? outDir = null,
        [Description("Game id from games.json (e.g. valorant, dbd). Defaults to the most recently loaded game.")] string? game = null,
        CancellationToken ct = default)
        => ops.ExportAudioAsync(game, path, mode, outDir, ct);
}
