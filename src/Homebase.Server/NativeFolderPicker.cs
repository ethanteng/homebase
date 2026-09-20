using System.Diagnostics;
using Homebase.Core;

namespace Homebase.Server;

public interface IFolderPicker
{
    bool IsSupported { get; }
    Task<string?> ChooseAsync(CancellationToken cancellationToken);
}

public sealed class NativeFolderPicker : IFolderPicker
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public bool IsSupported => OperatingSystem.IsMacOS();

    public async Task<string?> ChooseAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported) throw new LibraryException("Enter the folder’s full path on this computer.", "unsupported");
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new LibraryException("A folder chooser is already open. Check your desktop.", "busy");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var start = new ProcessStartInfo("/usr/bin/osascript")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("POSIX path of (choose folder with prompt \"Choose your Homebase folder\")");
            using var process = Process.Start(start) ?? throw new IOException("Could not open the folder chooser.");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var errorText = await error;
                if (process.ExitCode != 0)
                {
                    if (errorText.Contains("(-128)", StringComparison.Ordinal)) return null;
                    throw new LibraryException("The folder chooser couldn’t open. Enter the full folder path instead.", "picker_failed");
                }
                return (await output).TrimEnd('\r', '\n');
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new LibraryException("The folder chooser timed out. Try again or enter a folder path.", "picker_failed");
            }
        }
        finally { _gate.Release(); }
    }
}
