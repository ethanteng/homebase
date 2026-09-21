using System.Security.Cryptography;
using System.Text;

namespace Homebase.Core.Accounts;

/// <summary>
/// Seals provider refresh tokens with AES-256-GCM under a key kept beside the host's
/// preferences. The context — the owning user and the provider — is authenticated but not
/// encrypted, so a sealed token moved into another user's row fails its tag check instead of
/// handing that user somebody else's account.
/// </summary>
public sealed class SecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public SecretProtector(string directory)
    {
        var file = Path.Combine(directory, "host.key");
        Directory.CreateDirectory(directory);
        if (File.Exists(file))
        {
            _key = File.ReadAllBytes(file);
            if (_key.Length != 32)
                throw new LibraryException(
                    "Uncloud's host key is damaged. Restore it, or disconnect and reconnect each provider account.",
                    "host_key");
            return;
        }
        _key = RandomNumberGenerator.GetBytes(32);
        var temporary = Path.Combine(directory, $"host.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, _key);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            // Never overwrite: another process that got there first holds the key the stored
            // tokens were sealed under, and replacing it would silently orphan every connection.
            File.Move(temporary, file);
        }
        catch (IOException) when (File.Exists(file))
        {
            _key = File.ReadAllBytes(file);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public byte[] Seal(string plaintext, string context)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + bytes.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, bytes, output.AsSpan(NonceSize, bytes.Length),
            output.AsSpan(NonceSize + bytes.Length, TagSize), Encoding.UTF8.GetBytes(context));
        return output;
    }

    /// <summary>The plaintext, or null when this was not sealed for this context.</summary>
    public string? Open(byte[] sealedBytes, string context)
    {
        if (sealedBytes.Length < NonceSize + TagSize) return null;
        var length = sealedBytes.Length - NonceSize - TagSize;
        var plaintext = new byte[length];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(sealedBytes.AsSpan(0, NonceSize), sealedBytes.AsSpan(NonceSize, length),
                sealedBytes.AsSpan(NonceSize + length, TagSize), plaintext, Encoding.UTF8.GetBytes(context));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            // A tampered, relocated, or differently-keyed token reads as no connection at all.
            return null;
        }
    }
}
