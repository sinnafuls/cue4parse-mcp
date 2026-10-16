using System.Buffers.Binary;
using System.Text;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.Sound.Node;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse_Conversion.Sounds;

namespace Cue4ParseMcp.Core;

/// <summary>Header facts parsed from a RIFF/RIFX WAVE payload; nothing here is inferred from the codec name.</summary>
public sealed record WaveFormatInfo(
    string Container,
    int FormatTag,
    string Format,
    int Channels,
    int SampleRate,
    int BitsPerSample,
    int BlockAlign,
    long? DataBytes,
    long? Frames);

/// <param name="Files">The single written file.</param>
/// <param name="Mode">original or wav, as requested.</param>
/// <param name="Source">Canonical "Package.Object" path of the sound asset, or the archive path of a loose audio file.</param>
/// <param name="SourceKind">The asset class (SoundWave, SoundNodeWave, AkMediaAssetData) or "LooseFile".</param>
/// <param name="SourceCodec">Codec the cooked data declares (e.g. OPUS, BINKA, RADA, OGG, ADPCM, PCM, WEM), or the codec detected in a loose file.</param>
/// <param name="Container">Container actually found in the bytes, e.g. "RIFF/WAVE PCM (0x0001)", "Ogg Vorbis", or "none (Unreal OPUS stream)".</param>
/// <param name="OutputFormat">Extension of the written file, without the dot.</param>
/// <param name="Transcoded">Always false: the pinned CUE4Parse decoder never re-encodes, so the written bytes equal the payload.</param>
/// <param name="Bytes">Size of the written file.</param>
/// <param name="Wave">Parsed WAVE header when the payload is RIFF/RIFX WAVE.</param>
public sealed record AudioExportReport(
    ExportReport Files,
    string Mode,
    string Source,
    string SourceKind,
    string SourceCodec,
    string Container,
    string OutputFormat,
    bool Transcoded,
    long Bytes,
    WaveFormatInfo? Wave,
    IReadOnlyList<string> Notes);

/// <summary>
/// Extracts audio through CUE4Parse's <see cref="SoundDecoder"/>. That decoder only selects and concatenates cooked
/// payloads; it has no codec that turns OPUS/BINKA/RADA/OGG/WEM/ADPCM into PCM. So "original" writes the payload as is
/// with an extension chosen from its real header, and "wav" succeeds only when the payload already is PCM RIFF/WAVE.
/// </summary>
public sealed class AudioOps(GameManager games)
{
    private const string Supported = "SoundWave, SoundNodeWave and AkMediaAssetData";

    /// <summary>Loose single-stream audio files accepted by exact archive path. Banks/packages are refused below.</summary>
    private static readonly HashSet<string> LooseAudioExtensions = new(["wem", "wav", "ogg", "opus", "flac"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> BankExtensions = new(["bnk", "pck", "bank", "awb", "acb"], StringComparer.OrdinalIgnoreCase);

    public async Task<AudioExportReport> ExportAudioAsync(string? game, string path, string mode, string? outDir, CancellationToken ct)
    {
        ParseMode(mode); // reject a bad mode before mounting anything
        using var lease = await games.AcquireAsync(game, ct);
        return await ExportAudioAsync(lease, path, mode, outDir, ct);
    }

    /// <summary>Borrows <paramref name="lease"/> for the whole operation; the caller disposes it.</summary>
    public async Task<AudioExportReport> ExportAudioAsync(GameLease lease, string path, string mode, string? outDir, CancellationToken ct)
    {
        var wav = ParseMode(mode);
        ct.ThrowIfCancellationRequested();
        var session = lease.Session;
        var outputDir = Path.GetFullPath(games.ResolveOutputDir(lease, outDir));

        var source = FindLooseFile(session, path) is { } file ? ReadLoose(file) : ReadCooked(session, path);
        ct.ThrowIfCancellationRequested();

        var notes = new List<string>(source.Notes);
        var wave = ParseWave(source.Data);
        var (container, extension) = Describe(source, wave, notes);

        byte[] output;
        if (wav)
        {
            output = DecodeToPcmWav(source, container, extension, out wave);
            extension = "wav";
            notes.Add("mode=wav: the payload already is uncompressed PCM RIFF/WAVE (confirmed by CUE4Parse SoundDecoder and header validation); written byte-for-byte, nothing was decoded or transcoded.");
        }
        else
        {
            output = source.Data;
            notes.Add("mode=original: bytes are the unmodified payload returned by CUE4Parse; nothing was decoded or transcoded.");
        }

        var destination = source.LoosePath is { } loose
            ? ExportFiles.WithinDirectory(outputDir, loose)
            : ExportFiles.AssetPath(session, path, outputDir, extension);
        await ExportFiles.WriteAsync(destination, (stream, token) => stream.WriteAsync(output, token).AsTask(), ct);

        return new AudioExportReport(
            new ExportReport(outputDir, [destination], [], []),
            wav ? "wav" : "original", source.Name, source.Kind, source.Codec, container, extension,
            Transcoded: false, output.LongLength, wave, notes);
    }

    internal static bool ParseMode(string mode) => mode.Trim().ToLowerInvariant() switch
    {
        "original" or "" => false,
        "wav" => true,
        _ => throw new ArgumentException($"Unknown audio mode '{mode}'. Use original (cooked payload as is) or wav (only for payloads that already are PCM WAVE).")
    };

    private sealed record AudioSource(string Name, string Kind, string Codec, byte[] Data, string? LoosePath, IReadOnlyList<string> Notes);

    #region Sources

    private static AudioSource ReadCooked(GameSession session, string path)
    {
        var (package, _) = session.ParsePath(path);
        var obj = session.LoadMain(path);
        if (obj is not (USoundWave or USoundNodeWave or UAkMediaAssetData))
            throw new NotSupportedException(
                $"{path} is a {obj.ExportType}; export_audio supports {Supported}. SoundCues, Wwise events and banks only reference audio: " +
                "use get_dependencies or inspect_asset (properties=true) to find the SoundWave/AkMediaAssetData they play.");

        string codec;
        byte[]? data;
        try
        {
            obj.Decode(shouldDecompress: false, out codec, out data);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"{path} ({obj.ExportType}): CUE4Parse could not read the cooked audio payload ({ex.GetType().Name}: {ex.Message}). " +
                "Its bulk data may live in an unmounted or optional container, or the asset failed to deserialize (check mappings).", ex);
        }
        if (data is not { Length: > 0 })
            throw new InvalidDataException(
                $"{path} ({obj.ExportType}) has no cooked audio payload: no compressed format data, raw data or non-prefetch streamed chunks were found. " +
                "Editor-only, platform-stripped or prefetch-only sounds carry no exportable audio.");

        var notes = new List<string>();
        if (obj is USoundWave { bStreaming: false, CompressedFormatData.Formats: { Count: > 1 } formats })
            notes.Add($"The asset holds {formats.Count} cooked formats ({string.Join(", ", formats.Keys.Select(k => k.Text))}); CUE4Parse SoundDecoder exports the first.");

        return new AudioSource($"{package}.{obj.Name}", obj.ExportType, codec.Length > 0 ? codec : "unknown", data, null, notes);
    }

    internal static GameFile? FindLooseFile(GameSession session, string input)
    {
        var path = input.Trim().Trim('"').Replace('\\', '/');
        var leaf = path[(path.LastIndexOf('/') + 1)..];
        var dot = leaf.LastIndexOf('.');
        if (dot <= 0) return null;
        var extension = leaf[(dot + 1)..];

        if (BankExtensions.Contains(extension))
            throw new NotSupportedException(
                $"{input} is a sound bank/package (.{extension}) holding many streams plus metadata, not one audio stream; export_audio does not unpack banks. " +
                "Export the AkMediaAssetData asset or the loose .wem of the sound you need instead.");
        if (!LooseAudioExtensions.Contains(extension)) return null;

        // The provider's lookup falls back to "<name>.umap" for unknown names; only accept the exact requested file.
        if (session.Provider.TryGetGameFile(path, out var file) && file.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase))
            return file;
        throw new FileNotFoundException(
            $"Loose audio file not found: {input}. search_assets/list_folder index packages only, so loose .wem/.ogg/.wav files must be " +
            "passed by their exact archive path including the extension (as listed by FModel or a Wwise media reference).");
    }

    private static AudioSource ReadLoose(GameFile file)
    {
        var data = file.Read();
        var extension = file.Extension.ToLowerInvariant();
        var head = data.AsSpan();
        var isWave = head.Length >= 12 && head[8..12].SequenceEqual("WAVE"u8);
        var riff = isWave && head[..4].SequenceEqual("RIFF"u8);
        var rifx = isWave && head[..4].SequenceEqual("RIFX"u8);
        var ogg = head.StartsWith("OggS"u8);

        string? codec = extension switch
        {
            "wem" when riff || rifx => "WEM",
            "wav" when riff => "WAV",
            "ogg" when ogg => OggCodec(head) ?? "OGG",
            "opus" when ogg && OggCodec(head) == "Opus" => "Opus",
            "flac" when head.StartsWith("fLaC"u8) => "FLAC",
            _ => null
        };
        if (codec is null)
            throw new InvalidDataException(
                $"{file.Path} has the .{extension} extension but its content starts with {Magic(head)}, which is not a valid .{extension} header; " +
                "refusing to write it under a misleading extension.");
        return new AudioSource(file.Path, "LooseFile", codec, data, file.Path, ["Loose archive file: written under its own path and extension."]);
    }

    #endregion

    #region Format detection

    /// <summary>Container from the real header and an extension that matches it; never ".wav" unless the bytes are RIFF/WAVE.</summary>
    private static (string Container, string Extension) Describe(AudioSource source, WaveFormatInfo? wave, List<string> notes)
    {
        var data = source.Data.AsSpan();
        var family = source.Codec.Contains("OGG", StringComparison.OrdinalIgnoreCase) ? "OGG" : source.Codec.ToUpperInvariant();
        var looseExtension = source.LoosePath is null ? null : Path.GetExtension(source.LoosePath).TrimStart('.').ToLowerInvariant();

        if (wave is not null)
        {
            var container = $"{wave.Container}/WAVE {wave.Format}";
            var extension = looseExtension ?? family switch
            {
                "WEM" => "wem",
                "AT9" => "at9",
                "XMA" or "XMA2" => "xma",
                _ => "wav"
            };
            if (extension == "wem")
                notes.Add("Wwise WEM: a RIFF variant whose codec (see Wave.Format) usually needs a Wwise-aware decoder such as vgmstream.");
            else if (extension == "wav" && wave.FormatTag != 0x0001 && !(wave.FormatTag == 0xFFFE && wave.Format.Contains("PCM")))
                notes.Add($"RIFF/WAVE container with {wave.Format} samples, not uncompressed PCM.");
            return (container, extension);
        }

        if (data.StartsWith("OggS"u8))
        {
            var inner = OggCodec(data);
            if (family is not ("OGG" or "OPUS" or "VORBIS"))
                notes.Add($"Declared codec {source.Codec} but the payload is an Ogg stream.");
            return (inner is null ? "Ogg" : $"Ogg {inner}", looseExtension ?? (inner == "Opus" ? "opus" : "ogg"));
        }
        if (data.StartsWith("fLaC"u8)) return ("FLAC", looseExtension ?? "flac");

        // No recognised container header: an engine-specific stream. Name it after the codec, never after a generic format.
        var magic = Magic(data);
        switch (family)
        {
            case "OPUS":
                notes.Add("Unreal OPUS stream without an Ogg container: written as .ueopus because standard .opus (Ogg Opus) players cannot open it; decode with an Unreal-aware tool.");
                return ($"none (Unreal OPUS stream, starts with {magic})", "ueopus");
            case "BINKA":
                notes.Add("Unreal Bink Audio stream: needs a Bink Audio decoder; CUE4Parse does not include one.");
                return ($"none (Unreal Bink Audio stream, starts with {magic})", "binka");
            case "RADA":
                notes.Add("Unreal RAD Audio stream: needs a RAD Audio decoder; CUE4Parse does not include one.");
                return ($"none (Unreal RAD Audio stream, starts with {magic})", "rada");
            case "WEM":
                notes.Add("Declared WEM but the payload has no RIFF/RIFX header; it may be truncated or prefetch-only.");
                return ($"none (declared WEM, starts with {magic})", "wem");
            default:
                Span<char> buffer = stackalloc char[16];
                var length = 0;
                foreach (var character in family)
                {
                    if (!char.IsAsciiLetterOrDigit(character)) continue;
                    buffer[length++] = char.ToLowerInvariant(character);
                    if (length == buffer.Length) break;
                }
                var name = new string(buffer[..length]);
                notes.Add($"Codec {source.Codec} is not recognised by this tool; the raw payload is written as .{(name.Length > 0 ? name : "bin")}.");
                return ($"unknown (starts with {magic})", name.Length > 0 ? name : "bin");
        }
    }

    /// <summary>The pinned decoder decides whether the payload is PCM WAVE; the header is then validated before writing.</summary>
    private static byte[] DecodeToPcmWav(AudioSource source, string container, string extension, out WaveFormatInfo? wave)
    {
        // Loose .wav files go through the decoder's PCM path; every other loose type has no PCM decoder.
        var format = source.LoosePath is null ? source.Codec : source.Codec == "WAV" ? "PCM" : source.Codec;
        byte[]? decoded;
        try
        {
            decoded = SoundDecoder.Decompress(shouldDecompress: true, ref format, source.Data);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"{source.Name}: declared {source.Codec} but CUE4Parse could not read a WAVE header from the payload ({ex.Message}). " +
                $"Use mode=original to get the unmodified .{extension} payload.", ex);
        }

        if (decoded is null || !format.Equals("WAV", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"{source.Name}: declared codec {source.Codec}, payload {container}. The pinned CUE4Parse SoundDecoder only passes through payloads that already are " +
                "integer PCM RIFF/WAVE and has no decoder that converts this one to PCM, so mode=wav cannot produce a genuine WAV. " +
                $"Use mode=original to get the unmodified .{extension} file and decode it with a tool that supports {source.Codec}.");

        wave = ParseWave(decoded);
        var problem = wave is null ? "it is not RIFF/WAVE" : PcmProblem(wave, decoded);
        if (problem is not null)
            throw new InvalidDataException($"{source.Name}: the PCM WAVE payload is malformed ({problem}); not writing a broken .wav. mode=original writes the raw bytes.");
        return decoded;
    }

    private static string? PcmProblem(WaveFormatInfo wave, byte[] data)
    {
        if (wave.Container != "RIFF") return "big-endian RIFX is not a standard WAV file";
        if (wave.Channels < 1 || wave.SampleRate < 1) return "channel count or sample rate is zero";
        if (wave.BitsPerSample is not (8 or 16 or 24 or 32)) return $"unsupported {wave.BitsPerSample}-bit samples";
        if (wave.BlockAlign != wave.Channels * wave.BitsPerSample / 8) return "block align does not match channels x bits per sample";
        if (wave.DataBytes is not { } bytes) return "no complete data chunk";
        if (bytes % wave.BlockAlign != 0) return "data chunk is not a whole number of sample frames";
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)) + 8L > data.Length) return "RIFF size exceeds the payload";
        return null;
    }

    /// <summary>Parses RIFF/RIFX WAVE fmt and data chunks. Null when the bytes are not WAVE or have no fmt chunk.</summary>
    internal static WaveFormatInfo? ParseWave(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[8..12].SequenceEqual("WAVE"u8)) return null;
        var bigEndian = data[..4].SequenceEqual("RIFX"u8);
        if (!bigEndian && !data[..4].SequenceEqual("RIFF"u8)) return null;

        uint U32(ReadOnlySpan<byte> s) => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
        ushort U16(ReadOnlySpan<byte> s) => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);

        ReadOnlySpan<byte> fmt = default;
        long? dataBytes = null;
        for (long offset = 12; offset + 8 <= data.Length;)
        {
            var id = data.Slice((int)offset, 4);
            long size = U32(data.Slice((int)offset + 4, 4));
            var available = data.Length - offset - 8;
            if (id.SequenceEqual("fmt "u8) && size <= available) fmt = data.Slice((int)offset + 8, (int)size);
            else if (id.SequenceEqual("data"u8) && size <= available) dataBytes = size;
            if (size > available) break; // truncated chunk: leave data unset rather than trusting it
            offset += 8 + size + (size & 1);
        }
        if (fmt.Length < 16) return null;

        int tag = U16(fmt);
        int channels = U16(fmt[2..]);
        var sampleRate = (int)Math.Min(U32(fmt[4..]), (uint)int.MaxValue);
        int blockAlign = U16(fmt[12..]);
        int bits = U16(fmt[14..]);
        var name = FormatName(tag);
        if (tag == 0xFFFE && fmt.Length >= 26)
            name += ", subformat " + FormatName(U16(fmt[24..]));
        long? frames = dataBytes is { } bytes && blockAlign > 0 && tag is 0x0001 or 0x0003 or 0xFFFE ? bytes / blockAlign : null;
        return new WaveFormatInfo(bigEndian ? "RIFX" : "RIFF", tag, name, channels, sampleRate, bits, blockAlign, dataBytes, frames);
    }

    private static string FormatName(int tag) => tag switch
    {
        0x0001 => "PCM (0x0001)",
        0x0002 => "MS ADPCM (0x0002)",
        0x0003 => "IEEE float (0x0003)",
        0x0011 => "IMA ADPCM (0x0011)",
        0x0166 => "XMA2 (0x0166)",
        0xFFFE => "extensible (0xFFFE)",
        0xFFFF => "Wwise Vorbis (0xFFFF)",
        _ => $"0x{tag:X4}"
    };

    private static string? OggCodec(ReadOnlySpan<byte> data)
    {
        var page = data[..Math.Min(data.Length, 128)];
        if (page.IndexOf("\u0001vorbis"u8) >= 0) return "Vorbis";
        if (page.IndexOf("OpusHead"u8) >= 0) return "Opus";
        return page.IndexOf("FLAC"u8) >= 0 ? "FLAC" : null;
    }

    private static string Magic(ReadOnlySpan<byte> data)
    {
        var head = data[..Math.Min(data.Length, 8)];
        if (head.IsEmpty) return "no bytes";
        var printable = head.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E) < 0;
        var ascii = printable ? $" ('{Encoding.ASCII.GetString(head)}')" : "";
        return "0x" + Convert.ToHexString(head) + ascii;
    }

    #endregion
}
