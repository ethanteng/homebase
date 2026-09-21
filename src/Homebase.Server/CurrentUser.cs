using Homebase.Core;
using Homebase.Core.Accounts;

namespace Homebase.Server;

/// <summary>
/// The account a request was authenticated as. A handler asks for one by taking it as a
/// parameter, and the binding refuses rather than handing back nothing: a route that reaches a
/// handler without having been through the session check fails loudly instead of quietly
/// serving somebody's files to nobody in particular.
/// </summary>
public sealed class CurrentUser(UserAccount account, string token)
{
    internal const string Key = "homebase.user";

    public UserAccount Account { get; } = account;
    /// <summary>The session this request arrived on, so changing a password can spare it.</summary>
    public string Token { get; } = token;
    public string Id => Account.Id;
    public bool IsAdmin => Account.IsAdmin;

    public static ValueTask<CurrentUser?> BindAsync(HttpContext context) =>
        ValueTask.FromResult<CurrentUser?>(context.Items[Key] as CurrentUser
            ?? throw new LibraryException("Sign in to Uncloud to continue.", "unauthenticated"));
}
