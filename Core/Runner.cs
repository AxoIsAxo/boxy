using System.Diagnostics;
using System.Text;

namespace Boxy.Core;

public sealed record RunResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}

/// Runs external processes (distrobox/podman) with streaming output.
public static class Runner
{
    /// <summary>
    /// Runs a process with redirected stdout/stderr. <paramref name="onOutput"/> and
    /// <paramref name="onError"/> are invoked per line; if <paramref name="sync"/> is
    /// provided they are marshaled onto that synchronization context (UI thread).
    /// </summary>
    public static async Task<RunResult> RunAsync(
        IReadOnlyList<string> args,
        Action<string>? onOutput = null,
        Action<string>? onError = null,
        SynchronizationContext? sync = null,
        CancellationToken ct = default,
        int? timeoutSeconds = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutSeconds is int t && t > 0) cts.CancelAfter(TimeSpan.FromSeconds(t));

        var psi = new ProcessStartInfo
        {
            FileName = args[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        for (int i = 1; i < args.Count; i++) psi.ArgumentList.Add(args[i]);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();

        void Raise(Action<string>? cb, string line)
        {
            if (cb == null) return;
            if (sync is null) cb(line);
            else sync.Post(_ => cb(line), null);
        }

        var t1 = ReadLoopAsync(proc.StandardOutput, sbOut, s => Raise(onOutput, s), cts.Token);
        var t2 = ReadLoopAsync(proc.StandardError, sbErr, s => Raise(onError, s), cts.Token);

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        await Task.WhenAll(t1, t2);
        return new RunResult(proc.ExitCode, sbOut.ToString().TrimEnd('\n'), sbErr.ToString().TrimEnd('\n'));
    }

    public static Task<RunResult> CapturedAsync(
        IReadOnlyList<string> args,
        CancellationToken ct = default,
        int? timeoutSeconds = null)
        => RunAsync(args, null, null, null, ct, timeoutSeconds);

    private static async Task ReadLoopAsync(
        StreamReader reader, StringBuilder sb, Action<string> onLine, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                sb.AppendLine(line);
                onLine(line);
            }
        }
        catch (OperationCanceledException) { /* process killed */ }
    }
}
