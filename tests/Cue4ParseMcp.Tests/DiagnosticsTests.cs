using System.Text.Json;
using CUE4Parse.UE4.Objects.Core.Misc;
using Cue4ParseMcp.Config;
using Cue4ParseMcp.Core;

namespace Cue4ParseMcp.Tests;

public sealed class DiagnosticsTests
{
    private const string EndpointUser = "fixture-user";
    private const string EndpointPassword = "fixture-pass";
    private const string EndpointPathSecret = "SECRETPATHSEGMENT";
    private const string EndpointQuerySecret = "querysecretvalue";
    private const string EndpointFragmentSecret = "fragmentsecretvalue";

    // Port 9 on loopback refuses immediately, so both endpoints fail and their URLs end up in load warnings.
    private static string SecretUrl(string path) =>
        $"https://{EndpointUser}:{EndpointPassword}@127.0.0.1:9/{path}/{EndpointPathSecret}?token={EndpointQuerySecret}#{EndpointFragmentSecret}";

    [Fact]
    public async Task EncryptedIndexReportIsVerifiedAndLeaksNoSecrets()
    {
        using var fixture = new EncryptedPakFixture(4, encryptedIndex: true, ECompressionFlags.COMPRESS_None);
        var game = fixture.Config.Games["fixture"];
        game.AesUrl = SecretUrl("aes");
        game.MappingsUrl = SecretUrl("mappings");
        var key = game.AesKey!;
        using var manager = new GameManager(fixture.Config);

        var report = await new DiagnosticsOps(manager).DiagnoseAsync("fixture", EncryptedPakFixture.ProbePath, CancellationToken.None);

        Assert.Equal("mounted", report.Mount.Status);
        var group = Assert.Single(report.Encryption!.Keys);
        Assert.Equal("verified", group.State);
        Assert.Equal("games.json", group.Configured); // The endpoint failed, so the configured key was used.
        Assert.Equal(1, group.IndexEncrypted);
        Assert.Empty(report.Encryption.RequiredKeyGuids);
        Assert.Equal("https://127.0.0.1", report.Configuration.AesEndpoint);
        Assert.Equal("https://127.0.0.1", report.Configuration.MappingsEndpoint);
        Assert.Contains(report.Warnings, w => w.Contains("https://127.0.0.1", StringComparison.Ordinal));

        AssertNoLeak(report, key, game.PaksDir,
            EndpointUser, EndpointPassword, EndpointPathSecret, EndpointQuerySecret, EndpointFragmentSecret);
    }

    [Fact]
    public async Task PlainIndexKeyIsVerifiedOnlyByDecryptedContentNotByTheProbeOfAnOpaqueFile()
    {
        using var fixture = new EncryptedPakFixture(3, encryptedIndex: false, ECompressionFlags.COMPRESS_None);
        var game = fixture.Config.Games["fixture"];
        using var manager = new GameManager(fixture.Config);

        var report = await new DiagnosticsOps(manager).DiagnoseAsync("fixture", EncryptedPakFixture.ProbePath, CancellationToken.None);

        // The plain index mounts with any key; the evidence is the encrypted DefaultGame.ini decrypting into
        // real project settings, which a wrong key cannot produce.
        var group = Assert.Single(report.Encryption!.Keys);
        Assert.Equal("verified", group.State);
        Assert.Equal((0, 1, 2L), (group.IndexEncrypted, group.EntryEncryptedContainers, group.EncryptedEntries));
        Assert.Contains("DefaultGame.ini", group.Evidence, StringComparison.Ordinal);

        // Decrypting an opaque .bin succeeds even with a wrong key, so the probe must not claim verification.
        var probe = report.Probe!;
        Assert.True(probe.ExercisedEncryption);
        Assert.Equal("file", probe.Kind);
        Assert.Equal("ok", probe.Stages.Single(s => s.Stage == "read").Status);
        Assert.Equal("skipped", probe.Stages.Single(s => s.Stage == "header").Status);
        Assert.DoesNotContain("Verified", probe.KeyEvidence, StringComparison.Ordinal);

        AssertNoLeak(report, game.AesKey!, game.PaksDir);
    }

    [Fact]
    public async Task MissingKeyForEncryptedEntriesIsReportedAsDecryptionFailure()
    {
        using var fixture = new EncryptedPakFixture(3, encryptedIndex: false, ECompressionFlags.COMPRESS_None, supplyKey: false);
        using var manager = new GameManager(fixture.Config);

        var report = await new DiagnosticsOps(manager).DiagnoseAsync("fixture", EncryptedPakFixture.ProbePath, CancellationToken.None);

        Assert.Equal("none-mounted", report.Mount.Status);
        Assert.Contains("DefaultGame.ini", Assert.Single(report.Mount.Unmounted).Reason, StringComparison.Ordinal);
        var group = Assert.Single(report.Encryption!.Keys);
        Assert.Equal(("missing", "none", 0), (group.State, group.Configured, group.Assigned));
        var read = report.Probe!.Stages.Single(s => s.Stage == "read");
        Assert.Equal(("failed", "decryption"), (read.Status, read.Category));
        Assert.False(report.Configuration.MainKeyConfigured);

        AssertNoLeak(report, fixture.Config.Games["fixture"].PaksDir);
    }

    [Fact]
    public async Task WrongKeyForEncryptedIndexIsRejectedWithoutLeakingIt()
    {
        using var fixture = new EncryptedPakFixture(4, encryptedIndex: true, ECompressionFlags.COMPRESS_None);
        var game = fixture.Config.Games["fixture"];
        game.AesKey = "0x" + new string('B', 64);
        using var manager = new GameManager(fixture.Config);

        var report = await new DiagnosticsOps(manager).DiagnoseAsync("fixture", EncryptedPakFixture.ProbePath, CancellationToken.None);

        Assert.Equal("none-mounted", report.Mount.Status);
        var unmounted = Assert.Single(report.Mount.Unmounted);
        Assert.Equal("pak", unmounted.Type);
        Assert.Contains("failed", unmounted.Reason, StringComparison.Ordinal);
        var group = Assert.Single(report.Encryption!.Keys);
        Assert.Equal("rejected", group.State);
        Assert.Equal(1, group.Rejected);
        Assert.NotEmpty(report.Encryption.RequiredKeyGuids);
        var locate = report.Probe!.Stages.Single(s => s.Stage == "locate");
        Assert.Equal(("failed", "not-found"), (locate.Status, locate.Category));

        AssertNoLeak(report, game.AesKey, game.PaksDir);
    }

    [Fact]
    public async Task UnencryptedReadsSayNothingAboutAConfiguredKey()
    {
        using var fixture = new EncryptedPakFixture(3, encryptedIndex: false, ECompressionFlags.COMPRESS_None, encryptedFiles: false);
        var game = fixture.Config.Games["fixture"];
        using var manager = new GameManager(fixture.Config);

        var report = await new DiagnosticsOps(manager).DiagnoseAsync("fixture", EncryptedPakFixture.ProbePath, CancellationToken.None);

        Assert.True(report.Configuration.MainKeyConfigured);
        Assert.Empty(report.Encryption!.Keys); // Nothing needs a key, so nothing is claimed about it.
        var probe = report.Probe!;
        Assert.False(probe.ExercisedEncryption);
        Assert.Equal(false, probe.EntryEncrypted);
        Assert.Equal("ok", probe.Stages.Single(s => s.Stage == "read").Status);
        Assert.Contains("says nothing", probe.KeyEvidence, StringComparison.Ordinal);

        AssertNoLeak(report, game.AesKey!, game.PaksDir);
    }

    [Fact]
    public async Task MountFailureIsReportedWithoutPathsOrKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), "cue4parse-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paks = Path.Combine(root, "Secret Project", "Paks");
            var key = Convert.ToHexString(Enumerable.Range(100, 32).Select(i => (byte)i).ToArray());
            var configPath = Path.Combine(root, "games.json");
            File.WriteAllText(configPath, JsonSerializer.Serialize(new
            {
                CacheDir = Path.Combine(AppContext.BaseDirectory, "native-cache"),
                Games = new Dictionary<string, object>
                {
                    ["broken"] = new
                    {
                        PaksDir = "Secret Project/Paks", // relative to the config file, like a user's games.json
                        UeVersion = "GAME_UE5_8",
                        AesKey = key,
                        DynamicKeys = new Dictionary<string, string> { ["00112233445566778899aabbccddeeff"] = key.ToLowerInvariant() },
                        AesUrl = SecretUrl("aes")
                    }
                }
            }));
            using var manager = new GameManager(AppConfig.Load(configPath));

            var report = await new DiagnosticsOps(manager).DiagnoseAsync("broken", "Game/Content/Anything", CancellationToken.None);

            Assert.Equal("failed", report.Mount.Status);
            Assert.Equal("paks-dir-not-found", report.Mount.FailureCategory);
            Assert.Contains("<paksDir>", report.Mount.FailureMessage, StringComparison.Ordinal);
            Assert.Equal("games.json", report.Configuration.ConfigFile);
            Assert.False(report.Configuration.PaksDirExists);
            Assert.Equal(1, report.Configuration.DynamicKeysConfigured);
            Assert.Equal("https://127.0.0.1", report.Configuration.AesEndpoint);
            Assert.True(report.Engine.Recognized);
            Assert.Null(report.Probe);
            Assert.Null(report.Encryption);

            AssertNoLeak(report, key, paks, root, "Secret Project",
                EndpointUser, EndpointPassword, EndpointPathSecret, EndpointQuerySecret, EndpointFragmentSecret);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProbeSeparatesReadHeaderAndDeserializeForARealPackage()
    {
        using var fixture = new ParsedAssetFixture();
        var diagnostics = new DiagnosticsOps(fixture.Games);

        var report = await diagnostics.DiagnoseAsync("fixture", "/Game/Fixtures/Properties/DA_AllProperties", CancellationToken.None);

        Assert.Equal("mounted", report.Mount.Status);
        Assert.Equal("<in-memory>", report.Configuration.ConfigFile);
        Assert.Equal("GAME_UE5_8", report.Engine.Effective);
        Assert.Contains(report.Mount.ContainerTypes, t => t is { Type: "pak", Mounted: 1, Unmounted: 0 });
        Assert.Empty(report.Encryption!.Keys);
        var probe = report.Probe!;
        Assert.Equal("package", probe.Kind);
        Assert.False(probe.ExercisedEncryption);
        Assert.Equal(["locate", "read", "header", "deserialize"], probe.Stages.Select(s => s.Stage));
        Assert.All(probe.Stages, s => Assert.Equal("ok", s.Status));
        Assert.EndsWith("Fixtures/Properties/DA_AllProperties.uasset", probe.Path, StringComparison.OrdinalIgnoreCase);

        var missing = await diagnostics.DiagnoseAsync("fixture", "/Game/Fixtures/DoesNotExist", CancellationToken.None);
        Assert.Equal(("failed", "not-found"), (missing.Probe!.Stages[0].Status, missing.Probe.Stages[0].Category));
        Assert.All(missing.Probe.Stages.Skip(1), s => Assert.Equal("skipped", s.Status));

        AssertNoLeak(report, fixture.Config.Games["fixture"].PaksDir, fixture.OutputDir);
    }

    /// <summary>Checks every string the MCP/CLI serializer would emit, after JSON escaping is undone.</summary>
    private static void AssertNoLeak(GameDiagnostics report, params string[] secrets)
    {
        var strings = new List<string>();
        Collect(JsonDocument.Parse(JsonSerializer.Serialize(report)).RootElement, strings);
        var forbidden = secrets
            .Where(s => s.Length > 0)
            .SelectMany(s => new[] { s, s.Replace('\\', '/'), s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s })
            .Append(Path.GetTempPath().TrimEnd('\\', '/'))
            .Distinct()
            .ToList();
        foreach (var value in strings)
        {
            foreach (var secret in forbidden)
                Assert.DoesNotContain(secret, value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("   at ", value, StringComparison.Ordinal); // no stack traces
            Assert.DoesNotMatch(@"[0-9A-Fa-f]{40,}", value); // no raw key material of any spelling
        }
    }

    private static void Collect(JsonElement element, List<string> strings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                strings.Add(element.GetString()!);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Collect(item, strings);
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) Collect(property.Value, strings);
                break;
        }
    }
}
