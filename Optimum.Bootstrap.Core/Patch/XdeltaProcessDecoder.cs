using System.Diagnostics;

namespace Optimum.Bootstrap.Core.Patch;

/// <summary>
/// Adapter for an explicitly supplied xdelta3 executable. No PATH lookup, tool
/// acquisition or SDK invocation. Bundling/native in-process decoding is separate work.
/// </summary>
public sealed class XdeltaProcessDecoder : IBinaryDeltaDecoder
{
    private readonly string executable;
    public XdeltaProcessDecoder(string executable)
    {
        if (!Path.IsPathFullyQualified(executable)) throw new ArgumentException("Decoder path must be absolute.");
        this.executable = executable;
    }

    public async Task DecodeAsync(string original, string delta, string output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment.Remove("XDELTA");
        foreach (var argument in new[] { "-d", "-D", "-s", original, delta, output })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start delta decoder.");
        // Drain without retaining unbounded diagnostics from a failing decoder.
        Task stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Delta decoder exceeded its two-minute limit.");
        }
        if (process.ExitCode != 0) throw new InvalidDataException($"Delta decoder failed with exit code {process.ExitCode}.");
    }
}
