namespace Homebase.Core.Accounts;

/// <summary>Where one account's files live under the host's folder.</summary>
public static class UserPaths
{
    public const string UsersDirectory = "users";

    /// <summary>
    /// This account's folder, made if it isn't there yet. The id comes from the session the
    /// request was authenticated with and never from the request itself, which is the whole of
    /// why one account cannot ask for another's files: there is no path to ask along.
    /// </summary>
    public static string RootFor(string hostRoot, string userId)
    {
        // Resolving through the ordinary policy means the symbolic-link and traversal checks
        // apply to the account folder itself, not just to what is browsed inside it.
        var path = PathPolicy.Resolve(hostRoot, $"{UsersDirectory}/{userId}");
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        PathPolicy.RejectLink(path);
        return path;
    }

    /// <summary>
    /// The entries sitting at the top of the host's folder that belong to no account — what a v0
    /// library left behind. <c>users</c> and anything hidden are the host's own and never listed.
    /// </summary>
    public static IReadOnlyList<string> Unclaimed(string hostRoot)
    {
        if (!Directory.Exists(hostRoot)) return [];
        return new DirectoryInfo(hostRoot)
            .EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true })
            .Where(entry => !entry.Name.StartsWith('.')
                && !entry.Name.Equals(UsersDirectory, StringComparison.Ordinal))
            .Select(entry => entry.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
