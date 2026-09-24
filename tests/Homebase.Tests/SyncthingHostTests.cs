using Homebase.Server;

namespace Homebase.Tests;

/// <summary>Which Syncthing Uncloud runs: the one it ships, unless somebody said otherwise.</summary>
public sealed class SyncthingHostTests : IDisposable
{
    private readonly string _application = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "homebase-tests", Guid.NewGuid().ToString("N"))).FullName;

    private string Bundled => Path.Combine(_application, OperatingSystem.IsWindows() ? "syncthing.exe" : "syncthing");

    [Fact]
    public void The_copy_beside_the_application_is_used_ahead_of_the_path()
    {
        File.WriteAllText(Bundled, "");

        Assert.Equal(Bundled, SyncthingHost.Binary(null, _application));
        Assert.Equal(Bundled, SyncthingHost.Binary(" ", _application));
    }

    [Fact]
    public void A_configured_path_wins_even_over_the_bundled_copy()
    {
        File.WriteAllText(Bundled, "");

        Assert.Equal("/opt/syncthing/bin/syncthing", SyncthingHost.Binary("/opt/syncthing/bin/syncthing", _application));
    }

    [Fact]
    public void Without_a_bundled_copy_the_path_is_asked()
    {
        Assert.Equal("syncthing", SyncthingHost.Binary(null, _application));
    }

    public void Dispose()
    {
        try { Directory.Delete(_application, true); } catch (IOException) { }
    }
}
