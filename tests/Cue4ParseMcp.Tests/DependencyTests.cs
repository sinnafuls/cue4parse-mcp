using Cue4ParseMcp.Config;
using Cue4ParseMcp.Core;

namespace Cue4ParseMcp.Tests;

public sealed class DependencyTests
{
    private const string DataAsset = "/Game/Fixtures/Properties/DA_AllProperties";

    [Fact]
    public async Task IoStorePackagesReportStoreEntryImports()
    {
        // The legacy pak has no materials; the genuine IoStore cook does. Tagged, uncompressed: no mappings or codecs.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        const string tagged = "lib/CUE4Parse/CUE4Parse.Tests/Fixtures/UE5_8/IoStore/Tagged";
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, tagged))) root = root.Parent;
        Assert.NotNull(root);
        var source = Path.Combine(root.FullName, tagged);
        var directory = Path.Combine(Path.GetTempPath(), "cue4parse-deps-" + Guid.NewGuid().ToString("N"));
        var paks = Directory.CreateDirectory(Path.Combine(directory, "Paks")).FullName;
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(source, "Uncompressed")).Concat(Directory.EnumerateFiles(source, "global.*")))
                File.Copy(file, Path.Combine(paks, Path.GetFileName(file)));
            var unavailable = Path.Combine(directory, "unavailable-codec.dll");
            File.WriteAllText(unavailable, "No codec is needed for uncompressed fixtures.");
            var config = new AppConfig
            {
                CacheDir = Path.Combine(AppContext.BaseDirectory, "native-cache"),
                OodlePath = unavailable,
                ZlibPath = unavailable,
                Games = new(StringComparer.OrdinalIgnoreCase) { ["iostore"] = new GameConfig { PaksDir = paks, UeVersion = "GAME_UE5_8" } }
            };
            using (var games = new GameManager(config))
            {
                var report = await new DependencyOps(games).GetAsync("iostore", "/Game/Fixtures/Materials/MI_Fixture", 2, 500, CancellationToken.None);

                var instance = report.Packages[0];
                Assert.Equal("zen", instance.Format);
                Assert.True(instance.HardReferencesComplete);
                Assert.NotEmpty(instance.ScriptPackages);
                var parent = Assert.Single(instance.References, r => r.Target.EndsWith("Fixtures/Materials/M_Fixture", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(("hard", "store-entry", true, true), (parent.Kind, parent.Source, parent.Resolved, parent.Scanned));
                Assert.Contains(report.Packages, p => p.Path.Equals(parent.Target, StringComparison.OrdinalIgnoreCase) && p is { Depth: 1, Format: "zen" });
                Assert.False(report.Truncated);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DirectReferencesComeFromTypedImportAndSoftPathData()
    {
        using var fixture = new ParsedAssetFixture();
        var report = await new DependencyOps(fixture.Games).GetAsync("fixture", DataAsset, depth: 1, limit: 200, CancellationToken.None);

        Assert.EndsWith("Fixtures/Properties/DA_AllProperties", report.Root, StringComparison.OrdinalIgnoreCase);
        var root = Assert.Single(report.Packages);
        Assert.Equal((0, "legacy"), (root.Depth, root.Format));
        Assert.True(root.HardReferencesComplete);
        Assert.True(root.SoftReferencesComplete);
        Assert.Equal(root.ExportCount, root.ExportsScanned);

        // HardObjectReference imports the DataTable; SoftTextureReference is a serialized FSoftObjectPath.
        var table = Assert.Single(root.References, r => r.Kind == "hard"
            && r.Target.EndsWith("Fixtures/DataTables/DT_AllProperties", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(("hard", "import-map", true), (table.Kind, table.Source, table.Resolved));
        Assert.Contains("DT_AllProperties", table.Objects!);
        var texture = Assert.Single(root.References, r => r.Target.EndsWith("Fixtures/Textures/T_BC6H", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(("soft", "property", true), (texture.Kind, texture.Source, texture.Resolved));
        Assert.Contains("/Game/Fixtures/Textures/T_BC6H.T_BC6H", texture.Objects!);

        // Native classes are imported from /Script packages: listed, never presented as file dependencies.
        Assert.NotEmpty(root.ScriptPackages);
        Assert.All(root.ScriptPackages, p => Assert.StartsWith("/Script/", p, StringComparison.Ordinal));
        Assert.DoesNotContain(root.References, r => r.Target.StartsWith("/Script/", StringComparison.Ordinal));
        Assert.DoesNotContain(root.References, reference => reference.Target.StartsWith("Default__", StringComparison.Ordinal));

        Assert.All(root.References, r => Assert.False(r.Scanned));
        Assert.True(report.DepthLimited);
        Assert.False(report.Truncated);
        Assert.Equal(0, report.ErrorCount);
        Assert.True(report.Complete);
        Assert.Equal(root.References.Count, report.ReferencesReported);
        Assert.NotEmpty(report.Omitted);
    }

    [Fact]
    public async Task DeeperTraversalScansEachPackageOnce()
    {
        using var fixture = new ParsedAssetFixture();
        var report = await new DependencyOps(fixture.Games).GetAsync("fixture", DataAsset + ".DA_AllProperties", depth: 3, limit: 2000, CancellationToken.None);

        Assert.Equal("DA_AllProperties", report.RootObject);
        Assert.Equal(report.Packages.Count, report.Packages.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var scanned = report.Packages.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = report.Packages[0];
        foreach (var target in new[] { "Fixtures/DataTables/DT_AllProperties", "Fixtures/Textures/T_BC6H" })
        {
            var references = root.References.Where(r => r.Target.EndsWith(target, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.Contains(references, reference => reference.Scanned);
            Assert.All(references, reference =>
            {
                Assert.True(reference.Scanned);
                Assert.Contains(reference.Target, scanned);
                Assert.Equal(1, report.Packages.Single(p => p.Path.Equals(reference.Target, StringComparison.OrdinalIgnoreCase)).Depth);
            });
        }
        // Without truncation a reference is marked scanned exactly when its resolved target is in the report.
        Assert.All(report.Packages.SelectMany(p => p.References),
            r => Assert.Equal(r.Resolved && scanned.Contains(r.Target), r.Scanned));
        Assert.DoesNotContain(root.References, r => r.Target.Equals(root.Path, StringComparison.OrdinalIgnoreCase));
        Assert.False(report.Truncated);
    }

    [Fact]
    public async Task LimitTruncatesHonestly()
    {
        using var fixture = new ParsedAssetFixture();
        var report = await new DependencyOps(fixture.Games).GetAsync("fixture", DataAsset, depth: 2, limit: 1, CancellationToken.None);

        Assert.True(report.Truncated);
        Assert.False(report.Complete);
        Assert.Equal(1, report.ReferencesReported);
        var root = Assert.Single(report.Packages); // traversal stops before scanning the queued target
        Assert.Single(root.References);
        Assert.False(root.HardReferencesComplete && root.SoftReferencesComplete);
        Assert.False(root.References[0].Scanned);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(9, 10)]
    [InlineData(1, 0)]
    [InlineData(1, 2001)]
    public async Task OutOfRangeBoundsAreRejected(int depth, int limit)
    {
        using var fixture = new ParsedAssetFixture();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new DependencyOps(fixture.Games).GetAsync("fixture", DataAsset, depth, limit, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationAndMissingRootsThrow()
    {
        using var fixture = new ParsedAssetFixture();
        var ops = new DependencyOps(fixture.Games);
        // Mount first so cancellation is observed by the traversal itself, not only by the mount.
        await ops.GetAsync("fixture", DataAsset, 1, 1, CancellationToken.None);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ops.GetAsync("fixture", DataAsset, 8, 2000, cancelled.Token));
        await Assert.ThrowsAsync<FileNotFoundException>(() => ops.GetAsync("fixture", "/Game/Fixtures/DoesNotExist", 1, 10, CancellationToken.None));
    }
}
