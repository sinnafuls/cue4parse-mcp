using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Objects.Core.Misc;
using Cue4ParseMcp.Config;
using Cue4ParseMcp.Core;

namespace Cue4ParseMcp.Tests;

public sealed class ConfigReloadTests
{
    [Fact]
    public async Task NewlyAddedGameLoadsWithoutRestartOrRemountingAnUnchangedGame()
    {
        using var first = new EncryptedPakFixture(3, false, ECompressionFlags.COMPRESS_None);
        using var second = new EncryptedPakFixture(3, false, ECompressionFlags.COMPRESS_Custom);
        using var file = new ConfigFile(first.Config);
        using var manager = new GameManager(AppConfig.Load(file.Path));
        using var original = await manager.AcquireAsync("fixture", CancellationToken.None);

        file.Values.Games.Add("added", second.Config.Games["fixture"]);
        file.Save();

        Assert.Contains("added", manager.GetState().Config.Games.Keys);
        using var added = await manager.AcquireAsync("added", CancellationToken.None);
        Assert.Equal(EncryptedPakFixture.Probe, added.Session.Provider.SaveAsset(EncryptedPakFixture.ProbePath));
        using var unchanged = await manager.AcquireAsync("fixture", CancellationToken.None);
        Assert.Same(original.Session, unchanged.Session);
        Assert.Equal(EncryptedPakFixture.Ini, original.Session.Provider.SaveAsset(EncryptedPakFixture.IniPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyChangesReplaceTheProviderButKeepActiveOperationsAlive(bool dynamicKey)
    {
        using var pak = new EncryptedPakFixture(dynamicKey ? 7 : 3, false, ECompressionFlags.COMPRESS_None, dynamicKey);
        using var file = new ConfigFile(pak.Config);
        using var manager = new GameManager(AppConfig.Load(file.Path));
        using var original = await manager.AcquireAsync("fixture", CancellationToken.None);
        var originalKey = file.Values.Games["fixture"].AesKey;
        var originalDynamicKeys = file.Values.Games["fixture"].DynamicKeys;

        file.Values.Games["fixture"].AesKey = null;
        file.Values.Games["fixture"].DynamicKeys = null;
        file.Save();
        using var missingKey = await manager.AcquireAsync("fixture", CancellationToken.None);
        Assert.NotSame(original.Session, missingKey.Session);
        Assert.Throws<InvalidAesKeyException>(() => missingKey.Session.Provider.SaveAsset(EncryptedPakFixture.ProbePath));
        Assert.Equal(EncryptedPakFixture.Probe, original.Session.Provider.SaveAsset(EncryptedPakFixture.ProbePath));

        file.Values.Games["fixture"].AesKey = originalKey;
        file.Values.Games["fixture"].DynamicKeys = originalDynamicKeys;
        file.Save();
        using var restored = await manager.AcquireAsync("fixture", CancellationToken.None);
        Assert.Equal(EncryptedPakFixture.Probe, restored.Session.Provider.SaveAsset(EncryptedPakFixture.ProbePath));
    }

    [Fact]
    public async Task DynamicKeyCasingChangesDoNotInvalidateTheMountedGame()
    {
        using var pak = new EncryptedPakFixture(7, false, ECompressionFlags.COMPRESS_Custom, dynamicKey: true);
        using var file = new ConfigFile(pak.Config);
        using var manager = new GameManager(AppConfig.Load(file.Path));
        using var original = await manager.AcquireAsync("fixture", CancellationToken.None);
        var key = file.Values.Games["fixture"].DynamicKeys!.Single();

        file.Values.Games["fixture"].DynamicKeys = new() { [key.Key.ToUpperInvariant()] = key.Value.ToLowerInvariant() };
        file.Save();
        using var updated = await manager.AcquireAsync("fixture", CancellationToken.None);

        Assert.Same(original.Session, updated.Session);
        Assert.Equal(EncryptedPakFixture.Probe, updated.Session.Provider.SaveAsset(EncryptedPakFixture.ProbePath));
    }

    [Fact]
    public async Task RemovedGameIsUnavailableToNewCallsWhileAnOldLeaseCanFinish()
    {
        using var pak = new EncryptedPakFixture(3, false, ECompressionFlags.COMPRESS_None);
        using var file = new ConfigFile(pak.Config);
        using var manager = new GameManager(AppConfig.Load(file.Path));
        using var original = await manager.AcquireAsync("fixture", CancellationToken.None);

        file.Values.Games.Clear();
        file.Save();

        var state = manager.GetState();
        Assert.Empty(state.Config.Games);
        Assert.Empty(state.LoadedGames);
        await Assert.ThrowsAsync<ArgumentException>(() => manager.AcquireAsync("fixture", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.AcquireAsync(null, CancellationToken.None));
        Assert.Equal(EncryptedPakFixture.Probe, original.Session.Provider.SaveAsset(EncryptedPakFixture.ProbePath));
    }

    [Theory]
    [InlineData("{\"games\":")]
    [InlineData("{\"games\":{\"fixture\":null}}")]
    [InlineData("{\"games\":{\"A\":{},\"a\":{}}}")]
    public async Task InvalidOrMissingSavesRetainTheLastGoodStateAndAtomicReplacementRecovers(string invalid)
    {
        using var pak = new EncryptedPakFixture(3, false, ECompressionFlags.COMPRESS_None);
        using var file = new ConfigFile(pak.Config);
        using var manager = new GameManager(AppConfig.Load(file.Path));
        using var original = await manager.AcquireAsync("fixture", CancellationToken.None);

        file.Write(invalid);
        using (var afterInvalid = await manager.AcquireAsync("fixture", CancellationToken.None))
        {
            Assert.Same(original.Session, afterInvalid.Session);
            Assert.Equal(EncryptedPakFixture.Probe, afterInvalid.Session.Provider.SaveAsset(EncryptedPakFixture.ProbePath));
        }
        File.Delete(file.Path);
        using (var afterDelete = await manager.AcquireAsync("fixture", CancellationToken.None))
        {
            Assert.Same(original.Session, afterDelete.Session);
            Assert.Equal(EncryptedPakFixture.Ini, afterDelete.Session.Provider.SaveAsset(EncryptedPakFixture.IniPath));
        }

        file.Values.Games["fixture"].DisplayName = "Recovered configuration";
        file.Save(replace: true);
        Assert.Equal("Recovered configuration", manager.GetState().Config.Games["fixture"].DisplayName);
        using var recovered = await manager.AcquireAsync("fixture", CancellationToken.None);
        Assert.Same(original.Session, recovered.Session);
    }

    [Fact]
    public async Task LockedSaveKeepsTheOldSnapshotAndLoadsItAfterUnlockWithoutAnotherEdit()
    {
        using var pak = new EncryptedPakFixture(3, false, ECompressionFlags.COMPRESS_None);
        using var file = new ConfigFile(pak.Config);
        file.Values.Games["fixture"].DisplayName = "Before locked save";
        file.Save();
        using var manager = new GameManager(AppConfig.Load(file.Path));
        using var original = await manager.AcquireAsync("fixture", CancellationToken.None);

        file.Values.Games["fixture"].DisplayName = "After locked save";
        file.Save();
        using (var locked = new FileStream(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal("Before locked save", manager.GetState().Config.Games["fixture"].DisplayName);
            using var duringSave = await manager.AcquireAsync("fixture", CancellationToken.None);
            Assert.Same(original.Session, duringSave.Session);
            Assert.Equal(EncryptedPakFixture.Probe, duringSave.Session.Provider.SaveAsset(EncryptedPakFixture.ProbePath));
        }

        Assert.Equal("After locked save", manager.GetState().Config.Games["fixture"].DisplayName);
        using var unlocked = await manager.AcquireAsync("fixture", CancellationToken.None);
        Assert.Same(original.Session, unlocked.Session);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputChangesUsePerOperationSnapshotsWithoutRemounting(bool perGame)
    {
        using var pak = new EncryptedPakFixture(3, false, ECompressionFlags.COMPRESS_None);
        using var file = new ConfigFile(pak.Config);
        file.Values.DefaultOutputDir = "old-exports";
        file.Save();
        using var manager = new GameManager(AppConfig.Load(file.Path));
        using var original = await manager.AcquireAsync("fixture", CancellationToken.None);
        var oldOutput = System.IO.Path.Combine(file.Directory, "old-exports", "fixture");
        Assert.Equal(oldOutput, manager.ResolveOutputDir(original, null));

        file.Values.DefaultOutputDir = "new-exports";
        file.Values.Games["fixture"].DisplayName = "Updated name";
        if (perGame) file.Values.Games["fixture"].OutputDir = "game-exports";
        file.Save();
        using var updated = await manager.AcquireAsync("fixture", CancellationToken.None);
        var newOutput = perGame ? System.IO.Path.Combine(file.Directory, "game-exports")
            : System.IO.Path.Combine(file.Directory, "new-exports", "fixture");

        Assert.Same(original.Session, updated.Session);
        Assert.Equal(newOutput, manager.ResolveOutputDir(updated, null));
        Assert.Equal(oldOutput, manager.ResolveOutputDir(original, null));
        Assert.Equal("Updated name", manager.GetState().Config.Games["fixture"].DisplayName);
    }

    [Fact]
    public async Task GameRemovedDuringASlowMountIsNotPublishedAndItsArchiveHandlesAreReleased()
    {
        using var pak = new EncryptedPakFixture(3, false, ECompressionFlags.COMPRESS_None);
        using var file = new ConfigFile(pak.Config);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var respond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = file.Values.Games["fixture"].AesKey;
        var pakDirectory = file.Values.Games["fixture"].PaksDir;
        var endpoint = ServeKey();
        file.Values.Games["fixture"].AesUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/aes";
        file.Save();
        using var manager = new GameManager(AppConfig.Load(file.Path));
        var mounting = manager.LoadAsync("fixture", reload: false, timeout.Token);
        try
        {
            await received.Task.WaitAsync(timeout.Token);
            file.Values.Games.Clear();
            file.Save();
            Assert.Empty(manager.GetState().Config.Games);
            await Assert.ThrowsAsync<ArgumentException>(() => manager.AcquireAsync("fixture", timeout.Token)).WaitAsync(timeout.Token);
            respond.SetResult();
            await Assert.ThrowsAsync<ArgumentException>(() => mounting);
            Assert.Empty(manager.GetState().LoadedGames);
            // Windows cannot delete a pak still held open by a discarded provider.
            System.IO.Directory.Delete(pakDirectory, recursive: true);
        }
        finally
        {
            respond.TrySetResult();
            await timeout.CancelAsync();
            await endpoint;
            try { await mounting; }
            catch (Exception ex) when (ex is ArgumentException or OperationCanceledException) { }
        }

        async Task ServeKey()
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
                received.SetResult();
                await respond.Task.WaitAsync(timeout.Token);
                var body = JsonSerializer.Serialize(new { mainKey = key });
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
                await stream.WriteAsync(response, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }
    }

    private sealed class ConfigFile : IDisposable
    {
        private int _revision;
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cue4parse-config-tests-" + Guid.NewGuid());
        public string Path => System.IO.Path.Combine(Directory, "games.json");
        public AppConfig Values { get; }

        public ConfigFile(AppConfig values)
        {
            Values = values;
            System.IO.Directory.CreateDirectory(Directory);
            Save();
        }

        public void Save(bool replace = false)
        {
            var content = JsonSerializer.Serialize(new
            {
                Values.DefaultOutputDir, Values.CacheDir, Values.OodlePath, Values.ZlibPath, Values.Games
            });
            if (replace)
            {
                File.WriteAllText(Path + ".tmp", content);
                File.Move(Path + ".tmp", Path, overwrite: true);
                MarkChanged();
            }
            else Write(content);
        }

        public void Write(string content)
        {
            File.WriteAllText(Path, content);
            MarkChanged();
        }

        // File-system timestamp resolution must not make tests depend on sleeps or machine speed.
        private void MarkChanged() => File.SetLastWriteTimeUtc(Path, DateTime.UnixEpoch.AddDays(20000).AddSeconds(++_revision));
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
