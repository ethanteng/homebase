using System.Diagnostics;

namespace Homebase.Core;

/// <summary>
/// Stopping a process and everything it started — without freezing any of them first.
///
/// .NET's own <c>Process.Kill(entireProcessTree: true)</c> stops each process with SIGSTOP before
/// it kills it, so none can start more children in between. On macOS that is what brought the Mac
/// app down: the server it runs, Syncthing and the tunnel are all in the app's process group, and
/// when a process in a group nobody outside it is watching exits while another in it is stopped,
/// the system hangs the whole group up — SIGHUP to every process in it, the app included. So
/// stopping the server took the app with it.
///
/// Here each process is sent SIGKILL and nothing else, which never leaves one stopped.
/// </summary>
public static class ProcessTree
{
    public static void Kill(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            process.Kill(entireProcessTree: true);
            return;
        }
        // Found before anything goes: once their parent has gone, its children belong to launchd and
        // can't be told apart from anybody else's.
        var descendants = Descendants(process.Id);
        process.Kill();
        foreach (var id in descendants)
        {
            try
            {
                using var child = Process.GetProcessById(id);
                child.Kill();
            }
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Gone already, or not this process's to stop.
            }
        }
    }

    /// <summary>
    /// Every process <paramref name="root"/> started, and every one they started, as ps tells it —
    /// which every Mac and Linux has. Where there is no ps to ask, none are known, and only
    /// <paramref name="root"/> itself is stopped: the safe way to be wrong.
    /// </summary>
    internal static IReadOnlyList<int> Descendants(int root)
    {
        var children = new Dictionary<int, List<int>>();
        try
        {
            using var ps = Process.Start(new ProcessStartInfo("ps")
            {
                ArgumentList = { "-A", "-o", "pid=", "-o", "ppid=" },
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
            if (ps is null) return [];
            var said = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(5000);
            foreach (var line in said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 2 && int.TryParse(fields[0], out var pid) && int.TryParse(fields[1], out var parent))
                {
                    if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
                    list.Add(pid);
                }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return [];
        }

        var found = new List<int>();
        var waiting = new Queue<int>([root]);
        while (waiting.TryDequeue(out var parent))
            if (children.TryGetValue(parent, out var list))
                foreach (var child in list)
                    if (child != root && !found.Contains(child))
                    {
                        found.Add(child);
                        waiting.Enqueue(child);
                    }
        return found;
    }
}
