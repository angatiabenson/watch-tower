using System.Diagnostics;

using AwesomeAssertions;

using WatchTower.Services;

namespace WatchTower.Tests.Services;

public class ProcessRunnerTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly string PowerShell = Path.Combine(
        Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    [Fact]
    public async Task RunAsync_CapturesStdoutStderrAndExitCode()
    {
        var result = await ProcessRunner.RunAsync(
            Cmd, new[] { "/c", "echo out& echo err 1>&2& exit /b 3" }, TimeSpan.FromSeconds(30), CancellationToken.None);

        result.TimedOut.Should().BeFalse();
        result.ExitCode.Should().Be(3);
        result.Succeeded.Should().BeFalse();
        result.StdOut.Trim().Should().Be("out");
        result.StdErr.Trim().Should().Be("err");
    }

    [Fact]
    public async Task RunAsync_LargeOutputOnBothStreams_DoesNotDeadlock()
    {
        // Well past the 4 KB anonymous pipe buffer on both streams: reading them one after the
        // other (the previous implementation) blocks the child forever.
        const string script = "$s = 'x' * 1MB; [Console]::Out.Write($s); [Console]::Error.Write($s)";

        var result = await ProcessRunner.RunAsync(
            PowerShell, new[] { "-NoProfile", "-NonInteractive", "-Command", script },
            TimeSpan.FromSeconds(60), CancellationToken.None);

        result.TimedOut.Should().BeFalse();
        result.StdOut.Length.Should().Be(1024 * 1024);
        result.StdErr.Length.Should().Be(1024 * 1024);
    }

    [Fact]
    public async Task RunAsync_Timeout_KillsProcessAndReportsTimedOut()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await ProcessRunner.RunAsync(
            Cmd, new[] { "/c", "ping -n 30 127.0.0.1 >nul" }, TimeSpan.FromMilliseconds(500), CancellationToken.None);

        result.TimedOut.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_Throws()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var act = () => ProcessRunner.RunAsync(
            Cmd, new[] { "/c", "ping -n 30 127.0.0.1 >nul" }, TimeSpan.FromSeconds(60), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}