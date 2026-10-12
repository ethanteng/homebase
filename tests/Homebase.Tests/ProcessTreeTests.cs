using System.Diagnostics;
using Homebase.Core;

namespace Homebase.Tests;

/// <summary>
/// Stopping a process and everything it started, without .NET's tree kill — whose stopping each
/// process first hung up the Mac app that runs the server, so the app went with it.
/// </summary>
public sealed class ProcessTreeTests
{
    [Fact]
    public async Task A_process_and_everything_it_started_are_stopped()
    {
        if (OperatingSystem.IsWindows()) return;
        using var parent = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", "sleep 60 & sleep 60 & wait" },
            UseShellExecute = false
        })!;
        IReadOnlyList<int> children = [];
        for (var attempt = 0; attempt < 50 && children.Count < 2; attempt++)
        {
            await Task.Delay(100);
            children = ProcessTree.Descendants(parent.Id);
        }
        Assert.Equal(2, children.Count);

        ProcessTree.Kill(parent);

        Assert.True(parent.WaitForExit(5000));
        foreach (var id in children)
        {
            var gone = false;
            for (var attempt = 0; attempt < 50 && !gone; attempt++)
            {
                try
                {
                    using var child = Process.GetProcessById(id);
                    gone = child.HasExited;
                }
                catch (ArgumentException) { gone = true; }
                if (!gone) await Task.Delay(100);
            }
            Assert.True(gone, $"Process {id} was left running.");
        }
    }

    [Fact]
    public void A_process_that_started_nothing_has_no_descendants()
    {
        if (OperatingSystem.IsWindows()) return;
        using var lone = Process.Start(new ProcessStartInfo("/bin/sleep") { ArgumentList = { "60" }, UseShellExecute = false })!;
        try { Assert.Empty(ProcessTree.Descendants(lone.Id)); }
        finally { ProcessTree.Kill(lone); }
    }
}
