using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Objects.Core.Misc;
using Cue4ParseMcp.Core;

namespace Cue4ParseMcp.Tests;

public sealed class EncryptedPakTests
{
    [Theory]
    [InlineData(3, false, ECompressionFlags.COMPRESS_None, false)]
    [InlineData(3, false, ECompressionFlags.COMPRESS_ZLIB, false)]
    [InlineData(3, false, ECompressionFlags.COMPRESS_Custom, false)]
    [InlineData(4, true, ECompressionFlags.COMPRESS_ZLIB, false)]
    [InlineData(7, true, ECompressionFlags.COMPRESS_None, true)]
    [InlineData(7, false, ECompressionFlags.COMPRESS_Custom, true)]
    public async Task EncryptedEntriesAreReadableAndRemainMounted(int version, bool encryptedIndex, ECompressionFlags compression, bool dynamicKey)
    {
        using var fixture = new EncryptedPakFixture(version, encryptedIndex, compression, dynamicKey);
        using var manager = new GameManager(fixture.Config);
        using var lease = await manager.AcquireAsync("fixture", CancellationToken.None);

        Assert.Equal(EncryptedPakFixture.Probe, lease.Session.Provider.SaveAsset(EncryptedPakFixture.ProbePath));
        Assert.Equal(EncryptedPakFixture.Ini, lease.Session.Provider.SaveAsset(EncryptedPakFixture.IniPath));
        Assert.Equal(1, lease.Session.Summary.MountedContainers);
        Assert.Equal(0, lease.Session.Summary.UnmountedContainers);
    }

    [Fact]
    public async Task UnencryptedPakRemainsReadableWithoutKeys()
    {
        using var fixture = new EncryptedPakFixture(3, false, ECompressionFlags.COMPRESS_None, encryptedFiles: false, supplyKey: false);
        using var manager = new GameManager(fixture.Config);
        using var lease = await manager.AcquireAsync("fixture", CancellationToken.None);

        Assert.Equal(EncryptedPakFixture.Probe, lease.Session.Provider.SaveAsset(EncryptedPakFixture.ProbePath));
        Assert.Equal(1, lease.Session.Summary.MountedContainers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncryptedEntriesCannotBeReadWithoutTheirKey(bool encryptedIndex)
    {
        using var fixture = new EncryptedPakFixture(encryptedIndex ? 4 : 3, encryptedIndex, ECompressionFlags.COMPRESS_None, supplyKey: false);
        using var manager = new GameManager(fixture.Config);
        using var lease = await manager.AcquireAsync("fixture", CancellationToken.None);

        if (encryptedIndex)
            Assert.Throws<KeyNotFoundException>(() => lease.Session.Provider.SaveAsset(EncryptedPakFixture.ProbePath));
        else
            Assert.Throws<InvalidAesKeyException>(() => lease.Session.Provider.SaveAsset(EncryptedPakFixture.ProbePath));
    }
}
