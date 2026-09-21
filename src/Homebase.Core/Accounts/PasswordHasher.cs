using System.Security.Cryptography;

namespace Homebase.Core.Accounts;

/// <summary>
/// PBKDF2-HMAC-SHA512 over a per-password salt. The iteration count is stored with the hash, so
/// raising it later re-hashes on the next sign-in rather than locking everybody out.
/// </summary>
public static class PasswordHasher
{
    private const int Default = 210_000;

    /// <summary>
    /// Lowered only by the test suite, which signs in hundreds of times where a person signs in
    /// once a month. Not reachable outside this assembly, so a deployment always gets the real
    /// number.
    /// </summary>
    internal static int Override { get; set; }

    public static int Iterations => Override > 0 ? Override : Default;
    public const int MinimumLength = 10;
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const string Scheme = "pbkdf2-sha512";

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, KeySize);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme) return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations < 1) return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// A length floor and nothing else. Composition rules push people towards worse passwords
    /// they write down, and this host has no way to check a password against a breach list.
    /// </summary>
    public static void Require(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength)
            throw new LibraryException(
                $"Choose a password of at least {MinimumLength} characters.", "invalid_password");
        if (password.Length > 256)
            throw new LibraryException("That password is too long for Uncloud to store.", "invalid_password");
    }
}
