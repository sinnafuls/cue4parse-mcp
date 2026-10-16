using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.UE4.Objects.Core.Misc;
using Cue4ParseMcp.Config;
using K4os.Compression.LZ4;

namespace Cue4ParseMcp.Tests;

/// <summary>Generates small legacy paks with real AES-256-ECB data, including the Sea of Thieves v3 layout.</summary>
internal sealed class EncryptedPakFixture : IDisposable
{
    public const string ProbePath = "Athena/Content/EncryptionProbe.bin";
    public const string IniPath = "Athena/Config/DefaultGame.ini";
    public static readonly byte[] Probe = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Encrypted pak fixture payload.\r\n", 32)));
    public static readonly byte[] Ini = Encoding.UTF8.GetBytes("[/Script/EngineSettings.GeneralProjectSettings]\nProjectName=EncryptionFixture\n");
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private const string DynamicGuid = "112233445566778899aabbccddeeff00";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cue4parse-pak-tests-" + Guid.NewGuid());

    public AppConfig Config { get; }

    public EncryptedPakFixture(int version, bool encryptedIndex, ECompressionFlags compression, bool dynamicKey = false, bool encryptedFiles = true, bool supplyKey = true)
    {
        Directory.CreateDirectory(_directory);
        var paks = Path.Combine(_directory, "paks");
        Directory.CreateDirectory(paks);
        var native = Path.Combine(_directory, "unavailable-native.dll");
        // These formats don't require native codecs. Existing invalid DLLs produce warnings without downloads,
        // keeping the fixture independent of network access and machine-installed decompression libraries.
        File.WriteAllText(native, "No native codec is required by this fixture.");
        // Native codecs are process-global and Windows keeps their DLLs locked while loaded.
        // Give their cache the test output's lifetime, not this disposable pak directory's.
        Config = new AppConfig { CacheDir = Path.Combine(AppContext.BaseDirectory, "native-cache"), OodlePath = native, ZlibPath = native };
        Config.Games.Add("fixture", new GameConfig
        {
            PaksDir = paks,
            UeVersion = "GAME_SeaOfThieves",
            AesKey = supplyKey && !dynamicKey ? Convert.ToHexString(Key) : null,
            DynamicKeys = supplyKey && dynamicKey ? new() { [DynamicGuid] = Convert.ToHexString(Key) } : null
        });

        using var pak = new BinaryWriter(File.Create(Path.Combine(paks, "fixture.pak")));
        var entries = new List<(string Path, byte[] Header)>();
        foreach (var (path, content) in new[] { ("Config/DefaultGame.ini", Ini), ("Content/EncryptionProbe.bin", Probe) })
        {
            var compressed = Compress(content, compression);
            var header = WriteEntry(pak.BaseStream.Position, content, compressed, version, compression, encryptedFiles);
            entries.Add((path, header));
            pak.Write(header);
            pak.Write(encryptedFiles ? Encrypt(compressed) : compressed);
        }

        using var indexStream = new MemoryStream();
        using (var index = new BinaryWriter(indexStream, Encoding.UTF8, leaveOpen: true))
        {
            WriteString(index, "../../../Athena/");
            index.Write(entries.Count);
            foreach (var (path, header) in entries)
            {
                WriteString(index, path);
                index.Write(header);
            }
        }
        var plainIndex = indexStream.ToArray();
        var storedIndex = encryptedIndex ? Encrypt(plainIndex) : plainIndex;
        var indexOffset = pak.BaseStream.Position;
        pak.Write(storedIndex);
        // Older footer versions omit the fields that were added before the magic number.
        if (version >= 7)
        {
            var guid = dynamicKey ? new FGuid(DynamicGuid) : default;
            pak.Write(guid.A); pak.Write(guid.B); pak.Write(guid.C); pak.Write(guid.D);
        }
        if (version >= 4) pak.Write((byte)(encryptedIndex ? 1 : 0));
        pak.Write(0x5A6F12E1u);
        pak.Write(version);
        pak.Write(indexOffset);
        pak.Write((long)storedIndex.Length);
        pak.Write(SHA1.HashData(plainIndex));
    }

    private static byte[] WriteEntry(long offset, byte[] content, byte[] compressed, int version, ECompressionFlags compression, bool encrypted)
    {
        using var stream = new MemoryStream();
        using var entry = new BinaryWriter(stream);
        entry.Write(offset);
        entry.Write((long)compressed.Length);
        entry.Write((long)content.Length);
        entry.Write((uint)compression);
        entry.Write(SHA1.HashData(content));
        if (compression != ECompressionFlags.COMPRESS_None)
        {
            const int compressedHeaderSize = 73; // 3 int64s, compression flag, SHA1, one block, encryption flag, block size
            var start = compressedHeaderSize + (version >= 5 ? 0 : offset);
            entry.Write(1);
            entry.Write(start);
            entry.Write(start + compressed.Length);
        }
        entry.Write((byte)(encrypted ? 1 : 0));
        entry.Write(compression == ECompressionFlags.COMPRESS_None ? 0u : (uint)content.Length);
        return stream.ToArray();
    }

    private static byte[] Compress(byte[] content, ECompressionFlags compression)
    {
        if (compression == ECompressionFlags.COMPRESS_None) return content;
        if (compression == ECompressionFlags.COMPRESS_Custom)
        {
            var buffer = new byte[LZ4Codec.MaximumOutputSize(content.Length)];
            var size = LZ4Codec.Encode(content, buffer);
            return buffer[..size];
        }
        using var stream = new MemoryStream();
        using (var zlib = new ZLibStream(stream, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(content);
        return stream.ToArray();
    }

    private static byte[] Encrypt(byte[] content)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        return aes.EncryptEcb(content, PaddingMode.Zeros);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value + '\0');
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
