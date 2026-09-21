using System.Collections.Concurrent;

namespace Homebase.Core.Accounts;

/// <summary>
/// Slows down guessing. Counted per username and per client address, so neither one account
/// nor one machine can keep trying, and an attacker spraying many usernames from one address
/// runs into the same wall.
/// </summary>
public sealed class LoginThrottle
{
    public int Allowed { get; init; } = 10;
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Replaced in tests so they don't wait a quarter of an hour.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Until)> _failures = new();

    public void RequireAllowed(string? username, string? address)
    {
        foreach (var key in Keys(username, address))
            if (_failures.TryGetValue(key, out var entry) && entry.Count >= Allowed && Now() < entry.Until)
                throw new LibraryException(
                    "Too many sign-in attempts. Wait a few minutes and try again.", "too_many_attempts");
    }

    public void RecordFailure(string? username, string? address)
    {
        var now = Now();
        foreach (var key in Keys(username, address))
            _failures.AddOrUpdate(key, (1, now + Window),
                (_, entry) => now >= entry.Until ? (1, now + Window) : (entry.Count + 1, entry.Until));
        Prune(now);
    }

    public void RecordSuccess(string? username, string? address)
    {
        foreach (var key in Keys(username, address)) _failures.TryRemove(key, out _);
    }

    private static IEnumerable<string> Keys(string? username, string? address)
    {
        if (!string.IsNullOrWhiteSpace(username)) yield return $"user:{username.Trim().ToLowerInvariant()}";
        if (!string.IsNullOrWhiteSpace(address)) yield return $"from:{address}";
    }

    /// <summary>Keeps a spray of invented usernames from growing the table without bound.</summary>
    private void Prune(DateTimeOffset now)
    {
        if (_failures.Count < 1000) return;
        foreach (var (key, entry) in _failures)
            if (now >= entry.Until)
                _failures.TryRemove(key, out _);
    }
}
