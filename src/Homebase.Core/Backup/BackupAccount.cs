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

/// <summary>A host the service has just let in: its household, itself, and the credential it's known by.</summary>
public sealed record BackupRegistration(string HouseholdId, string HostId, string HostName, string Credential);

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
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new BackupRegistration(
            body.GetProperty("household").GetProperty("id").GetString()!,
            body.GetProperty("host").GetProperty("id").GetString()!,
            body.GetProperty("host").GetProperty("name").GetString()!,
            body.GetProperty("credential").GetString()!);
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
            "not_a_host" => new LibraryException(
                "Uncloud Backup no longer knows this Uncloud. Ask for a new invite to join again.", "conflict"),
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
}

/// <summary>A host that has just joined, and the password its backup will be encrypted with.</summary>
public sealed record BackupJoined(string Password, BackupAccountStatus Account);

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
    private const string Key = "backup_account";
    private const string CredentialContext = "backup:credential";
    private const string PasswordContext = "backup:password";
    private const string StorageContext = "backup:storage";
    // Read out and typed from a recovery kit some day, so it leaves out what's easily misread (0 and
    // O, 1 and I), in groups of five: 30 characters from 32 is 150 bits.
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _changing = new(1, 1);
    // Let go when the host joins, so whatever is waiting to ask for its storage asks now.
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
            Bucket = saved.Storage?.Bucket
        };

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
            return new BackupJoined(password, Status);
        }
        finally
        {
            _changing.Release();
        }
    }

    /// <summary>
    /// Asks the service for this host's storage, once. When it's ready the answer carries a new key,
    /// kept in place of any before it, which then stops working.
    /// </summary>
    public async Task<StorageAnswer> RefreshStorageAsync(CancellationToken cancellationToken)
    {
        await _changing.WaitAsync(cancellationToken);
        try
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
        finally
        {
            _changing.Release();
        }
    }

    /// <summary>
    /// Waits <paramref name="wait"/>, or less if the host joins meanwhile. Says whether it joined.
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
        string Credential, string Password, SavedStorage? Storage, string? SetupState, string? SetupStep);

    private sealed record SavedStorage(
        string Endpoint, string Region, string Bucket, string AccessKeyId, string SecretAccessKey, DateTimeOffset IssuedAt);
}
