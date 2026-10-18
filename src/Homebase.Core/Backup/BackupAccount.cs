using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Homebase.Core.Accounts;
using Microsoft.Extensions.Logging;

namespace Homebase.Core.Backup;

/// <summary>Where a host backs up, as Uncloud Backup's service handed it out.</summary>
public sealed record BackupStorage(string Endpoint, string Region, string Bucket, string AccessKeyId, string SecretAccessKey);

/// <summary>
/// What the service said when asked for this host's storage: the storage, or that its household is
/// still being set up (or setting it up keeps failing), and when to ask again.
/// </summary>
public sealed record StorageAnswer(BackupStorage? Storage, string? State, string? Step, TimeSpan RetryAfter);

/// <summary>
/// A host the service has just let in: its household, itself, and the credential it's known by, and
/// when the household's recovery kit in use was made, where there is one.
/// </summary>
public sealed record BackupRegistration(
    string HouseholdId, string HostId, string HostName, string Credential, DateTimeOffset? RecoveryMadeAt = null);

/// <summary>
/// Uncloud Backup's service (services/backup in this repository), as a host talks to it. Its errors
/// come back as <c>{ error: { code, message } }</c>, with words meant for a person.
/// </summary>
public sealed class BackupServiceClient(HttpClient http, Uri service)
{
    /// <summary>Setting a household up can take the service most of a minute, in one request.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    public Uri Service => service;

    /// <summary>Joins with an invite: the household and this host are made, and the host gets its credential.</summary>
    public async Task<BackupRegistration> RegisterAsync(string invite, string name, CancellationToken cancellationToken)
    {
        using var response = await SendAsync("api/hosts", null, new { invite, name }, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Created)
            throw await RefusalAsync(response, "join Uncloud Backup", cancellationToken);
        return Registration(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    private static BackupRegistration Registration(JsonElement body) => new(
        body.GetProperty("household").GetProperty("id").GetString()!,
        body.GetProperty("host").GetProperty("id").GetString()!,
        body.GetProperty("host").GetProperty("name").GetString()!,
        body.GetProperty("credential").GetString()!,
        body.TryGetProperty("recoveryMadeAt", out var made) && made.ValueKind == JsonValueKind.String ? made.GetDateTimeOffset() : null);

    /// <summary>A new recovery token for this host's household, in place of any before it.</summary>
    public async Task<(string Recovery, DateTimeOffset MadeAt)> IssueRecoveryAsync(string credential, CancellationToken cancellationToken)
    {
        using var response = await SendAsync("api/host/recovery", credential, new { }, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Created)
            throw await RefusalAsync(response, "make a recovery kit", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return (body.GetProperty("recovery").GetString()!, body.GetProperty("madeAt").GetDateTimeOffset());
    }

    /// <summary>
    /// The first of recovery's two steps: with the household's recovery token, this host is let in
    /// provisionally, with a credential of its own, to try the kit's password. The household's
    /// other hosts stay until it confirms. A recovery already under way is retired by this one.
    /// </summary>
    public async Task<BackupRegistration> RecoverAsync(string recovery, string name, CancellationToken cancellationToken)
    {
        using var response = await SendAsync("api/hosts/recover", null, new { recovery, name }, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Created)
            throw await RefusalAsync(response, "open the household's backup", cancellationToken);
        return Registration(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    /// <summary>
    /// The second step, the kit's password having opened the backup: this host takes the lost
    /// one's place, and the household's other hosts are revoked, their keys removed.
    /// </summary>
    public async Task ConfirmRecoveryAsync(string credential, CancellationToken cancellationToken)
    {
        using var response = await SendAsync("api/host/recovery/confirm", credential, new { }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.OK) return;
        var failure = await RefusalAsync(response, "take the lost Uncloud’s place", cancellationToken);
        // A repeat, after the first answer was lost: this host is already the household's host.
        if (failure.Code == "not_recovering") return;
        throw failure;
    }

    /// <summary>
    /// Instead of the second step, the kit's password not having opened the backup: only this host
    /// is retired. The household's hosts are as they were.
    /// </summary>
    public async Task AbandonRecoveryAsync(string credential, CancellationToken cancellationToken)
    {
        using var response = await SendAsync("api/host/recovery/abandon", credential, new { }, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw await RefusalAsync(response, "withdraw from the household", cancellationToken);
    }

    /// <summary>
    /// Asks where to back up. Once the household is set up, the answer carries a new key, and any key
    /// this host had before stops working.
    /// </summary>
    public async Task<StorageAnswer> StorageAsync(string credential, CancellationToken cancellationToken)
    {
        using var response = await SendAsync("api/host/storage", credential, null, cancellationToken);
        var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
        if (response.StatusCode == HttpStatusCode.Created)
        {
            var storage = (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("storage");
            string Text(string name) => storage.GetProperty(name).GetString()!;
            return new StorageAnswer(
                new BackupStorage(Text("endpoint"), Text("region"), Text("bucket"), Text("accessKeyId"), Text("secretAccessKey")),
                "ready", null, TimeSpan.Zero);
        }
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            var setup = (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("setup");
            return new StorageAnswer(null, setup.GetProperty("state").GetString(), setup.GetProperty("step").GetString(), retry);
        }
        throw await RefusalAsync(response, "get this Uncloud's backup storage", cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string? credential, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(service, path));
        if (credential is not null) request.Headers.Authorization = new("Bearer", credential);
        request.Content = body is null ? null : JsonContent.Create(body);
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        patience.CancelAfter(Patience);
        try
        {
            return await http.SendAsync(request, patience.Token);
        }
        catch (HttpRequestException)
        {
            throw new LibraryException(
                "Uncloud couldn’t reach Uncloud Backup. Check this computer’s internet connection, then try again.", "unavailable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LibraryException("Uncloud Backup didn’t answer in time. Try again in a minute.", "unavailable");
        }
    }

    private static async Task<LibraryException> RefusalAsync(HttpResponseMessage response, string doing, CancellationToken cancellationToken)
    {
        string? code = null;
        string? message = null;
        try
        {
            var error = (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("error");
            code = error.GetProperty("code").GetString();
            message = error.GetProperty("message").GetString();
        }
        catch (Exception unreadable) when (unreadable is JsonException or KeyNotFoundException or InvalidOperationException
                                               or NotSupportedException)
        {
            // Not the service's own answer: something in between, or a page. Said by its status below.
        }
        return code switch
        {
            "invite_invalid" => new LibraryException(message ?? "Uncloud Backup didn’t take that invite code.", "invalid_invite"),
            "recovery_invalid" => new LibraryException(message ?? "Uncloud Backup didn’t take that recovery token.", "invalid_recovery"),
            "not_a_host" => new LibraryException(
                "Uncloud Backup no longer knows this Uncloud. Ask for a new invite to join again.", "conflict"),
            "not_recovering" => new LibraryException(
                message ?? "This Uncloud isn’t taking another’s place.", "not_recovering"),
            _ => new LibraryException(
                $"Uncloud couldn’t {doing}: {message ?? $"Uncloud Backup answered {(int)response.StatusCode}."}", "unavailable")
        };
    }
}

/// <summary>How this host stands with Uncloud Backup, for whoever looks after it.</summary>
public sealed record BackupAccountStatus
{
    public bool Joined { get; init; }
    public string? Service { get; init; }
    public string? Household { get; init; }
    public string? Host { get; init; }
    public string? HostName { get; init; }
    public DateTimeOffset? JoinedAt { get; init; }
    /// <summary><c>ready</c>, or how setting the household's storage up stands: <c>setting_up</c> or <c>failing</c>.</summary>
    public string? Storage { get; init; }
    /// <summary>Which part of setting up it's at, until it's ready.</summary>
    public string? Step { get; init; }
    public string? Bucket { get; init; }
    /// <summary>When the household's recovery kit in use was made, or null if none has been.</summary>
    public DateTimeOffset? RecoveryKitMadeAt { get; init; }
    /// <summary>
    /// Whether backing up waits to be resumed: a host that opened a lost one's backup with its kit
    /// doesn't add to it until whoever looks after it has got back what they need.
    /// </summary>
    public bool Paused { get; init; }
}

/// <summary>
/// What gets a household's backup back when the host is gone: the password the backup is encrypted
/// with, and the token the service takes in place of the lost host's credential. Shown once each
/// time one is made; making another retires it.
/// </summary>
public sealed record BackupKit(string Password, string Recovery, string Household, DateTimeOffset MadeAt);

/// <summary>A host that has just joined, and the password its backup will be encrypted with, in a kit if one could be made.</summary>
public sealed record BackupJoined(string Password, BackupKit? Kit, BackupAccountStatus Account);

/// <summary>
/// This host's place in Uncloud Backup. Joining with an invite gets it a credential the service
/// knows it by, and makes the password its backup is encrypted with, which only this host and
/// whoever looks after it ever hold. Once its household's storage is set up, it has where to back up
/// and a key of its own to get there. Every secret is sealed with host.key, as connector tokens are.
/// </summary>
public sealed class BackupAccount(
    ControlDatabase database, SecretProtector protector, BackupServiceClient service, ILogger<BackupAccount> logger,
    TimeProvider? time = null)
{
    internal const string Key = "backup_account";
    private const string CredentialContext = "backup:credential";
    private const string PasswordContext = "backup:password";
    private const string StorageContext = "backup:storage";
    // Read out and typed from a recovery kit some day, so it leaves out what's easily misread (0 and
    // O, 1 and I), in groups of five: 30 characters from 32 is 150 bits.
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _changing = new(1, 1);
    // Let go when the host joins, or resumes backing up, so whatever is waiting on either goes on now.
    private readonly SemaphoreSlim _joined = new(0, 1);

    public bool Joined => Load() is not null;

    /// <summary>Where to back up, once the household's storage is set up; until then, null.</summary>
    public BackupTarget? Target
    {
        get
        {
            if (Load() is not { Storage: { } storage } saved) return null;
            var password = Open(saved.Password, PasswordContext);
            var secret = Open(storage.SecretAccessKey, StorageContext);
            if (password is null || secret is null) return null;
            return new BackupTarget($"s3:{storage.Endpoint.TrimEnd('/')}/{storage.Bucket}", password, storage.AccessKeyId, secret, storage.Region);
        }
    }

    public BackupAccountStatus Status => Load() is not { } saved
        ? new BackupAccountStatus()
        : new BackupAccountStatus
        {
            Joined = true,
            Service = saved.Service,
            Household = saved.Household,
            Host = saved.Host,
            HostName = saved.HostName,
            JoinedAt = saved.JoinedAt,
            Storage = saved.Storage is not null ? "ready" : saved.SetupState ?? "setting_up",
            Step = saved.Storage is null ? saved.SetupStep : null,
            Bucket = saved.Storage?.Bucket,
            RecoveryKitMadeAt = saved.RecoveryMadeAt,
            Paused = saved.Paused
        };

    public bool Paused => Load()?.Paused == true;

    /// <summary>
    /// Joins Uncloud Backup with an invite. The password in the answer is what the backup is
    /// encrypted with: shown to whoever is joining, once, to keep somewhere safe, since without it
    /// nobody can open the backup when this computer is gone.
    /// </summary>
    public async Task<BackupJoined> JoinAsync(string? invite, string? name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(invite))
            throw new LibraryException("Paste the invite code you were given for Uncloud Backup.", "invalid_invite");
        var hostName = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
        await _changing.WaitAsync(cancellationToken);
        try
        {
            if (Joined) throw new LibraryException("This Uncloud has already joined Uncloud Backup.", "conflict");
            var registration = await service.RegisterAsync(invite.Trim(), hostName[..Math.Min(hostName.Length, 100)], cancellationToken);
            var password = NewPassword();
            // Kept before anything else can go wrong: the invite is spent, and this credential is now
            // the only way back in.
            Save(new Saved(
                service.Service.ToString(), registration.HouseholdId, registration.HostId, registration.HostName,
                _time.GetUtcNow(), Seal(registration.Credential, CredentialContext), Seal(password, PasswordContext),
                null, "setting_up", null));
            logger.LogInformation("Joined Uncloud Backup as host {Host} of household {Household}", registration.HostId, registration.HouseholdId);
            if (_joined.CurrentCount == 0) _joined.Release();
            // The password goes in a kit with the household's recovery token, if one can be had now.
            // If not, joining has worked all the same, and a kit can be made later.
            BackupKit? kit = null;
            try { kit = await KitHeldAsync(cancellationToken); }
            catch (LibraryException error)
            {
                logger.LogWarning("Joined Uncloud Backup, but no recovery kit could be made yet: {Message}", error.Message);
            }
            return new BackupJoined(password, kit, Status);
        }
        finally
        {
            _changing.Release();
        }
    }

    /// <summary>
    /// A new recovery kit: the backup's password, and a new recovery token in place of any before
    /// it, which stops working. Shown once; whoever looks after the host keeps it.
    /// </summary>
    public async Task<BackupKit> MakeKitAsync(CancellationToken cancellationToken)
    {
        await _changing.WaitAsync(cancellationToken);
        try { return await KitHeldAsync(cancellationToken); }
        finally { _changing.Release(); }
    }

    private async Task<BackupKit> KitHeldAsync(CancellationToken cancellationToken)
    {
        var saved = Load() ?? throw new LibraryException("This Uncloud hasn’t joined Uncloud Backup.", "not_configured");
        var credential = Open(saved.Credential, CredentialContext);
        var password = Open(saved.Password, PasswordContext);
        if (credential is null || password is null)
            throw new LibraryException(
                "Uncloud’s host key doesn’t open what it kept for Uncloud Backup. Restore host.key, or ask for a new invite.", "host_key");
        var (recovery, madeAt) = await service.IssueRecoveryAsync(credential, cancellationToken);
        Save(saved with { RecoveryMadeAt = madeAt });
        return new BackupKit(password, recovery, saved.Household, madeAt);
    }

    /// <summary>
    /// Takes a lost host's place in its household with the household's recovery kit, in two steps,
    /// so a mistyped password never costs the household the host it has. The token lets this host in
    /// provisionally, with a credential of its own; then, once the kit's password has opened the
    /// backup, it confirms, and only then are the household's other hosts revoked. If the password
    /// doesn't open it, this host withdraws instead, and the others are as they were. Backing up
    /// starts paused, so nothing this host has yet is added to the
    /// backup before what's needed from it has been got back. It counts as joined only once
    /// <paramref name="opens"/> has said the kit's password opens the backup, or that there's nothing
    /// backed up there yet to open. Anything short of that, whether storage that isn't handed over,
    /// a check that can't be made or a password the backup doesn't take, and this host forgets it
    /// joined, so the kit can be given again: its token goes on working until a new kit replaces it.
    /// The account stays held until that check is done: asking for storage the moment a host joins
    /// would otherwise save it again after it had been forgotten.
    /// </summary>
    public async Task<BackupAccountStatus> RecoverAsync(
        string? recovery, string? password, string? name, Func<CancellationToken, Task<bool?>> opens, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recovery))
            throw new LibraryException("Type in the recovery token from the household’s recovery kit.", "invalid_recovery");
        if (string.IsNullOrWhiteSpace(password))
            throw new LibraryException("Type in the backup password from the household’s recovery kit.", "invalid_recovery");
        var hostName = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
        await _changing.WaitAsync(cancellationToken);
        try
        {
            if (Joined) throw new LibraryException("This Uncloud has already joined Uncloud Backup.", "conflict");
            var registration = await service.RecoverAsync(recovery.Trim(), hostName[..Math.Min(hostName.Length, 100)], cancellationToken);
            Save(new Saved(
                service.Service.ToString(), registration.HouseholdId, registration.HostId, registration.HostName,
                _time.GetUtcNow(), Seal(registration.Credential, CredentialContext), Seal(password.Trim(), PasswordContext),
                null, "setting_up", null, RecoveryMadeAt: registration.RecoveryMadeAt, Paused: true));
            logger.LogInformation("Took a lost host's place in household {Household} as host {Host}", registration.HouseholdId, registration.HostId);
            var accepted = false;
            try
            {
                // The household is set up already, so its storage is usually there at once.
                await RefreshStorageHeldAsync(cancellationToken);
                if (Target is null)
                    throw new LibraryException(
                        "Uncloud Backup couldn’t hand this Uncloud the household’s storage just now, so the kit couldn’t be "
                        + "checked. Try again in a minute: the kit still works.", "unavailable");
                if (await opens(cancellationToken) == false)
                    throw new LibraryException(
                        "That password doesn’t open this household’s backup. Check it against the recovery kit, then try again.", "invalid_recovery");
                await service.ConfirmRecoveryAsync(registration.Credential, cancellationToken);
                accepted = true;
            }
            catch (Exception error)
            {
                if (!accepted)
                {
                    // Withdrawn, so the household's hosts stay as they were. If the service can't be
                    // reached to say so, it retires this one by itself within the hour. not_recovering
                    // means the confirm landed and its answer was lost: this host is the household's
                    // host, and forgetting it would throw away the only credential left.
                    try { await service.AbandonRecoveryAsync(registration.Credential, CancellationToken.None); }
                    catch (LibraryException withdraw) when (withdraw.Code == "not_recovering")
                    {
                        accepted = true;
                    }
                    catch (LibraryException withdraw)
                    {
                        logger.LogWarning("Couldn't withdraw from household {Household}: {Message}", registration.HouseholdId, withdraw.Message);
                    }
                }
                if (!accepted)
                {
                    ForgetHeld();
                    throw;
                }
                logger.LogWarning("Recovery confirm for household {Household} landed, but its answer didn't: {Message}",
                    registration.HouseholdId, error.Message);
            }
            // Only now is this host joined, so whatever is waiting to ask for its storage asks now.
            if (_joined.CurrentCount == 0) _joined.Release();
            return Status;
        }
        finally
        {
            _changing.Release();
        }
    }

    /// <summary>Lets backing up go on: nightly again, and by hand.</summary>
    public void Resume()
    {
        _changing.Wait();
        try
        {
            if (Load() is not { } saved) return;
            var wake = saved.Paused;
            Save(saved with { Paused = false });
            // The nightly schedule picks up at once, not at its next look. Said while this is held,
            // as it is when the host joins: two resumes at once would otherwise both find the wait
            // unreleased, and the second would fail.
            if (wake && _joined.CurrentCount == 0) _joined.Release();
        }
        finally
        {
            _changing.Release();
        }
    }

    /// <summary>Forgets this host's place in Uncloud Backup, as if it had never joined.</summary>
    public void Forget()
    {
        _changing.Wait();
        try { ForgetHeld(); }
        finally { _changing.Release(); }
    }

    private void ForgetHeld() => database.RemoveSetting(Key);

    /// <summary>
    /// Carries the place in Uncloud Backup kept in <paramref name="from"/> over to <paramref name="to"/>,
    /// each secret sealed again with the key <paramref name="to"/> is opened with. A host rebuilt from
    /// its backup keeps the place the host rebuilding it has, which the service knows, not the lost
    /// host's from the backup, which taking its place revoked; and it backs up again, nothing being
    /// left to get back first. Says whether there was a place to carry over that its key opened.
    /// </summary>
    public static bool CarryOver(ControlDatabase from, SecretProtector fromKey, ControlDatabase to, SecretProtector toKey)
    {
        Saved? saved = null;
        try { saved = from.Setting(Key) is { } text ? JsonSerializer.Deserialize<Saved>(text) : null; }
        catch (JsonException) { }
        string? Again(string sealedText, string context)
        {
            try
            {
                return fromKey.Open(Convert.FromBase64String(sealedText), context) is { } secret
                    ? Convert.ToBase64String(toKey.Seal(secret, context))
                    : null;
            }
            catch (FormatException) { return null; }
        }
        if (saved is null || Again(saved.Credential, CredentialContext) is not { } credential
            || Again(saved.Password, PasswordContext) is not { } password)
            return false;
        // A storage key that doesn't open is asked for again, as it is before there's ever been one.
        var storage = saved.Storage is { } kept && Again(kept.SecretAccessKey, StorageContext) is { } secretKey
            ? kept with { SecretAccessKey = secretKey }
            : null;
        to.SetSetting(Key, JsonSerializer.Serialize(saved with
        {
            Credential = credential, Password = password, Storage = storage,
            SetupState = storage is null ? "setting_up" : saved.SetupState, Paused = false
        }));
        return true;
    }

    /// <summary>
    /// Asks the service for this host's storage, once. When it's ready the answer carries a new key,
    /// kept in place of any before it, which then stops working.
    /// </summary>
    public async Task<StorageAnswer> RefreshStorageAsync(CancellationToken cancellationToken)
    {
        await _changing.WaitAsync(cancellationToken);
        try { return await RefreshStorageHeldAsync(cancellationToken); }
        finally { _changing.Release(); }
    }

    /// <summary>As <see cref="RefreshStorageAsync"/>, for a caller that already holds the change lock.</summary>
    private async Task<StorageAnswer> RefreshStorageHeldAsync(CancellationToken cancellationToken)
    {
        var saved = Load() ?? throw new LibraryException("This Uncloud hasn’t joined Uncloud Backup.", "not_configured");
        var credential = Open(saved.Credential, CredentialContext) ?? throw new LibraryException(
            "Uncloud’s host key doesn’t open what it kept for Uncloud Backup. Restore host.key, or ask for a new invite.", "host_key");
        var answer = await service.StorageAsync(credential, cancellationToken);
        Save(answer.Storage is { } storage
            ? saved with
            {
                Storage = new SavedStorage(storage.Endpoint, storage.Region, storage.Bucket, storage.AccessKeyId,
                    Seal(storage.SecretAccessKey, StorageContext), _time.GetUtcNow()),
                SetupState = null,
                SetupStep = null
            }
            : saved with { SetupState = answer.State, SetupStep = answer.Step });
        return answer;
    }

    /// <summary>
    /// Waits <paramref name="wait"/>, or less if the host joins or resumes backing up meanwhile. Says
    /// whether either happened.
    /// </summary>
    public Task<bool> NapAsync(TimeSpan wait, CancellationToken cancellationToken) =>
        _joined.WaitAsync(wait, cancellationToken);

    private static string NewPassword()
    {
        // 256 is a multiple of 32, so taking each byte modulo 32 favours no character.
        var characters = RandomNumberGenerator.GetBytes(30).Select(value => Alphabet[value % Alphabet.Length]).ToArray();
        return string.Join('-', characters.Chunk(5).Select(group => new string(group)));
    }

    private string Seal(string secret, string context) => Convert.ToBase64String(protector.Seal(secret, context));

    private string? Open(string sealedText, string context)
    {
        try { return protector.Open(Convert.FromBase64String(sealedText), context); }
        catch (FormatException) { return null; }
    }

    private Saved? Load()
    {
        var text = database.Setting(Key);
        if (text is null) return null;
        try { return JsonSerializer.Deserialize<Saved>(text); }
        catch (JsonException) { return null; }
    }

    private void Save(Saved saved) => database.SetSetting(Key, JsonSerializer.Serialize(saved));

    /// <summary>As kept in the host's settings. Every secret in it is sealed.</summary>
    private sealed record Saved(
        string Service, string Household, string Host, string HostName, DateTimeOffset JoinedAt,
        string Credential, string Password, SavedStorage? Storage, string? SetupState, string? SetupStep,
        DateTimeOffset? RecoveryMadeAt = null, bool Paused = false);

    private sealed record SavedStorage(
        string Endpoint, string Region, string Bucket, string AccessKeyId, string SecretAccessKey, DateTimeOffset IssuedAt);
}
