using System.Diagnostics;

namespace WatchTower.Services;

/// <summary>
/// Runs a short-lived helper process with a hard timeout. stdout and stderr are drained
/// concurrently (so a chatty child cannot deadlock on a full pipe), the wait is fully async and
/// cancellable, and the whole process tree is killed on timeout or cancellation.
/// </summary>
internal static class ProcessRunner
{
    internal sealed record Result(int ExitCode, string StdOut, string StdErr, bool TimedOut)
    {
        public bool Succeeded => !TimedOut && ExitCode == 0;
    }

    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static async Task<Result> RunAsync(
        string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        // Start both reads before waiting so neither pipe can fill up and block the child.
        var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            var output = await stdout;
            var error = await stderr;
            return new Result(process.ExitCode, output, error, TimedOut: false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            ObserveQuietly(stdout);
            ObserveQuietly(stderr);

            ct.ThrowIfCancellationRequested();
            return new Result(-1, string.Empty, $"Timed out after {timeout.TotalSeconds:0}s", TimedOut: true);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best effort: the process may have exited in the meantime
        }
    }

    private static void ObserveQuietly(Task task) =>
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}