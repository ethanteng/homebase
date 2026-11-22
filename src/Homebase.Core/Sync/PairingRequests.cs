using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Homebase.Core.Accounts;

namespace Homebase.Core.Sync;

/// <summary>
/// What a computer is handed when it asks to be added: the id its QR code carries, which anybody
/// looking at its screen can read, and the secret only the computer holds, which collects the
/// answer.
/// </summary>
public sealed record PairingTicket(string Id, string Secret, DateTimeOffset ExpiresAt);

/// <summary>What the person approving is shown about the computer that asked.</summary>
/// <param name="State">
/// <see cref="PairingRequests.Waiting"/> until they answer, then <see cref="PairingRequests.Added"/>
/// or <see cref="PairingRequests.Declined"/> — said only to whoever answered, so a second look at
/// the same link shows what they decided instead of an error.
/// </param>
public sealed record PairingAsk(string Name, DateTimeOffset ExpiresAt, string State = PairingRequests.Waiting);

/// <summary>Whether a computer's request has been approved yet, and if so, what it needs to sync.</summary>
/// <param name="Declined">Somebody signed in said no: the computer stops asking and says so.</param>
public sealed record PairingAnswer(bool Approved, PairingResult? Result, bool Declined = false);

/// <summary>
/// Pairing the other way round from <see cref="PairingCodes"/>: the computer asks first and shows
/// a QR code, and a signed-in person scans it with their phone and approves. Nobody copies or
/// types anything. The id in the QR code only ever leads to a page that needs a session, and the
/// computer collects the answer with a secret that never leaves it, so somebody who photographs
/// the screen can neither approve the request for themselves nor learn what was approved.
/// Requests are kept in memory: each lasts minutes, and a host that restarts in the middle of
/// one costs the computer nothing but a fresh QR code.
/// </summary>
public sealed class PairingRequests(
    SyncService sync,
    SyncOwnership ownership,
    UserStore users,
    ISyncthingApi syncthing)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public const string Waiting = "waiting";
    public const string Added = "added";
    public const string Declined = "declined";

    /// <summary>
    /// Asking needs no session, so what can be waiting at once is bounded: across the host, and
    /// from any one address. A household setting up a few computers together is well inside both.
    /// </summary>
    public const int MostWaiting = 32;
    public const int MostFromOneAddress = 8;

    private readonly Dictionary<string, Request> _requests = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _approving = new(1, 1);

    /// <summary>The clock, replaceable so expiry can be tested without waiting ten minutes.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// A computer asking to be added. The same computer asking again — its window reopened, or
    /// its QR code about to run out — replaces what it asked before, so only the code on its
    /// screen now can be approved.
    /// </summary>
    /// <param name="everything">
    /// Whether to bring the computer in on everything the account syncs once it is approved, as
    /// the Uncloud app always asks to be.
    /// </param>
    public PairingTicket Open(string? deviceId, string? name, bool everything, string? address)
    {
        if (!syncthing.IsAvailable)
            throw new LibraryException(syncthing.Unavailable ?? "Syncthing isn’t running on this Uncloud.", "sync_unavailable");
        var device = SyncService.NormalizeDeviceId(deviceId);
        var label = string.IsNullOrWhiteSpace(name) ? "A computer" : name.Trim();
        if (label.Length > 64) label = label[..64];

        lock (_requests)
        {
            var now = Now();
            foreach (var (id, waiting) in _requests.ToArray())
                if (waiting.ExpiresAt <= now || waiting.DeviceId == device)
                    _requests.Remove(id);
            // One already answered and collected is only kept to say so; it isn't waiting.
            var open = _requests.Values.Where(waiting => !waiting.Collected).ToList();
            if (address is not null && open.Count(waiting => waiting.Address == address) >= MostFromOneAddress)
                throw new LibraryException(
                    "Too many computers are waiting to be added from here. Wait a few minutes and try again.", "too_many_attempts");
            if (open.Count >= MostWaiting)
                throw new LibraryException(
                    "Too many computers are waiting to be added right now. Wait a few minutes and try again.", "busy");

            var request = new Request(device, label, everything, address, Token(32), now + Lifetime);
            var ticket = Token(16);
            _requests[ticket] = request;
            return new PairingTicket(ticket, request.Secret, request.ExpiresAt);
        }
    }

    /// <summary>
    /// What a person about to approve is shown: the name the computer gave itself. Once they have
    /// answered, the same link says what they answered, for as long as the request is kept — a
    /// second scan or a reload isn't an error. Anybody else is told it's gone, as before.
    /// </summary>
    public PairingAsk Describe(string? id, string userId)
    {
        lock (_requests)
        {
            if (Find(id) is not { } request) throw Gone();
            if (request.DeclinedBy is { } declinedBy)
                return declinedBy == userId ? new PairingAsk(request.Name, request.ExpiresAt, Declined) : throw Gone();
            if (request.Result is not null)
                return request.ApprovedBy == userId ? new PairingAsk(request.Name, request.ExpiresAt, Added) : throw Gone();
            return new PairingAsk(request.Name, request.ExpiresAt);
        }
    }

    /// <summary>
    /// Adds the computer that asked to this person's computers. Approving twice is harmless;
    /// approving a computer somebody else has just added is refused without saying whose it is.
    /// </summary>
    /// <param name="rootFor">Where an account's files are, for a computer brought in on everything.</param>
    public async Task ApproveAsync(string? id, string userId, Func<string, string> rootFor, CancellationToken cancellationToken)
    {
        await _approving.WaitAsync(cancellationToken);
        try
        {
            Request request;
            lock (_requests) request = Find(id) is { DeclinedBy: null } found ? found : throw Gone();
            if (request.Result is not null)
            {
                if (request.ApprovedBy == userId) return;
                throw SyncService.HeldByAnotherAccount(justNow: true);
            }
            var account = users.Find(userId) is { IsActive: true } active
                ? active
                : throw new LibraryException("Sign in to Uncloud to continue.", "unauthenticated");

            // Already this person's — approved once, then the computer asked again after a host
            // restart lost the answer — is success, not a conflict.
            if (ownership.FindDevice(request.DeviceId) is not { } owned || owned.UserId != userId)
                await sync.PairAsync(userId, request.DeviceId, request.Name, cancellationToken);
            var folders = request.Everything
                ? await sync.IncludeAsync(userId, rootFor(userId), request.DeviceId, cancellationToken)
                : [];
            var result = new PairingResult(await syncthing.DeviceIdAsync(cancellationToken), account.DisplayName,
                folders.Select(folder => new PairedFolder(folder.Id, folder.Label, folder.Path)).ToArray());
            lock (_requests)
            {
                request.ApprovedBy = userId;
                request.Result = result;
            }
        }
        finally
        {
            _approving.Release();
        }
    }

    /// <summary>
    /// Says no to a computer that asked, so it stops waiting and says it wasn't added. Final: the
    /// same code can't be approved afterwards, by this person or anybody else. Saying no twice is
    /// harmless; saying no to a computer already added is refused, since it's syncing by then.
    /// Decided one at a time with approving: a no from another tab waits for a yes already under
    /// way, rather than landing while it pairs the computer and leaving one added that the
    /// computer is told wasn't.
    /// </summary>
    public async Task DeclineAsync(string? id, string userId, CancellationToken cancellationToken)
    {
        await _approving.WaitAsync(cancellationToken);
        try
        {
            lock (_requests)
            {
                if (Find(id) is not { } request) throw Gone();
                if (request.DeclinedBy is { } declinedBy)
                {
                    if (declinedBy == userId) return;
                    throw Gone();
                }
                if (request.Result is not null)
                    throw request.ApprovedBy == userId
                        ? new LibraryException(
                            "That computer is already added. To stop it syncing, open Computers and click Remove next to it.", "conflict")
                        : Gone();
                request.DeclinedBy = userId;
            }
        }
        finally
        {
            _approving.Release();
        }
    }

    /// <summary>
    /// The computer asking whether it has been approved. An answer is handed over once, and then
    /// the computer is told the request is gone; a wrong secret is told the same as a request that
    /// never existed. What was answered is kept until the request runs out, for the person who
    /// answered to see again.
    /// </summary>
    public PairingAnswer Answer(string? id, string? secret)
    {
        lock (_requests)
        {
            if (Find(id) is not { Collected: false } request || !Matches(request.Secret, secret)) throw Gone();
            if (request.DeclinedBy is not null)
            {
                request.Collected = true;
                return new PairingAnswer(false, null, Declined: true);
            }
            if (request.Result is null) return new PairingAnswer(false, null);
            request.Collected = true;
            return new PairingAnswer(true, request.Result);
        }
    }

    private Request? Find(string? id) =>
        id is not null && _requests.TryGetValue(id, out var request) && request.ExpiresAt > Now() ? request : null;

    private static LibraryException Gone() => new(
        "This request has run out or was already answered. Start again from the Uncloud app on the computer you’re adding.", "not_found");

    private static bool Matches(string expected, string? given) =>
        given is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given));

    private static string Token(int bytes) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    private sealed class Request(string deviceId, string name, bool everything, string? address, string secret, DateTimeOffset expiresAt)
    {
        public string DeviceId { get; } = deviceId;
        public string Name { get; } = name;
        public bool Everything { get; } = everything;
        public string? Address { get; } = address;
        public string Secret { get; } = secret;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public string? ApprovedBy { get; set; }
        public PairingResult? Result { get; set; }
        public string? DeclinedBy { get; set; }
        /// <summary>The computer has its answer; the request is only kept to show it again.</summary>
        public bool Collected { get; set; }
    }
}
