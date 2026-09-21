namespace Homebase.Core.Accounts;

/// <summary>
/// One person with an account on this host. The id is opaque and permanent: it names the
/// directory their files live in, so renaming an account never moves a file.
/// </summary>
public sealed record UserAccount(
    string Id,
    string Username,
    string DisplayName,
    bool IsAdmin,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DisabledAt)
{
    public bool IsActive => DisabledAt is null;
}

/// <summary>A signed-in session, as it is handed to the browser. Only its hash is stored.</summary>
public sealed record AuthSession(string Token, DateTimeOffset ExpiresAt);

/// <summary>A provider account somebody has connected, without the secret that reaches it.</summary>
public sealed record ConnectorAccount(string Provider, string? AccountName, DateTimeOffset ConnectedAt);
