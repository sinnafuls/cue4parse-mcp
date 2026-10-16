using System.Text.Json;
using Cue4ParseMcp.Core;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Options;

namespace Cue4ParseMcp.Tests;

public sealed class BatchJobsTests
{
    private const string Table = "/Game/Fixtures/DataTables/DT_AllProperties";
    private const string DataAsset = "/Game/Fixtures/Properties/DA_AllProperties";
    private const string Texture = "/Game/Fixtures/Textures/T_Streaming";

    [Fact]
    public async Task PreviewDoesNotWriteAndRemountRequiresANewPreview()
    {
        using var fixture = new ParsedAssetFixture();
        using var jobs = CreateJobs(fixture);
        var preview = await jobs.PreviewAsync("fixture", new BatchRequest { Kind = "json", Paths = [Table] }, CancellationToken.None);
        Assert.False(Directory.Exists(fixture.OutputDir));
        await fixture.Games.LoadAsync("fixture", reload: true, CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.StartAsync(preview.PreviewId, CancellationToken.None));
        Assert.Contains("preview", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.OutputDir));
    }

    [Fact]
    public async Task MixedTableSelectionProducesPartialSuccessAndACompleteManifest()
    {
        using var fixture = new ParsedAssetFixture();
        using var jobs = CreateJobs(fixture);
        var preview = await jobs.PreviewAsync("fixture", new BatchRequest { Kind = "table", Paths = [Table, DataAsset] }, CancellationToken.None);
        var started = await jobs.StartAsync(preview.PreviewId, CancellationToken.None);
        var completed = await jobs.WaitAsync(started.JobId, CancellationToken.None);
        Assert.Equal("completed_with_errors", completed.State);
        Assert.Equal(2, completed.CompletedItems);
        Assert.Equal("completed", completed.Items.Single(item => item.Path.EndsWith("DT_AllProperties", StringComparison.Ordinal)).State);
        var failed = completed.Items.Single(item => item.Path.EndsWith("DA_AllProperties", StringComparison.Ordinal));
        Assert.Equal("failed", failed.State);
        Assert.NotEmpty(failed.Failures);
        Assert.Empty(failed.RootFiles);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(completed.ManifestPath!, TestContext.Current.CancellationToken));
        Assert.Equal("completed_with_errors", manifest.RootElement.GetProperty("state").GetString());
        Assert.Equal(2, manifest.RootElement.GetProperty("items").GetArrayLength());
        var successful = manifest.RootElement.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("state").GetString() == "completed");
        var file = Assert.Single(successful.GetProperty("rootFiles").EnumerateArray()).GetString()!;
        Assert.True(File.Exists(file));
        using var exported = JsonDocument.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Object, exported.RootElement.ValueKind);
        var page = jobs.Get(started.JobId, 0, 1);
        Assert.True(page.ItemsTruncated);
        Assert.Equal(1, page.NextItemOffset);
        Assert.Equal(completed.Items[1], Assert.Single(jobs.Get(started.JobId, 1, 1).Items));
    }

    [Fact]
    public async Task NativeObjectAliasesAreExportedOnlyOnce()
    {
        using var fixture = new ParsedAssetFixture();
        using var jobs = CreateJobs(fixture);
        var preview = await jobs.PreviewAsync("fixture", new BatchRequest
        {
            Kind = "textures", Paths = [Texture, "Texture2D'" + Texture + ".T_Streaming'"]
        }, CancellationToken.None);
        var started = await jobs.StartAsync(preview.PreviewId, CancellationToken.None);
        var completed = await jobs.WaitAsync(started.JobId, CancellationToken.None);
        Assert.Equal("completed", completed.State);
        Assert.Equal(2, completed.CompletedItems);
        Assert.Equal(1, completed.ExportedObjects);
        var first = Assert.Single(completed.Items[0].RootFiles);
        Assert.Equal(first, Assert.Single(completed.Items[1].RootFiles));
        await using var output = File.OpenRead(first);
        var header = new byte[8];
        await output.ReadExactlyAsync(header, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, header);
    }

    [Fact]
    public async Task CancellationWhileWaitingForAnOutputDirectoryWritesCancelledManifest()
    {
        using var fixture = new ParsedAssetFixture();
        using var lease = await fixture.Games.AcquireAsync("fixture", CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new HoldCompletedExport(entered, release, timeout.Token);
        var outputDir = fixture.Games.ResolveOutputDir(lease, null);
        var texture = lease.Session.LoadMain(Texture);
        var existing = Task.Run(() => lease.Session.ExportAsync([texture], new ExportOptions(), outputDir, timeout.Token, hold));
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            using var jobs = CreateJobs(fixture);
            var preview = await jobs.PreviewAsync("fixture", new BatchRequest { Kind = "textures", Paths = [Texture] }, timeout.Token);
            var started = await jobs.StartAsync(preview.PreviewId, timeout.Token);
            jobs.Cancel(started.JobId);
            var completed = await jobs.WaitAsync(started.JobId, timeout.Token);
            Assert.Equal("cancelled", completed.State);
            Assert.Equal(0, completed.CompletedItems);
            Assert.Equal("cancelled", Assert.Single(completed.Items).State);
            Assert.Empty(completed.Items[0].RootFiles);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(completed.ManifestPath!, timeout.Token));
            Assert.Equal("cancelled", manifest.RootElement.GetProperty("state").GetString());
            Assert.Equal("cancelled", manifest.RootElement.GetProperty("items")[0].GetProperty("state").GetString());
        }
        finally
        {
            release.Set();
            await existing;
        }
    }

    private static BatchJobs CreateJobs(ParsedAssetFixture fixture) =>
        new(fixture.Games, new DataOps(fixture.Games), new AudioOps(fixture.Games));

    private sealed class HoldCompletedExport(TaskCompletionSource entered, ManualResetEventSlim release,
        CancellationToken ct) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value)
        {
            if (value.LastResult is null) return;
            entered.TrySetResult();
            release.Wait(ct);
        }
    }
}
