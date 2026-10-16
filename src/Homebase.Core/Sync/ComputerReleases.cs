using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Homebase.Core.Accounts;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace Homebase.Core.Sync;

/// <summary>What a computer is handed to sign when it asks to be let go.</summary>
public sealed record ReleaseChallenge(string Nonce, DateTimeOffset ExpiresAt);

/// <summary>
/// A computer letting itself go from the account it was added to, as the Uncloud app on it does
/// when it disconnects, so it can be added to another account without somebody signing in as the
/// first one to click Remove. There's no session to ask for, so the computer proves it's the one by
/// signing a challenge with its Syncthing key. Its device ID is the hash of that key's certificate,
/// and the host already trusts whoever holds the key with everything the account syncs, which is
/// more than letting go. Nothing is handed over at pairing for this, so computers added before it
/// are covered too. Challenges are kept in memory: each lasts a couple of minutes.
/// </summary>
public sealed class ComputerReleases(SyncService sync, SyncOwnership ownership, ISyncthingApi syncthing)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Asking needs no session, so what can be waiting at once is bounded, across the host and from
    /// any one address, as it is for <see cref="PairingRequests"/>.
    /// </summary>
    public const int MostWaiting = 32;
    public const int MostFromOneAddress = 8;

    /// <summary>The most looked at. A Syncthing certificate is under a kilobyte; an old RSA one, about two.</summary>
    private const int LargestCertificate = 8 * 1024;
    private const int LargestSignature = 2 * 1024;
    private const int LargestRsaKey = 4096;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private readonly Dictionary<string, Waiting> _challenges = new(StringComparer.Ordinal);
    private readonly LoginThrottle _throttle = new();

    /// <summary>The clock, replaceable so expiry can be tested without waiting.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Something for this computer to sign. Handed out for any device ID that's well formed, whether
    /// this host has that computer or not, so asking says nothing about which computers it has.
    /// </summary>
    public ReleaseChallenge Challenge(string? deviceId, string? address)
    {
        RequireAllowed(address);
        var device = SyncService.NormalizeDeviceId(deviceId);
        lock (_challenges)
        {
            var now = Now();
            foreach (var (nonce, waiting) in _challenges.ToArray())
                if (waiting.ExpiresAt <= now) _challenges.Remove(nonce);
            if (address is not null && _challenges.Values.Count(waiting => waiting.Address == address) >= MostFromOneAddress)
                throw TooMany();
            if (_challenges.Count >= MostWaiting)
                throw new LibraryException(
                    "Too many computers are disconnecting right now. Wait a few minutes and try again.", "busy");

            var challenge = new ReleaseChallenge(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)), now + Lifetime);
            _challenges[challenge.Nonce] = new Waiting(device, address, challenge.ExpiresAt);
            return challenge;
        }
    }

    /// <summary>
    /// Lets go of the computer whose key signed this challenge, as its account would from My
    /// computers: its folders stop being shared with it, and it can be added to any account. A
    /// challenge is good for one try. Anything wrong gets the same answer, whether the challenge was
    /// used or ran out, the certificate isn't that computer's, the key is wrong, or this host doesn't
    /// have the computer.
    /// </summary>
    /// <param name="certificate">The computer's Syncthing certificate, as PEM.</param>
    /// <param name="signature">What <see cref="Message"/> gives for this challenge, signed with that certificate's key, in base64.</param>
    public async Task ReleaseAsync(string? certificate, string? nonce, string? signature, string? address,
        CancellationToken cancellationToken)
    {
        RequireAllowed(address);
        Waiting? waiting = null;
        lock (_challenges)
            if (nonce is not null && _challenges.Remove(nonce, out var found) && found.ExpiresAt > Now())
                waiting = found;

        // The signature is checked before whether this host has the computer, so how long a refusal
        // takes says nothing about which computers it has.
        if (waiting is null || Identify(certificate) is not { } signer || signer.DeviceId != waiting.DeviceId
            || !Verify(signer.Key, Message(await syncthing.DeviceIdAsync(cancellationToken), signer.DeviceId, nonce!), signature)
            || ownership.FindDevice(signer.DeviceId) is not { } owned)
        {
            _throttle.RecordFailure(null, address);
            throw new LibraryException("Uncloud doesn’t have this computer, or couldn’t tell that it’s this one asking.", "not_found");
        }
        await sync.UnpairAsync(owned.UserId, owned.DeviceId, cancellationToken);
        _throttle.RecordSuccess(null, address);
    }

    /// <summary>
    /// What a computer signs: the challenge, tied to this host, to the computer and to what it's for,
    /// so a signature made for anything else, or for another host, is no use here.
    /// </summary>
    public static byte[] Message(string hostDeviceId, string deviceId, string nonce) =>
        Encoding.UTF8.GetBytes($"uncloud-release-v1\n{hostDeviceId}\n{deviceId}\n{nonce}");

    /// <summary>
    /// Signs <paramref name="message"/> as the computer holding this Syncthing key, in base64: Ed25519
    /// for a key Syncthing 2 made, ECDSA or RSA for one an earlier Syncthing made.
    /// </summary>
    /// <exception cref="CryptographicException">There's no key in the file that Syncthing could have made.</exception>
    public static string Sign(string keyPem, byte[] message)
    {
        AsymmetricKeyParameter key;
        try
        {
            using var reader = new StringReader(keyPem);
            key = new PemReader(reader).ReadObject() switch
            {
                AsymmetricCipherKeyPair pair => pair.Private,
                AsymmetricKeyParameter { IsPrivate: true } alone => alone,
                _ => throw new CryptographicException("There’s no private key in Syncthing’s key file.")
            };
        }
        catch (Exception error) when (error is IOException or SecurityUtilityException or ArgumentException or InvalidCastException)
        {
            throw new CryptographicException("Syncthing’s key file can’t be read.", error);
        }
        var signer = SignerUtilities.GetSigner(Algorithm(key)
            ?? throw new CryptographicException("Syncthing’s key isn’t a kind Uncloud can sign with."));
        signer.Init(true, key);
        signer.BlockUpdate(message, 0, message.Length);
        return Convert.ToBase64String(signer.GenerateSignature());
    }

    /// <summary>The device ID of the computer with this Syncthing certificate, or null for anything that isn't one.</summary>
    public static string? DeviceIdOf(string? certificatePem) => Identify(certificatePem)?.DeviceId;

    /// <summary>
    /// The device ID Syncthing gives the computer with this certificate: the SHA-256 of its DER in
    /// base32, with a check character after every thirteen, in groups of seven.
    /// </summary>
    public static string DeviceIdFor(ReadOnlySpan<byte> certificate)
    {
        var plain = Base32(SHA256.HashData(certificate));
        var checkedId = string.Concat(Enumerable.Range(0, 4)
            .Select(group => plain.Substring(group * 13, 13))
            .Select(part => part + Check(part)));
        return string.Join('-', Enumerable.Range(0, 8).Select(group => checkedId.Substring(group * 7, 7)));
    }

    /// <summary>RFC 4648 base32, without the padding Syncthing drops.</summary>
    private static string Base32(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var value in bytes)
        {
            buffer = ((buffer << 8) | value) & 0xFFFF;
            for (bits += 8; bits >= 5; bits -= 5)
                text.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
        }
        if (bits > 0) text.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    /// <summary>
    /// Syncthing's check character: Luhn mod 32 over the base32 alphabet, doubling every second
    /// character counted from the first rather than the last, as Syncthing always has.
    /// </summary>
    private static char Check(string part)
    {
        int factor = 1, sum = 0;
        foreach (var character in part)
        {
            var addend = factor * Base32Alphabet.IndexOf(character);
            factor = factor == 2 ? 1 : 2;
            sum += addend / 32 + addend % 32;
        }
        return Base32Alphabet[(32 - sum % 32) % 32];
    }

    /// <summary>
    /// The computer a certificate is for and the key it holds, for a certificate of a kind Syncthing
    /// makes; null for anything else. The device ID is the hash of exactly the bytes the key is read
    /// from, so nothing but that computer's own certificate can stand for it.
    /// </summary>
    private static Identity? Identify(string? pem)
    {
        if (pem is null || pem.Length > LargestCertificate || !PemEncoding.TryFind(pem, out var fields)
            || !pem.AsSpan()[fields.Label].SequenceEqual("CERTIFICATE"))
            return null;
        try
        {
            var der = Convert.FromBase64String(pem[fields.Base64Data]);
            return new X509CertificateParser().ReadCertificate(der)?.GetPublicKey() is { } key && Algorithm(key) is not null
                ? new Identity(DeviceIdFor(der), key)
                : null;
        }
        // Whatever a malformed certificate makes the parser throw, it is only something that isn't
        // a computer's certificate.
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool Verify(AsymmetricKeyParameter key, byte[] message, string? signature)
    {
        if (signature is null || signature.Length > LargestSignature || Algorithm(key) is not { } algorithm) return false;
        try
        {
            var bytes = Convert.FromBase64String(signature);
            var verifier = SignerUtilities.GetSigner(algorithm);
            verifier.Init(false, key);
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(bytes);
        }
        // Nor does a malformed signature say anything but no.
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// How a key of each kind Syncthing has made signs a release; null for any other kind. RSA keys
    /// are bounded, since checking with a big enough one would be slow.
    /// </summary>
    private static string? Algorithm(AsymmetricKeyParameter key) => key switch
    {
        Ed25519PublicKeyParameters or Ed25519PrivateKeyParameters => "Ed25519",
        ECKeyParameters => "SHA-256withECDSA",
        RsaKeyParameters rsa when rsa.Modulus.BitLength <= LargestRsaKey => "SHA-256withRSAandMGF1",
        _ => null
    };

    private void RequireAllowed(string? address)
    {
        try { _throttle.RequireAllowed(null, address); }
        catch (LibraryException) { throw TooMany(); }
    }

    private static LibraryException TooMany() => new(
        "Too many computers have tried to disconnect from here. Wait a few minutes and try again.", "too_many_attempts");

    private sealed record Waiting(string DeviceId, string? Address, DateTimeOffset ExpiresAt);

    private sealed record Identity(string DeviceId, AsymmetricKeyParameter Key);
}
