using Homebase.Core.Providers;
using Microsoft.Extensions.Logging;

namespace Homebase.Core.Accounts;

/// <summary>
/// Everything that belongs to one account: their folder, their Dropbox connection, their
/// imports, the folders on this computer they bring files in from. Nothing here is shared with another account, and none of it can be reached without
/// a user id that came from an authenticated session.
/// </summary>
public sealed class UserWorkspace
{
    public string UserId { get; }
    public string Root { get; }
    public LibraryService Library { get; }
    public IDropboxConnection Dropbox { get; }
    public ImportService Imports { get; }
    public ImportJobs Jobs { get; }
    /// <summary>Sizes of folders in the places this account brings files in from.</summary>
    public SourceSizes SourceSizes { get; } = new();

    private readonly ImportPlaces _places;

    public UserWorkspace(
        string userId,
        string root,
        MetadataIndex index,
        ImportLog log,
        IDropboxConnection dropbox,
        ImportPlaces places,
        ILoggerFactory loggers,
        Func<string, StorageReport?>? space = null)
    {
        UserId = userId;
        Root = root;
        Dropbox = dropbox;
        _places = places;
        // Everybody's room comes out of the one allotment, so it is asked about rather than the drive.
        space ??= Storage.For;
        Library = new LibraryService(root, index) { Space = space };
        Library.Initialize();
        Imports = new ImportService(Library, log, loggers.CreateLogger<ImportService>()) { Space = space };
        // One import at a time per account, rather than one for the whole host: somebody else
        // bringing a folder home is no reason to be told to wait.
        Jobs = new ImportJobs(Imports, loggers.CreateLogger<ImportJobs>());
    }

    /// <summary>
    /// The place a request names, as something the import engine can read. The id is the only thing
    /// a request gets to choose, and it has to match a folder this account added or one somebody
    /// shared with everyone — which is why no request can ever name a folder of its own.
    /// </summary>
    public IImportSource Source(string? sourceId) =>
        sourceId is null || sourceId.Length == 0 || sourceId == DropboxApi.ProviderName
            ? Dropbox
            : _places.Open(_places.Require(sourceId, UserId));
}
