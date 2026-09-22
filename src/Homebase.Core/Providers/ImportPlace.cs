using System.Security.Cryptography;

namespace Homebase.Core.Providers;

/// <summary>
/// A folder on the host's own computer that everybody here may bring files in from — the Dropbox
/// or Google Drive folder a desktop app already syncs, an old external drive, a Pictures folder.
///
/// Only an administrator adds one, and that is the whole of the permission model: Uncloud runs as
/// one operating-system user and can read anything that user can, so a member naming a folder of
/// their own choosing would be a way around the isolation between accounts rather than a feature.
/// What is listed here is what the person who looks after this Uncloud has decided to share.
/// </summary>
public sealed record ImportPlace(string Id, string Name, string Path, DateTimeOffset AddedAt)
{
    /// <summary>What imports from this place are recorded under, so two places never collide.</summary>
    public string ProviderId => $"folder:{Id}";

    /// <summary>The folder under the library that files from here land in.</summary>
    public string DestinationPrefix => $"Files/{FolderName(Name)}";

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    /// <summary>
    /// The place's name as a folder can be called. A name is typed by a person, so it has to be
    /// made safe before it becomes a path: separators, hidden-file dots and control characters all
    /// mean something to the filesystem that they don't mean to whoever typed them.
    /// </summary>
    public static string FolderName(string name)
    {
        var cleaned = new string(name.Trim()
            .Select(character => character is '/' or '\\' or ':' || char.IsControl(character) ? ' ' : character)
            .ToArray()).Trim(' ', '.');
        if (cleaned.Length > 60) cleaned = cleaned[..60].TrimEnd(' ', '.');
        return cleaned.Length == 0 ? "Imported" : cleaned;
    }
}
