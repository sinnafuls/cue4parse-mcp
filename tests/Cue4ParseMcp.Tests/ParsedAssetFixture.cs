using Cue4ParseMcp.Config;
using Cue4ParseMcp.Core;

namespace Cue4ParseMcp.Tests;

/// <summary>Genuine tagged cooked packages; disposable exports, process-lifetime native codec cache.</summary>
internal sealed class ParsedAssetFixture : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cue4parse-assets-" + Guid.NewGuid().ToString("N"));
    public AppConfig Config { get; }
    public GameManager Games { get; }
    public string OutputDir => Path.Combine(_directory, "exports");

    public ParsedAssetFixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        const string fixture = "lib/CUE4Parse/CUE4Parse.Tests/Fixtures/UE5_8/LegacyPak/Tagged/Uncompressed";
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, fixture))) root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("The recursive CUE4Parse checkout with cooked fixtures is required.");
        Directory.CreateDirectory(_directory);
        var unavailable = Path.Combine(_directory, "unavailable-codec.dll");
        File.WriteAllText(unavailable, "No downloaded Oodle/zlib codec is needed for uncompressed fixtures.");
        Config = new AppConfig
        {
            DefaultOutputDir = OutputDir,
            CacheDir = Path.Combine(AppContext.BaseDirectory, "native-cache"),
            OodlePath = unavailable,
            ZlibPath = unavailable,
            Games = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fixture"] = new GameConfig
                {
                    PaksDir = Path.Combine(root.FullName, fixture),
                    UeVersion = "GAME_UE5_8"
                }
            }
        };
        Games = new GameManager(Config);
    }

    public void Dispose()
    {
        Games.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
