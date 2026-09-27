using System.Diagnostics;

namespace Optimum.Bootstrap.Core.Patch;

/// <summary>Run the staged, self-contained launcher check before an install is activated.</summary>
public static class DeltaRuntimeValidator
{
    public static async Task ValidateAsync(string runtime, CancellationToken token)
    {
        string executable = Path.Combine(runtime, OperatingSystem.IsWindows() ? "Optimum.exe" : "Optimum");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = runtime,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("--validate-only");
        start.ArgumentList.Add("--dataPath");
        start.ArgumentList.Add(Path.Combine(runtime, ".optimum", "validation-data"));
        start.Environment["OPTIMUM_HEADLESS"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("Could not validate the staged runtime.");
        Task stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("Runtime validation timed out.");
        }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new InvalidDataException("The staged runtime failed its startup checks. The installation was not activated.");
    }
}
