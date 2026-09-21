using Homebase.Core.Providers;
using Microsoft.Extensions.Logging;

namespace Homebase.Core.Accounts;

/// <summary>
/// Everything that belongs to one account: their folder, their Dropbox connection, their
/// imports. Nothing here is shared with another account, and none of it can be reached without
/// a user id that came from an authenticated session.
/// </summary>
public sealed class UserWorkspace : IDisposable
{
    public string UserId { get; }
    public string Root { get; }
    public LibraryService Library { get; }
    public IDropboxConnection Dropbox { get; }
    public ImportService Imports { get; }
    public ImportJobs Jobs { get; }

    public UserWorkspace(
        string userId,
        string root,
        MetadataIndex index,
        ImportLog log,
        IDropboxConnection dropbox,
        ILoggerFactory loggers)
    {
        UserId = userId;
        Root = root;
        Dropbox = dropbox;
        Library = new LibraryService(root, index);
        Library.Initialize();
        Imports = new ImportService(Library, log, dropbox,
            loggers.CreateLogger<ImportService>());
        // One import at a time per account, rather than one for the whole host: somebody else
        // bringing a folder home is no reason to be told to wait.
        Jobs = new ImportJobs(Imports, loggers.CreateLogger<ImportJobs>());
    }

    public void Dispose() => Library.Dispose();
}
