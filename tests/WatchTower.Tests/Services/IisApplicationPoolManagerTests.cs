using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using WatchTower.Models;
using WatchTower.Services;

namespace WatchTower.Tests.Services;

public class IisApplicationPoolManagerTests
{
    private static readonly string AppCmd = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "system32", "inetsrv", "appcmd.exe");

    [Fact]
    public async Task GetStatus_MissingPool_IsNeverReportedAsRunningAndNeverThrows()
    {
        // Without IIS (or without admin rights to read its config) the query fails and must
        // degrade to Unknown; with IIS it resolves to NotFound via the PowerShell fallback.
        var manager = new IisApplicationPoolManager(NullLogger<IisApplicationPoolManager>.Instance);

        var status = await manager.GetApplicationPoolStatusAsync(
            $"wt-test-missing-{Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        status.IsRunning.Should().BeFalse();
        status.Status.Should().BeOneOf(ApplicationPoolStatus.NotFoundStatus, ApplicationPoolStatus.UnknownStatus);
    }

    [Fact]
    public async Task GetStatus_Cancelled_Throws()
    {
        Assert.SkipUnless(File.Exists(AppCmd), "IIS (appcmd.exe) is not installed");
        var manager = new IisApplicationPoolManager(NullLogger<IisApplicationPoolManager>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => manager.GetApplicationPoolStatusAsync("DefaultAppPool", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}