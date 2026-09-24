using System.Security.Cryptography;
using System.Text;
using Homebase.Core.Accounts;

namespace Homebase.Core.Sync;

/// <summary>
/// What a computer learns by redeeming a pairing code: who to sync with, for whom, and — when it
/// asked to be brought in on everything — which folders to set up.
/// </summary>
public sealed record PairingResult(string HostDeviceId, string AccountName, IReadOnlyList<PairedFolder> Folders);

/// <summary>A folder the computer should keep, by the id Syncthing knows it by.</summary>
public sealed record PairedFolder(string Id, string Label, string Path);

/// <summary>A code shown to a signed-in person, for their computer to type in.</summary>
public sealed record PairingCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// Pairs a computer with an account without anybody copying a device ID. A signed-in person asks
/// for a short code; the Uncloud app on their computer sends it back with its own device ID, and
/// that computer is paired with the account the code was issued to. The code is the only thing
/// that says which account, which is why it is short-lived, single-use, stored only as a hash and
/// throttled — the redeeming call is necessarily made without a session.
/// </summary>
public sealed class PairingCodes(
    ControlDatabase database,
    SyncService sync,
    SyncOwnership ownership,
    UserStore users,
    ISyncthingApi syncthing)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    // Crockford's base32: no I, L, O or U, so nothing read aloud or off a screen is ambiguous.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int Length = 10;

    private readonly LoginThrottle _throttle = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The clock, replaceable so expiry can be tested without waiting ten minutes.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// A new code for this account. Any earlier one stops working: the code on screen is the
    /// only one a person expects to be live.
    /// </summary>
    public PairingCode Issue(string userId)
    {
        var raw = new string(RandomNumberGenerator.GetItems<char>(Alphabet, Length));
        var now = Now();
        var expires = now + Lifetime;
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM pairing_codes WHERE user_id = $user OR expires_at < $now";
            clear.Parameters.AddWithValue("$user", userId);
            clear.Parameters.AddWithValue("$now", now.ToString("O"));
            clear.ExecuteNonQuery();
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO pairing_codes(code_hash, user_id, expires_at) VALUES ($hash, $user, $expires)";
            insert.Parameters.AddWithValue("$hash", Fingerprint(raw));
            insert.Parameters.AddWithValue("$user", userId);
            insert.Parameters.AddWithValue("$expires", expires.ToString("O"));
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        return new PairingCode($"{raw[..5]}-{raw[5..]}", expires);
    }

    /// <summary>
    /// Pairs the computer that sent this code with the account it was issued to. Anything wrong
    /// with the code gets one answer, whether it never existed, expired, was used, or belongs to
    /// an account that has since been disabled.
    /// </summary>
    /// <param name="rootFor">
    /// Where an account's files are, when the computer asked to be brought in on everything the
    /// account syncs; null to pair it and nothing more.
    /// </param>
    public async Task<PairingResult> RedeemAsync(string? code, string? deviceId, string? name, string? address,
        Func<string, string>? rootFor, CancellationToken cancellationToken)
    {
        try { _throttle.RequireAllowed(null, address); }
        catch (LibraryException)
        {
            throw new LibraryException("Too many pairing attempts. Wait a few minutes and try again.", "too_many_attempts");
        }
        // Checked before the code is looked at, so a mistake that has nothing to do with the
        // code doesn't use it up.
        if (!syncthing.IsAvailable)
            throw new LibraryException(syncthing.Unavailable ?? "Syncthing isn’t running on this Uncloud.", "sync_unavailable");
        var device = SyncService.NormalizeDeviceId(deviceId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var hash = Normalize(code) is { } normalized ? Fingerprint(normalized) : null;
            var account = hash is null ? null : Lookup(hash);
            if (account is null)
            {
                _throttle.RecordFailure(null, address);
                throw new LibraryException(
                    "That pairing code isn’t valid. Codes last ten minutes and work once; ask Uncloud for a new one.", "invalid_code");
            }

            // The app retrying after a dropped connection finds its computer already paired
            // with the same account, which is success rather than a conflict.
            if (ownership.FindDevice(device) is not { } owned || owned.UserId != account.Id)
                await sync.PairAsync(account.Id, device, name, cancellationToken);

            var folders = rootFor is null
                ? []
                : await sync.IncludeAsync(account.Id, rootFor(account.Id), device, cancellationToken);

            Delete(hash!);
            _throttle.RecordSuccess(null, address);
            return new PairingResult(await syncthing.DeviceIdAsync(cancellationToken), account.DisplayName,
                folders.Select(folder => new PairedFolder(folder.Id, folder.Label, folder.Path)).ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    private UserAccount? Lookup(string hash)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id, expires_at FROM pairing_codes WHERE code_hash = $hash";
        command.Parameters.AddWithValue("$hash", hash);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var userId = reader.GetString(0);
        if (DateTimeOffset.Parse(reader.GetString(1)) <= Now()) return null;
        return users.Find(userId) is { IsActive: true } account ? account : null;
    }

    private void Delete(string hash)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM pairing_codes WHERE code_hash = $hash";
        command.Parameters.AddWithValue("$hash", hash);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Whatever a person typed, as the code it was meant to be: any case, dashes and spaces
    /// anywhere, and the letters people confuse with digits read as those digits.
    /// </summary>
    private static string? Normalize(string? code)
    {
        var characters = (code ?? "").ToUpperInvariant()
            .Where(character => character is not ('-' or ' '))
            .Select(character => character switch { 'O' => '0', 'I' or 'L' => '1', _ => character })
            .ToArray();
        return characters.Length == Length && characters.All(Alphabet.Contains) ? new string(characters) : null;
    }

    private static string Fingerprint(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
