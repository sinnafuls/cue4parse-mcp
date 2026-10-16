using System.Buffers.Binary;
using CUE4Parse_Conversion.Sounds;
using Cue4ParseMcp.Config;
using Cue4ParseMcp.Core;

namespace Cue4ParseMcp.Tests;

/// <summary>
/// The shared legacy pak has no sounds; the checked-in uncompressed tagged IoStore container has the SW_* matrix.
/// Copies only that variant (plus its global container) so the Oodle/zlib variants beside it are not mounted.
/// </summary>
public sealed class AudioIoStoreFixture : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cue4parse-audio-" + Guid.NewGuid().ToString("N"));
    public GameManager Games { get; }
    public string OutputRoot => Path.Combine(_directory, "exports");

    public AudioIoStoreFixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        const string tagged = "lib/CUE4Parse/CUE4Parse.Tests/Fixtures/UE5_8/IoStore/Tagged";
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, tagged))) root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("The recursive CUE4Parse checkout with cooked fixtures is required.");
        var source = Path.Combine(root.FullName, tagged);

        var paks = Path.Combine(_directory, "paks");
        Directory.CreateDirectory(paks);
        foreach (var file in Directory.GetFiles(source, "global.*").Concat(Directory.GetFiles(Path.Combine(source, "Uncompressed"))))
            File.Copy(file, Path.Combine(paks, Path.GetFileName(file)));

        var unavailable = Path.Combine(_directory, "unavailable-codec.dll");
        File.WriteAllText(unavailable, "No downloaded Oodle/zlib codec is needed for uncompressed fixtures.");
        Games = new GameManager(new AppConfig
        {
            DefaultOutputDir = OutputRoot,
            CacheDir = Path.Combine(AppContext.BaseDirectory, "native-cache"),
            OodlePath = unavailable,
            ZlibPath = unavailable,
            Games = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fixture"] = new GameConfig { PaksDir = paks, UeVersion = "GAME_UE5_8" }
            }
        });
    }

    public void Dispose()
    {
        Games.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}

public sealed class AudioOpsTests(AudioIoStoreFixture fixture) : IClassFixture<AudioIoStoreFixture>
{
    private const string AudioFolder = "CUE4ParseFixtures/Content/Fixtures/Audio/";
    private readonly AudioOps _audio = new(fixture.Games);
    private readonly string _outDir = Path.Combine(fixture.OutputRoot, Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("SW_Format_PCM", 48_000, 96_000)]
    [InlineData("SW_Format_ADPCM", 48_000, 96_000)] // cooked by UE as PCM inside the ADPCM format
    [InlineData("SW_Inline", 22_050, 22_050)]
    [InlineData("SW_Streaming", 22_050, 6 * 22_050)]
    public async Task PcmPayloadsExportAsTheSameGenuineWaveInBothModes(string asset, int sampleRate, int frames)
    {
        using var lease = await fixture.Games.AcquireAsync("fixture", CancellationToken.None);
        var path = AudioFolder + asset;
        var (declaredCodec, payload) = PinnedPayload(lease, path);

        var original = await _audio.ExportAudioAsync(lease, path, "original", _outDir, CancellationToken.None);
        var originalBytes = AssertSingleFile(original, path + ".wav");
        Assert.Equal(payload, originalBytes);
        Assert.Equal(declaredCodec, original.SourceCodec);
        Assert.Equal("wav", original.OutputFormat);
        Assert.False(original.Transcoded);
        Assert.Equal(payload.LongLength, original.Bytes);

        var wave = ReadPcmWave(originalBytes);
        Assert.Equal(1, wave.Channels);
        Assert.Equal(sampleRate, wave.SampleRate);
        Assert.Equal(16, wave.BitsPerSample);
        Assert.Equal(frames, wave.Samples.Length);
        Assert.Contains(wave.Samples, sample => sample > 1_000);
        Assert.Contains(wave.Samples, sample => sample < -1_000);
        Assert.Equal(new WaveFormatInfo("RIFF", 1, "PCM (0x0001)", 1, sampleRate, 16, 2, frames * 2L, frames), original.Wave);

        // The borrowed lease is still usable, and wav mode writes the identical, untranscoded PCM file.
        var wav = await _audio.ExportAudioAsync(lease, path, "wav", _outDir, CancellationToken.None);
        Assert.Equal(payload, AssertSingleFile(wav, path + ".wav"));
        Assert.Equal("wav", wav.Mode);
        Assert.False(wav.Transcoded);
        Assert.Equal(original.Wave, wav.Wave);
    }

    [Theory]
    [InlineData("SW_Format_BinkAudio", "BINKA", "binka")]
    [InlineData("SW_Format_Opus", "OPUS", "ueopus")]
    [InlineData("SW_Format_RADAudio", "RADA", "rada")]
    [InlineData("SW_Format_PlatformSpecific", "OGG", "ogg")]
    [InlineData("SW_Format_ProjectDefined", "OGG", "ogg")]
    public async Task CompressedPayloadsKeepTheirCodecAndAreNeverPassedOffAsWav(string asset, string codec, string extension)
    {
        using var lease = await fixture.Games.AcquireAsync("fixture", CancellationToken.None);
        var path = AudioFolder + asset;
        var (declaredCodec, payload) = PinnedPayload(lease, path);
        Assert.Equal(codec, declaredCodec);

        var report = await _audio.ExportAudioAsync(lease, path, "original", _outDir, CancellationToken.None);
        var bytes = AssertSingleFile(report, path + "." + extension);
        Assert.Equal(payload, bytes);
        Assert.Equal(codec, report.SourceCodec);
        Assert.Equal(extension, report.OutputFormat);
        Assert.False(report.Transcoded);
        Assert.Null(report.Wave);
        Assert.False(bytes.AsSpan().StartsWith("RIFF"u8), "A compressed payload must not be a RIFF file.");
        if (codec == "OGG")
        {
            Assert.True(bytes.AsSpan().StartsWith("OggS"u8));
            Assert.Equal("Ogg Vorbis", report.Container);
        }
        else
        {
            Assert.StartsWith("none (Unreal", report.Container);
        }

        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            _audio.ExportAudioAsync(lease, path, "wav", _outDir, CancellationToken.None));
        Assert.Contains(codec, error.Message);
        Assert.Contains("mode=original", error.Message);
        Assert.False(File.Exists(ExpectedPath(path + ".wav")));
        Assert.Equal(new[] { ExpectedPath(path + "." + extension) }, Directory.GetFiles(_outDir, "*", SearchOption.AllDirectories),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonAudioAssetsAndBanksAreRejectedWithoutWriting()
    {
        using var lease = await fixture.Games.AcquireAsync("fixture", CancellationToken.None);

        var cue = await Assert.ThrowsAsync<NotSupportedException>(() =>
            _audio.ExportAudioAsync(lease, AudioFolder + "SC_Fixture", "original", _outDir, CancellationToken.None));
        Assert.Contains("SoundCue", cue.Message);
        Assert.Contains("get_dependencies", cue.Message);

        var bank = await Assert.ThrowsAsync<NotSupportedException>(() =>
            _audio.ExportAudioAsync(lease, AudioFolder + "Init.bnk", "original", _outDir, CancellationToken.None));
        Assert.Contains("bank", bank.Message);

        var loose = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _audio.ExportAudioAsync(lease, AudioFolder + "SW_Format_PCM.wem", "original", _outDir, CancellationToken.None));
        Assert.Contains("exact archive path", loose.Message);

        var mode = await Assert.ThrowsAsync<ArgumentException>(() =>
            _audio.ExportAudioAsync(lease, AudioFolder + "SW_Format_PCM", "mp3", _outDir, CancellationToken.None));
        Assert.Contains("Unknown audio mode", mode.Message);

        Assert.False(Directory.Exists(_outDir));
    }

    [Fact]
    public async Task GameOverloadAcquiresItsOwnLeaseAndHonoursCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _audio.ExportAudioAsync("fixture", AudioFolder + "SW_Format_Opus", "original", _outDir, cancelled.Token));
        Assert.False(Directory.Exists(_outDir));

        var report = await _audio.ExportAudioAsync("fixture", AudioFolder + "SW_Format_Opus", "original", _outDir, CancellationToken.None);
        Assert.Equal("OPUS", report.SourceCodec);
        Assert.Equal(report.Bytes, new FileInfo(report.Files.RootFiles.Single()).Length);
    }

    private static (string Codec, byte[] Payload) PinnedPayload(GameLease lease, string path)
    {
        lease.Session.LoadMain(path).Decode(shouldDecompress: false, out var codec, out var payload);
        return (codec, Assert.IsType<byte[]>(payload));
    }

    private string ExpectedPath(string relative) => Path.GetFullPath(Path.Combine(_outDir, relative));

    private byte[] AssertSingleFile(AudioExportReport report, string relative)
    {
        Assert.Empty(report.Files.DependencyFiles);
        Assert.Empty(report.Files.Failures);
        var file = Assert.Single(report.Files.RootFiles);
        Assert.Equal(ExpectedPath(relative), file, ignoreCase: true);
        Assert.Empty(Directory.GetFiles(_outDir, "*.tmp", SearchOption.AllDirectories));
        return File.ReadAllBytes(file);
    }

    /// <summary>Independent RIFF/WAVE reader: integer PCM only, every chunk bounded by the file.</summary>
    private static (int Channels, int SampleRate, int BitsPerSample, short[] Samples) ReadPcmWave(byte[] data)
    {
        ReadOnlySpan<byte> bytes = data;
        Assert.True(bytes.Length >= 44);
        Assert.True(bytes[..4].SequenceEqual("RIFF"u8));
        Assert.True(bytes.Slice(8, 4).SequenceEqual("WAVE"u8));
        Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) + 8L <= bytes.Length);

        ReadOnlySpan<byte> format = default, samples = default;
        for (var offset = 12; offset + 8 <= bytes.Length;)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset + 4, 4));
            Assert.InRange(length, 0, bytes.Length - offset - 8);
            var chunk = bytes.Slice(offset + 8, length);
            if (bytes.Slice(offset, 4).SequenceEqual("fmt "u8)) format = chunk;
            else if (bytes.Slice(offset, 4).SequenceEqual("data"u8)) samples = chunk;
            offset += 8 + length + (length & 1);
        }

        Assert.True(format.Length >= 16);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(format));
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(format[4..]);
        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
        Assert.Equal(16, bits);
        Assert.Equal(channels * 2, blockAlign);
        Assert.Equal(0, samples.Length % blockAlign);

        var values = new short[samples.Length / 2];
        for (var i = 0; i < values.Length; i++) values[i] = BinaryPrimitives.ReadInt16LittleEndian(samples.Slice(i * 2, 2));
        return (channels, sampleRate, bits, values);
    }
}
