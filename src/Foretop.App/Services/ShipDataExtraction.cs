using System.Diagnostics;
using System.IO;
using Foretop.Core.GameData;
using Microsoft.Extensions.Logging;

namespace Foretop.App.Services;

/// <summary>
/// Runs the ship-data extraction in a second copy of Foretop.exe (<c>--extract-ship-data &lt;game&gt; &lt;out&gt;</c>) so the
/// ~1 GB needed to read GameParams is returned to Windows as soon as it finishes. Progress comes back on stdout.
/// </summary>
public static class ShipDataExtraction
{
    public const string Switch = "--extract-ship-data";

    /// <summary>Entry point of the helper process. Returns the exit code.</summary>
    public static int RunHelper(string[] args)
    {
        var i = Array.IndexOf(args, Switch);
        if (i < 0 || i + 2 >= args.Length) return 2;
        var stdout = Console.OpenStandardOutput();
        using var w = new StreamWriter(stdout) { AutoFlush = true };
        try
        {
            var last = -1;
            // Synchronous reporter: Progress<T> would post to the WPF dispatcher, which runs after the writer is closed.
            var set = ShipDataStore.ExtractToFile(args[i + 1], args[i + 2], new SyncProgress<(int Done, int Total)>(p =>
            {
                if (p.Done == last) return;
                last = p.Done;
                if (p.Done % 10 == 0 || p.Done == p.Total) w.WriteLine($"progress {p.Done} {p.Total}");
            }), msg => w.WriteLine("warn " + msg));
            w.WriteLine($"done {set.Ships.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            w.WriteLine("error " + ex.Message.ReplaceLineEndings(" "));
            return 1;
        }
    }

    /// <summary>Starts the helper and relays its progress. Throws when it fails.</summary>
    public static async Task RunAsync(string gameRoot, string outFile, IProgress<(int, int)> progress, ILogger log, CancellationToken ct)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find Foretop.exe");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(Switch);
        psi.ArgumentList.Add(gameRoot);
        psi.ArgumentList.Add(outFile);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the ship data reader");
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }
        string? error = null;
        var warnings = 0;
        using var reg = ct.Register(() => { try { p.Kill(); } catch (InvalidOperationException) { } });
        while (await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            var parts = line.Split(' ', 3);
            switch (parts[0])
            {
                case "progress" when parts.Length == 3 && int.TryParse(parts[1], out var d) && int.TryParse(parts[2], out var t):
                    progress.Report((d, t));
                    break;
                case "warn":
                    warnings++;
                    log.LogWarning("Ship data: {Warning}", line[5..]);
                    break;
                case "error":
                    error = line[6..];
                    break;
            }
        }
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0 || error is not null)
            throw new InvalidOperationException("Reading the game files failed: " + (error ?? $"exit code {p.ExitCode}"));
        log.LogInformation("Ship data extracted with {Warnings} warnings", warnings);
    }
}

/// <summary>IProgress that reports on the calling thread.</summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
