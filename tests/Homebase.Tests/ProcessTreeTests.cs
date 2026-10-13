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
    public async Task Children_are_stopped_even_when_their_parent_has_already_gone()
    {
        // The parent leaves its child behind and exits by itself — after its children were looked
        // for, as can happen at any moment. Stopping it then has nothing to stop but the child.
        if (OperatingSystem.IsWindows()) return;
        using var parent = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", "sleep 60 & read line" },
            RedirectStandardInput = true,
            UseShellExecute = false
        })!;
        IReadOnlyList<int> children = [];
        for (var attempt = 0; attempt < 50 && children.Count < 1; attempt++)
        {
            await Task.Delay(100);
            children = ProcessTree.Descendants(parent.Id);
        }
        var child = Assert.Single(children);

        parent.StandardInput.Close();
        Assert.True(parent.WaitForExit(5000));
        // Gone and waited for, so stopping it now finds nothing, and must still reach the child.
        // Its child was found by the parent's own id, which is all a caller still has by now.
        ProcessTree.Kill(parent, children);

        var gone = false;
        for (var attempt = 0; attempt < 50 && !gone; attempt++)
        {
            try
            {
                using var left = Process.GetProcessById(child);
                gone = left.HasExited;
            }
            catch (ArgumentException) { gone = true; }
            if (!gone) await Task.Delay(100);
        }
        Assert.True(gone, $"Process {child} was left running.");
    }

    [Fact]
    public async Task A_process_can_stop_everything_it_started_and_carry_on()
    {
        // What Uncloud does on its way out when it has stopped answering: its Syncthing and tunnel
        // go first, so they don't hold their ports against the Uncloud that starts next. The parent
        // here waits for its children as Uncloud does, so the ones stopped don't linger unreaped,
        // and then carries on.
        if (OperatingSystem.IsWindows()) return;
        using var parent = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", "sleep 60 & sleep 60 & wait; read line" },
            RedirectStandardInput = true,
            UseShellExecute = false
        })!;
        try
        {
            IReadOnlyList<int> children = [];
            for (var attempt = 0; attempt < 50 && children.Count < 2; attempt++)
            {
                await Task.Delay(100);
                children = ProcessTree.Descendants(parent.Id);
            }
            Assert.Equal(2, children.Count);

            ProcessTree.KillDescendants(parent.Id);

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
            Assert.False(parent.HasExited);
        }
        finally { ProcessTree.Kill(parent); }
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
